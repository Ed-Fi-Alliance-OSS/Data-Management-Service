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

Each document carries two stamps set together by the same **stamping triggers** on the document
tables: a `ContentVersion` (the change-version number, from the shared change-version sequence) and
a `ContentLastModifiedAt` timestamp (the current UTC time, refreshed on every write). Those triggers
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

On read, `_lastModifiedDate` is served from the stored `ContentLastModifiedAt` and `_etag` is
composed from `ContentVersion` plus the active representation `variantKey`; the materialized
content is not hashed to build `_etag`. When a served `_etag` or `_lastModifiedDate` looks wrong,
start here:

- [`RelationalReadMaterializer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadMaterializer.cs) — composes `_etag` and serves `_lastModifiedDate` for resources
- [`DescriptorDocumentMaterializer.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/DescriptorDocumentMaterializer.cs) — the same for descriptors

#### Change-version filtering (`minChangeVersion` / `maxChangeVersion`)

Root and descriptor tables carry **mirrored** `ContentVersion` / `ContentLastModifiedAt` columns
(`ColumnKind.MirroredContentVersion`) that the query change-version filter ranges over. This
query-time filter **is wired up and works** — unlike the `/deletes` and `/keyChanges` change-query
endpoints, which are still a placeholder shim (see the note below).

- [`DeriveContentVersionMirrorPass.cs`](../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveContentVersionMirrorPass.cs) — derives the mirrored `ContentVersion` / `ContentLastModifiedAt` columns on root resource tables (descriptor mirror columns live on the shared `dms.Descriptor` table from the core DDL pass)
- [`RelationalQueryPageKeysetPlanner.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalQueryPageKeysetPlanner.cs) — the change-version range predicate (`ChangeVersionFilterConstants`, `AppendChangeVersionPredicates`)

> [!NOTE]
> **Change-query read endpoints are not wired up yet.** The tracked-change tables and triggers
> are real and populated, but the runtime read side is a placeholder. `IChangeQueryRepository`'s
> relational implementation
> ([`RelationalChangeQueryRepository.cs`](../src/dms/backend/EdFi.DataManagementService.Backend/RelationalChangeQueryRepository.cs))
> only returns the newest change version through dialect-specific SQL. PostgreSQL calls
> `SELECT "dms"."GetMaxChangeVersion"() AS "NewestChangeVersion"` and SQL Server calls
> `SELECT [dms].[GetMaxChangeVersion]() AS [NewestChangeVersion]`. The
> `/deletes` and `/keyChanges` endpoints are a temporary empty-response shim
> ([`TrackedChangesEndpointModule.cs`](../src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/TrackedChangesEndpointModule.cs))
> that returns `[]` (with a `Total-Count: 0` header only when `totalCount=true` is requested). Do not
> expect `/deletes` or `/keyChanges` to read the tracked-change tables yet.

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
