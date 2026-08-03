# Relational Backend Developer Guide

This is a developer runbook for the **relational backend** — the tables-per-resource
storage model for the Ed-Fi API (DMS). It explains how to provision a database for a
given effective schema, how DMS validates that schema on first use, how to debug the
write/read paths and update tracking, and how to run the relevant tests locally.

It is a hub: the deep design rationale lives under
[`reference/design/backend-redesign/design-docs/`](../reference/design/backend-redesign/design-docs/overview.md),
and command/option details live in the
[`api-schema-tools` CLI README](../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md).
This guide ties those together for day-to-day work and links to them rather than
restating them.

## Contents

- [1. Overview](#1-overview)
- [2. Provisioning a database for an effective schema](#2-provisioning-a-database-for-an-effective-schema)
- [3. Schema-fingerprint validation — how DMS validates schema on first use](#3-schema-fingerprint-validation--how-dms-validates-schema-on-first-use)
- [4. Debugging the write/read paths and update tracking (stored stamps)](#4-debugging-the-writeread-paths-and-update-tracking-stored-stamps)
- [5. Mapping packs (optional)](#5-mapping-packs-optional)
- [6. Running the relevant tests locally](#6-running-the-relevant-tests-locally)
- [7. E2E setup/teardown and the "no hot reload" rule](#7-e2e-setupteardown-and-the-no-hot-reload-rule)

## 1. Overview

The **relational backend** is the DMS storage model. It derives a dedicated set of tables,
views, constraints, and triggers **per resource** from the effective schema (the normalized
combination of the core `ApiSchema.json` plus any extension schemas).

For the design rationale, start with these:

- [`overview.md`](../reference/design/backend-redesign/design-docs/overview.md) — the redesign at a glance
- [`data-model.md`](../reference/design/backend-redesign/design-docs/data-model.md) — the relational schema (`dms.*` core tables, per-resource tables, descriptor projections)
- [`new-startup-flow.md`](../reference/design/backend-redesign/design-docs/new-startup-flow.md) — how the service starts up against a provisioned database

## 2. Provisioning a database for an effective schema

Provisioning is done with the **`api-schema-tools`** CLI
([project](../src/dms/clis/EdFi.DataManagementService.SchemaTools),
[README](../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md)). The CLI is
deterministic and does not require a database for artifact generation — only `ddl provision`
connects to one. See the CLI README for the full option tables; the essentials follow.

### Compute the effective schema hash

A provisioned database is keyed to one effective schema, identified by its hash. To see
that hash for a set of inputs:

```bash
api-schema-tools hash core/ApiSchema.json [extensions/.../ApiSchema.json ...]
```

The first path is the core schema; any additional paths are extensions.

### Inspect the generated artifacts (`ddl emit`)

`ddl emit` writes the DDL and manifests to a directory without touching a database —
useful for review, diffing, and golden-file testing:

```bash
api-schema-tools ddl emit --schema core/ApiSchema.json --output ./ddl-output --dialect both
```

| Output file | When | Contents |
|---|---|---|
| `pgsql.sql` / `mssql.sql` | per selected dialect | the full DDL script for that engine |
| `effective-schema.manifest.json` | always | the schema fingerprint, components, and resource-key seed summary |
| `relational-model.{dialect}.manifest.json` | per selected dialect | the derived relational model inventory (tables, columns, constraints, indexes, views, triggers) |
| `ddl.manifest.json` | only with `--ddl-manifest` | dialect-independent summary (normalized-SQL hash + statement count per dialect) for diagnostics |

`--dialect` accepts `pgsql`, `mssql`, or `both` (default `both`). All output uses Unix
line endings so the same inputs produce byte-for-byte identical files.

### Apply the DDL to a database (`ddl provision`)

`ddl provision` generates the DDL for one dialect and executes it against a target
database in a single transaction:

```bash
# PostgreSQL (create the database if it does not exist)
api-schema-tools ddl provision \
  --schema core/ApiSchema.json \
  --connection-string "Host=localhost;Port=5432;Database=edfi_dms;Username=postgres;Password=secret" \
  --dialect pgsql --create-database

# SQL Server (targets an existing database; --create-database works for either dialect)
api-schema-tools ddl provision \
  --schema core/ApiSchema.json \
  --connection-string "Server=localhost;Initial Catalog=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true" \
  --dialect mssql
```

`--dialect` here is `pgsql` or `mssql` (not `both` — provision one database at a time).
`--create-database` creates the target if missing; `--timeout` (default `300` seconds)
bounds DDL execution. For SQL Server, provisioning configures Read Committed Snapshot
Isolation (and `ALLOW_SNAPSHOT_ISOLATION`) on newly created databases.

### Scripted local provisioning

For the local Docker E2E stack, the helper
[`provision-e2e-database.ps1`](../eng/docker-compose/provision-e2e-database.ps1)
wraps the above; see [`eng/docker-compose/README.md`](../eng/docker-compose/README.md).

## 3. Schema-fingerprint validation — how DMS validates schema on first use

The relational backend records a **fingerprint** of the effective schema in the database
at provisioning time, then verifies it before serving traffic. This guarantees the
running service and the database agree on exactly one effective schema.

### Where the fingerprint lives

The fingerprint is a single row in the `dms.EffectiveSchema` singleton table (column names in
[`EffectiveSchemaTableDefinition.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.External/EffectiveSchemaTableDefinition.cs);
the table DDL and the singleton `CHECK` constraint are emitted by
[`CoreDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs)):

| Column | Meaning |
|---|---|
| `EffectiveSchemaSingletonId` | always `1` (a `CHECK` constraint enforces the single row) |
| `ApiSchemaFormatVersion` | the ApiSchema format version |
| `EffectiveSchemaHash` | 64-char lowercase hex SHA-256 of the effective schema |
| `ResourceKeyCount` | number of resource keys |
| `ResourceKeySeedHash` | 32-byte SHA-256 over the resource-key seed |
| `AppliedAt` | when the row was written |

The hash algorithm versions are pinned in
[`SchemaHashConstants.cs`](../src/dms/core/EdFi.DataManagementService.Core/Utilities/SchemaHashConstants.cs).
Bumping `HashVersion` or `RelationalMappingVersion` deliberately forces a new
`EffectiveSchemaHash` even for identical schema content; bumping `ResourceKeySeedHashVersion`
forces a new `ResourceKeySeedHash` (the separate resource-key seed hash), not the
`EffectiveSchemaHash`.

### Guards baked into the DDL (provision time)

The generated DDL ([`SeedDmlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/SeedDmlEmitter.cs),
assembled by [`FullDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/FullDdlEmitter.cs))
protects the database in two places:

- **Preflight** (search the script for the full `-- Phase 0: Preflight (fail fast on schema hash mismatch)` header). Before any DDL runs,
  if `dms.EffectiveSchema` already exists with a *different* hash, the script raises an error and
  aborts. You cannot accidentally re-provision an existing database for a different effective schema.
- **Seed insert-if-missing + validate** (search for the full `-- Phase 7: Seed Data (insert-if-missing
  + validation)` header — the bare "Phase 7" number is reused by other emitters for unrelated
  sections, so match on the label text). The fingerprint row is inserted only if absent
  (`ON CONFLICT DO NOTHING` / `IF NOT EXISTS`), then the stored `ApiSchemaFormatVersion`,
  `ResourceKeyCount`, and `ResourceKeySeedHash` are validated against the expected values and
  the script fails on any mismatch.

### The runtime first-use check

When DMS starts, it reads the stored fingerprint
([`DatabaseFingerprintReaderSupport.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DatabaseFingerprintReaderSupport.cs),
with PostgreSQL/SQL Server reader implementations in the respective backend projects) and
compares it to the effective schema it loaded. The check runs in
[`ValidateDatabaseFingerprintMiddleware`](../src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateDatabaseFingerprintMiddleware.cs):

- If `dms.EffectiveSchema` (or its singleton row) does not exist, the database is treated as not yet
  provisioned and requests receive **HTTP 503** (`ForDatabaseNotProvisioned`); run `ddl provision` to
  initialize the schema. Like the mismatch cases below, this result is cached for the process lifetime —
  if you provision after the service has already tried to use the database, restart it.
- If the stored hash does **not** match the loaded effective schema, requests receive **HTTP 503**
  with a detail explaining that the database was provisioned for a different effective schema and
  that it **must be reprovisioned with `ddl provision` against a fresh database and the service
  restarted** to clear the cached validation state.

Immediately after the fingerprint check, each routed resource pipeline runs a second first-use check,
[`ValidateResourceKeySeedMiddleware`](../src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateResourceKeySeedMiddleware.cs)
(pipeline order in [`ApiService.cs`](../src/dms/core/EdFi.DataManagementService.Core/ApiService.cs)), which
compares the stored `ResourceKeyCount` and `ResourceKeySeedHash` against the loaded effective schema.
A resource-key-seed mismatch also returns **HTTP 503** with the same remediation — reprovision against
a fresh database and restart the service. (The available-change-versions endpoint runs only the
fingerprint check, not this seed check.)

> [!IMPORTANT]
> All first-use validation failures — not-provisioned, hash mismatch, and resource-key-seed
> mismatch — are cached for the process lifetime. Reprovisioning alone does not clear
> a 503 — you must also restart the DMS process. See
> [§7, "no hot reload"](#7-e2e-setupteardown-and-the-no-hot-reload-rule).

## 4. Debugging the write/read paths and update tracking (stored stamps)

### Write and read at a glance

On **write**, a document's JSON is *flattened* into the per-resource relational tables; on
**read**, the rows are *reconstituted* back into JSON. The mapping rules and their rationale
are in the design docs:

- [`flattening-reconstitution.md`](../reference/design/backend-redesign/design-docs/flattening-reconstitution.md)
- [`update-tracking.md`](../reference/design/backend-redesign/design-docs/update-tracking.md)
- [`change-queries.md`](../reference/design/backend-redesign/design-docs/change-queries.md)

### Stored stamps and tracked-change tables

Each document carries two stamp **pairs**, both written by the same **stamping triggers** on the
document tables. The *content* pair is set together on every write: a `ContentVersion` (the
change-version number, from the shared change-version sequence) and a `ContentLastModifiedAt`
timestamp (the current UTC time). The *identity* pair — `IdentityVersion` / `IdentityLastModifiedAt`
— is set unconditionally on `INSERT` (a new row has no prior identity to preserve) and on `UPDATE` is
bumped by the same triggers only when a stored identity value actually changes. Those triggers
also populate per-resource **tracked-change tables** that live under a per-project schema named
`tracked_changes_<projectSchema>` (for example the `tracked_changes_edfi` schema), recording the
old/new identity and securable values plus a `ChangeVersion`. (Descriptors share a single
tracked-change table within that schema rather than one table per resource.) When debugging a stamp or a
tracked-change row, these are the sources of truth:

- [`RelationalModelDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs) (per-resource root tables) and [`CoreDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs) (descriptors) — the stamping-trigger bodies that write the `ContentVersion` / `ContentLastModifiedAt` stamps
- [`TrackedChangeTriggerBodyEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/TrackedChangeTriggerBodyEmitter.cs) — the trigger bodies that write the tracked-change rows (they read the already-stamped `ContentVersion`)
- [`DeriveTrackedChangeInventoryPass.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTrackedChangeInventoryPass.cs) — how the tracked-change table inventory and columns are derived

Inspect the relevant per-resource table under that schema (for example
`tracked_changes_edfi.<resourceTable>`) directly to see the `OldX`/`NewX` value columns, the
document `Id`, and the `ChangeVersion` for a given write. Only the separator after the `Old` or
`New` prefix is removed; source-name underscores are preserved, for example
`OldStudent_DocumentId`.

#### Read metadata (`_etag`, `_lastModifiedDate`)

On read, `_lastModifiedDate` is served from the stored `ContentLastModifiedAt`, and `_etag` is composed
as `"{ContentVersion}-{variantKey}"` — the stored change-version number plus a representation
discriminator (`schemaEpoch.format.profileCode.linkFlag.contentCoding`), with **no hashing of content**
([`EtagComposer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/Etag/EtagComposer.cs),
[`VariantKey.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/Etag/VariantKey.cs)). Both
values therefore come from the same row the read already loaded. When a served `_etag` or
`_lastModifiedDate` looks wrong, start here:

- [`RelationalReadMaterializer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadMaterializer.cs) — derives `_etag` and serves `_lastModifiedDate` for resources
- [`DescriptorDocumentMaterializer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorDocumentMaterializer.cs) — the same for descriptors

#### Change-version filtering (`minChangeVersion` / `maxChangeVersion`)

Root and descriptor tables carry **mirrored** `ContentVersion` / `ContentLastModifiedAt` columns
(`ColumnKind.MirroredContentVersion`) that the query change-version filter ranges over. It is the
resource-page filter only; the `/deletes` and `/keyChanges` endpoints range over the tracked-change
tables through their own planner (see [Change-query reads](#change-query-reads) below).

The **"mirror" name is historical**: it dates from when these columns duplicated a `dms.Document` row.
That table is gone, so a mirror column is now the only copy of the value — the code names (`Mirrored…`,
`Derive…MirrorPass`) stayed, the second copy did not.

- [`DeriveContentVersionMirrorPass.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveContentVersionMirrorPass.cs) — derives the mirrored `ContentVersion` / `ContentLastModifiedAt` columns on root resource tables (descriptor mirror columns live on the shared `dms.Descriptor` table from the core DDL pass)
- [`RelationalQueryPageKeysetPlanner.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalQueryPageKeysetPlanner.cs) — the change-version range predicate (`ChangeVersionFilterConstants`, `AppendChangeVersionPredicates`)
- [`DescriptorQueryPageKeysetPlanner.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorQueryPageKeysetPlanner.cs) — the descriptor equivalent. Descriptor pages root on `dms.Descriptor` itself, so the range predicate reads the descriptor row's own mirrored `ContentVersion` and the page is single-table

#### Document-metadata mirror columns

Alongside the two change-version mirrors, every resource **root** table carries five more
document-metadata columns — `DocumentUuid`, `IdentityVersion`, `IdentityLastModifiedAt`,
`CreatedAt`, and `CreatedByOwnershipTokenId` — plus a `UX_<Table>_DocumentUuid` unique constraint. The
shared `dms.Descriptor` table carries the same set — plus one mirror no root table needs,
`ResourceKeyId`, the project-qualified descriptor type that descriptor reads filter by — and each
`<AbstractResource>Identity` table carries `DocumentUuid`. They are **not** client content: nothing in
a write plan can set them (`IsWritable=false`). The stamping triggers write the root and descriptor
copies; the `TR_<Root>_AbstractIdentity` triggers write the abstract-identity copy.

Where a mirror has **no column default**, that is what keeps an out-of-band insert from fabricating a
value the triggers never produced. `<AbstractResource>Identity.DocumentUuid` is `NOT NULL` with no
default, so an insert that bypasses the triggers fails loudly instead of quietly acquiring a random
UUID that belongs to no document — and link injection reads that column. `dms.Descriptor.ResourceKeyId`
likewise has no default, so such a row cannot invent a descriptor type; it is nullable and carries no
FK to `dms.ResourceKey`, following the other mirror columns' precedent. That is its **final** shape:
these columns are the document's only stored metadata, so there is no later phase left to make them
authoritative.

- [`DeriveDocumentMetadataColumnsPass.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveDocumentMetadataColumnsPass.cs) — derives the five metadata columns onto root tables, including their non-writable classification
- [`CoreDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs) — the `dms.Descriptor` copies, including `ResourceKeyId` and the `IX_Descriptor_ResourceKeyId_DocumentId` index that serves the descriptor page keyset

These columns are **authoritative**: the resource root row — or the `dms.Descriptor` row for a
descriptor — is the document, and the stamping triggers write its metadata there and nowhere else.
Both the read paths and the write path take a document's metadata from a row they were already reading,
so a wrong *served* value points at that row (see
[Locking and write-side metadata reads](#locking-and-write-side-metadata-reads) below).

- **GET metadata** (`id`, `_etag`, `_lastModifiedDate`) — the hydration batch's metadata `SELECT` reads
  the resource **root** table, and that is the only table it can read: the keyset batch and the
  single-document fast path both pass the plan's own `Model.Root.Table` as the metadata table, and the
  builder takes no other source
  ([`HydrationBatchBuilder.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs),
  `AppendDocumentMetadataSelect` / `AppendSingleDocumentMetadataSelect`).
- **GET-by-id target resolution** — probes the root table's `UX_<Root>_DocumentUuid` unique index.
  Resource scoping is now structural (a uuid belonging to another resource is simply absent from this
  root table), so a miss is a plain **404**
  ([`RelationalDocumentUuidLookup.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentUuidLookup.cs),
  [`RelationalReadTargetLookupService.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadTargetLookupService.cs)).
- **The `?id=` query filter** — compiles to a predicate on the root's own `DocumentUuid`, so the page
  SQL stays on the tables it was already reading
  ([`PageDocumentIdSqlCompiler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageDocumentIdSqlCompiler.cs)).
- **Descriptor reads** — GET-by-id, the page keyset, and the page rows are single-table on
  `dms.Descriptor`, discriminated by its mirrored `ResourceKeyId`
  ([`DescriptorReadHandler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorReadHandler.cs),
  [`DescriptorReadRowReader.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorReadRowReader.cs)).
- **Reference link injection** (`link.rel` / `link.href`) — the auxiliary lookup joins each target's
  root table, or its `<AbstractResource>Identity` table for a polymorphic target, for the target's
  `DocumentUuid`; the target's `'Project:Resource'` discriminator is a compile-time literal for a
  concrete target, or the `<AbstractResource>Identity` row's stored `Discriminator` (the concrete
  subclass) for a polymorphic one, and the resource slug the href needs is resolved from that
  discriminator
  ([`DocumentReferenceLookupPlanCompiler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReferenceLookupPlanCompiler.cs),
  [`DocumentLinkSlugResolver.cs`](../src/dms/core/EdFi.DataManagementService.Core/DocumentLinkSlugResolver.cs)).
- **The relationship-authorization boundary check** — the stored-target CTE selects the root row's
  mirrored `ContentVersion` alongside the securable columns it authorizes, so the check is single-table
  on the root. The same compiler serves GET-by-id, DELETE, and the POST/PUT stored boundary; only
  GET-by-id consumes the version, the write callers discard it
  ([`SingleRecordRelationshipAuthorizationSqlCompiler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs)).
- **Tracked-change trigger bodies** — take old/new values from the root row image (`OLD` / `NEW` in
  PostgreSQL, the `deleted` / `inserted` pseudo-tables in SQL Server) and the `ContentVersion` the
  stamping step just captured, so a tracked-change row is written entirely from the row image plus that
  stamp. A body whose tracked identity columns are descriptor-typed still joins `dms.Descriptor` for its
  `Old<Ref>_Namespace` / `Old<Ref>_CodeValue` values — that join is unrelated to the metadata mirrors
  ([`TrackedChangeTriggerBodyEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/TrackedChangeTriggerBodyEmitter.cs)).

There is no second copy of any of this metadata anywhere in the schema. On a relationship-authorized
GET-by-id, the authorization boundary's version and the representation it authorizes now **both** come
from the root row — but they are read by *separate statements*, up to four per attempt: the target lookup,
the authorization boundary's stored-target CTE, the hydration metadata `SELECT`, and the post-hydration
re-resolve that re-reads the target ([`RelationalDocumentStoreRepository.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs),
`GetDocumentByIdAsync` / `ShouldRetryPostHydrationReadBoundaryAsync`). The comparison across them keeps
doing its original job: a concurrent mutation between any two of those reads makes them disagree and
the read re-resolves its target instead of serving a torn view. What the comparison no longer does is
cross-check the row against a second stored copy — there is none left to check against. A root mirror
tampered with out of band therefore agrees with itself on every read, on **both** dialects, so the
tampered `ContentVersion` is **served** rather than failing the request closed. That tripwire only ever
covered relationship-authorized GETs; every other read already trusted the row it read.

Caveats when debugging a mismatch — all of them concern rows written **out of band**, since the
generated triggers are the only legitimate writers of these columns:

- `CreatedByOwnershipTokenId` is a forward-compatible placeholder and is **permanently NULL** — this
  schema base has no ownership token to record, so no trigger writes it. It is deliberately left
  unindexed until the phase that populates it.
- **Neither dialect self-heals out-of-band tampering** of these columns, because there is no second
  copy of the row to repair from. `DocumentUuid` and `CreatedAt` are settled on the **insert** path and
  never revisited: the write path binds `DocumentUuid` explicitly on the root `INSERT` (it is a required
  binding even though the column is not client-writable — `WritePlanCompiler.DeriveRequiredBindingColumns`
  / `WriteValueSource.DocumentUuid`), with the table's own default (`gen_random_uuid()` / `newid()`) as a
  backstop for a row inserted out of band, and the stamping trigger writes `CreatedAt` on insert on both
  dialects. No later stamp re-asserts either one; the stamp `UPDATE` writes only the content (and, on an
  identity change, the identity) pair.
- Reads **fail closed rather than loudly**: the mirror predicates are equality comparisons and NULL
  matches nothing, so a trigger-bypassed `dms.Descriptor` row whose `ResourceKeyId` is NULL is invisible
  to descriptor reads — a **404**, not an error. `DescriptorReadRowReader`'s "must not be null"
  invariant throw sits behind that filter as defense in depth and is unreachable through the normal
  read path.
- Link injection compares mirrors **across** branches: a polymorphic reference can resolve through the
  concrete root table and through its `<AbstractResource>Identity` row, and if those two `DocumentUuid`
  values ever disagreed the GET fails with a conflicting-rows exception rather than serving a wrong
  link ([`PageReconstitutionContext.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs)).
  A divergence there means the database was written to behind the triggers' back.
- A **tombstone's `ChangeVersion` is allocated from the change-version sequence**, never read back from
  another row: PostgreSQL's stamping trigger fills its `_stampedContentVersion` local with a `nextval`
  on the `DELETE` branch and SQL Server inlines `NEXT VALUE FOR` into the tombstone `INSERT … SELECT`,
  once per deleted row. Nothing survives a delete to carry a post-delete stamp, so sourcing one from a
  second row would have made the *order* of the statements inside a delete load-bearing. It does not:
  a delete is a **single** statement against the root (or `dms.Descriptor`) row, and the tombstone's
  version is drawn inside the trigger that fires for it — see
  [Deleting a document](#deleting-a-document) below
  ([`TrackedChangeTriggerBodyEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/TrackedChangeTriggerBodyEmitter.cs),
  `EmitPgsqlTombstoneInsert` / `EmitMssqlTombstoneInsert`).

> [!IMPORTANT]
> **Re-provision only.** Re-applying the generated DDL over a database provisioned before these
> columns existed does **not** migrate it. It applies partially: the `CREATE TABLE` statements are
> skipped (`IF NOT EXISTS`), so the new columns never appear on the existing tables, while the
> triggers *are* replaced (`CREATE OR REPLACE` / `CREATE OR ALTER`) and the guarded `ALTER TABLE ...
> ADD CONSTRAINT` statements *are* attempted against columns that do not exist —
> `UX_Descriptor_DocumentUuid` against `dms.Descriptor.DocumentUuid`, and
> `UX_Descriptor_UriLowered_Discriminator` against the engine-computed `dms.Descriptor.UriLowered` — so
> the run fails loudly partway through, leaving the database half-updated. Provision a fresh database
> instead, and remember the first-use fingerprint check in
> [§3](#3-schema-fingerprint-validation--how-dms-validates-schema-on-first-use) turns any leftover
> drift into an **HTTP 503**.

#### Resolving references and detecting upserts by natural key

A write no longer identifies anything by hash. Both of the write path's lookups — *"what document does
this reference point at?"* and *"does a document with this identity already exist?"* — are now **index
seeks on stored identity values**, so a debugging session starts from the index, not from a UUIDv5
computation.

| Question | Index sought | Table |
|---|---|---|
| Reference → target document | `UX_<Target>_RefKey` | the target's root table, or its `<Abstract>Identity` table for a polymorphic target (never the abstract union view — it carries no index) |
| Descriptor reference → descriptor document | `UX_Descriptor_UriLowered_Discriminator` | `dms.Descriptor` |
| POST: does this identity already exist? | `UX_<R>_NK` | the resource's own root table |
| Descriptor POST: same question | `UX_Descriptor_UriLowered_Discriminator` | `dms.Descriptor` |
| PUT: which row is this uuid? | `UX_<Root>_DocumentUuid` / `UX_Descriptor_DocumentUuid` | root table / `dms.Descriptor` |

- The column lists are **compiled**, not discovered at runtime. The probe compiler and the constraint
  passes are *parallel* derivations from the same `identityJsonPaths` ordering — not one calling the
  other — so a probe binds the constraint's columns, in the constraint's order, resolved to canonical
  storage columns (binding a unified-alias column would be semantically correct but could not seek the
  index). That the two agree is enforced by **parity tests**, not by construction, so if a probe ever
  stops seeking, suspect a divergence between the two derivations first
  ([`NaturalKeyProbeCompiler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/NaturalKeyProbeCompiler.cs),
  [`NaturalKeyProbeContracts.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.External/NaturalKeyProbeContracts.cs),
  and the `Given_NaturalKeyProbes_Over_Authoritative_MappingSets` RefKey-parity fixture).
- **Reference resolution** batches every reference in a request into one command per round trip: one
  statement, and therefore one result set, per target resource, in group order. Each matched row is
  attributed back to its request reference by its one-based `Ordinal` *within its group* — never by row
  position, because rows arrive in unspecified order. Every statement is an `INNER JOIN`, so an entry
  with no row is a miss ([`NaturalKeyReferenceResolver.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/NaturalKeyReferenceResolver.cs)).
- A probe column that is itself a **descriptor foreign key** gets an inner join to `dms.Descriptor`
  ahead of the target join, so the target's `ON` clause still carries every `RefKey` column and can
  still seek. A URI that resolves to nothing makes the whole reference a miss, which is correct.
- The **descriptor** statement deliberately carries *no* discriminator predicate: seeking `UriLowered`
  alone is a prefix seek of `UX_Descriptor_UriLowered_Discriminator` and still returns the row for a URI
  that names a descriptor of the wrong type. That is what lets the caller report `DescriptorTypeMismatch`
  instead of a bare `Missing` — the projection returns the matched row's `Discriminator` and
  `ResourceKeyId` for exactly that comparison.
- **Upsert detection** runs *after* reference resolution inside the same write session, because a
  reference-sourced natural-key part binds the already-resolved reference's `DocumentId`. If any part
  of the key has no resolvable stored value for this request, no persisted row can carry the request's
  natural key, so the probe short-circuits to "create new" **without issuing SQL**
  ([`RelationalWriteTargetLookupResolver.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteTargetLookupResolver.cs),
  `TryResolveByNaturalKeyAsync`). It returns the `(DocumentId, DocumentUuid, ContentVersion)` triple
  straight from the root row's mirrors — the same row the session then locks.
- The descriptor upsert probe adds `ResourceKeyId` as a **residual predicate**, because the stored
  `Discriminator` is a *bare* resource name with no project qualifier and so does not by itself scope
  the seek to the routed resource.

The two dialects differ only in how the batch's values reach the server, and the difference is
performance-critical:

- **PostgreSQL** passes **parallel arrays** — one array parameter per probe column per group — expanded
  with `unnest(…) WITH ORDINALITY`. The ordinal is the array position, cast to `integer` so a
  dialect-neutral reader reads the same type on both providers
  ([`PostgresqlNaturalKeyLookupCommandBuilder.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/PostgresqlNaturalKeyLookupCommandBuilder.cs)).
- **SQL Server** passes **one `nvarchar(max)` JSON payload per group**, shredded by `OPENJSON … WITH`
  into a typed relation; the ordinal rides inside the JSON as `$.o` rather than being fabricated by
  `ROW_NUMBER()`. The JSON is written with `Utf8JsonWriter`, never string concatenation, so identity
  values are escaped by construction and can never alter the SQL text
  ([`MssqlNaturalKeyLookupCommandBuilder.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Mssql/MssqlNaturalKeyLookupCommandBuilder.cs)).

Neither dialect's parameter count grows with the number of references, so the emitted text is identical
for one reference and for five thousand and caches per shape. The set-valued input on SQL Server is not
a stylistic choice: an earlier per-value `VALUES` binding measured 2.65×–3.76× *slower* than the hash
resolver it replaces, because `SqlClient` costs roughly 17 µs per bound parameter and the cost grows
faster than linearly.

> [!WARNING]
> **Every SQL Server natural-key statement ends with `OPTION (FORCE ORDER)`, and the `OPENJSON` input
> must stay leftmost in the join order.** `OPENJSON` is a table-valued function: it carries no
> statistics and its cardinality is always guessed at 50 rows, so without the hint the optimizer places
> it on the *inner* side of a nested loop against `dms.Descriptor` and re-parses the whole payload once
> per descriptor row. Measured on SQL Server 2022 against a 257-row descriptor table: 32 entries cost
> 5.6 ms unhinted versus 0.25 ms hinted; 256 entries cost 44 ms versus 0.5 ms. The hint pins only the
> join *order*, which these statements already write in the only sensible sequence (shred the small key
> set → resolve descriptor-valued parts → seek the target's `RefKey` index). **Any future edit to these
> statements must keep the `OPENJSON` relation first and must keep the hint** — see
> `MssqlNaturalKeyLookupCommandBuilder.AppendJoinOrderHint`'s remarks for the measurement.

#### Locking and write-side metadata reads

**The locked row and the read row are the same row.** `RelationalDocumentLockCommandBuilder` takes the
table to lock as a parameter, and every production caller passes the row the rest of the write will
read: the resource **root** table for a non-descriptor write, `dms.Descriptor` for a descriptor write —
and those are the only two tables it is ever handed. The statement selects only `ContentVersion`, under
`FOR UPDATE` on PostgreSQL and `WITH (UPDLOCK, HOLDLOCK, ROWLOCK)` on SQL Server
([`RelationalDocumentLockCommandBuilder.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentLockCommandBuilder.cs)).

The same statement is reused for the guarded-no-op freshness re-check and for the post-persist stamp
read-back, and the write path's current-state load hydrates from that same root row — so a
stale-versus-fresh comparison never straddles two tables
([`RelationalWriteCurrentState.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteCurrentState.cs),
[`RelationalWriteNoProfilePersister.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteNoProfilePersister.cs)).

> [!NOTE]
> **Resolved lock-ordering hazard (historical).** While the write path still wrote `dms.Document`,
> locking the root row inverted this writer's lock order relative to the stamping/propagation cascades —
> the writer took `Root(D)` first and reached `dms.Document(D)` only later through the stamping trigger,
> while a cascade from another transaction took `dms.Document(D)` before `Root(D)`. That narrow
> contention pattern could deadlock, and was absorbed rather than prevented: deadlock and serialization
> failures classify as transient and Core's Polly pipeline replays the **whole write transaction**. The
> write path stopped writing `dms.Document` — first for the id, then for the delete signal — and the
> table itself then left the generated DDL, so the root row is the only row either side takes and the
> cycle cannot re-form. The full analysis lives in `RelationalDocumentLockCommandBuilder`'s remarks; the descriptor path never had such a cycle
> (`DescriptorWriteHandler._descriptorLockTable`'s remarks).

#### Case sensitivity and fail-closed postures

Moving from a hash to an index seek moves identity comparison from .NET into the database, which makes
**collation** part of the contract. When a lookup behaves differently on the two engines, this is
almost always why.

- **Descriptor URIs** are matched through `dms.Descriptor.UriLowered`, an engine-computed, persisted
  lower-cased projection of `Uri` (`lower("Uri")` / `LOWER([Uri])`). Nothing writes it, so it stays out
  of every `INSERT` column list and out of the stamping trigger. The probe binds that persisted column
  rather than wrapping the predicate in `lower(…)`, which would be non-sargable on PostgreSQL
  ([`CoreDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs),
  `RenderDescriptorUriLoweredColumn`). On PostgreSQL the generated column is declared `varchar(612)` —
  double the stored `Uri` width — because ICU full case mapping can make `lower()` output *longer* than
  its input, and a generated column that overflows its declared width errors the write instead of
  truncating. SQL Server infers the computed column's type from `[Uri]` and rejects an explicit one, so
  that arm has no width to widen; the stored `Uri` stays 306 (namespace 255 + `#` + code value 50) on
  both dialects, as does the SQL Server probe parameter sizing.

  The bound *value* is lower-cased **at the probe boundary**, by whichever supplier builds the value
  being bound. That is the standing invariant, not a redundancy to collapse: every path that binds
  `UriLowered` lower-cases on its way in, so no one site is load-bearing for all of them and none may be
  deleted on the theory that another already did the work. The suppliers, and what each one covers:

  - `DescriptorExtractor.CreateDescriptorReference` — a descriptor **reference** in a request body,
    lower-cased in Core before the backend sees it.
  - `IdentityValueCanonicalizer.Canonicalize` — any descriptor-typed part of a document's **own**
    identity (selected by `DocumentIdentity.IsDescriptorIdentityPath`).
  - `DescriptorDocument.ToDocumentIdentity` — a **descriptor resource's** own `namespace#codeValue`
    identity value.
  - `DescriptorWriteHandler.DescriptorProbeUri` — the URI a descriptor **write** probes with.
  - `NaturalKeyReferenceResolver` — twice: a descriptor-valued column of a document reference's
    natural key, and a descriptor reference's own URI.
  - `RelationalQueryRequestPreprocessor.CreateDescriptorReference` — a descriptor **query filter**
    value.
  - `FlatteningResolvedReferenceLookupSet` — both ends of the in-memory descriptor-URI → `DocumentId`
    map flattening reads, so a raw stored-document value and an already-lowered resolver value land on
    the same key.

  Knowing which supplier applies to the request you are debugging is what locates an unexpectedly
  case-sensitive miss.
- `UX_Descriptor_UriLowered_Discriminator` is the **sole uniqueness rule over the descriptor URI**, and on
  PostgreSQL it is a genuinely new one: case-variant spellings of one descriptor URI can no longer coexist.
  That is not a new *semantic* — the UUIDv5 it replaces was computed over the lower-cased URI, so those
  spellings always collapsed to a single document anyway. An original-case `UX_Descriptor_Uri_Discriminator`
  constraint and a plain `IX_Descriptor_Uri_Discriminator` index over the identical column pair used to be
  emitted beside it; both were strictly implied by it — on PostgreSQL because case-insensitive uniqueness
  subsumes the case-sensitive one, on SQL Server because the default case-insensitive collation made the
  original-case constraint its exact twin — so dropping them moved no enforced boundary and saved two index
  maintenances on every descriptor write. The surviving constraint is emitted on both dialects so the
  compiled probe binds one column name everywhere.
- **On SQL Server, string identity comparison is case-insensitive, on both lookups.** The generated DDL
  pins no collation on identity columns, so they inherit the database default — case-insensitive on a
  stock SQL Server install, whereas PostgreSQL's default (deterministic) collations compare text
  byte-for-byte.
  - For **reference resolution**, a case-differing string identity value resolves a reference that
    PostgreSQL treats as a miss — on SQL Server the write succeeds and binds the referenced document, on
    PostgreSQL it is refused as an unresolved reference. Pinned by
    `Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Reference_Identity` and its PostgreSQL twin
    `Given_A_Postgresql_Relational_Post_With_A_Case_Variant_String_Reference_Identity`, each of which also
    pins the exact-casing control so the case of the identity value is the only variable.

    When it does resolve, the **referencing** row keeps the *request's* casing in its scalar identity
    binding columns (for example `Sponsor_SponsorName` on the extension table) while binding the
    *target's* `DocumentId`. The composite `FK_<Table>_<Ref>_RefKey` — which spans both the identity
    scalars and `DocumentId` — still passes, because the case-insensitive collation compares the stored
    variant equal to the target's. So a SQL Server database can hold a reference row whose identity
    text does not byte-match the row it points at; that is expected, not corruption
    ([`MssqlCaseVariantNaturalKeyReferenceTests.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCaseVariantNaturalKeyReferenceTests.cs)).
  - For **upsert detection**, a POST whose string natural key differs from a stored row only by case
    now seeks `UX_<R>_NK`, matches, and resolves to an **existing** document. The write then merges the
    request's casing over the stored row, and the immutable-identity guard
    ([`RelationalWriteIdentityStability.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteIdentityStability.cs)) —
    which compares merged against current **ordinally** — refuses it. The net effect on SQL Server is
    **409 → 400**: the old flow resolved to no target, inserted, and lost to `UX_<R>_NK` as an identity
    conflict; the new flow refuses up front as an immutable-identity violation. Both refuse the write,
    neither mutates stored state, and the new outcome is what PUT already returned for a case-variant
    identity edit — so POST and PUT are now consistent on this dialect. PostgreSQL still creates a
    second document. Pinned by `Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Natural_Key`
    and its PostgreSQL twin.

    > This is the **only** way the POST arm of the immutable-identity guard is reachable. Natural-key
    > selection was ruled to make it structurally unreachable, and on PostgreSQL that holds — but SQL
    > Server's case-insensitive match reaches it, so a later cleanup must not treat that arm as dead
    > code and delete it.

  Each side follows from its own index: reference resolution from `UX_<T>_RefKey` on the *target*, and
  upsert detection from `UX_<R>_NK` on the resource's *own* root table. Under a case-insensitive
  collation both already treat case variants as one identity, so the row that would have made the old
  409 *mean* something could never have coexisted in the first place.
- **Stored descriptor casing is immutable through POST.** A POST-as-update writes every descriptive
  field from the request but takes `Namespace`, `CodeValue` and `Uri` from the **persisted** row, so a
  resource always echoes the first-created canonical form. A POST that differs from the stored row
  *only* in identity casing is therefore a full no-op: no `UPDATE`, no `ContentVersion` bump, no change
  event. PUT is unaffected — its `Ordinal` `Uri` comparison rejects a case-only identity edit outright
  ([`DescriptorWriteHandler.PreserveStoredDescriptorIdentity`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs),
  [`DescriptorNoOpComparer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorNoOpComparer.cs)).

  > This is a deliberate **behavior change** from the hash-resolver era, not a preservation. A
  > descriptor's own `ReferentialId` was a UUIDv5 over the **lower-cased** URI
  > ([`DescriptorDocument.ToDocumentIdentity`](../src/dms/core/EdFi.DataManagementService.Core/Model/DescriptorDocument.cs)),
  > so a case-variant POST always **matched** the stored row — on both engines — and the old
  > upsert-update then rewrote `Namespace`/`CodeValue`/`Uri` to the request's casing (request-wins,
  > with a `ContentVersion` bump). The ODS/API on SQL Server behaves the same way (rewrite); ODS on
  > PostgreSQL instead creates a case-variant duplicate row. Ruling (2026-08-02): the immutable
  > behavior is accepted — a client that "fixes" descriptor casing via re-POST now gets a no-op
  > instead of a silent rewrite. Reverting to request-wins, if ever wanted for ODS/legacy parity, is
  > confined to the two files linked above.
- **A NULL mirror is a miss, not an error.** Every probe predicate is an equality comparison and NULL
  matches nothing. A `dms.Descriptor` row written with triggers suppressed carries `NULL` in
  `ResourceKeyId`, so it is invisible to descriptor reads (a **404**, not an error) *and* invisible to
  the descriptor upsert probe — in which case the POST attempts an insert and the URI unique constraint
  rejects it as a write conflict. Both directions fail closed by construction.

#### Behavior deltas worth knowing when debugging

Two reference-failure classifications changed with the cutover. Neither is a bug; both are worth
recognizing before hunting one.

- **`DescriptorTypeMismatch` is now reachable where the hash resolver reported `Missing`.** A URI that
  names a real descriptor of the *wrong* type used to hash to nothing; the probe finds the row and
  compares its `ResourceKeyId`. On **writes** this is the same `400` family with a more accurate detail
  message. On **reads** it is invisible: a descriptor-filtered query returns the same empty page either
  way — the reason only selects an internal diagnostic string that nothing serves
  ([`RelationalQueryRequestPreprocessor.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalQueryRequestPreprocessor.cs),
  `BuildDescriptorLookupFailureMessage`).
- **`IncompatibleTargetType` is unreachable for a concrete document reference.** The probe seeks the
  target's own table, so a hit is a member of that resource by construction; there is no longer a way
  for a resolved id to belong to some other resource.

#### Deleting a document

A delete is **one statement**. It targets the resource root row — or the `dms.Descriptor` row for a
descriptor — and returns the deleted `DocumentId`, which *is* the affected-rows signal: a returned row
means the target existed, no row means it did not. PostgreSQL uses `RETURNING "DocumentId"`; SQL Server
routes the id into a `@deletedDocumentId` table variable via `OUTPUT DELETED` and exposes it with a
trailing `SELECT`, because a bare `OUTPUT` clause is illegal on a trigger-bearing table and every root
table — and `dms.Descriptor` — carries the stamping trigger
([`OrderedDeleteCommandBuilder.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/OrderedDeleteCommandBuilder.cs)).
The descriptor arm seeks `UX_Descriptor_DocumentUuid` and adds the row's `ResourceKeyId` mirror as a
residual scoping predicate. Child and collection rows still go away through the root row's own
foreign-key cascades, so there is no statement ordering left inside a delete to get wrong.

Two things ride on that single statement that used to ride on a cascade out of `dms.Document`:

- **The `<Abstract>Identity` row is retired by a trigger arm, not by a foreign key.** Dropping
  `FK_<Abstract>Identity_Document` removed the `ON DELETE CASCADE` that used to retire the identity row
  along with its document, so retiring it moved onto `TR_<Root>_AbstractIdentity`, which gained a
  `DELETE` arm on both dialects. PostgreSQL runs
  `DELETE FROM <Abstract>Identity WHERE "DocumentId" = OLD."DocumentId"` in a `TG_OP = 'DELETE'`
  branch and returns `OLD`; SQL Server leads the body with
  `IF NOT EXISTS (SELECT 1 FROM inserted)` and deletes every identity row named by the `deleted` image,
  with the insert/update dispatch in the `ELSE` arm
  ([`RelationalModelDdlEmitter.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs),
  `EmitPgsqlAbstractIdentityDelete` / `EmitMssqlAbstractIdentityBody`). If that arm ever stops firing
  the symptom is not a visible orphan — it is a *deleted* document that still resolves through its
  abstract identity.
- **That arm is also what makes a still-referenced document's delete a 409.** An abstract reference's
  foreign key targets the `<Abstract>Identity` row, not the concrete root row, so deleting the root row
  alone would not violate it. Deleting the identity row does. The violated constraint
  (`FK_<Referrer>_<Abstract>_RefKey`) is owned by a **table of the referrer's own resource** — its root
  table, or whichever child table holds the reference — and that is exactly what
  `RelationalDeleteConstraintResolver` enumerates when it builds its constraint-name → resource index
  (every concrete resource's `TablesInDependencyOrder`), so the driver error maps back to the
  referencing resource name and is served as a reference conflict — the same mechanism a concrete
  reference already used
  ([`RelationalDeleteConstraintResolver.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalDeleteConstraintResolver.cs)).
  Pinned end to end by `It_refuses_to_delete_a_document_referenced_through_an_abstract_reference` in the
  abstract-`EducationOrganization` link-injection integration suites, one per dialect.

#### `dms.Document`, `dms.DocumentCache` and `dms.ReferentialIdentity` are gone

The three tables are **dropped from the generated DDL**. A provisioned database now carries exactly
four `dms.*` tables — `Descriptor`, `EffectiveSchema`, `ResourceKey`, `SchemaComponent` —
plus the sequences `ChangeVersionSequence`, `CollectionItemIdSequence` and `DocumentIdSequence`, the
`GetMaxChangeVersion` function (and `throw_error` on PostgreSQL), and the SQL Server table type
`BigIntTable`. One consequence worth knowing before you debug a "missing" descriptor: `dms.ResourceKey`
is now referenced by **zero** foreign keys — `FK_Document_ResourceKey` was the only one — so
`dms.Descriptor.ResourceKeyId` is an unenforced discriminator (nullable, no default, no FK). A row
carrying a wrong or `NULL` value falls out of the scope of every descriptor probe and reads as absent
(404) rather than erroring, and a `DELETE FROM dms.ResourceKey` now succeeds and orphans descriptor
rows instead of being refused. What went, and what took over each job:

- **`dms.Document`.** It stopped being read first; then the write path stopped inserting it, in two
  steps — `DocumentId` origination moved to the sequence, then the delete signal moved to the root
  row; then it left the emitter together with `FK_Document_ResourceKey` and
  `IX_Document_ResourceKeyId_DocumentId`. Both of its remaining jobs moved onto the row that replaced it:
  - **Originating `DocumentId`** — every resource root table and `dms.Descriptor` carries a
    `dms.DocumentIdSequence` column default. The root `INSERT` omits the column and returns the drawn
    value (PostgreSQL `RETURNING "DocumentId"`; SQL Server `OUTPUT INSERTED.[DocumentId] INTO
    @newDocumentId` plus a trailing `SELECT`, for the same trigger-bearing-table reason as the delete)
    ([`WritePlanCompiler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/WritePlanCompiler.cs),
    [`RelationalWriteNoProfilePersister.InsertRootRowAsync`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteNoProfilePersister.cs));
    the descriptor insert does the same on `dms.Descriptor`
    ([`DescriptorWriteHandler.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs)).
    Note what changed in kind: `PK_Document` made `DocumentId` unique across *all* resources
    structurally, whereas each root table now declares only its own `PK_<Root>` and cross-resource
    uniqueness rests on the convention that every default draws from the one shared sequence. Nothing
    enforces it, and the `TR_<Root>_AbstractIdentity` insert is `ON CONFLICT ("DocumentId") DO UPDATE`
    — so two root rows sharing a `DocumentId` would silently rebind an abstract identity row to the
    other document rather than raising.
  - **Carrying the delete signal** — now the first and only `DELETE`'s `RETURNING` / `OUTPUT`, per
    [Deleting a document](#deleting-a-document) above.
- **`dms.ReferentialIdentity`.** The generated `TR_<Root>_ReferentialIdentity` triggers that maintained
  it stopped being derived and emitted, and the UUIDv5 hash resolver that read the table was deleted
  with them. Its table, both foreign keys and `IX_ReferentialIdentity_DocumentId` are now gone too,
  and with them **`dms.uuidv5()`** on both dialects, the **pgcrypto** extension that existed only
  for that function's `digest()`, and the **`dms.UniqueIdentifierTable`** TVP that bound referential-id
  lists. (`BigIntTable` stays — it serves the surviving authorization TVPs.)
- **`dms.DocumentCache`** was never wired to a runtime reader or writer; its table, unique constraint,
  JSON check and index went with the other two.
- **`ReferentialId` the *value* survives — in memory only.** Core still derives the deterministic UUIDv5
  from `(project, resource, ordered identity values)`
  ([`ReferentialIdFactory.cs`](../src/dms/core/EdFi.DataManagementService.Core.External/Model/ReferentialIdFactory.cs)),
  but nothing persists it, no probe binds it, and no generated SQL mentions it. Its whole remaining job
  is request-scoped equality: it detects duplicate references inside one reference array
  ([`ReferenceArrayUniquenessValidationMiddleware.cs`](../src/dms/core/EdFi.DataManagementService.Core/Middleware/ReferenceArrayUniquenessValidationMiddleware.cs))
  and it keys the `LookupsByReferentialId` map the resolver hands back
  ([`ReferenceResolverContracts.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/ReferenceResolverContracts.cs)).
  Note that this is *not* the resolver's dedupe key: inside `NaturalKeyReferenceResolver` the memo key
  is the reference's own `(target resource, ordered identity values)` tuple, not a hash of it. A
  mismatch between a `ReferentialId` and stored data is therefore no longer a database-consistency
  question — there is no stored copy to be consistent with.

The test estate followed: reads of the vanished rows are re-anchored on the owning root or
`dms.Descriptor` row, and every seed helper that used to mint ids from `dms.Document`'s `IDENTITY` now
draws from `dms.DocumentIdSequence`, so seeded and production-created rows share one id space.

Debugging consequence: a stale or missing document row can no longer explain a wrong *served* value or
a failed lookup, because the row that would be stale **is** the row being served. For the design-era
argument against removing `ReferentialId`s — the analysis this work supersedes — see
[`the-problem-with-removing-referentialids.md`](../reference/design/backend-redesign/design-docs/the-problem-with-removing-referentialids.md),
which now carries a superseded banner, as does the rest of the design corpus that describes these
tables as current.

#### Change-query reads

`/deletes` and `/keyChanges` read the tracked-change tables for real.
[`TrackedChangesEndpointModule.cs`](../src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/TrackedChangesEndpointModule.cs)
routes both onto `IApiService` like any other endpoint, and `IChangeQueryRepository`'s relational
implementation
([`RelationalChangeQueryRepository.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalChangeQueryRepository.cs))
plans, authorizes and executes the query: `ReadChangesAuthorizationPlanner` builds the authorization
plan, `TrackedChangeAuthorizationSqlEmitter` renders it as SQL, `ChangeQueryResponseFieldMapper`
resolves the response fields from the mapping set, `TrackedChangeQueryPlanner` builds the command, and
`TrackedChangeQueryRowReader` reads the rows. End-to-end coverage lives in
`Features/ChangeQueries/TrackedChange{DeletesByResource,KeyChangesByResource,Endpoints,Authorization}.feature`.

An empty response is a specific outcome, not the default one. `QueryTrackedChanges` returns `[]`
without touching the database only when the authorization plan fails closed (an unavailable strategy,
or a namespace strategy with no configured prefixes), when the planner produces an empty plan, when
the emitted command would exceed the dialect's parameter budget, or for a `/keyChanges` request
against a tracked-change table that is tombstone-only by design — the shared descriptor table and the
concrete-abstract tables, which record deletes but never key changes.

`GetNewestChangeVersion` is separate and still a single dialect-specific call:
`SELECT "dms"."GetMaxChangeVersion"() AS "NewestChangeVersion"` on PostgreSQL,
`SELECT [dms].[GetMaxChangeVersion]() AS [NewestChangeVersion]` on SQL Server
([`ChangeVersionSqlProvider.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/ChangeQueries/ChangeVersionSqlProvider.cs)).

## 5. Mapping packs (optional)

A "mapping pack" (`.mpack`) is a planned ahead-of-time-compiled artifact that would let DMS load
precompiled mapping sets instead of compiling them at runtime.

**Current behavior:** with the default settings (`Enabled=false`), mapping sets are
**compiled at runtime** from the effective schema. Mapping packs are **not available yet** —
the pack store is a no-op and pack decoding is not implemented, so there is no `pack build`
workflow to run today. The configuration surface, however, already exists and is bound and
validated. Note that mapping-set resolution runs eagerly at startup: if you set `Enabled=true`
with no pack present, the no-op pack store returns nothing and DMS **fails to start** when
`Required=true` or `AllowRuntimeCompileFallback=false` (with the defaults — `Required=false`,
`AllowRuntimeCompileFallback=true` — it falls back to runtime compilation).

The `MappingPacks` configuration section
([`appsettings.json`](../src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json),
bound to [`MappingSetProviderOptions`](../src/dms/backend/EdFi.DataManagementService.Backend.External/MappingSetProviderOptions.cs)):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Load mapping packs. When `false`, runtime compilation is used directly. |
| `Required` | `false` | Fail fast if a pack is missing/invalid (only meaningful when `Enabled=true`). |
| `RootPath` | `null` | Filesystem root for `.mpack` files (used only when `Enabled=true`). |
| `AllowRuntimeCompileFallback` | `true` | Allow runtime compilation when a pack is enabled but not found. |
| `FailureCooldownSeconds` | `0` | Seconds a faulted cache entry is retained; `0` evicts immediately. |
| `CacheMode` | `InMemory` | Cache strategy (currently only `InMemory`). |

Validation rule: `Required` cannot be `true` while `Enabled` is `false`
([`MappingSetProviderOptionsValidator`](../src/dms/backend/EdFi.DataManagementService.Backend.Plans/MappingSetProviderOptionsValidator.cs)).

For the planned format and compilation model, see
[`aot-compilation.md`](../reference/design/backend-redesign/design-docs/aot-compilation.md) and
[`mpack-format-v1.md`](../reference/design/backend-redesign/design-docs/mpack-format-v1.md).

## 6. Running the relevant tests locally

### Unit tests

The DDL generator has extensive deterministic / golden-file unit coverage (the
`EdFi.DataManagementService.Backend.Ddl.Tests.Unit` project and related relational-model
tests). Run them with the standard `dotnet test` against the project.

### Integration tests (real databases, in-process)

- **`api-schema-tools` CLI integration** —
  [`EdFi.DataManagementService.SchemaTools.Tests.Integration`](../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#integration-tests).
  PostgreSQL is **required** (tests fail if it is unreachable, by design). SQL Server tests
  also **run by default**: the test project's committed `appsettings.json` supplies an
  `MssqlAdmin` connection string pointing at `localhost`, and the skip guard only checks that
  `MssqlAdmin` is set (no connectivity probe), so they fail on connection errors if no SQL
  Server is reachable there. They report as skipped only if `MssqlAdmin` is removed from the
  committed config; point them at a different server via `appsettings.Test.json` or the
  `ConnectionStrings__MssqlAdmin` environment variable.
- **Backend integration** — `EdFi.DataManagementService.Backend.Postgresql.Tests.Integration` and
  `EdFi.DataManagementService.Backend.Mssql.Tests.Integration` provision a fresh database from the
  generated DDL, run against it, and drop it on teardown.
- **API-level integration** —
  [`EdFi.DataManagementService.Tests.Integration`](../src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md)
  exercises an in-process DMS against real databases (not the Docker stack).

### End-to-end (E2E) tests

E2E runs against the Docker stack. The full setup is documented in
[`eng/docker-compose/README.md`](../eng/docker-compose/README.md); the suite itself is described in
[`src/dms/tests/EdFi.DataManagementService.Tests.E2E/README.md`](../src/dms/tests/EdFi.DataManagementService.Tests.E2E/README.md).
A typical shard run from the repo root:

```powershell
./build-dms.ps1 E2ETest -EnvironmentFile ./.env.e2e -TestFilter "Category=@e2e-ci-shard-3"
```

The environment file lives at [`eng/docker-compose/.env.e2e`](../eng/docker-compose/.env.e2e);
`build-dms.ps1` resolves the `./.env.e2e` argument to that location automatically.

> [!NOTE]
> The setup/teardown helpers
> [`setup-local-dms.ps1`](../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1)
> and `teardown-local-dms.ps1` start and stop the local stack.

## 7. E2E setup/teardown and the "no hot reload" rule

The effective schema is **fixed at provisioning time**. There is no in-place schema migration
and **no hot reload**: changing any `ApiSchema.json` input changes the effective schema hash, and
a DMS instance running against a database provisioned for the old hash will fail the first-use
fingerprint check and return **HTTP 503** (see [§3](#3-schema-fingerprint-validation--how-dms-validates-schema-on-first-use)).

So, after **any** schema change, the developer loop is:

1. **Re-provision a fresh database** for the new effective schema (`api-schema-tools ddl provision`
   against a clean database, or the scripted helper).
2. **Restart the DMS process** so it reloads the schema and clears the cached fingerprint
   validation state.

This is exactly what the test infrastructure does: integration fixtures create a fresh database
from the generated DDL per run and drop it on teardown, and the E2E setup tears down stale state
(including removing a stale `.bootstrap` workspace) before starting. Because each run provisions
cleanly, tests never rely on updating an already-provisioned database in place.

> [!WARNING]
> If you change a schema and only restart the service (without reprovisioning), or only
> reprovision (without restarting), you will still see 503s. Both steps are required.
