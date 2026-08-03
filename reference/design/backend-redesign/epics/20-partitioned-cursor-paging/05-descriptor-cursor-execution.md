---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S05: Descriptor Cursor Execution

## Outcome

Execute the approved cursor contract for descriptor GET-many responses using descriptor rows'
existing `DocumentId` values and the shared candidate and provider plans.

## Design References

- [`GET-many cursor paging`](EPIC.md#get-many-cursor-paging)
- [`Cursor Token Contract`](EPIC.md#cursor-token-contract)
- [`Cursor page selection`](EPIC.md#cursor-page-selection)
- [`Test Expectations`](EPIC.md#test-expectations)

## Dependencies

- Hard dependencies: E20-S00a, E20-S00b, E20-S01, E20-S02, and E20-S03.
- Existing E08 descriptor endpoint behavior remains a compatibility input.
- E20-S08a, E20-S08b, and E20-S10 consume this story's completed descriptor path.

## Implementation Scope

- Pass typed cursor paging through `DescriptorReadHandler` and descriptor query execution.
- Obtain the selected boundary from ordered descriptor query rows without a second query.
- Carry the nullable maximum selected descriptor `DocumentId` to Core and emit the same
  keyset-presence-gated `Next-Page-Token` contract as regular resources.
- Preserve descriptor materialization, namespace handling, filters, change-version behavior, and
  traditional total-count behavior.

## Acceptance Evidence and Test Expectations

- Unit tests cover boundary propagation, header creation, empty/zero-size pages, terminal ranges,
  and traditional behavior.
- PostgreSQL and real SQL Server descriptor integration tests cover multiple pages, sparse ids,
  resource filters, live change-version bounds, and maximum page size.
- Tests prove cursor mode executes no count SQL and adds no database command.
- Regression tests preserve descriptor response materialization and `Total-Count` semantics in
  traditional mode.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and SQL Server must return the same ordered descriptor ids for equivalent fixtures.
- Cover descriptor no-further and namespace authorization, the strategies currently supported by
  descriptor query execution. Do not promise unsupported relationship/ownership strategies.

## Explicit Exclusions / Not Assigned

- Regular-resource hydration belongs to E20-S04.
- Descriptor partition boundaries belong to E20-S06.
- Descriptor OpenAPI belongs to E20-S07.
- The authorization matrix belongs to E20-S08a, and broad parity/E2E and performance gates belong to
  E20-S08b and E20-S10.
