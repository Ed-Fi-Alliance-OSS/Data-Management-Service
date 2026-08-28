---
jira: DMS-1386
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Cursor Execution

## Outcome

Execute cursor paging for regular resources and descriptors through their existing single-command
query paths and emit the correct next-page header without a second candidate query or database
roundtrip.

Regular-resource and descriptor execution are one story because they emit the same
`Next-Page-Token` contract from the same Core response path, no downstream consumer can use one
without the other, and split they would either duplicate that header logic or make the descriptor
half a hidden dependency of the regular-resource half.

## Design References

- [`GET-many cursor paging`](../../design-docs/partitioned-cursor-paging.md#get-many-cursor-paging)
- [`Cursor Token Contract`](../../design-docs/partitioned-cursor-paging.md#cursor-token-contract)
- [`Carrying the selected-keyset boundary`](../../design-docs/partitioned-cursor-paging.md#carrying-the-selected-keyset-boundary)
- [`Provider cursor SQL`](../../design-docs/partitioned-cursor-paging.md#provider-cursor-sql)
- [`Consistency Under Writes`](../../design-docs/partitioned-cursor-paging.md#consistency-under-writes)
- [`Test Expectations`](EPIC.md#test-expectations)

## Dependencies

- Hard dependencies: DMS-1383, DMS-1384, and DMS-1385.
- Existing E08 hydration, query-execution, and descriptor endpoint behavior remain compatibility
  inputs.
- DMS-1387 route activation, DMS-1388 publication, DMS-1389, DMS-1390, DMS-1392, and DMS-1393
  consume this story's completed execution paths.

## Implementation Scope

- Pass typed cursor paging through the regular-resource repository and page-keyset execution path.
- Return inserted collection-query keyset ids from hydration using PostgreSQL `RETURNING` and SQL
  Server `OUTPUT`, without changing GET-by-id hydration result sets.
- Calculate and carry a nullable highest selected key through hydration and `QuerySuccess`. DMS-1394
  renamed the carrier to `HighestSelectedAnchor` when a windowed page gained the option of ordering
  by `ContentVersion`; under this story's `DocumentId` ordering the value is the highest selected
  `DocumentId`.
- Pass typed cursor paging through `DescriptorReadHandler` and descriptor query execution, obtaining
  the selected boundary from ordered descriptor query rows without a second query.
- Emit `Next-Page-Token` from one shared Core path for both resource families whenever
  `HighestSelectedAnchor` is present, even when concurrent deletion leaves the hydrated response
  body empty. Omit it when the selected keyset is empty or selection is skipped, and for the
  `Int64.MaxValue` overflow case.
- Preserve existing total-count and profile projection behavior in traditional mode, and preserve
  descriptor materialization, namespace handling, filters, and change-version behavior.

## Acceptance Evidence and Test Expectations

- Hydration unit/golden tests lock provider-specific keyset output and the batch result-set
  sequence, without assuming any row order within `RETURNING` or `OUTPUT` results.
- Handler tests cover traditional first pages, cursor pages, page size 0, empty terminal pages,
  authorization/preprocessing/planner no-query empty paths, bounded inverted tokens, and
  `Int64.MaxValue`, for both regular resources and descriptors.
- Tests prove regular-resource and descriptor responses emit the header from the same
  keyset-presence gate rather than from two divergent rules.
- PostgreSQL and real SQL Server integration tests cover sparse ids, filters, change-version
  ranges, multiple pages, maximum page size, and no added command/roundtrip, for both regular
  resources and descriptors.
- Tests prove cursor mode executes no count SQL for either resource family.
- A concurrency test deletes all selected rows before hydration and proves the empty response body
  still emits boundary progress from the selected keyset maximum.
- Regression tests preserve descriptor response materialization and `Total-Count` semantics in
  traditional mode.

## Cross-Provider and Authorization Responsibilities

- Both provider executors expose the same selected-id result contract, and both providers return the
  same ordered descriptor ids for equivalent fixtures.
- Regular-resource cursor pages reapply every supported row-level authorization strategy through
  the shared candidate plan; a forged range cannot bypass it.
- Cover descriptor no-further, namespace, and custom-view authorization, the strategies currently
  supported by descriptor query execution: `DescriptorReadHandler` compiles both namespace checks and
  custom-view checks into the descriptor candidate relation. Do not promise unsupported
  relationship/ownership strategies.

## Explicit Exclusions / Not Assigned

- Partition endpoints and boundary SQL belong to DMS-1387.
- OpenAPI publication belongs to DMS-1388.
- The authorization matrix belongs to DMS-1389, and broad parity/E2E and performance gates belong to
  DMS-1390 and DMS-1392.
- Production telemetry belongs to DMS-1393.
