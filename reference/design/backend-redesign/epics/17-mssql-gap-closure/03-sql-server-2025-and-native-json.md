---
jira: DMS-1279
jira_url: https://edfi.atlassian.net/browse/DMS-1279
---

# Story: Adopt SQL Server 2025 and Evaluate Native JSON Document Storage

## Description

Move the supported local and CI MSSQL runtime from SQL Server 2022 to SQL Server 2025, then deliberately
evaluate changing generated `DocumentJson` columns from `nvarchar(max)` to SQL Server's native `json` type.

The runtime upgrade and storage-format change are separate delivery phases. A successful runtime upgrade must
not be blocked by a decision to defer the native-JSON transition. Phase 1 is required. Phase 2 ends with a
recorded adopt/defer decision; its implementation criteria apply only when the release-status gate chooses
adoption. A defer decision keeps `nvarchar(max)` and links a follow-up without preventing Phase 1 completion.

## Dependencies

- Delivered prerequisite: `DMS-1255` supplies the SQL Server 2022 template packages and image-coupling contract
  used by the forward-restore compatibility gate. Retain the Jira relationship for provenance rather than as
  an active blocker.
- Native-JSON adoption coordinates with the `DMS-1271` restore-manifest contract, but `DMS-1271` does not block
  the required runtime upgrade or a recorded defer decision.

## Phase 1: SQL Server 2025 Runtime

- Update every authoritative image pin used by local compose, DMS CI, CMS CI, and template-build workflows.
- Isolate local SQL Server 2025 storage from volumes initialized by SQL Server 2022; preserve legacy volumes
  for explicit backup and cleanup instead of attaching them to the new runtime.
- Keep workflow comments and package documentation aligned with the actual image used to build MSSQL backup
  packages.
- Verify container readiness and in-container `sqlcmd` tooling before changing generated DDL.
- Prove SQL Server 2022-built template backups restore on the SQL Server 2025 runtime.
- Run the existing MSSQL backend, API integration, SchemaTools, CMS, and template build/verify lanes unchanged.

### Authoritative Pins, Lanes, And Tooling Inventory

Executable image pins to flip to the selected SQL Server 2025 image:

- `eng/docker-compose/mssql.yml` (shared `db` service used by local compose, the CMS MSSQL E2E lanes, and both template build workflows through `start-local-dms.ps1`).
- `.github/workflows/on-dms-pullrequest.yml` `MSSQL_IMAGE` env, consumed by `.github/actions/start-mssql-test-container` for the backend (4-shard), DMS API, and SchemaTools MSSQL integration lanes.
- `.github/workflows/on-config-pullrequest.yml` CMS integration-test `services:` container.

Comment and documentation references to keep aligned with the same image string:

- The image-coupling comments in `.github/workflows/build-minimal-template.yml` and `.github/workflows/build-populated-template.yml`.
- `eng/docker-compose/README.md`, `docs/RUNNING-LOCALLY.md`, `AGENTS.md`, `src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md`, and `src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md`.

Image selection: use the `2025-latest` moving tag, matching the existing `2022-latest` convention.
The documented backup coupling is at the SQL Server major-version level, not an exact build.

Tooling and compatibility-level notes:

- `/opt/mssql-tools18/bin/sqlcmd` is a hard in-container dependency (compose healthcheck, container readiness polls, template content gates, `eng/DatabaseTemplates` and `eng/docker-compose` scripts, and Pester fixtures asserting the exact path).
  Verify the path exists in the selected 2025 image before flipping pins; relocating the tools path is in scope if the image layout changed.
  The SchemaTools lane's host-installed `go-sqlcmd` is unaffected.
- Before this story, nothing in the repo set or checked the database compatibility level.
  The template content gates added by this story verify, but never set, level 170 only for newly built template databases.
  Templates restored from published SQL Server 2022-built packages keep their original compatibility level, including level 160, and are not altered.

Forward-restore proof: restore the published SQL Server 2022-built `EdFi.Api.*.Template.MsSql` packages (from `DMS-1255`) on the 2025 runtime with the existing `verify-template-restore.ps1` machinery, prove served data, and document the result.
That proof is a one-off documented validation of the 2022-to-2025 boundary crossing.
After the pin flip, the template lanes provide ongoing build/restore coverage only for packages created on SQL Server 2025; no lane continuously re-restores a legacy 2022-built package.

## Phase 2: Native `json` Storage

The native `json` type is a physical storage decision behind `ISqlDialect.JsonColumnType`. Shared document
semantics and PostgreSQL `jsonb` behavior do not change. Today that dialect property is used only by the
always-provisioned `dms.DocumentCache.DocumentJson` DDL; DMS has no production cache projector or cache-backed read path.
Ordinary resource CRUD, query, batching, and reconstitution therefore cannot prove native-JSON parameter or
result behavior, and this story must not claim that coverage unless it also receives explicit ownership of a
production `DocumentCache` path.

### Decision Record (2026-07-22): Defer

The release-status gate below was executed on 2026-07-22 and the decision is defer.

- The native `json` type remains in preview for boxed SQL Server 2025 (17.x).
  The live Microsoft Learn `json` data-type page, re-verified on 2026-07-22, states general availability for Azure SQL Database and Azure SQL Managed Instance only.
  The latest cumulative update at verification time (CU7, build 17.0.4065.4, KB5096981, 2026-07-16) records no change to that status.
- The provider-capability leg passes: the repo targets `net10.0` and pins `Microsoft.Data.SqlClient` 6.1.4, which includes the JSON surface (`SqlDbType.Json`, `SqlJson`) introduced in provider version 6.0.
- The project does not accept a preview storage format for the supported MSSQL tier, so `MssqlDialect.JsonColumnType` stays `nvarchar(max)` and the conditional adoption criteria transfer to [`DMS-1328`](https://edfi.atlassian.net/browse/DMS-1328).
- Gate steps 1-3 below are executed by this story; step 3 resolves to the direct `DocumentCache` provider-round-trip boundary.
  Steps 4-6 are adoption work owned by `DMS-1328`.
- The `DMS-1328` provider baseline is explicit: PostgreSQL `DocumentJson` uses `jsonb`, MSSQL
  `DocumentJson` uses `nvarchar(max)`, and only MSSQL adoption of native `json` is in scope.

### Evaluation Spike (In Scope Under Defer)

The defer decision still ships evaluation tests so their findings detail `DMS-1328`.

- Add a dedicated real-SQL-Server evaluation fixture to `EdFi.DataManagementService.Backend.Mssql.Tests.Integration` that creates a scratch `DocumentCache`-shaped table with a native `json` column directly, without changing `MssqlDialect.JsonColumnType`, generated DDL, goldens, or manifests.
- Exercise the provider surface enumerated in step 5 below against that scratch table: table creation, object-only validation via `ISJSON(DocumentJson, OBJECT) = 1`, explicit (`SqlDbType.Json`) and inferred (CLR `string`) parameter binding, insert/select round trips, `OPENJSON`, `JSON_VALUE`, supported bulk operations, schema inspection, and result materialization.
- The fixture runs in the existing MSSQL backend integration lane once Phase 1 moves it to SQL Server 2025, and is isolated so it can be excluded quickly if preview behavior shifts.
- Record findings, including provider and TDS limitations observed, in `DMS-1328`.

Before enabling native storage:

1. Verify the native `json` type's current SQL Server 2025 release status. Microsoft currently documents it as
   preview for boxed SQL Server 2025. Adopt it only after it is generally available or the project explicitly
   accepts a preview database format for the supported MSSQL tier.
2. Validate the pinned `Microsoft.Data.SqlClient` version and the .NET 10 parameter-binding path. Prefer
   `SqlDbType.Json`/the provider's supported JSON surface when explicit typing is required; retain CLR `string`
   only where provider inference is intentional and covered by integration tests.
3. Choose the executable validation boundary:
   - without a production `DocumentCache` path, validate generated DDL and direct `DocumentCache` provider
     round trips; or
   - explicitly add the cache projector/read path to an owning ticket before requiring DMS CRUD/query coverage.
4. Establish and bump the MSSQL physical-schema identity described below, change
   `MssqlDialect.JsonColumnType`, regenerate provisioned-schema goldens and relational-model manifests, and
   update the authoritative data-model DDL. Replace the current string-based
   `LEFT(LTRIM(DocumentJson), 1)` object check, because native `json` does not allow implicit string conversion;
   use a native-compatible root-object constraint such as `ISJSON(DocumentJson, OBJECT) = 1` and cover it.
5. In direct real-SQL-Server integration tests, verify table creation, object-only validation, explicit and
   inferred provider parameter binding, insert/select round trips, `OPENJSON`, `JSON_VALUE`, supported bulk
   operations, schema inspection, and result materialization against the native column.
6. Rebuild and republish MSSQL database-template packages after the generated schema changes.

### Artifact and Schema Identity

`EffectiveSchemaHash` and `RelationalMappingVersion` remain the shared logical mapping identity. Do not bump
the global relational mapping version for this provider-only optional-cache storage change: doing so would
change PostgreSQL fingerprints and generated seed DDL even though PostgreSQL mapping and storage are
unchanged.

Instead, generated MSSQL artifacts have a DMS-owned `MssqlPhysicalSchemaVersion`:

- The `nvarchar(max)` baseline is version `v1`; native `json` adoption introduces `v2`.
- MSSQL DDL records the version in a SQL Server-only singleton `dms.MssqlPhysicalSchema` metadata table. A
  legacy database without the table is recognized as `v1` only when catalog inspection also finds the legacy
  `nvarchar(max)` shape; absence can never imply the native baseline.
- MSSQL DDL artifact identity and determinism use `(ApiSchema set, mssql, RelationalMappingVersion,
  MssqlPhysicalSchemaVersion)`. Update the authoritative DDL-generation contract and diagnostic manifests to
  include that final component. PostgreSQL keeps its existing identity tuple and byte-for-byte DDL.
- The database-template restore manifest from `DMS-1271` records `MssqlPhysicalSchemaVersion` beside the
  physical `DocumentJson` type. Package production, scratch restore, SchemaTools verification, and any future
  cache startup path require the version marker and live catalog type to agree.
- This physical version does not key ordinary mapping packs while `DocumentCache` has no production read/write
  path. If such a path is introduced, its owning design must either add the version to the relevant compiled
  artifact key or prove that the existing physical-type startup gate is the only required discriminator.

### Existing Database Transition

SchemaTools provisioning is create-only and does not migrate an existing `nvarchar(max)` column. Runtime
`dms.EffectiveSchema` validation also does not inspect physical column types. Native-JSON adoption therefore
uses mandatory reprovisioning rather than an in-place migration:

- Phase 1 continues to support existing SQL Server 2022-created databases with `nvarchar(max)` unchanged.
- An environment or template may claim the native-JSON baseline only after it is recreated from newly
  generated `v2` DDL or a newly published `v2` template package.
- Schema/template verification must inspect the actual SQL Server column type and reject an `nvarchar(max)`
  `DocumentJson` when native JSON is expected. It must also reject a missing or mismatched
  `MssqlPhysicalSchemaVersion`, with drop/reprovision guidance.
- Do not enable a future cache writer, reader, or `SqlDbType.Json` binding path without a startup compatibility
  gate that verifies the physical type. `EffectiveSchemaHash` alone is insufficient for this provider-specific
  storage decision.
- Document that there is no in-place data-preserving conversion in this story. A future production migration
  requires its own design and tests.

SQL Server 2025 uses compatibility level 170 as its default/recommended release baseline. For databases newly
created on SQL Server 2025, the template content gates verify, but never set, level 170. Restored databases
preserve their existing compatibility level, including level 160 for SQL Server 2022-built templates.
Microsoft documents the native `json` type as available at all database compatibility levels, so level 170 is
not a prerequisite for the type and must not be described as the mechanism that enables native JSON.

## Backup Compatibility

- Local Compose uses a SQL Server-major-versioned volume identity. Moving to this branch creates a fresh 2025
  volume and leaves legacy 2022 volumes untouched for explicit backup and cleanup.
- Validate the forward path from SQL Server 2022-built backups to the 2025 runtime before republishing.
- Once packages are built against SQL Server 2025, document that they are not expected to restore to SQL Server 2022.
  This boundary applies from the runtime flip alone and does not depend on the deferred native-JSON storage change.
- Image, compatibility-level, generated-schema, MSSQL physical-schema version, and package metadata must
  identify the supported baseline consistently.

## Acceptance Criteria

### Required Runtime Upgrade And Decision

- All local, CI, CMS, and template workflow image pins use the selected SQL Server 2025 image.
- Local Compose mounts a SQL Server 2025-specific volume and never automatically attaches the 2025 runtime
  to a legacy SQL Server 2022 data volume.
- Existing MSSQL lanes pass on SQL Server 2025 before generated JSON storage changes.
- Published SQL Server 2022-built templates restore and serve data on the 2025 runtime.
- The Phase 2 decision is recorded as defer per the 2026-07-22 Decision Record: `nvarchar(max)` is kept on MSSQL, the preview status is recorded as the reason, and `DMS-1328` is linked as the adoption follow-up without blocking Phase 1 completion.
- The native-JSON evaluation spike runs against SQL Server 2025 using a directly created scratch table and covers the provider round-trip surface in Phase 2 step 5, without changing generated DDL, dialects, goldens, or manifests.
- Evaluation findings, including observed provider and TDS limitations, are recorded in `DMS-1328`.
- PostgreSQL behavior and generated DDL remain unchanged.
- Documentation states the backup compatibility boundary and the reason for compatibility level 170.

### Conditional Native-JSON Adoption

The 2026-07-22 decision is defer, so these criteria are out of scope for this story and transfer to `DMS-1328`.
They apply only when a future decision is adopt:

- Generated DDL, authoritative data-model DDL, goldens, manifests, and newly built template packages use native
  `json` and `MssqlPhysicalSchemaVersion=v2` consistently, including a native-compatible object-only
  constraint.
- SQL Server `v1` and `v2` outputs have distinct physical artifact identities. PostgreSQL
  `EffectiveSchemaHash`, mapping identity, generated DDL, goldens, and runtime behavior remain byte-for-byte
  unchanged by the adoption commit.
- Real-SQL-Server integration tests exercise direct `DocumentCache` DDL and provider round trips for parameter
  binding, object validation, insert/select, JSON functions, supported bulk operations, schema inspection, and
  result materialization. DMS CRUD/query coverage is required only if a production cache path is explicitly
  added to scope.
- Existing databases are not silently treated as converted: native-baseline validation rejects
  `nvarchar(max)` or missing/mismatched MSSQL physical-version metadata with reprovisioning guidance, and any
  future runtime cache path has a physical-type startup gate.
- Newly rebuilt Minimal and Populated MSSQL templates pass physical-type and served-data verification.

## Non-Goals

- Adopting the native `json` type in this story; the 2026-07-22 decision is defer and adoption is owned by `DMS-1328`.
- Changing the public JSON document contract.
- Introducing JSON-specific indexes without measured query requirements.
- Treating a preview feature as mandatory without explicit project acceptance.
- Implementing the optional `DocumentCache` projector/read path unless separately assigned to this ticket.
- Providing an in-place, data-preserving `nvarchar(max)`-to-`json` migration.
- Supporting restore of SQL Server 2025-built backups on SQL Server 2022.

## Design References

- [`../../design-docs/data-model.md`](../../design-docs/data-model.md)
- [`../../design-docs/ddl-generation.md`](../../design-docs/ddl-generation.md)
- [Microsoft: JSON data type](https://learn.microsoft.com/en-us/sql/t-sql/data-types/json-data-type?view=sql-server-ver17)
- [Microsoft: JSON data type support in SqlClient](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/json-data-sql-server?view=sql-server-ver17)
- [Microsoft: ALTER DATABASE compatibility level](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql-compatibility-level?view=sql-server-ver17)
- [Microsoft: Latest SQL Server updates and version history](https://learn.microsoft.com/en-us/troubleshoot/sql/releases/download-and-install-latest-updates)

## Review Log

### 2026-07-22

- **D1 - Phase 2 decision committed: defer, with an in-scope evaluation spike.**
  Verified live on 2026-07-22: the native `json` type is GA on Azure SQL Database and Managed Instance but still in preview for boxed SQL Server 2025 (17.x), and CU7 (build 17.0.4065.4, KB5096981, 2026-07-16) records no status change.
  The provider leg passes (`net10.0`, `Microsoft.Data.SqlClient` 6.1.4).
  Evaluation tests still run so their findings detail `DMS-1328`.
- **D2 - Evaluation spike shape**: dedicated fixture in `EdFi.DataManagementService.Backend.Mssql.Tests.Integration` against a directly created scratch native-`json` table; generated DDL, dialects, goldens, and manifests untouched; runs in the existing MSSQL backend integration lane and is isolated for quick exclusion if preview behavior shifts.
- **D3 - Image tag**: `2025-latest` moving tag, consistent with the existing `2022-latest` convention and the major-version-level backup coupling documented in the template workflows.
- **D4 - Compatibility level**: nothing sets it; newly created 2025 databases default to level 170, which the template content gates verify without altering; restored 2022-built templates keep level 160 and are not altered.
- **D5 - Forward-restore proof**: one-off documented validation restoring published 2022-built `EdFi.Api.*.Template.MsSql` packages on the 2025 image via the existing `verify-template-restore.ps1` machinery; after the pin flip the template lanes provide ongoing 2025 build/restore coverage only, with no continuous legacy-2022-package restore lane.
- **Self-resolved**: `EffectiveSchemaHash` is computed only from the logical API schema with no dialect input (`EffectiveSchemaHashProvider`), so the story's no-PostgreSQL-impact premise holds as written.
- **Self-resolved**: `dms.DocumentCache` is emitted unconditionally by `CoreDdlEmitter`; "optional" is a design label, not a runtime flag, and no production read/write path or round-trip test exists.
- **Self-resolved**: physical column types are observed only by SchemaTools provisioned-schema introspection; relational-model manifests record logical types; runtime fingerprint validation reads no column types.
- **Self-resolved**: MSSQL lane inventory enumerated in Phase 1 (three DMS lanes via `start-mssql-test-container` and `MSSQL_IMAGE`, the CMS integration services container, two CMS E2E compose lanes, two MsSql template lanes); no scheduled MSSQL smoke exists today (owned by `DMS-1289`).
- **Self-resolved**: `/opt/mssql-tools18/bin/sqlcmd` is hardcoded across healthchecks, readiness polls, template gates, scripts, and Pester fixtures; the Phase 1 tooling-verification step covers this risk explicitly.
- **Self-resolved**: `DMS-1271` already carries "any engine-defined physical-schema version" in its restore-manifest contract; `MssqlPhysicalSchemaVersion` semantics remain owned by this story's design and transfer to `DMS-1328` under defer.
