---
jira: DMS-984
jira_url: https://edfi.atlassian.net/browse/DMS-984
---

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

## Authorization Batching Consideration

Authorization is out of scope for this story, but the transaction and batching structure should be designed to allow authorization check statements to be prepended within the same roundtrip. For POST, auth checks are batched into the roundtrip that creates the `dms.Document` row; for PUT, auth checks run in the roundtrip that precedes the persist step. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" (POST roundtrip #3, PUT roundtrip #3).

## Tasks

1. Implement a write executor that applies the compiled `ResourceWritePlan` table-by-table in dependency order.
2. Implement delete-by-parent operations for child/extension tables.
3. Implement bulk insert batching with dialect-specific limits and strategies.
4. Add integration tests that write a resource with nested collections and verify row counts/keys after commit (pgsql + mssql where available).

