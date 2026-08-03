---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S03: Provider Cursor SQL

## Outcome

Compile seek-based cursor keyset selection for PostgreSQL and SQL Server without changing
traditional paging behavior or introducing offset/count work in cursor mode.

## Design References

- [`Cursor page selection`](EPIC.md#cursor-page-selection)
- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)

## Dependencies

- Hard dependencies: E20-S00 and E20-S02.
- Sequencing gate: the baseline portion of E20-S09 must complete before this story changes planner
  SQL. E20-S09's final performance gate runs after implementation and does not create a cycle.

## Implementation Scope

- Extend page-query plan contracts and parameter roles for inclusive cursor bounds and page size.
- Compile PostgreSQL range predicates plus `LIMIT @pageSize` with no offset/count SQL.
- Compile SQL Server range predicates plus `TOP (@pageSize)` with no `OFFSET`/count SQL.
- Preserve existing traditional PostgreSQL `LIMIT/OFFSET` and SQL Server `OFFSET/FETCH` output
  except for reviewed mechanical factoring.
- Supply compiled cursor plans to both regular-resource and descriptor execution stories.

## Acceptance Evidence and Test Expectations

- Provider SQL-golden tests assert exact range predicates, ordering, size syntax, and parameter
  roles.
- Negative assertions prove cursor plans contain no offset, row-number skip, or total-count SQL.
- Traditional plan goldens demonstrate no semantic or unexplained textual regression.
- Edge cases cover inverted and extreme `Int64` ranges and page sizes 0, 1, and maximum.
- Focused PostgreSQL and real SQL Server integration probes prove the generated SQL executes.

## Cross-Provider and Authorization Responsibilities

- Both providers consume the same E20-S02 candidate plan and return the same ordered ids for
  equivalently seeded data.
- Provider compilers must place range predicates alongside, not instead of, all candidate
  authorization predicates.

## Explicit Exclusions / Not Assigned

- Keyset hydration/output and HTTP headers belong to E20-S04 and E20-S05.
- Partition row-number/count SQL belongs to E20-S06.
- Full plan and latency acceptance belongs to E20-S09.
