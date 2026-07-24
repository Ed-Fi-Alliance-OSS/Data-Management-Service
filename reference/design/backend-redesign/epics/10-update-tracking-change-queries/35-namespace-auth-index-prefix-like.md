---
jira: TBD
jira_url: TBD
---

# Story: Make Namespace Authorization Indexes Serve Prefix `LIKE` on PostgreSQL

## Description

Spike DMS-1185 verified that under non-C PostgreSQL collations, a plain B-tree never uses a prefix `LIKE` predicate as an index boundary condition.
The pinned `postgres:16.8-alpine` image initializes databases with `en_US.utf8`, so the live-side `IX_*_Namespace_Auth` indexes emitted by `DeriveAuthorizationIndexInventoryPass` execute `NamespaceBased` authorization as a full-index scan plus filter today (measured: `IX_Descriptor_Namespace_Auth` visits all entries and filters out 99.6%), not as a range seek.
An index declared with `varchar_pattern_ops` produces true boundary conditions (`~>=~` / `~<~`).
SQL Server seeks prefix `LIKE` natively and needs no change.

This predates DMS-1185 and affects live-resource `NamespaceBased` authorization generally; it also gates the tracked-change namespace indexes (see the subject-preresolution story, which keeps namespace columns unindexed until this lands).

## Acceptance Criteria

- Decide the PostgreSQL mechanism: `varchar_pattern_ops` on namespace authorization indexes (leading candidate) versus a documented C-collation deployment requirement; record the decision and rationale in `auth.md` (normative home for authorization indexes) with a cross-reference from `change-queries.md`.
- Extend the index inventory contract so an entry can carry a PostgreSQL operator class (dialect-specific, ignored by the SQL Server emitter), and render it in `PgsqlDialect.CreateIndexIfNotExists`; enumerate the manifest ripple.
- `DeriveAuthorizationIndexInventoryPass` emits namespace securable-element indexes with the chosen operator class on PostgreSQL; non-namespace indexes are unchanged.
- EXPLAIN-based integration fixture on PostgreSQL asserting the `NamespaceBased` predicate produces index boundary conditions rather than a filter over the full index.
- Upgrade consideration recorded: already-provisioned databases carry the old index definition; the `RelationalMappingVersion` bump and provisioning path must replace them (or the decision to leave existing deployments as-is must be explicit).
- Golden regeneration for changed index DDL and manifests on PostgreSQL.
