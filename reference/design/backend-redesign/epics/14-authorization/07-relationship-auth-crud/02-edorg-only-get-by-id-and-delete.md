---
jira: DMS-1056
jira_url: https://edfi.atlassian.net/browse/DMS-1056
---

# Slice 2: EdOrg-only GET-by-id And DELETE

## Purpose

Use the operation-neutral core from Slice 1 to implement the first vertical relationship CRUD authorization path: EdOrg-only stored-value checks for GET-by-id and DELETE.

These operations are grouped because they authorize only the already-stored root resource values and do not need proposed request-body authorization.

## In Scope

- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for GET-by-id.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for DELETE.
- Stored-value `EXISTS` checks using concrete root-table EdOrg subjects from Slice 1.
- Authorization checks batched into the same roundtrip as reconstitution for GET-by-id.
- Authorization checks batched into the same roundtrip as delete execution for DELETE.
- PostgreSQL and SQL Server SQL generation/execution coverage.
- Minimal relationship authorization failure mapping compatible with Slice 6 hardening.

## Explicitly Out Of Scope

- POST-create authorization.
- PUT and POST-as-update authorization.
- People-involved relationship authorization.
- Exact final ProblemDetails wording, hint aggregation, and EdOrg claim formatting hardening.
- New auth database objects or DDL.

## Runtime Behavior

### GET-by-id

- Resolve the target document according to the existing GET-by-id flow.
- If the document does not exist, preserve existing not-found behavior.
- Before reconstitution, execute the relationship authorization check against stored root-table values.
- If authorization fails, return 403 and do not execute reconstitution.
- If authorization succeeds, continue with the existing reconstitution behavior.

### DELETE

- Resolve the target document and existing delete preconditions according to the current delete path.
- Before deleting, execute the relationship authorization check against stored root-table values.
- If authorization fails, return 403 and do not execute the delete statement.
- If authorization succeeds, execute the existing delete statement in the same transaction/roundtrip shape.

## Acceptance Criteria

- GET-by-id for an EdOrg-only relationship resource returns the document only when the caller has access through at least one configured EdOrg-only relationship strategy.
- GET-by-id with `RelationshipsWithEdOrgsOnlyInverted` uses inverted Source/Target hierarchy filtering.
- GET-by-id with multiple relationship strategies ORs the strategies and keeps each strategy's configured index metadata.
- GET-by-id with multiple concrete root-table EdOrg subjects inside one strategy ANDs the subjects.
- Unauthorized GET-by-id returns 403 without running reconstitution queries.
- DELETE for an EdOrg-only relationship resource deletes the document only when the caller has access through at least one configured EdOrg-only relationship strategy.
- Unauthorized DELETE returns 403 without deleting `dms.Document` or resource rows.
- Empty EdOrg claims for a single-record stored-value check return 403 rather than the GET-many empty-page behavior.
- Missing/null stored EdOrg values required for authorization produce the relationship invalid-data failure metadata consumed by Slice 6.
- Security-configuration failures from Slice 1 surface as configuration failures, not as 403 authorization denials.
- PostgreSQL uses `dms.throw_error('AUTH1', ...)` or the established PostgreSQL AUTH1 mechanism for aborting unauthorized batches.
- SQL Server uses the established `CAST('AUTH1 - ...' AS INT)` batch-abort pattern.
- SQL Server parameter binding uses scalar parameters below 2,000 unique EdOrg IDs and `dms.BigIntTable` at 2,000 or more.

## Tests Required

### Unit tests

- GET-by-id SQL/check composition for normal and inverted EdOrg-only strategies.
- DELETE SQL/check composition for normal and inverted EdOrg-only strategies.
- OR composition across two relationship strategies.
- AND composition across multiple root-table EdOrg subjects.
- Empty EdOrg claim list produces single-record unauthorized behavior.
- AUTH1 strategy index parsing maps back to the configured strategy metadata.

### Backend integration tests

- PostgreSQL and SQL Server authorized GET-by-id returns reconstituted content.
- PostgreSQL and SQL Server unauthorized GET-by-id returns 403 and does not run reconstitution.
- PostgreSQL and SQL Server authorized DELETE removes the document.
- PostgreSQL and SQL Server unauthorized DELETE leaves `dms.Document` and resource rows intact.
- Inverted strategy tests prove Source/Target filtering is swapped.

### E2E tests

- Focused DMS E2E coverage with real claim-set/token wiring for one GET-by-id scenario and one DELETE scenario, including an unauthorized case.

## Reviewer Focus

Reviewers should focus on whether the operation-neutral core can be consumed without reintroducing GET-many-specific assumptions, and whether unauthorized requests abort before expensive or mutating work.
