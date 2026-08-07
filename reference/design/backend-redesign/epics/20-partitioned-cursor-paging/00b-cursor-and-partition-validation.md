---
jira: DMS-1384
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Request Validation and Typed Paths

## Outcome

Own the cursor and partition request boundary end to end: ODS-precedence single-error cursor
validation, the separately phase-gated partition validator, the shared parameter-validation
ProblemDetails shell, operation-scoped cursor parameter recognition, typed
collection/by-id/partition path operations, and parameter canonicalization. The design doc is the
source of truth for exact
messages, phase gating, and within-phase tie-breakers; the epic owns work partitioning, the ODS
comparison, and the acceptance evidence.

Validation and canonicalization are one story because phase selection turns on query-key presence,
and which keys are present is exactly what canonicalization decides. Splitting them would let one
story assert a presence semantic the other owns.

## Design References

- [`Cursor validation and ProblemDetails`](../../design-docs/partitioned-cursor-paging.md#cursor-validation-and-problemdetails)
- [`Worked precedence examples`](../../design-docs/partitioned-cursor-paging.md#worked-precedence-examples)
- [`Query-parameter name canonicalization`](../../design-docs/partitioned-cursor-paging.md#query-parameter-name-canonicalization)
- [`Operation scoping`](../../design-docs/partitioned-cursor-paging.md#operation-scoping)
- [`/partitions`](../../design-docs/partitioned-cursor-paging.md#partitions)
- [`Application Boundaries`](../../design-docs/partitioned-cursor-paging.md#application-boundaries)
- [`ODS Precedence Comparison`](EPIC.md#ods-precedence-comparison) — for the ODS 7.3.2 column
- [`Approved Intentional ODS Differences`](EPIC.md#approved-intentional-ods-differences)

## Dependencies

- Hard dependency: DMS-1383 for the typed paging/range contracts and the token codec that phase 0
  consumes.
- This story blocks DMS-1386 for the canonicalized cursor parameters that GET-many consumes and
  DMS-1387 for partition validation and the typed partition route.
- DMS-1387 owns activation of the dedicated partition pipeline and endpoint behavior.
- Existing E08 query contracts and E10 live change-version behavior are compatibility inputs.

## Implementation Scope

- Add a cursor validator that evaluates token decoding, mixed-mode conflicts, required
  relationships, and syntax/range rules in the approved four-phase ODS-compatible precedence and
  returns exactly one error.
- Treat query-key presence, including a blank, malformed, or zero value, as the phase-selection and
  conflict-ordering signal rather than deferring to a bound value.
- Add the separately phase-gated partition validator with `number` precedence over the
  unsupported-parameter phase and canonical unsupported-parameter ordering.
- Add the approved parameter-validation ProblemDetails shell for cursor and partition failures while
  leaving traditional-only `limit`/`offset` failures on their existing generic bad-request response.
- Keep cursor parameter recognition operation-scoped so `/deletes` and `/keyChanges` retain their
  existing invalid-query-field HTTP 400 behavior instead of globally reserving the names.
- Add typed `ResourcePathOperation` collection, by-id, and partition cases, and update path parsing,
  request state, route semantics, logging classification, and API dispatch to consume them.
- Canonicalize `pageToken`, `pageSize`, and partition `number` at the HTTP boundary, preserving
  last-value-wins behavior across repeated parameters including case variants.
- Preserve the existing invalid-UUID result for unknown third segments and unmatched behavior for
  additional segments.
- Until DMS-1387 activates the partition pipeline, dispatch
  `/{project}/{resource}/partitions` through the existing invalid-UUID HTTP 400 behavior, including
  `"validationErrors":{"$.id":["The value 'partitions' is not valid."]}`; do not return an
  incomplete partition response.
- Cursor parameter recognition on live GET-many is staged differently from `/partitions`, and
  deliberately so: the names are recognized from this story onward, so a request that passes cursor
  validation reaches the relational read path's cursor guard and is answered HTTP 501
  `"Cursor paging is not yet supported for relational queries."` until DMS-1386 implements cursor
  execution. No `Next-Page-Token` header is emitted before then. The alternative — withholding
  recognition until execution lands — was not taken, because the parameter contract is what the
  sibling stories and the public-contract suite build against, and a cursor request that is rejected
  is answered by that contract either way.

## Acceptance Evidence and Test Expectations

- Cursor validator tests prove token-decode, mixed-mode, required-relationship, and syntax/range
  precedence, exactly one error, and exact messages, including the ODS
  `Use limit instead of pageSize...` case.
- Tests implement every row of the design doc's worked precedence table, including the blank
  `pageSize` and non-numeric `pageSize` rows recorded as approved intentional ODS differences in the
  epic.
- Partition validator tests preserve `number` precedence and canonical unsupported-parameter
  ordering, and cover several reserved parameters in one request. They also cover malformed, blank,
  and out-of-range `number`, including the blank case the design doc treats as malformed rather than
  absent, and prove resource-property and change-version filters are accepted rather than reported
  as unsupported.
- Traditional-only pagination failures retain their current response shell and messages, and `limit`,
  `offset`, and `totalCount` remain case-insensitive, with the fold made culture-invariant so a server
  whose culture is not the invariant one recognizes them as well.
- `/deletes` and `/keyChanges` tests prove `pageToken` and `pageSize` are rejected rather than
  ignored.
- Unit tests cover every typed path case, unknown child segments, extra segments, route qualifiers,
  and tenant-prefixed paths.
- Frontend tests prove repeated exact-name and case-variant query parameters choose the last value
  in request order, asserting the query parameters handed to Core. That the chosen value is what
  drives validator phase selection is proven by an API-level integration scenario against the
  assembled pipeline, because query validation is not reachable without a database.
- Regression tests cover existing GET-many, GET-by-id, write, delete, and tracked-change routing.
- Before DMS-1387, `/partitions` regression coverage locks the existing invalid-UUID HTTP 400; no
  incomplete endpoint is externally exposed.

## Cross-Provider and Authorization Responsibilities

- Validation and routing are provider-neutral and contain no PostgreSQL or SQL Server syntax.
- Validation must not make authorization decisions. A decoded range is not an access grant, and
  resource and row authorization remain independent inputs reapplied by later stories.
- Typed dispatch must preserve the existing authentication, resource-action authorization, profile,
  tenant, and datastore-resolution boundaries for collection and by-id operations.

## Explicit Exclusions / Not Assigned

- Typed paging/range contracts, the token codec, result-boundary shapes, and configuration belong to
  DMS-1383.
- Candidate planning and SQL compilation belong to DMS-1385 and DMS-1387.
- Response-header execution belongs to DMS-1386.
- Partition response generation belongs to DMS-1387.
- OpenAPI publication belongs to DMS-1388, and the static ODS-comparison cases belong to DMS-1390.
