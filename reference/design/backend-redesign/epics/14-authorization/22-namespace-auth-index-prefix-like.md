---
jira: TBD
jira_url: TBD
---

# Story: Make Namespace Authorization Indexes Serve Prefix `LIKE` on PostgreSQL

## Description

Spike DMS-1185 verified that under non-C PostgreSQL collations, a plain B-tree never uses a prefix `LIKE` predicate as an index boundary condition.
The pinned `postgres:16.8-alpine` image initializes databases with `en_US.utf8`, so the live-side `IX_*_Namespace_Auth` indexes emitted by `DeriveAuthorizationIndexInventoryPass` execute `NamespaceBased` authorization as a full-index scan plus filter today (measured: `IX_Descriptor_Namespace_Auth` visits all entries and filters out 99.6%), not as a range seek.
An index declared with `varchar_pattern_ops` produces true boundary conditions (`~>=~` / `~<~`) for a prefix pattern the planner can see as a constant.
The operator class alone is not sufficient for the production predicate, and this story must change the predicate shape together with the index definition: PostgreSQL authorization emits a parameterized `LIKE ANY(@array)` (`NamespacePrefixSqlHelper` PgsqlArray shape, mirrored by `TrackedChangeAuthorizationSqlEmitter`), and PostgreSQL does not decompose that scalar-array predicate into per-pattern boundary conditions regardless of operator class.
Plan caching is a second constraint: the prefix-to-range rewrite for a parameterized pattern happens only when the planner sees the value (custom plans); server-side prepared statements switch to generic plans after a few executions and lose the boundary conditions, so the chosen design must produce seeks under generic plans.
SQL Server seeks parameterized prefix `LIKE` natively (dynamic range seeks) and needs no change.

This predates DMS-1185 and affects live-resource `NamespaceBased` authorization generally, so the Authorization epic owns the live predicate/index mechanism.
The dedicated Change Queries tracked-namespace story consumes that decision and owns any tracked predicate adaptation plus tracked-change index emission.

## Acceptance Criteria

- Decide the PostgreSQL mechanism as a predicate-shape plus index-definition pair that produces index seeks under generic plans; candidates include per-prefix predicates over a `varchar_pattern_ops` index (with the plan-caching constraint addressed), explicit pattern-ops range operators (`~>=~` / `~<~`) over client-computed bounds, or a documented C-collation deployment requirement; record the decision and rationale in `auth.md` (normative home for authorization indexes) with a cross-reference from `change-queries.md`.
- Extend the index inventory contract so an entry can carry a PostgreSQL operator class (dialect-specific, ignored by the SQL Server emitter), and render it in `PgsqlDialect.CreateIndexIfNotExists`; enumerate the manifest ripple and update the `DbIndexInfo` contract documentation in `compiled-mapping-set.md`.
- Make index coverage predicate- and operator-class-aware. On PostgreSQL, an ordinary PK/UK or B-tree index on the same leading Namespace column must not suppress a required pattern-capable index; an existing index with the selected compatible operator class may deduplicate it. SQL Server retains its existing leading-column coverage rule.
- `DeriveAuthorizationIndexInventoryPass` emits live namespace securable-element indexes with the chosen operator class on PostgreSQL; non-namespace indexes are unchanged.
- Unit coverage includes a PostgreSQL PK/UK collision fixture proving that ordinary equality coverage still emits the pattern-capable namespace index, a matching-operator-class fixture proving true equivalent coverage deduplicates it, and a SQL Server fixture proving its existing coverage rule is unchanged.
- Tracked-change predicate adaptation and namespace indexes are out of scope here and owned only by `../10-update-tracking-change-queries/37-tracked-namespace-index-emission.md`, which depends on this story and consumes the selected mechanism.
- Functional authorization parity and fail-closed coverage for the reshaped live predicate on both dialects: multiple configured prefixes, prefixes containing `%`, `_`, and backslash (escaping semantics preserved), null and empty namespace values remain unauthorized, case and collation boundary values, and both resource and descriptor `NamespaceBased` authorization.
- Array-nested (`[*]`) namespace securables retain their existing live-side behavior; this story does not change the tracked-change root-scope boundary.
- EXPLAIN-based integration fixture on PostgreSQL asserting the `NamespaceBased` predicate produces index boundary conditions rather than a filter over the full index, exercised under generic-plan conditions (or with the plan mode explicitly pinned by the chosen design) so the assertion reflects steady-state prepared-statement behavior.
- Upgrade consideration recorded: already-provisioned databases carry the old index definition; the `RelationalMappingVersion` bump and provisioning path must replace them (or the decision to leave existing deployments as-is must be explicit).
- Golden regeneration for changed index DDL and manifests on PostgreSQL.
