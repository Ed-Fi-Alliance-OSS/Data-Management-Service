---
jira: DMS-1279
jira_url: https://edfi.atlassian.net/browse/DMS-1279
---

# Story: Adopt SQL Server 2025 and Evaluate Native JSON Document Storage

## Description

Move the supported local and CI MSSQL runtime from SQL Server 2022 to SQL Server 2025, then deliberately
evaluate changing generated `DocumentJson` columns from `nvarchar(max)` to SQL Server's native `json` type.

The runtime upgrade and storage-format change are separate delivery phases. A successful runtime upgrade must
not be blocked by a decision to defer the native-JSON transition.

## Phase 1: SQL Server 2025 Runtime

- Update every authoritative image pin used by local compose, DMS CI, CMS CI, and template-build workflows.
- Keep workflow comments and package documentation aligned with the actual image used to build MSSQL backup
  packages.
- Verify container readiness and in-container `sqlcmd` tooling before changing generated DDL.
- Prove SQL Server 2022-built template backups restore on the SQL Server 2025 runtime.
- Run the existing MSSQL backend, API integration, SchemaTools, CMS, and template build/verify lanes unchanged.

## Phase 2: Native `json` Storage

The native `json` type is a physical storage decision behind `ISqlDialect.JsonColumnType`. Shared document
semantics and PostgreSQL `jsonb` behavior do not change.

Before enabling native storage:

1. Verify the native `json` type's current SQL Server 2025 release status. Microsoft currently documents it as
   preview for boxed SQL Server 2025. Adopt it only after it is generally available or the project explicitly
   accepts a preview database format for the supported MSSQL tier.
2. Validate the pinned `Microsoft.Data.SqlClient` version and the .NET 10 parameter-binding path. Prefer
   `SqlDbType.Json`/the provider's supported JSON surface when explicit typing is required; retain CLR `string`
   only where provider inference is intentional and covered by integration tests.
3. Change `MssqlDialect.JsonColumnType`, regenerate provisioned-schema goldens and relational-model manifests,
   and audit every read/write/bulk path for `nvarchar(max)` assumptions.
4. Verify `OPENJSON`, `JSON_VALUE`, triggers, views, bulk operations, schema comparison, and reconstitution against
   native columns.
5. Rebuild and republish MSSQL database-template packages after the generated schema changes.

SQL Server 2025 uses compatibility level 170 as its default/recommended release baseline. Microsoft documents
the native `json` type as available at all database compatibility levels, so level 170 is not a prerequisite
for the type. Set or verify level 170 for a consistent supported-runtime baseline, and do not describe it as
the mechanism that enables native JSON.

## Backup Compatibility

- Validate the forward path from SQL Server 2022-built backups to the 2025 runtime before republishing.
- Once packages are built against SQL Server 2025/native JSON, document that they are not expected to restore
  to SQL Server 2022.
- Image, compatibility-level, generated-schema, and package metadata must identify the supported baseline
  consistently.

## Acceptance Criteria

- All local, CI, CMS, and template workflow image pins use the selected SQL Server 2025 image.
- Existing MSSQL lanes pass on SQL Server 2025 before generated JSON storage changes.
- Published SQL Server 2022-built templates restore and serve data on the 2025 runtime.
- Native JSON is enabled only after the release-status gate is satisfied and provider behavior is proven.
- Generated DDL, goldens, manifests, and template packages consistently use the selected document-column type.
- Provider integration tests cover create, read, update, query, bulk/batch, and reconstitution paths with native
  JSON parameters and results.
- PostgreSQL behavior and generated DDL remain unchanged.
- Documentation states the backup compatibility boundary and the reason for compatibility level 170.

## Non-Goals

- Changing the public JSON document contract.
- Introducing JSON-specific indexes without measured query requirements.
- Treating a preview feature as mandatory without explicit project acceptance.
- Supporting restore of SQL Server 2025-built backups on SQL Server 2022.

## Design References

- [`../../design-docs/data-model.md`](../../design-docs/data-model.md)
- [`../../design-docs/ddl-generation.md`](../../design-docs/ddl-generation.md)
- [Microsoft: JSON data type](https://learn.microsoft.com/en-us/sql/t-sql/data-types/json-data-type?view=sql-server-ver17)
- [Microsoft: JSON data type support in SqlClient](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/json-data-sql-server?view=sql-server-ver17)
- [Microsoft: ALTER DATABASE compatibility level](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql-compatibility-level?view=sql-server-ver17)
