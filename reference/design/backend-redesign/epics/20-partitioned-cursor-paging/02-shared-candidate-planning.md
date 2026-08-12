---
jira: DMS-1385
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Candidate Planning and Provider Cursor SQL

## Outcome

Extend the existing shared page-document-id plan contract so traditional pages, cursor pages, and
partition boundaries cannot drift in filtering, change-version behavior, parameter binding, or
row-level authorization, then compile seek-based cursor keyset selection for PostgreSQL and SQL
Server without changing traditional paging behavior or introducing offset/count work in cursor mode.

The contract extension and the provider compilers are one story because they are the same code area
and the same goldens: the parameter roles added to the shared spec have no observable behavior until
a dialect compiler emits them.

## Design References

- [`Paging-mode choice`](../../design-docs/partitioned-cursor-paging.md#paging-mode-choice)
- [`Shared candidate relation`](../../design-docs/partitioned-cursor-paging.md#shared-candidate-relation)
- [`Provider cursor SQL`](../../design-docs/partitioned-cursor-paging.md#provider-cursor-sql)
- [`Partition planning`](../../design-docs/partitioned-cursor-paging.md#partition-planning)
- [`Consistency Under Writes`](../../design-docs/partitioned-cursor-paging.md#consistency-under-writes)
- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](../../design-docs/partitioned-cursor-paging.md#risks-and-guardrails)

## Dependencies

- Hard dependency: DMS-1383 for typed paging/range and backend contract boundaries.
- DMS-1391 independently provides the traditional-paging harness and baseline used by DMS-1392's
  final performance gate. DMS-1385 preserves traditional page-selection output behaviorally and
  textually so that later comparison remains meaningful, but DMS-1391 does not gate this story.
- External foundations: E08 regular/descriptor query planning, E10 live change-version filters,
  E14 row-level authorization planning, and E15 plan-SQL foundations plus plan-contract and
  deterministic-binding artifacts. This story extends the E15-owned `PageDocumentIdSqlCompiler`
  output and plan contract, whose canonicalized/golden output must stay stable for both dialects.
- Blocks execution in DMS-1386 and DMS-1387.
- DMS-1392 performs the final performance gate after implementation; it does not create a cycle.

## Implementation Scope

- Extend `PageDocumentIdQuerySpec` and `PageDocumentIdSqlCompiler`, already shared by the existing
  regular-resource and descriptor page planners, with explicit cursor-bound/page-size and
  partition-count/minimum-size parameter roles and an unpaged partition candidate form.
- Share resource-filter and live change-version planning between the GET-many and `/partitions`
  consumers in the backend: both page keyset planners expose one candidate entry point, so any
  consumer supplying the same preprocessed filters, change-version window, and authorization receives
  identical predicates, parameter names, and bound values.
- Core-side request validation is not shared here. There is no `/partitions` request pipeline to share
  with until DMS-1387 builds one, and `PartitionRequestValidator` deliberately leaves resource-property
  filters and change-version parameters to that caller. Change-version parameters are already parsed by
  the standalone `ChangeVersionParameterValidator` that the partitions pipeline can call directly;
  resource-filter parsing still lives inside `ValidateQueryMiddleware` and must be extracted rather
  than duplicated when that pipeline arrives. That extraction is assigned to DMS-1387.
- Preserve regular-resource root-table behavior and descriptor `dms.Descriptor` plus
  `ResourceKeyId` behavior.
- Add explicit test assertions that every consumer and every supported authorization strategy
  yields one row per `DocumentId`. This is test coverage, not a per-request runtime uniqueness
  check.
- Compile PostgreSQL range predicates plus `LIMIT @pageSize` with no offset/count SQL.
- Compile SQL Server range predicates plus `TOP (@pageSize)` with no `OFFSET`/count SQL.
- Preserve existing traditional PostgreSQL `LIMIT/OFFSET` and SQL Server `OFFSET/FETCH`
  page-selection output unchanged. Collection hydration-batch result-set changes belong to DMS-1386
  and are outside this textual gate.
- Supply compiled cursor plans to the DMS-1386 execution story and the DMS-1387 partition story.

## Acceptance Evidence and Test Expectations

- Planner unit tests prove both existing planners construct the extended shared spec and that
  traditional, cursor, and partition consumers receive identical predicates, authorization specs,
  and filter parameter values for the same request.
- Tests cover resource filters, id filters, min/max change version, unified aliases, empty
  candidates, and descriptors.
- Authorization planner tests cover no-further, relationship, ownership, namespace, and view-based
  strategies where supported and detect duplicate candidate ids.
- Normalized plan-contract tests lock deterministic parameter ordering.
- Provider SQL-golden tests assert exact range predicates, ordering, size syntax, and parameter
  roles.
- Negative assertions prove cursor plans contain no offset, row-number skip, or total-count SQL.
- Existing traditional page-selection SQL goldens remain unchanged and demonstrate no semantic
  regression.
- Edge cases cover inverted and extreme `Int64` ranges and page sizes 0, 1, and maximum.
- Focused PostgreSQL and real SQL Server integration probes prove the generated SQL executes.

## Cross-Provider and Authorization Responsibilities

- Candidate semantics and parameter roles are shared by PostgreSQL and SQL Server, and both
  providers return the same ordered ids for equivalently seeded data.
- Authorization is compiled into the candidate relation before any cursor range, row numbering,
  count, or partition sizing is applied.
- Provider compilers must place range predicates alongside, not instead of, all candidate
  authorization predicates.

## Explicit Exclusions / Not Assigned

- Keyset hydration/output, descriptor boundary propagation, and HTTP headers belong to DMS-1386.
- Partition window SQL and endpoint execution belong to DMS-1387.
- Full plan and latency acceptance belongs to DMS-1392.
