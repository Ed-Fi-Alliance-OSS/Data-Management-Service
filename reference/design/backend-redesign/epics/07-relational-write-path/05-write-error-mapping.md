---
jira: DMS-986
jira_url: https://edfi.atlassian.net/browse/DMS-986
---

# Story: Map DB Constraint Errors to DMS Write Error Shapes

## Description

Provide consistent, actionable error mapping for relational writes across PostgreSQL and SQL Server:

- Unique constraint violations that survive pre-DML validation (for example root natural-key conflicts or unexpected persisted-state races).
- FK violations (unresolved/missing references when writing, or schema mismatch).
- Deterministic fallback mapping for final DB write exceptions that are not profile failures.

Submitted duplicate collection/common-type/extension collection items are not in scope for this DB-exception story. They remain request-validation failures keyed by the compiled semantic identity for the scope, sourced from scope-resolved `arrayUniquenessConstraints` or the qualifying scope-local `documentPathsMapping.referenceJsonPaths` binding set, and should be rejected before backend DML reaches the database.

Profile-specific classification and API mapping are handled by `DMS-1104`; deadlock/serialization retry policy remains owned by `DMS-996`. This story stays limited to final database exception classification and mapping.

## Acceptance Criteria

- Natural key uniqueness failures map to a deterministic DMS conflict response.
- Duplicate submitted collection/common-type/extension collection items that collide on compiled semantic identity within the same parent scope are excluded from DB exception mapping; the design requires them to fail earlier as `400 Data Validation Failed` request-validation errors.
- FK violations are mapped to:
  - reference validation failures when possible (preferred), or
  - consistent internal errors when they represent unexpected states.
- The story does not implement retry loops or own retryable error-code policy; it only classifies/maps the final DB exception surfaced by the write pipeline.
- Unit tests validate exception classification and mapping for both dialects, including fallback handling for unexpected DB write errors.

## Tasks

1. Implement a DB exception classifier for pgsql + mssql with:
   - unique violation detection,
   - FK violation detection,
   - deterministic fallback classification for unrecognized write exceptions.
2. Implement mapping from constraint names to resource context where possible (deterministic naming rules).
3. Add unit tests covering representative unique-violation, FK-violation, and fallback error cases with expected DMS results.
