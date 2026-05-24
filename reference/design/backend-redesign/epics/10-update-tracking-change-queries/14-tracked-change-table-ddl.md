---
jira: Unassigned
---

# Story: Emit `tracked_changes_*` Tables

## Description

Emit tracked-change tables from `TrackedChangeTableInfo` for PostgreSQL and SQL Server.

Each regular tracked-change table follows the `tracked_changes_<schema>.<resource>` naming convention. Descriptor tombstones and descriptor key-change metadata use the shared `tracked_changes_edfi.Descriptor` table.

The tables are append-only stores for delete tombstones and key-change rows. They back `/deletes` and `/keyChanges` and replace the old unified `dms.DocumentChangeEvent` journal for Change Query semantics.

## Acceptance Criteria

- DDL emission creates one schema per tracked-change project namespace using the `tracked_changes_<ProjectName>` convention.
- Identifier shortening follows the existing DMS naming rules and PostgreSQL length constraints.
- Each regular tracked-change table renders value columns in `ValueColumnsInTableOrder`, followed by system columns.
- The shared descriptor tracked-change table is emitted as `tracked_changes_edfi.Descriptor`.
- `Id` uses PostgreSQL `uuid` and SQL Server `uniqueidentifier`.
- `ChangeVersion` uses `bigint`.
- `CreatedAt` uses a UTC-current timestamp default appropriate for each dialect.
- Shared descriptor `Discriminator` uses PostgreSQL `varchar(128)` and SQL Server `nvarchar(128)`.
- Old/new value column nullability follows `TrackedChangeColumnInfo`.
- Primary keys are clustered or ordered by `ChangeVersion` where the dialect supports that shape.
- DDL manifests include tracked-change tables and columns.
- PostgreSQL and SQL Server fixture tests cover a regular resource, a concrete abstract resource, and the shared descriptor table.

## Dependencies

- `12-tracked-change-inventory.md`.

## Out of Scope

- Populating tracked-change tables.
- Adding optional auth-performance indexes on tracked-change tables.
- Operational truncation automation.
