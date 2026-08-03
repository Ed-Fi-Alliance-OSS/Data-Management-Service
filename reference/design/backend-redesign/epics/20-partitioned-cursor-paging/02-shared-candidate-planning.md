---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S02: Shared Candidate Planning

## Outcome

Extend the existing shared page-document-id plan contract so traditional pages, cursor pages, and
partition boundaries cannot drift in filtering, change-version behavior, parameter binding, or
row-level authorization.

## Design References

- [`Cursor page selection`](EPIC.md#cursor-page-selection)
- [`Partition planning`](EPIC.md#partition-planning)
- [`Consistency Under Writes`](EPIC.md#consistency-under-writes)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)

## Dependencies

- Hard dependency: E20-S00 for typed paging/range and backend contract boundaries.
- E20-S09 may run in parallel. Its captured baseline is a hard prerequisite of E20-S03 before
  generated provider SQL changes, not of this contract-extension story.
- External foundations: E08 regular/descriptor query planning, E10 live change-version filters,
  and E14 row-level authorization planning.
- Blocks provider compilation and execution in E20-S03 through E20-S06.

## Implementation Scope

- Extend `PageDocumentIdQuerySpec` and `PageDocumentIdSqlCompiler`, already shared by the existing
  regular-resource and descriptor page planners, with explicit cursor-bound/page-size and
  partition-count/minimum-size parameter roles and an unpaged partition candidate form.
- Share resource-filter and live change-version validation/planning between GET-many and
  `/partitions`.
- Preserve regular-resource root-table behavior and descriptor `dms.Descriptor` plus
  `ResourceKeyId` behavior.
- Add an explicit one-row-per-`DocumentId` assertion for every consumer and supported
  authorization strategy.
- Keep provider-neutral candidate semantics separate from paging- and partition-specific SQL.

## Acceptance Evidence and Test Expectations

- Planner unit tests prove both existing planners construct the extended shared spec and that
  traditional, cursor, and partition consumers receive identical predicates, authorization specs,
  and filter parameter values for the same request.
- Tests cover resource filters, id filters, min/max change version, unified aliases, empty
  candidates, and descriptors.
- Authorization planner tests cover no-further, relationship, ownership, namespace, and view-based
  strategies where supported and detect duplicate candidate ids.
- Normalized plan-contract tests lock deterministic parameter ordering.
- Existing traditional page-selection SQL goldens remain unchanged; E20-S03 owns comparison with
  the E20-S09 baseline before provider SQL changes.

## Cross-Provider and Authorization Responsibilities

- Candidate semantics and parameter roles are shared by PostgreSQL and SQL Server.
- Authorization is compiled into the candidate relation before any cursor range, row numbering,
  count, or partition sizing is applied.

## Explicit Exclusions / Not Assigned

- PostgreSQL/SQL Server cursor syntax belongs to E20-S03.
- Hydration, headers, and descriptor materialization belong to E20-S04 and E20-S05.
- Partition window SQL and endpoint execution belong to E20-S06.
