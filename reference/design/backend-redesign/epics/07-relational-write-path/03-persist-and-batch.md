# Story: Persist Row Buffers with Replace Semantics (Batching, Limits, Transactions)

## Description

Persist flattened row buffers to the database in a single transaction:

- Insert/update `dms.Document` and resource root rows.
- For child/extension tables, use replace semantics:
  - delete existing rows for the parent key,
  - bulk insert current rows.
- Respect dialect parameter limits and implement batching to avoid N+1 patterns.

## Acceptance Criteria

- POST/PUT runs in a single transaction and either commits all rows or rolls back fully on failure.
- Collections use replace semantics and do not require generated ids per element.
- Bulk operations avoid N+1 insert/update patterns.
- Implementation works on both PostgreSQL and SQL Server with appropriate batching/parameterization behavior.

## Tasks

1. Implement a write executor that applies the compiled `ResourceWritePlan` table-by-table in dependency order.
2. Implement delete-by-parent operations for child/extension tables.
3. Implement bulk insert batching with dialect-specific limits and strategies.
4. Add integration tests that write a resource with nested collections and verify row counts/keys after commit (pgsql + mssql where available).

