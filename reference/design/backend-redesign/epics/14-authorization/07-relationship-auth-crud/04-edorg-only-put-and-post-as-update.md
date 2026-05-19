---
jira: DMS-1163
jira_url: https://edfi.atlassian.net/browse/DMS-1163
---

# Slice 4: EdOrg-only PUT And POST-as-update

## Purpose

Complete EdOrg-only relationship CRUD authorization by implementing stored-then-proposed checks for PUT and POST requests that resolve to an existing resource.

This is the riskiest EdOrg-only operation slice because it intersects target resolution, `If-Match`, current-state loading, guarded no-op, and profile-aware write behavior.

## In Scope

- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for PUT.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for POST-as-update.
- Stored-value authorization before update/no-op success can be returned.
- Proposed-value authorization when identifying authorization values can change.
- Integration with current-state loading and guarded no-op so unauthorized callers cannot bypass authorization with an unchanged request body.
- PostgreSQL and SQL Server SQL generation/execution coverage.
- Minimal relationship authorization failure mapping compatible with Slice 6 hardening.

## Explicitly Out Of Scope

- POST create-new behavior already owned by Slice 3.
- GET-by-id and DELETE behavior beyond preserving Slice 2.
- People-involved relationship authorization execution.
- Exact final ProblemDetails wording, hint aggregation, and EdOrg claim formatting hardening.
- Performance optimizations that skip proposed authorization because values are unchanged unless the existing executor already exposes a reliable comparison without additional risk.

## Runtime Behavior

### PUT

- Resolve the target document by id using the existing PUT flow.
- Authorize currently stored EdOrg values before applying update behavior.
- Authorize proposed EdOrg values when the resource allows updates to identifying authorization values.
- Abort before update or guarded no-op success if either required authorization check fails.
- Continue to existing reference resolution, current-state loading, no-op detection, profile merge, and persist behavior only after required relationship checks pass.

### POST-as-update

- Resolve POST target identity using the established POST upsert flow.
- If the target exists, follow the same stored-then-proposed authorization model as PUT.
- If the target does not exist, Slice 3 owns create-new behavior.

## Acceptance Criteria

- PUT succeeds only when the caller is authorized for stored values and, when required, proposed values.
- POST-as-update succeeds only when the caller is authorized for stored values and, when required, proposed values.
- Stored-value authorization executes before update DML, profile merge persistence, or guarded no-op success.
- Proposed-value authorization executes after request/reference data is available and before update DML.
- A caller authorized for proposed values but unauthorized for stored values is denied.
- A caller authorized for stored values but unauthorized for proposed values is denied when proposed authorization is required.
- Unauthorized PUT and POST-as-update do not update `dms.Document`, resource rows, child rows, update-tracking stamps, or referential identity data.
- Guarded no-op cannot return success before stored-value authorization passes.
- `If-Match` and target current-state interactions preserve the final authorization-before-mutation behavior required by `auth.md` and the namespace authorization deferred-guard notes.
- Inverted strategy tests prove stored and proposed checks both swap Source/Target hierarchy filtering.
- Missing/null stored EdOrg values produce relationship invalid-data failure metadata.
- Missing/null proposed EdOrg values produce relationship element-required failure metadata.
- PostgreSQL and SQL Server both abort unauthorized update batches with the established AUTH1 mechanism.

## Tests Required

### Unit tests

- PUT check ordering: stored check before proposed check before persist/no-op.
- POST-as-update check ordering mirrors PUT after target resolution.
- Stored-authorized/proposed-unauthorized denial.
- Stored-unauthorized/proposed-authorized denial.
- Guarded no-op still requires stored authorization.
- `If-Match` interaction tests cover stale etag, matching etag, and unauthorized existing target behavior.
- Normal and inverted Source/Target metadata is applied to both stored and proposed checks.

### Backend integration tests

- PostgreSQL and SQL Server authorized PUT updates a document.
- PostgreSQL and SQL Server unauthorized stored-value PUT leaves rows unchanged.
- PostgreSQL and SQL Server unauthorized proposed-value PUT leaves rows unchanged.
- PostgreSQL and SQL Server authorized POST-as-update updates a document.
- PostgreSQL and SQL Server unauthorized POST-as-update leaves rows unchanged.
- No-op PUT/POST-as-update with unauthorized stored values returns 403 rather than success.

### E2E tests

- Focused DMS E2E coverage with real claim-set/token wiring for authorized and unauthorized PUT.
- Focused DMS E2E coverage for POST-as-update when feasible with existing fixtures.

## Reviewer Focus

Reviewers should focus on ordering and side effects. The correct implementation denies unauthorized existing targets before any update, no-op success, profile merge, or persistence side effect can escape.
