---
jira: TBD
jira_url: TBD
---

# Story: Pre-resolve `ReadChanges` Authorization Subjects and Emit Per-Resource Tracked-Change Indexes

## Description

Spike DMS-1185 found that the per-resource tracked-change auth indexes (EdOrg securable columns, person `Old*_DocumentId` columns, namespace columns) are plan hazards on PostgreSQL as long as the `ReadChanges` predicate is `c."OldX" IN (SELECT ... FROM <UNION view> ...)`.
PostgreSQL estimates the `*IncludingDeletes` views' deduplicated output at the default 200 distinct rows (measured actuals 20k-80k); with the indexes present, the resulting misestimates flip plans to per-row nested loops (18x regression on `/keyChanges` at 10M tombstones) and join-filter anti-joins (~4x on narrow-window `/deletes`).
The Tier-1 indexes shipped anyway because their wins dominate, but the per-resource tier is blocked on fixing the query shape.

This story changes the runtime `ReadChanges` SQL so the planner sees real subject-set cardinalities, then emits the per-resource indexes that the fixed shape makes safe and effective.
The leading candidate is resolving the claim's subject sets before composing the main query (person `DocumentId` sets and hierarchy-expanded EdOrg sets), binding them as parameters; alternatives (lateral/EXISTS restructuring, statistics on the view inputs) may be evaluated, but the chosen design must be recorded in `change-queries.md` and must handle district-scale subject sets (tens of thousands of ids) on both dialects.

## Acceptance Criteria

- Decide and document (in `change-queries.md` § Authorization) the query-shape mechanism that exposes subject-set cardinalities to the planner, including how district-scale sets (50k+ subjects) bind on PostgreSQL (arrays) and SQL Server (TVP thresholds), and how the `KeyChanges are always authorized based on the old values` peculiarity is preserved.
- `/deletes` and `/keyChanges` authorization predicates use the new shape for relationship strategies on both dialects; `NamespaceBased` and `NoFurtherAuthorizationRequired` are unchanged.
- Extend `DeriveTrackedChangeIndexInventoryPass` with the per-resource rules anticipated in `change-queries.md` § "Per-resource securable-element indexes are deferred": one `(Old<column>)` `Authorization` index per value column with the securable-element origin flag and `Role = Scalar` (excluding the `SharedDescriptor` table), and one per `Role = PersonDocumentId` column; `(table, leading column)` dedupe against the Tier-1 covering entries.
- Namespace-column indexes are emitted only in coordination with the namespace operator-class story; if that story has not landed, namespace columns stay unindexed and the exclusion is asserted in tests.
- Re-run the DMS-1185 A/B benchmark shapes on PostgreSQL at 10M tombstones: no regression on full-window `/keyChanges`, narrow-window `/deletes`, or full-window `/deletes` with district claims; wins retained on selective claims and view evaluation.
- Run the equivalent A/B verification on SQL Server and record the results.
- Unit tests extended for the new derivation rules (securable scalar, person join and self-identity person columns, shared-descriptor exclusion, dedupe).
- `RelationalMappingVersion` bump and golden regeneration for the new index DDL and manifests.
