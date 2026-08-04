---
jira: DMS-1389
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Cursor and Partition Authorization Matrix

## Outcome

Prove through integration coverage that cursor pages and partition boundaries resolve the same
accessible candidate set under every supported authorization strategy, and that a forged range
cannot widen it.

## Design References

- [`Shared candidate relation`](../../design-docs/partitioned-cursor-paging.md#shared-candidate-relation)
- [`Provider cursor SQL`](../../design-docs/partitioned-cursor-paging.md#provider-cursor-sql)
- [`Partition planning`](../../design-docs/partitioned-cursor-paging.md#partition-planning)
- [`Cursor Token Contract`](../../design-docs/partitioned-cursor-paging.md#cursor-token-contract)
- [`Test Expectations`](EPIC.md#test-expectations)

## Dependencies

- Hard dependencies: DMS-1386 for the regular-resource and descriptor cursor paths and DMS-1387 for
  the partition execution path under test.
- E14 row-level authorization planning and E15 plan contracts are the upstream foundations whose
  strategies this matrix exercises.
- DMS-1390 consumes the same fixtures for public-contract and E2E coverage. DMS-1392 consumes the
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
  scenarios, and ODS comparison execution belong to DMS-1390.
- Fundamental contract, planner, SQL, execution, and OpenAPI implementation belongs to DMS-1383
  through DMS-1388.
- The static ODS-comparison cases and approved-difference enforcement belong to DMS-1390.
- Load/latency thresholds and provider plan capture belong to DMS-1392.
