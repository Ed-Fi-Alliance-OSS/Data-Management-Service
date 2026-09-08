---
jira: DMS-1052
jira_url: https://edfi.atlassian.net/browse/DMS-1052
---

# Story: Emit MSSQL TVPs and PGSQL throw_error Function

## Description

Generate the DDL for MSSQL's User-Defined Table Types and PgSql's throw_error custom function per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Generated DDL includes the next SQL Server User-Defined Table Type:
  - BigIntTable (used for EdOrgId lists and DocumentId lists)
  - UniqueIdentifierTable (used for ReferentialId lists; this might have already been created by [DMS-982](https://edfi.atlassian.net/browse/DMS-982))
- Note: No TVP is needed for Namespace prefixes or Ownership tokens. When those lists have >= 2,000 entries, DMS throws an error instead of using a TVP.
- Generated DDL includes the next PostgreSQL function:
  - throw_error
- All identifiers are quoted per dialect.
- DDL output for small fixtures is snapshot-testable and deterministic.

## Tasks

1. Implement DDL emission for each SQL object listed above, using the dialect writer.
2. Ensure deterministic ordering of statements (phased ordering per `ddl-generation.md`).
3. Add snapshot tests that validate DDL output for a small fixture (both dialects).
