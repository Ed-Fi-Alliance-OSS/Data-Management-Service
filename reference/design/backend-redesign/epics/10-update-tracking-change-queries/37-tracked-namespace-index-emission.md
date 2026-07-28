---
jira: TBD
jira_url: TBD
---

# Story: Adapt Tracked Namespace Authorization and Emit Tracked Namespace Indexes

## Description

Authorization Story 22 selects and implements the live PostgreSQL Namespace predicate/index mechanism.
This story consumes that decision for tracked-change queries, explicitly owns any `TrackedChangeAuthorizationSqlEmitter` adaptation needed to make the tracked predicate seekable, and emits per-resource tracked namespace indexes.
It depends on Story 33 and Authorization Story 22, but not on the unrelated relationship subject-cardinality rewrite.

## Acceptance Criteria

- Apply the live story's selected PostgreSQL prefix semantics, escaping behavior, generic-plan contract, and operator class to tracked `NamespaceBased` `/deletes` and `/keyChanges` authorization.
- Preserve SQL Server's existing parameterized prefix-`LIKE` behavior unless its isolated checkpoint selects a measured change.
- Extend the tracked-change portion of `DeriveIndexInventoryPass` with one namespace index per representable root tracked namespace column, excluding `SharedDescriptor`, using the selected provider mechanism.
- Classify namespace columns by either `SourceJsonPath` matching a declared namespace path or `CanonicalStorageColumn` matching the root live column to which that path resolves.
- Require a key-unification fixture whose surviving `SourceJsonPath` is an identity path and whose `CanonicalStorageColumn` is the only namespace match.
- Coverage is predicate- and operator-class-aware. On PostgreSQL, an ordinary equality PK/UK or B-tree must not suppress the required pattern-capable index; a genuinely compatible operator-class index may deduplicate it. SQL Server retains leading-column coverage.
- Array-nested namespace securables retain the existing tracked root-scope security-configuration behavior.
- Enumerate every candidate namespace index in the harness artifact (roughly 30 root tracked namespace columns at current DS 5.2 scale). This story has a single schema-derivable emission predicate (one namespace index per representable root tracked namespace column, excluding `SharedDescriptor`), so adoption is all-or-nothing and every matching table receives its index deterministically, including extension-project resources and tables absent from the benchmark schema; `DeriveIndexInventoryPass` sees only the derived model and never deployment cardinality.
- Stratify the benchmarked candidates by tracked-table cardinality tier and name at least one measured representative table per stratum, including each stratum's largest-cardinality table. Strata and representatives are recorded in the artifact before measurement; they exist to prove the predicate safe across the cardinality range and never participate in the emission rule.
- Run provider A/B tests with the full candidate overlay applied (with a single predicate this is also its isolated overlay) for resource and descriptor `NamespaceBased` changes, multiple prefixes, escaping characters, null/empty values, case/collation boundaries, and PostgreSQL generic plans. For per-resource tracked tables, cover every representative table's `/deletes` and `/keyChanges` at full and narrow `ChangeVersion` windows; require an index seek/prefix-boundary condition, a 20% median elapsed-time or buffer/logical-read improvement on at least one representative shape in the largest-cardinality stratum, and no measured shape in any stratum above the 1.20 regression ceiling. `SharedDescriptor` remains excluded from namespace-index emission, so its cases require functional parity and no regression above 1.20, not a namespace prefix-boundary assertion.
- A failed gate in any stratum on either provider blocks the predicate on both providers pending redesign; nothing is emitted from a blocked or unmeasured predicate.
- Apply Story 33's warm-up, repetition, ordering, and noise controls to bulk tombstone/key-change writes on each representative table: each measured table's bulk-write median elapsed-time ratio must stay at or below 1.85 on PostgreSQL and 2.00 on SQL Server.
- Verify storage exhaustively rather than by representative: after building the full adopted index set at implementation scale, every affected tracked table must keep its added namespace-index storage at or below 40% of that table's size on PostgreSQL and 50% on SQL Server.
- Regenerate affected DDL/manifests/goldens and cover both providers with functional and generated-DDL integration tests; the `RelationalMappingVersion` bump this physical change requires is tracked by its own open ticket rather than owned here, and that ticket must land no later than the first story that ships a physical index change.
- After emission, verify that the exact generated DDL is semantically identical to the pinned overlay over the benchmark deployment's tables (per-table key columns, PostgreSQL operator classes, deduplication outcomes, and shortened identifiers); tables absent from the benchmark schema are covered by the deterministic emission predicate rather than the overlay comparison. Then rerun the representative read and write matrix against that generated DDL. A mismatch or failed post-implementation gate blocks acceptance rather than being described as run-to-run noise.
