---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S00b: Cursor and Partition Validation

## Outcome

Implement ODS-precedence single-error cursor validation, the separately phase-gated partition
validator, the shared parameter-validation ProblemDetails shell, and operation-scoped cursor
parameter recognition. The epic remains the source of truth for exact messages, phase gating, and
within-phase tie-breakers.

## Design References

- [`Cursor validation and ProblemDetails`](EPIC.md#cursor-validation-and-problemdetails)
- [`Worked precedence examples`](EPIC.md#worked-precedence-examples)
- [`/partitions`](EPIC.md#partitions)
- [`Approved Intentional ODS Differences`](EPIC.md#approved-intentional-ods-differences)

## Dependencies

- Hard dependency: E20-S00a for the typed paging/range contracts and the token codec that phase 0
  consumes.
- This story blocks E20-S01 for reserved parameter names and the validation boundary, E20-S06 for
  partition validation, and E20-S11 for the fixed validation precedence it compares.
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

## Acceptance Evidence and Test Expectations

- Cursor validator tests prove token-decode, mixed-mode, required-relationship, and syntax/range
  precedence, exactly one error, and exact messages, including the ODS
  `Use limit instead of pageSize...` case.
- Tests implement every row of the epic's worked precedence table, including the blank `pageSize`
  and non-numeric `pageSize` rows recorded as approved intentional ODS differences.
- Partition validator tests preserve `number` precedence and canonical unsupported-parameter
  ordering, and cover several reserved parameters in one request.
- Traditional-only pagination failures retain their current response shell and messages, and the
  existing case-sensitive matching of `limit`, `offset`, and `totalCount` is unchanged.
- `/deletes` and `/keyChanges` tests prove `pageToken` and `pageSize` are rejected rather than
  ignored.

## Cross-Provider and Authorization Responsibilities

- Validation is provider-neutral and contains no PostgreSQL or SQL Server syntax.
- Validation must not make authorization decisions. A decoded range is not an access grant, and
  resource and row authorization remain independent inputs reapplied by later stories.

## Explicit Exclusions / Not Assigned

- Typed paging/range contracts, the token codec, result-boundary shapes, and configuration belong to
  E20-S00a.
- Frontend path classification and repeated-parameter canonicalization belong to E20-S01.
- Candidate planning and SQL compilation belong to E20-S02, E20-S03, and E20-S06.
- Response-header execution belongs to E20-S04 and E20-S05.
- OpenAPI publication belongs to E20-S07, and ODS-side comparison capture belongs to E20-S11.
