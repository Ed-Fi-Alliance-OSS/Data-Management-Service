# Story: Delete Path Tests (pgsql + mssql)

## Description

Add test coverage for relational delete semantics:

- delete success
- delete not-found
- delete conflict due to references
- conflict diagnostics are actionable

## Acceptance Criteria

- Tests run against provisioned databases (docker compose; no Testcontainers).
- The same test cases run for PostgreSQL and SQL Server where both backends are available.
- Failures include logs/diagnostics that show the violated constraints or referencing resources.

## Tasks

1. Add integration tests for delete success and not-found behavior.
2. Add integration tests for delete conflict behavior and diagnostic content.
3. Wire tests into CI categories (e.g., `DbApply`/`Integration`) consistent with repo practices.

