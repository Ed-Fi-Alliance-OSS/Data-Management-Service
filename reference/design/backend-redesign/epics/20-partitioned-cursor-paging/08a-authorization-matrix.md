---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S08a: Cursor and Partition Authorization Matrix

## Outcome

Prove through integration coverage that cursor pages and partition boundaries resolve the same
accessible candidate set under every supported authorization strategy, and that a forged range
cannot widen it.

## Design References

- [`Cursor page selection`](EPIC.md#cursor-page-selection)
- [`Partition planning`](EPIC.md#partition-planning)
- [`Cursor Token Contract`](EPIC.md#cursor-token-contract)
- [`Test Expectations`](EPIC.md#test-expectations)

## Dependencies

- Hard dependencies: E20-S04, E20-S05, and E20-S06 for the regular-resource, descriptor, and
  partition execution paths under test.
- E14 row-level authorization planning and E15 plan contracts are the upstream foundations whose
  strategies this matrix exercises.
- E20-S08b consumes the same fixtures for public-contract and E2E coverage. E20-S10 consumes the
  representative authorized fixtures where useful.

## Implementation Scope

- Add the regular-resource authorization matrix for cursor page requests and `/partitions` requests
  across no-further, relationship, ownership, namespace, and view-based strategies where supported.
- Add the descriptor authorization matrix for the no-further and namespace strategies that
  descriptor query execution supports, without promising unsupported strategies.
- Prove that for the same principal, filters, and fixture, a full cursor walk and the union of the
  partition ranges cover exactly the accessible candidate set.
- Add negative cases in which a forged or widened `pageToken` range, an inverted range, and an
  extreme `Int64` range return no inaccessible documents and no inaccessible starting ids.
- Add cases in which authorization admits zero candidates, proving an empty partition array and a
  cursor response with no `Next-Page-Token`.

## Acceptance Evidence and Test Expectations

- For every supported strategy, cursor pages and partition boundaries agree on the accessible
  candidate set for identically seeded PostgreSQL and real SQL Server databases.
- Forged, widened, inverted, and extreme ranges expose no inaccessible identifiers or documents.
- Partition starting ids are always actual accessible candidate ids under the tested strategy.
- Duplicate-producing authorization plans are detected rather than concealed, upholding the
  one-row-per-`DocumentId` candidate invariant for every strategy in the matrix.
- Descriptor coverage is limited to the supported strategies and records the exclusion explicitly.

## Cross-Provider and Authorization Responsibilities

- Every provider-sensitive authorization behavior receives real SQL Server coverage as well as
  PostgreSQL coverage; provider differences are recorded rather than hidden.
- Authorization is exercised through the shared candidate plan, not through test-only predicates, so
  the matrix reflects the compiled production path.

## Explicit Exclusions / Not Assigned

- Public parameter/header/body contracts, route qualifiers, tenants, profiles, walks, concurrency
  scenarios, and ODS comparison execution belong to E20-S08b.
- Fundamental contract, planner, SQL, execution, and OpenAPI implementation belongs to E20-S00a
  through E20-S07.
- The pinned ODS reference stack, comparison harness, and approved-difference ledger belong to
  E20-S11.
- Load/latency thresholds and provider plan capture belong to E20-S10.
