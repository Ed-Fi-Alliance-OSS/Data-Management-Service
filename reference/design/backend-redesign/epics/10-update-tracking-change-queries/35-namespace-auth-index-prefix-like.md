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

This predates DMS-1185 and affects live-resource `NamespaceBased` authorization generally; it also gates the tracked-change namespace indexes (see the subject-preresolution story, which keeps namespace columns unindexed until this lands).

## Acceptance Criteria

- Decide the PostgreSQL mechanism as a predicate-shape plus index-definition pair that produces index seeks under generic plans; candidates include per-prefix predicates over a `varchar_pattern_ops` index (with the plan-caching constraint addressed), explicit pattern-ops range operators (`~>=~` / `~<~`) over client-computed bounds, or a documented C-collation deployment requirement; record the decision and rationale in `auth.md` (normative home for authorization indexes) with a cross-reference from `change-queries.md`.
- Extend the index inventory contract so an entry can carry a PostgreSQL operator class (dialect-specific, ignored by the SQL Server emitter), and render it in `PgsqlDialect.CreateIndexIfNotExists`; enumerate the manifest ripple and update the `DbIndexInfo` contract documentation in `compiled-mapping-set.md`.
- `DeriveAuthorizationIndexInventoryPass` emits namespace securable-element indexes with the chosen operator class on PostgreSQL; non-namespace indexes are unchanged.
- Tracked-change namespace indexes are owned here when this story lands after the subject-preresolution story: extend `DeriveTrackedChangeIndexInventoryPass` with the namespace-column rule (the dual-check classification from `change-queries.md` § "Per-resource securable-element indexes are deferred", chosen operator class on PostgreSQL). If this story lands first, the subject-preresolution story emits them under the mechanism decided here; either ordering must leave tracked namespace columns indexed once both stories have landed.
- Functional authorization parity and fail-closed coverage for the reshaped predicate on live and tracked-change paths, on both dialects: multiple configured prefixes, prefixes containing `%`, `_`, and backslash (escaping semantics preserved), null and empty namespace values remain unauthorized, case and collation boundary values, resource and descriptor `NamespaceBased` authorization, and the `/deletes` plus `/keyChanges` tracked shapes.
- Scope boundary preserved: array-nested (`[*]`) namespace securables are root-scoped out of tracked-change tables by design (`change-queries.md` § "Per-resource securable-element indexes are deferred"); coverage asserts live-side behavior for such resources is unchanged by the predicate reshape and the tracked-change fail-closed outcome is preserved.
- EXPLAIN-based integration fixture on PostgreSQL asserting the `NamespaceBased` predicate produces index boundary conditions rather than a filter over the full index, exercised under generic-plan conditions (or with the plan mode explicitly pinned by the chosen design) so the assertion reflects steady-state prepared-statement behavior.
- Upgrade consideration recorded: already-provisioned databases carry the old index definition; the `RelationalMappingVersion` bump and provisioning path must replace them (or the decision to leave existing deployments as-is must be explicit).
- Golden regeneration for changed index DDL and manifests on PostgreSQL.
