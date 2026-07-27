---
jira: TBD
jira_url: TBD
---

# Story: Pre-resolve `ReadChanges` Authorization Subjects and Emit Per-Resource Tracked-Change Indexes

## Description

Spike DMS-1185 found that the per-resource tracked-change auth indexes (EdOrg securable columns, person `Old*_DocumentId` columns, namespace columns) are plan hazards on PostgreSQL as long as the `ReadChanges` predicate is `c."OldX" IN (SELECT ... FROM <UNION view> ...)`.
PostgreSQL estimates the `*IncludingDeletes` views' deduplicated output at the default 200 distinct rows (measured actuals 20k-80k); with the indexes present, the resulting misestimates flip plans to per-row nested loops (18x regression on `/keyChanges` at 10M tombstones) and join-filter anti-joins (~4x on narrow-window `/deletes`).
The Tier-1 indexes were selected anyway because their wins dominate; they ship in `33-tracked-change-index-emission.md`, on which this story depends, and carry one known bounded regression (narrow-window `/deletes` on the PA resources) that this story is expected to remove.
The per-resource tier is blocked on fixing the query shape.

This story changes the runtime `ReadChanges` SQL so the planner sees real subject-set cardinalities, then emits the per-resource indexes that the fixed shape makes safe and effective.
The leading candidate is resolving the claim's subject sets before composing the main query (person `DocumentId` sets and hierarchy-expanded EdOrg sets), binding them as parameters; alternatives (lateral/EXISTS restructuring, statistics on the view inputs) may be evaluated, but the chosen design must be recorded in `change-queries.md` and must handle district-scale subject sets (tens of thousands of ids) on both dialects.

## Acceptance Criteria

- Decide and document (in `change-queries.md` § Authorization) the query-shape mechanism that exposes subject-set cardinalities to the planner, including how district-scale sets (50k+ subjects) bind on PostgreSQL (arrays) and SQL Server (TVP thresholds), and how the `KeyChanges are always authorized based on the old values` peculiarity is preserved.
- The design must be transactionally consistent or fail closed: today authorization and data retrieval execute as a single command (`RelationalChangeQueryRepository`), so any phase split must guarantee that subject resolution and the main query observe one consistent snapshot (or an equivalent fail-closed contract) on both providers. Document the chosen consistency semantics and their interplay with the preserved peculiarities (person subjects retain access by design via the `*IncludingDeletes` views; the EdOrg hierarchy is current-only and is the freshness-sensitive input), and cover concurrent-revocation behavior with tests on both providers.
- `/deletes` and `/keyChanges` authorization predicates use the new shape for relationship strategies on both dialects; `NamespaceBased` and `NoFurtherAuthorizationRequired` are unchanged.
- Functional authorization correctness is covered through the new path on both providers: all six supported strategy shapes (including hierarchy direction for `RelationshipsWithEdOrgsOnlyInverted`), `keyChanges` old-value authorization, AND-within-strategy / OR-across-strategies / namespace-AND composition, empty subject sets failing closed (no rows, not a 500 and not fail-open), paging and `totalCount` ordering unchanged, and PostgreSQL-array vs SQL Server scalar/TVP parameterization boundary cases.
- Extend `DeriveTrackedChangeIndexInventoryPass` with the per-resource rules anticipated in `change-queries.md` § "Per-resource securable-element indexes are deferred": one `(Old<column>)` `Authorization` index per value column with the securable-element origin flag and `Role = Scalar` (excluding the `SharedDescriptor` table), and one per `Role = PersonDocumentId` column; `(table, leading column)` dedupe against the Tier-1 covering entries.
- Namespace-column indexes are emitted only in coordination with the namespace operator-class story; if that story has not landed, namespace columns stay unindexed and the exclusion is asserted in tests.
- Re-run the DMS-1185 A/B benchmark shapes on PostgreSQL at 10M tombstones: no regression on full-window `/keyChanges`, narrow-window `/deletes`, or full-window `/deletes` with district claims; wins retained on selective claims and view evaluation.
- Run the equivalent A/B verification on SQL Server and record the results.
- Unit tests extended for the new derivation rules (securable scalar, person join and self-identity person columns, shared-descriptor exclusion, dedupe).
- `RelationalMappingVersion` bump and golden regeneration for the new index DDL and manifests.
