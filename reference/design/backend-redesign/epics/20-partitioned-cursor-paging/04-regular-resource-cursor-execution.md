---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S04: Regular-Resource Cursor Execution

## Outcome

Execute cursor paging for regular resources through the existing single-command hydration path
and emit the correct next-page header without a second candidate query or database roundtrip.

## Design References

- [`GET-many cursor paging`](EPIC.md#get-many-cursor-paging)
- [`Cursor Token Contract`](EPIC.md#cursor-token-contract)
- [`Cursor page selection`](EPIC.md#cursor-page-selection)
- [`Consistency Under Writes`](EPIC.md#consistency-under-writes)

## Dependencies

- Hard dependencies: E20-S00, E20-S01, E20-S02, and E20-S03.
- E20-S08 and E20-S09 consume this story's completed execution path.

## Implementation Scope

- Pass typed cursor paging through the regular-resource repository and page-keyset execution path.
- Return inserted keyset ids from hydration using PostgreSQL `RETURNING` and SQL Server `OUTPUT`.
- Calculate and carry `HighestSelectedDocumentId` through hydration and `QuerySuccess`.
- Emit `Next-Page-Token` for every non-empty traditional or cursor GET-many response and omit it
  for empty responses and the `Int64.MaxValue` overflow case.
- Preserve existing total-count and profile projection behavior in traditional mode.

## Acceptance Evidence and Test Expectations

- Hydration unit/golden tests lock provider-specific keyset output and result-set ordering.
- Handler tests cover traditional first pages, cursor pages, page size 0, empty terminal pages,
  bounded inverted tokens, and `Int64.MaxValue`.
- PostgreSQL and real SQL Server integration tests cover sparse ids, filters, change-version
  ranges, multiple pages, maximum page size, and no added command/roundtrip.
- A concurrency test deletes the selected final row before hydration and proves boundary progress
  uses the selected keyset maximum.

## Cross-Provider and Authorization Responsibilities

- Both provider executors expose the same selected-id result contract.
- Regular-resource cursor pages reapply every supported row-level authorization strategy through
  the shared candidate plan; a forged range cannot bypass it.

## Explicit Exclusions / Not Assigned

- Descriptor execution belongs to E20-S05.
- Partition endpoints and boundary SQL belong to E20-S06.
- OpenAPI publication belongs to E20-S07.
- Broad parity/E2E and performance gates belong to E20-S08 and E20-S09.
