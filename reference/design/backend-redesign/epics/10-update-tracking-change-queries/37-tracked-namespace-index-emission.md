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
- Run isolated provider A/B tests for resource and descriptor `NamespaceBased` changes, multiple prefixes, escaping characters, null/empty values, case/collation boundaries, and PostgreSQL generic plans. For per-resource tracked tables, cover `/deletes` and `/keyChanges` at full and narrow `ChangeVersion` windows, and require an index seek/prefix-boundary condition, at least a 20% median elapsed-time or buffer/logical-read improvement on at least one covered per-resource shape, and no measured shape above the 1.20 regression ceiling. `SharedDescriptor` remains excluded from namespace-index emission, so its cases require functional parity and no regression above 1.20, not a namespace prefix-boundary assertion.
- Apply Story 33's warm-up, repetition, ordering, and noise controls to bulk tombstone/key-change writes and storage. PostgreSQL must keep the tier's bulk-write median ratio at or below 1.85 and added index storage at or below 40% of the affected tracked-table size; SQL Server must remain at or below 2.00 and 50%, respectively. A provider failure blocks this provider-neutral tier pending redesign.
- Bump `RelationalMappingVersion`, regenerate affected DDL/manifests/goldens, and cover both providers with functional and generated-DDL integration tests.
