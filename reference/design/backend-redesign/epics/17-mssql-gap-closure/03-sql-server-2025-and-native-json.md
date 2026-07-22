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
- Keep workflow comments and package documentation aligned with the actual image used to build MSSQL backup
  packages.
- Verify container readiness and in-container `sqlcmd` tooling before changing generated DDL.
- Prove SQL Server 2022-built template backups restore on the SQL Server 2025 runtime.
- Run the existing MSSQL backend, API integration, SchemaTools, CMS, and template build/verify lanes unchanged.

## Phase 2: Native `json` Storage

The native `json` type is a physical storage decision behind `ISqlDialect.JsonColumnType`. Shared document
semantics and PostgreSQL `jsonb` behavior do not change. Today that dialect property is used only by the
always-provisioned `dms.DocumentCache.DocumentJson` DDL; DMS has no production cache projector or cache-backed read path.
Ordinary resource CRUD, query, batching, and reconstitution therefore cannot prove native-JSON parameter or
result behavior, and this story must not claim that coverage unless it also receives explicit ownership of a
production `DocumentCache` path.

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

SQL Server 2025 uses compatibility level 170 as its default/recommended release baseline. Microsoft documents
the native `json` type as available at all database compatibility levels, so level 170 is not a prerequisite
for the type. Set or verify level 170 for a consistent supported-runtime baseline, and do not describe it as
the mechanism that enables native JSON.

## Backup Compatibility

- Validate the forward path from SQL Server 2022-built backups to the 2025 runtime before republishing.
- Once packages are built against SQL Server 2025/native JSON, document that they are not expected to restore
  to SQL Server 2022.
- Image, compatibility-level, generated-schema, MSSQL physical-schema version, and package metadata must
  identify the supported baseline consistently.

## Acceptance Criteria

### Required Runtime Upgrade And Decision

- All local, CI, CMS, and template workflow image pins use the selected SQL Server 2025 image.
- Existing MSSQL lanes pass on SQL Server 2025 before generated JSON storage changes.
- Published SQL Server 2022-built templates restore and serve data on the 2025 runtime.
- The release-status and provider-capability decision is recorded as adopt or defer. A defer result keeps
  `nvarchar(max)`, records the reason, and links follow-up work without blocking Phase 1 completion.
- PostgreSQL behavior and generated DDL remain unchanged.
- Documentation states the backup compatibility boundary and the reason for compatibility level 170.

### Conditional Native-JSON Adoption

These criteria apply only when the decision is adopt:

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
