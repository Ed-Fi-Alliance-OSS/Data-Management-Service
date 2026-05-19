---
jira: DMS-1056
jira_url: https://edfi.atlassian.net/browse/DMS-1056
---

# Slice 6: Relationship Auth ProblemDetails Hardening

## Purpose

Harden relationship authorization error handling so the EdOrg CRUD slices and People core metadata produce the exact RFC 9457 ProblemDetails shapes described in `auth.md`.

This slice should refine response mapping and tests. It should not introduce new authorization strategies or endpoint execution behavior.

## In Scope

- Parse and map `AUTH1` failures back to relationship strategy metadata by configured strategy index.
- Format relationship authorization 403 responses per `auth.md` §"ProblemDetails".
- Translate securable element JSON paths to readable names, preferring MetaEd/readable names when available.
- Handle singular vs plural securable element error text.
- Format EdOrg claim values, including `none` and truncation after five claims followed by `...`.
- Aggregate distinct hints across multiple failing relationship OR strategies.
- Preserve strategy identity for invalid-data and element-required relationship failures.
- Unit, integration, and focused E2E tests for final response shape.

## Explicitly Out Of Scope

- New database authorization objects or DDL.
- New relationship strategy semantics.
- GET-many normal denial behavior; GET-many still filters rows rather than returning 403 for unauthorized rows.
- Implementing People CRUD endpoint execution.
- Implementing namespace, ownership, or view-based ProblemDetails beyond avoiding regressions in shared error infrastructure.

## ProblemDetails Cases

This slice owns final formatting for relationship authorization cases from `auth.md`:

- Relationship-based no relationships established with EdOrg claims.
- Relationship-based no relationships established without EdOrg claims where applicable to a relationship/custom-view-style check.
- Required relationship securable element uninitialized in existing data.
- Required relationship securable element missing from proposed data.
- Hint text appended to `detail`.
- Multiple distinct hints concatenated in deterministic configured strategy order.

## Acceptance Criteria

- A failed relationship auth check returns status 403 with:
  - `type`,
  - `title`,
  - `status`,
  - `detail`,
  - `errors`, and
  - `correlationId`
  matching the `auth.md` RFC 9457 contract.
- `AUTH1` strategy indexes are parsed reliably for PostgreSQL and SQL Server failure patterns.
- If one relationship strategy fails, the mapper uses that strategy's readable securable element metadata and hints.
- If multiple OR relationship strategies fail, the mapper combines the relevant securable names and distinct hints without losing configured strategy order.
- A single readable securable element uses the singular error message form.
- Multiple readable securable elements use the plural error message form and deterministic name ordering.
- EdOrg claims render as `none` when the caller has no EdOrg claims.
- One EdOrg claim uses `claim`; multiple EdOrg claims use `claims`.
- More than five EdOrg claims render only the first five deterministic values followed by `...`.
- JSON paths such as `$.schoolReference.schoolId` render as `SchoolId` when no MetaEd/readable name is available.
- MetaEd/readable names carried by the auth core are preferred over fallback JSON path derivation.
- Stored-value failures use existing-data wording.
- Proposed-value failures use proposed-data wording.
- Relationship invalid-data and element-required cases use the specific `auth.md` relationship `type` values.
- Security-configuration failures remain 500 security configuration failures and are not converted into 403 authorization denials.

## Tests Required

### Unit tests

- PostgreSQL `AUTH1` parsing.
- SQL Server `AUTH1` cast-failure parsing.
- Single securable element message.
- Multiple securable elements message.
- Readable name fallback from JSON path.
- MetaEd/readable name preference.
- No EdOrg claims formatting.
- One, five, and more-than-five EdOrg claim formatting.
- Multiple OR strategy hint aggregation with duplicate hints removed.
- Existing-data uninitialized and proposed-data missing relationship cases.

### Backend integration tests

- PostgreSQL and SQL Server unauthorized GET-by-id response body matches the final relationship ProblemDetails contract.
- PostgreSQL and SQL Server unauthorized POST-create response body matches the final relationship ProblemDetails contract.
- PostgreSQL and SQL Server unauthorized PUT response body distinguishes stored vs proposed value failures.
- PostgreSQL and SQL Server unauthorized DELETE response body matches the final relationship ProblemDetails contract.

### E2E tests

- Focused DMS E2E coverage validates the externally observable response shape for one stored-value operation and one proposed-value operation.

## Reviewer Focus

Reviewers should focus on wire compatibility with `auth.md`, not on authorization SQL semantics already owned by earlier slices.
