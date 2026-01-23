---
jira: DMS-986
jira_url: https://edfi.atlassian.net/browse/DMS-986
---

# Story: Map DB Constraint Errors to DMS Write Error Shapes

## Description

Provide consistent, actionable error mapping for relational writes across PostgreSQL and SQL Server:

- Unique constraint violations (natural key conflicts).
- FK violations (unresolved/missing references when writing, or schema mismatch).
- Deadlocks/serialization failures (retry where appropriate, otherwise fail with clear guidance).

## Acceptance Criteria

- Natural key uniqueness failures map to a deterministic DMS conflict response.
- FK violations are mapped to:
  - reference validation failures when possible (preferred), or
  - consistent internal errors when they represent unexpected states.
- Deadlock/serialization errors are retried according to a consistent policy (or surfaced with clear diagnostics when retries are exhausted).
- Unit tests validate exception classification and mapping for both dialects.

## Tasks

1. Implement a DB exception classifier for pgsql + mssql with:
   - unique violation detection,
   - FK violation detection,
   - deadlock/serialization detection.
2. Implement mapping from constraint names to resource context where possible (deterministic naming rules).
3. Add unit tests covering representative error cases and expected DMS results.

