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
- [`ddl-generation.md`](../reference/design/backend-redesign/design-docs/ddl-generation.md) — deterministic DDL and create-only provisioning semantics
- [`cdc-streaming.md`](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md) — DocumentCache configuration, projection, readiness, CDC admission, and operations
- [`new-startup-flow.md`](../reference/design/backend-redesign/design-docs/new-startup-flow.md) — how the service starts up against a provisioned database

### Compact descriptor storage (DMS-1404)

Descriptors have two independent keys:

| Key | Storage and use |
|---|---|
| `DescriptorId` | Native `int` primary key on `dms.Descriptor`; PostgreSQL `GENERATED ALWAYS AS IDENTITY` with its own backing sequence, SQL Server `IDENTITY(1,1)`. Every stored descriptor reference is `Int32` and targets this key, including collections, extensions, copied reference identities, and composite keys. |
| `DocumentId` | Unique, non-null `bigint` association to `dms.Document`; used for UUID lookup, document-level RI, locks, concurrency, paging, cache/projection work, and representation restamping. Updates preserve both keys. |

`ResourceKeyId` (`smallint`) identifies the qualified descriptor type. The descriptor
stamping trigger enforces agreement with the owning document.
`FK_Descriptor_Document` uses `RESTRICT` on PostgreSQL and `NO ACTION` on SQL Server;
`FK_Descriptor_ResourceKey` constrains
the type key to the catalog. Live descriptors have no `Discriminator`
and no physically stored `Uri`. `UX_Descriptor_ResourceKeyId_Uri` enforces uniqueness over
the unlowered whole `Namespace + '#' + CodeValue` within a type:

- PostgreSQL indexes `("ResourceKeyId", ("Namespace" || '#' || "CodeValue"))`.
- SQL Server defines non-persisted `Uri AS ([Namespace] + N'#' + [CodeValue])` and indexes
  `(ResourceKeyId, Uri)`.

On SQL Server, `Namespace` and `CodeValue` carry the DMS identity collation
(`SQL_Latin1_General_CP1_CI_AS`) independent of the database default, and the computed `Uri`
and its index inherit it. PostgreSQL uses the database-default collation. Neither expression
introduces component-wise uniqueness or a lowered URI column.
The current runtime still calculates and maintains RI: its existing descriptor join returns
both IDs in one lookup, and URI witnesses and original-case responses reconstruct the whole
string. Lowered-URI natural-key probes, RI removal, new validation/equality rules, platform
upgrades, and stored-wins descriptor writes belong to later stories. Abstract identity
discriminators and union-view discriminator outputs remain unchanged.

The regenerated [DS 5.2 artifacts](../src/dms/backend/Fixtures/authoritative/ds-5.2/expected)
cover 595 stored descriptor columns and 879 indexes containing such columns. These are schema
coverage counts, not measured storage savings or evidence of faster joins; benchmarks and
database-version upgrades are not DMS-1404 completion requirements.

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
| `pgsql.sql` / `mssql.sql` | per selected dialect | the full DDL script for that engine, without a built-in transaction wrapper |
| `effective-schema.manifest.json` | always | the schema fingerprint, components, and resource-key seed summary |
| `relational-model.{dialect}.manifest.json` | per selected dialect | derived inventory, including each descriptor resource's `resource_details[].shared_descriptor_table` with physical `DescriptorId` identity and `DocumentId` root locator/unique association; fixed core DDL adds type keys, mirrors, native allocation, and URI indexes |
| `ddl.manifest.json` | only with `--ddl-manifest` | dialect-independent summary (normalized-SQL hash + statement count per dialect) for diagnostics |

`--dialect` accepts `pgsql`, `mssql`, or `both` (default `both`). All output uses Unix
line endings so the same inputs produce byte-for-byte identical files. Scripts produced
by `ddl emit` are intentionally standalone DDL, not `BEGIN`/`COMMIT` wrapped artifacts;
the caller owns any all-or-nothing wrapper when applying emitted SQL manually.

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

`IX_Document_CreatedByOwnershipTokenId` is a filtered index and `UX_Descriptor_ResourceKeyId_Uri` indexes a computed column, so SQL Server requires the indexed-view SET option set (`ANSI_NULLS`, `ANSI_PADDING`, `ANSI_WARNINGS`, `ARITHABORT`, `CONCAT_NULL_YIELDS_NULL`, `QUOTED_IDENTIFIER` ON; `NUMERIC_ROUNDABORT` OFF) for index creation and for writes to `dms.Document` or `dms.Descriptor`. It captures `QUOTED_IDENTIFIER` and `ANSI_NULLS` into each stamp trigger the script creates.
The generated SQL Server script therefore opens with one `SET` batch that puts the session in that state, so applying it with any client, including ODBC `sqlcmd` without `-I`, provisions the index and bakes the right settings into the triggers.
The filter applies to newly provisioned databases only: both engines guard index creation by name, so a database provisioned before this change keeps its unfiltered index, with no startup-validation signal (generated DDL is not an input to the effective schema hash), until it is deliberately reprovisioned.
Sessions that write `dms.Document` directly still need `QUOTED_IDENTIFIER` ON: `Microsoft.Data.SqlClient` and go-sqlcmd default it ON, ODBC `sqlcmd` needs `-I`, and a write without it fails with `Msg 1934` while a read raises nothing and silently stops using the index.
(`ARITHABORT` needs no attention: at compatibility level 90 or above `ANSI_WARNINGS` ON implies it for this purpose, which is why `SqlClient` sessions, which open with it OFF, are fine.)
Fresh and reused ordinary `SqlClient` pools satisfy the effective options for descriptor DML
without a runtime initializer. Provisioning settings belong to that session and do not carry
over to pooled runtime connections; direct SQL clients must supply the effective options too.

### Reprovisioning and legacy data carry-forward

Existing databases require deliberate reprovisioning before using the compact-descriptor
runtime. Keep `SchemaHashConstants.RelationalMappingVersion` at `v3` for the current 8.1
release line. The constant tracks releases: bump at most once per release when mapping
changes, never lower it, and do not add another bump before 8.1 ships. The next eligible
bump is `v4` for the first qualifying mapping change after 8.1 ships.

`EffectiveSchemaHash` includes that constant and normalized ApiSchema inputs, but excludes
generated DDL and mapping-set output. Consequently, this mapping-only physical change can
leave the hash unchanged. Hash equality and successful first-use validation cannot prove
that an older database or template has compact descriptor columns. Rerunning create-only
provisioning against it does not convert its schema.

Provision a fresh target from current DDL for its exact core/extension schema set, or use a
compatible rebuilt template verified against that baseline. For PostgreSQL legacy dumps,
the supported carry-forward route is
[`Copy-NorthridgeDataForward.ps1`](../eng/northridge/Copy-NorthridgeDataForward.ps1), following
the [current conversion recipe](../eng/northridge/README.md#copy-legacy-data-into-the-current-compact-schema):

1. Stop writers. Provision fresh target and reference databases; restore the legacy dump
   into a separate source database for counts and source-shape checks.
2. Supply `-ExpectedModelManifestPath` in both Copy and Checkpoint modes. Use the reviewed
   `relational-model.pgsql.manifest.json` for that exact schema set with complete
   `resource_details`; the synthetic compact fixture is not a production baseline.
3. The tool stages source-shaped tables, allocates native independent descriptor IDs,
   saves `descriptor-key-map.<target>.tsv`, and remaps every canonical stored descriptor
   reference before insertion. Generated aliases remain read-only; legacy URI and
   discriminator columns remain in staging only.
4. Descriptor history translates source type strings through the qualified resource-key
   catalog to `ResourceKeyId`, including tombstones without live document/descriptor rows.
   Unknown or ambiguous types and unmapped non-null references stop the copy.
5. Validate preserved document IDs/UUIDs, RI rows, stamps, history, FK integrity, counts,
   fingerprints, trigger state, and source identity. Copy and Checkpoint also validate the
   independent descriptor allocator against allocated compact keys without consuming an ID.

This conversion loads legacy data into a fresh schema; an unchanged legacy dump restore
and an in-place schema upgrade do not produce the required target. Discard and reprovision
a partially loaded target after failure. The tool currently requires both document stamp
columns; DMS-1401 owns removing its document-timestamp dependency. A full Northridge reload
or dataset republication is not a DMS-1404 closure gate.

### Descriptor catalog checks and template handoff

Use the reusable [compact descriptor catalog assertions](../eng/DatabaseTemplates/Compact-Descriptor.md)
and their shared manifest inventory reader to check native allocation, the unique document
association, compact stored FKs and composite keys/indexes, URI storage/index shape, and
`ResourceKeyId` history. Expectations come from a reviewed implementation baseline, never
from detecting what an older source or restored database happens to contain.

DMS-1404's handoff to [DMS-1401](../reference/design/backend-redesign/epics/21-storage-reduction/16-drop-document-content-last-modified-at.md)
is the working RI runtime on fresh schemas on both engines, regenerated fixtures, reusable
catalog assertions, working fixture/performance loaders and legacy conversion, and passing
unequal-ID metadata, stamping, restamping, and cache regressions. `dms.Document.ContentVersion`
and `dms.Document.ContentLastModifiedAt` remain authoritative, with stamps mirrored to resource
roots and descriptors. DMS-1401 owns timestamp removal and the resulting ownership changes.

[DMS-1404](../reference/design/backend-redesign/epics/21-storage-reduction/15-compact-descriptor-id-and-storage-optimizations.md)
may close after its implementation, fixture, fresh-schema behavior/catalog, tooling, and
reprovisioning-documentation checks pass. In the planned sequence, DMS-1401 owns the shared
Minimal and Populated template builds and source/restore verification on PostgreSQL and
SQL Server for supported template Data Standards `5.2.0` and `6.1.0` (eight legs), plus affected
repository consumer pins to verified prereleases. Run the descriptor assertions on both
source and restored catalogs before API probes, alongside DMS-1401's timestamp checks;
retain `RequirePopulatedData` for Populated verification. These package gates are required
before DMS-1401 closes and are deferred from DMS-1404 closure.

Until compatible rebuilds are verified, use databases freshly provisioned from DMS-1404 DDL
and defer template-backed deployment. If templates are needed earlier, build and verify
compatible packages first. Retaining `v3` does not make old physical schemas or packages
compatible. Final v8.1 template production/publication, release-view promotion, and deployment
pins remain release-workflow responsibilities; verify the final release schema against both
stories' physical expectations when both changes are included.

### Always-provisioned DocumentCache inventory

Full relational provisioning always creates the fixed `dms` inventory needed by the
DocumentCache and CDC design:

- [`dms.DataStoreIdentity`](../reference/design/backend-redesign/design-docs/data-model.md#3-dmsdatastoreidentity)
- [`dms.DocumentCache`](../reference/design/backend-redesign/design-docs/data-model.md#5-dmsdocumentcache-always-provisioned-optional-projection)
- [`dms.DocumentProjectionWork`](../reference/design/backend-redesign/design-docs/data-model.md#5a-dmsdocumentprojectionwork-always-provisioned-durable-projection-work)
- [`dms.DocumentCacheState`](../reference/design/backend-redesign/design-docs/data-model.md#6-dmsdocumentcachestate-singleton-projection-state)

These objects are physical schema and durable state. Runtime projection, projection
administration, projection health/readiness, cache-backed reads, and complete target
eligibility validation are configured and operated explicitly; the authoritative behavior
lives in the DocumentCache/CDC design. This guide links to the design owners instead of
restating their contracts: the
[`data-model.md`](../reference/design/backend-redesign/design-docs/data-model.md) table
sections define the physical shape; the
[`Cached Document Contract`](../reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract)
and
[`Transactional Enqueue`](../reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#transactional-enqueue)
sections define cache and work semantics; DDL behavior is in
[`ddl-generation.md`](../reference/design/backend-redesign/design-docs/ddl-generation.md#provision-semantics-create-only-no-migrations);
schema/query integration is in
[`cdc-streaming.md`](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#schema-and-query-integration);
cache-backed read behavior is in
[`05-cache-backed-read-path.md`](../reference/design/backend-redesign/epics/18-document-cache/05-cache-backed-read-path.md);
runtime configuration is summarized in
[`docs/CONFIGURATION.md`](./CONFIGURATION.md#datamanagementdocumentcache);
operator workflows are in
[`reference/document-cache-documentation/operations-runbook.md`](../reference/document-cache-documentation/operations-runbook.md);
and the `CDC-INV-02` / `CDC-INV-03` traceability rows live under
[`Contract-to-Evidence Traceability`](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability).

Relational connector registration is available through the explicit managed opt-in in
the [CDC operator reference](../reference/cdc-documentation/README.md). Use its
[PostgreSQL](../reference/cdc-documentation/operations-runbook.md#postgresql-setup)
or [SQL Server](../reference/cdc-documentation/operations-runbook.md#sql-server-setup)
procedure and [SchemaTools command/configuration catalog](../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands).
Provisioning the fixed tables alone is not CDC admission; the
[enablement owner](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence)
defines the managed setup boundary. Existing deployments use
[state preservation](../reference/cdc-documentation/operations-runbook.md#deployment-state)
and [managed lifecycle](../reference/cdc-documentation/operations-runbook.md#managed-lifecycle),
governed by the [state-continuity contract](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral).

### Cache-backed read acceleration

Cache-backed reads are optional. DMS considers `dms.DocumentCache` only for external
resource and descriptor GET-by-id and GET-many response-body assembly when
`DataManagement:DocumentCache:ReadAcceleration:Enabled` is `true` and the selected
tenant/data-store pair matches an explicit `DocumentCache:Targets` entry. Other endpoints,
internal stored-document reads, mutations, change-query endpoints, discovery, OpenAPI, and
administrative commands continue on their existing paths.

The relational read path remains the correctness path. Fresh cache rows may supply only the
body for candidates that the relational flow already selected and authorized; misses,
stale rows, lifecycle fences, target ineligibility, expected cache-read availability
failures, invalid cached JSON, and direct-fill failures fall back to relational reads
without exposing whether cache was used. Optional direct fill after fallback is
best-effort, bounded by `ReadAcceleration:DirectFillTimeout`, and uses the shared
DocumentCache materializer/writer rather than the shaped API response. Snapshot and
read-replica requests skip direct fill when derivative routing is available because those
requests remain read-only.

### Create-only guardrails and reruns

Provisioning is still create-only: it does not migrate an older DocumentCache shape,
reconcile arbitrary drift, or classify every partial database state. Phase-zero checks are
bounded to the effective-schema hash, required singleton safety for
`dms.DataStoreIdentity` and `dms.DocumentCacheState`, known legacy DocumentCache artifacts
(`DocumentCache.Etag`, `UX_DocumentCache_DocumentUuid`, and
`IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt`), and PostgreSQL enqueue-owner
prerequisites. Other incompatible objects are allowed to fail through ordinary provider
DDL execution.

On a completed same-hash database, provisioning preserves `SourceIdentity`, projection
lifecycle, `CacheAheadRecoveryRequired`, cache rows, pending work, and existing enqueue
timestamps. Compatible reruns use the normal existence-check and replaceable
function/trigger patterns to finish or refresh generated definitions. If a known legacy
cache artifact is present, drop and recreate the database rather than expecting in-place
repair.

`ddl provision` executes generated statements in one transaction after any optional
database-creation pre-step; a failure rolls that transaction back. `ddl emit` writes
transaction-free SQL for review/manual use.

### Provider trigger security

PostgreSQL provisioning creates or safely reuses a locked-down `NOLOGIN`
`edfi_dms_enqueue_owner` role and gives the authenticated provisioning principal only the
direct membership needed to own and refresh the enqueue functions. That owner is not a
runtime DMS credential; production still uses the deployment-supplied data-store
credential. Separate CDC principals and grants belong to the
[provider setup contract](../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup);
see the [security checklist](../reference/cdc-documentation/operations-runbook.md#security-consumer-evidence).

SQL Server uses the existing same-owner ownership chain for the enqueue trigger and
referenced `dms` tables. The generated trigger has no `EXECUTE AS`, enqueue user, or
enqueue role.

### Scripted local provisioning

For the local Docker E2E stack, the helper
[`provision-e2e-database.ps1`](../eng/docker-compose/provision-e2e-database.ps1)
wraps the above; see [`eng/docker-compose/README.md`](../eng/docker-compose/README.md).

## 3. Schema-fingerprint validation — how DMS validates schema on first use

The relational backend records a **fingerprint** of the effective schema in the database
at provisioning time, then verifies it before serving traffic. This guarantees the
running service and the database agree on exactly one effective schema input fingerprint.
It does not verify every physical mapping feature; same-hash mapping changes require the
deliberate reprovisioning and catalog checks described above.

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

- **Preflight** (search the script for the full `-- Phase 0: Bounded Provisioning Guards` header). Before any DDL runs,
  if `dms.EffectiveSchema` already exists with a *different* hash, the script raises an error and
  aborts. You cannot accidentally re-provision an existing database for a different effective schema.
  The same phase performs only the bounded DocumentCache safety checks described above; it
  is not a full schema-drift validator.
- **Seed insert-if-missing + validate** (search for the full `-- Phase 10: Seed Data (insert-if-missing
  + validation)` header). The fingerprint row is inserted only if absent
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

Each document carries two stamps set together by the same **stamping triggers** on the document
tables: a `ContentVersion` (the change-version number, from the shared change-version sequence) and
a `ContentLastModifiedAt` timestamp (the current UTC time, refreshed on representation changes). Those triggers
also populate per-resource **tracked-change tables** that live under a per-project schema named
`tracked_changes_<projectSchema>` (for example the `tracked_changes_edfi` schema), recording the
old/new identity and securable values plus a `ChangeVersion`. Descriptors share
`tracked_changes_edfi.Descriptor`, routed by `ResourceKeyId`, with old/new namespace/code
snapshots and retained owning `DocumentId`, UUID `Id`, version and creation time. It has no
descriptor discriminator or FK requiring a live owner; history survives deletion. Resource
history snapshots dereference stored `int` values through `dms.Descriptor.DescriptorId`.
When debugging a stamp or a
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

On read, `_lastModifiedDate` is served from the stored `ContentLastModifiedAt` and `_etag` is
composed from `ContentVersion` plus the active representation `variantKey`; the materialized
content is not hashed to build `_etag`. When a served `_etag` or `_lastModifiedDate` looks wrong,
start here:

- [`RelationalReadMaterializer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadMaterializer.cs) — composes `_etag` and serves `_lastModifiedDate` for resources
- [`DescriptorDocumentMaterializer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorDocumentMaterializer.cs) — the same for descriptors

#### Change-version filtering (`minChangeVersion` / `maxChangeVersion`)

Root and descriptor tables carry **mirrored** `ContentVersion` / `ContentLastModifiedAt` columns
(`ColumnKind.MirroredContentVersion` / `ColumnKind.MirroredContentLastModifiedAt`).
The query-time change-version filter ranges over `ContentVersion`. Descriptor live windows use
`IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId`; history windows use
`IX_Descriptor_ResourceKeyId_ChangeVersion` on the shared history table.

- [`DeriveContentVersionMirrorPass.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveContentVersionMirrorPass.cs) — derives the mirrored `ContentVersion` / `ContentLastModifiedAt` columns on root resource tables (descriptor mirror columns live on the shared `dms.Descriptor` table from the core DDL pass)
- [`RelationalQueryPageKeysetPlanner.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalQueryPageKeysetPlanner.cs) — the change-version range predicate (`ChangeVersionFilterConstants`, `AppendChangeVersionPredicates`)

The relational `/deletes` and `/keyChanges` paths use
[`TrackedChangeQueryPlanner.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/ChangeQueries/TrackedChangeQueryPlanner.cs)
and retained old identity/securable values. Descriptor routes bind the compile-time qualified
`ResourceKeyId`; namespace/custom-view authorization and recreation checks retain their
existing behavior. See [change-queries.md](../reference/design/backend-redesign/design-docs/change-queries.md)
for the endpoint contract.

`newestChangeVersion` is the current value reported by the provider's change-version sequence, not
`MAX(ContentVersion)` over `dms.Document`. After identity stamp columns were removed from
`dms.Document`, document inserts and identity-changing root updates allocate one sequence value
instead of two: they formerly wrote both a content stamp and an identity stamp. Child scopes and
content-only root updates already allocated only one value. The reported watermark therefore no
longer appears one value ahead of the stored `ContentVersion` for document inserts and
identity-changing root updates. Database sequences can still have ordinary gaps, for example from
rolled-back transactions; SQL Server sequence-cache recovery after a restart can also leave
`current_value` ahead of every stored `ContentVersion`.

Compatibility note: change-version filters use inclusive bounds. A client that stores the previous
`newestChangeVersion` and later resumes by passing it as `minChangeVersion` can receive the boundary
item again. Incremental extraction clients should treat change-query windows as idempotent and
deduplicate by the appropriate resource identity and change version, or advance their lower bound
according to their own resume policy.

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
./build-dms.ps1 E2ETest -EnvironmentFile ./.env.e2e -TestFilter "(Category=@e2e-ci-shard-3)&(Category!=@MssqlOnly)"
```

The `@MssqlOnly` exclusion keeps SQL Server-only scenario variants out of a PostgreSQL run.

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
