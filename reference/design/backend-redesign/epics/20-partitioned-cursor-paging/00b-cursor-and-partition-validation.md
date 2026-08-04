---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S00b: Request Validation and Typed Paths

## Outcome

Own the cursor and partition request boundary end to end: ODS-precedence single-error cursor
validation, the separately phase-gated partition validator, the shared parameter-validation
ProblemDetails shell, operation-scoped cursor parameter recognition, typed collection/by-id/partition
path operations, and parameter canonicalization. The design doc is the source of truth for exact
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

- Hard dependency: E20-S00a for the typed paging/range contracts and the token codec that phase 0
  consumes.
- This story blocks E20-S04 for the canonicalized cursor parameters that GET-many consumes and
  E20-S06 for partition validation and the typed partition route.
- E20-S06 owns activation of the dedicated partition pipeline and endpoint behavior.
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
- Until E20-S06 activates the partition pipeline, dispatch
  `/{project}/{resource}/partitions` through the existing invalid-UUID HTTP 400 behavior, including
  `"validationErrors":{"$.id":["The value 'partitions' is not valid."]}`; do not return an
  incomplete partition response.

## Acceptance Evidence and Test Expectations

- Cursor validator tests prove token-decode, mixed-mode, required-relationship, and syntax/range
  precedence, exactly one error, and exact messages, including the ODS
  `Use limit instead of pageSize...` case.
- Tests implement every row of the design doc's worked precedence table, including the blank
  `pageSize` and non-numeric `pageSize` rows recorded as approved intentional ODS differences in the
  epic.
- Partition validator tests preserve `number` precedence and canonical unsupported-parameter
  ordering, and cover several reserved parameters in one request.
- Traditional-only pagination failures retain their current response shell and messages, and the
  existing case-sensitive matching of `limit`, `offset`, and `totalCount` is unchanged.
- `/deletes` and `/keyChanges` tests prove `pageToken` and `pageSize` are rejected rather than
  ignored.
- Unit tests cover every typed path case, unknown child segments, extra segments, route qualifiers,
  and tenant-prefixed paths.
- Frontend tests prove repeated exact-name and case-variant query parameters choose the last value
  in request order, and that the chosen value is what drives validator phase selection.
- Regression tests cover existing GET-many, GET-by-id, write, delete, and tracked-change routing.
- Before E20-S06, `/partitions` regression coverage locks the existing invalid-UUID HTTP 400; no
  incomplete endpoint is externally exposed.

## Cross-Provider and Authorization Responsibilities

- Validation and routing are provider-neutral and contain no PostgreSQL or SQL Server syntax.
- Validation must not make authorization decisions. A decoded range is not an access grant, and
  resource and row authorization remain independent inputs reapplied by later stories.
- Typed dispatch must preserve the existing authentication, resource-action authorization, profile,
  tenant, and datastore-resolution boundaries for collection and by-id operations.

## Explicit Exclusions / Not Assigned

- Typed paging/range contracts, the token codec, result-boundary shapes, and configuration belong to
  E20-S00a.
- Candidate planning and SQL compilation belong to E20-S02 and E20-S06.
- Response-header execution belongs to E20-S04.
- Partition response generation belongs to E20-S06.
- OpenAPI publication belongs to E20-S07, and the static ODS-comparison cases belong to E20-S08b.
