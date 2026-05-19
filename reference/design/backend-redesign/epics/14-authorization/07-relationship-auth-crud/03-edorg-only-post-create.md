---
jira: DMS-1162
jira_url: https://edfi.atlassian.net/browse/DMS-1162
---

# Slice 3: EdOrg-only POST-create

## Purpose

Authorize EdOrg-only relationship strategies for POST requests that resolve to a new resource create.

This slice proves proposed-value authorization before `dms.Document` insertion while leaving existing-resource POST-as-update behavior for Slice 4.

## In Scope

- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for POST create-new.
- Proposed-value authorization from request-body/root-row values after reference and descriptor resolution needed by the write path.
- No-insert behavior when authorization fails.
- PostgreSQL and SQL Server SQL generation/execution coverage.
- Minimal relationship authorization failure mapping compatible with Slice 6 hardening.

## Explicitly Out Of Scope

- POST requests that resolve to an existing resource.
- PUT authorization.
- People-involved relationship authorization.
- Exact final ProblemDetails wording, hint aggregation, and EdOrg claim formatting hardening.
- Optimizations that skip authorization based on direct token EdOrg matches.

## Runtime Behavior

- Run normal POST reference and descriptor resolution required to determine the proposed root-row values and target identity.
- Determine whether POST resolves to create-new or existing-document using the established write path.
- For create-new:
  - authorize proposed EdOrg values before inserting into `dms.Document`,
  - abort the batch on authorization failure, and
  - insert no document or resource rows on failure.
- For existing-document:
  - do not add partial behavior in this slice,
  - leave the existing explicit not-implemented/fail-closed behavior until Slice 4 lands.

## Acceptance Criteria

- POST create-new for an EdOrg-only relationship resource succeeds only when the caller has access through at least one configured EdOrg-only relationship strategy.
- POST create-new with `RelationshipsWithEdOrgsOnlyInverted` uses inverted Source/Target hierarchy filtering against proposed EdOrg values.
- Multiple relationship strategies OR together and preserve configured strategy index metadata.
- Multiple proposed EdOrg subjects inside one strategy AND together.
- Authorization checks execute before `dms.Document` insert.
- Unauthorized POST create-new returns 403 and inserts no `dms.Document` row, no resource root row, and no child rows.
- Proposed EdOrg values are parameterized; token EdOrg IDs are never inlined into SQL.
- Missing/null proposed EdOrg values required for authorization produce the relationship element-required failure metadata consumed by Slice 6.
- Reference-resolution failure remains a reference-resolution error and is not converted into an authorization denial.
- Authorization failure after successful reference resolution still prevents all inserts.
- Security-configuration failures from Slice 1 surface as configuration failures, not as 403 authorization denials.
- PostgreSQL and SQL Server both abort the create batch with the established AUTH1 mechanism.

## Tests Required

### Unit tests

- Proposed-value check spec generation for normal and inverted EdOrg-only strategies.
- POST create SQL/check placement before `dms.Document` insert.
- Missing proposed securable element failure metadata.
- Multiple strategies OR and multiple subjects AND.
- Reference-resolution failure and authorization failure remain distinct result paths.

### Backend integration tests

- PostgreSQL and SQL Server authorized POST create inserts document and resource rows.
- PostgreSQL and SQL Server unauthorized POST create inserts no rows.
- PostgreSQL and SQL Server reference-resolution failure does not execute authorization as a misleading substitute for reference validation.
- Inverted strategy tests prove proposed-value Source/Target filtering is swapped.

### E2E tests

- Focused DMS E2E coverage with real claim-set/token wiring for authorized and unauthorized POST create-new.

## Reviewer Focus

Reviewers should focus on the create boundary: the authorization check must happen after enough request processing to know the proposed values, but before any persistent insert can survive a failure.
