# Ed‑Fi DMS Primary Data Storage — Reference Table Insert Performance Reevaluation

This document reevaluates the three‑table relational design (Document, Alias, Reference) with a focus on slow inserts into `dms.Reference`. It summarizes the current implementation, identifies the primary causes of write amplification, provides a measurement plan on PostgreSQL 16, and recommends concrete mitigations (no‑migration first, followed by optional schema refinements).

## Executive Summary

- Insert latency is primarily driven by write amplification in `dms.Reference`: delete‑then‑insert churn, three foreign key validations, and maintenance of multiple B‑tree indexes per row.
- The current hash partition count for `Reference` is 16, concentrating writes and enlarging per‑partition indexes for a write‑hot table.
- Immediate, low‑risk improvements (no data migrations) include: more aggressive per‑partition autovacuum, lowering index fillfactor, and optionally making the Alias FK deferrable for bulk workloads. These actions reduce bloat and page splits without changing table shape.
- Larger gains come from simplifying Reference to “parent → alias” only (drop referenced‑document columns + FK) and increasing partition breadth; both require planned schema changes and small read‑path updates.

## Current State (Schema and Access Paths)

Tables and partitioning (hash):

- `dms.Document`: 16 partitions on `DocumentPartitionKey` (`PRIMARY KEY (DocumentPartitionKey, Id)`). Script: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0001_Create_Document_Table.sql:1`.
- `dms.Alias`: 16 partitions on `ReferentialPartitionKey` (`PRIMARY KEY (ReferentialPartitionKey, Id)`). Script: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0002_Create_Alias_Table.sql:1`.
- `dms.Reference`: 16 partitions on `ParentDocumentPartitionKey` (`PRIMARY KEY (ParentDocumentPartitionKey, Id)`). Script: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql:1`.

Keys and indexes (core):

- `Alias`:
  - Unique: `(ReferentialPartitionKey, ReferentialId)` for identity/lookup.
  - FK to `Document` on `(DocumentPartitionKey, DocumentId)` with `ON DELETE CASCADE`.
- `Reference`:
  - PK: `(ParentDocumentPartitionKey, Id)`.
  - Index on `(ParentDocumentPartitionKey, ParentDocumentId)` — delete/update by parent.
  - Index on `(ReferencedDocumentPartitionKey, ReferencedDocumentId)` — reverse lookups and delete checks.
  - FK to parent `Document` `(ParentDocumentPartitionKey, ParentDocumentId)` with `ON DELETE CASCADE`.
  - FK to referenced `Document` `(ReferencedDocumentPartitionKey, ReferencedDocumentId)` (nullable).
  - FK to `Alias` `(ReferentialPartitionKey, ReferentialId)` with `ON DELETE RESTRICT, ON UPDATE CASCADE`.

Insert path:

- Application issues a single call per upsert: `SELECT dms.InsertReferences($1,$2,$3,$4)`. Wrapper: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:703`.
- Procedure deletes existing rows for the given parent(s), then inserts from an `unnest(...)` set with a `LEFT JOIN` to `dms.Alias` to populate `ReferencedDocumentId`/`ReferencedDocumentPartitionKey`. Script: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql:1`.
- On `FK_Reference_ReferencedAlias` violations, the function returns the offending referential IDs to the caller for precise error reporting.

Read paths relying on `Reference.ReferencedDocumentId/PartitionKey`:

- Referencing resource names by `DocumentUuid`: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:784`.
- Referencing documents by `(DocumentId, DocumentPartitionKey)`: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:826`.

Design target for partition breadth:

- The original guidance targets 16 partitions for `Document`/`Alias` and 64 for `Reference` to spread write load for the hottest table (`/home/brad/work/dms-root/Project-Tanager/docs/DMS/PRIMARY-DATA-STORAGE/README.md:165`). The deployed DDL uses 16 for `Reference`.

## Primary Contributors to Insert Slowness

- Delete‑then‑insert update pattern (per upsert):
  - `dms.InsertReferences` deletes all existing rows for the parent document(s) and reinserts. This generates dead tuples in both heap and indexes and increases WAL volume. Without aggressive per‑partition autovacuum/analyze, bloat accumulates and slows subsequent inserts and scans.

- Multiple foreign key validations per row:
  - `Reference` validates three FKs: parent `Document`, referenced `Document`, and `Alias` referential identity. The referenced‑`Document` FK is functionally redundant when the Alias FK is present because `Alias → Document` has cascade delete and `Reference → Alias` restricts delete. This extra FK adds a per‑row check and requires an additional index.

- Join during insert:
  - The `LEFT JOIN` to `Alias` is only used to fill `ReferencedDocumentId/PartitionKey`. Retaining these columns requires both the join and the referenced‑`Document` FK + index maintenance.

- Write‑heavy, randomly distributed B‑tree updates:
  - Each insert maintains: PK, parent index, referenced‑document index, and referential index (for the Alias FK). UUID‑based keys yield randomized inserts that fragment B‑trees and increase page splits unless fillfactor leaves sufficient free space.

- Partition breadth:
  - With only 16 partitions on the hottest table, per‑partition relation and index sizes are larger, driving more random I/O, higher contention on relation extension, and longer vacuum/maintenance windows.

## What to Measure (PostgreSQL 16)

- Partition sizes and distribution
  - `SELECT child.relname AS partition, child_oid::regclass, pg_total_relation_size(child_oid) AS bytes
     FROM pg_inherits i
     JOIN pg_class parent ON i.inhparent = parent.oid
     JOIN pg_class child  ON i.inhrelid  = child.oid
     WHERE parent.relnamespace = 'dms'::regnamespace AND parent.relname = 'reference'
     ORDER BY partition;`

- Index sizes and usage
  - `SELECT schemaname, relname, indexrelname, pg_relation_size(indexrelid) AS bytes
     FROM pg_stat_user_indexes WHERE schemaname='dms' AND relname ILIKE 'reference%';`
  - `SELECT * FROM pg_stat_user_indexes WHERE schemaname='dms' AND relname ILIKE 'reference%';`

- Bloat (requires `pgstattuple`)
  - `SELECT * FROM pgstattuple_approx('dms.reference_00');` (repeat per partition and key indexes).

- Autovacuum activity and dead tuples
  - `SELECT relname, n_live_tup, n_dead_tup, last_autovacuum, vacuum_count
     FROM pg_stat_all_tables WHERE schemaname='dms' AND relname ILIKE 'reference%';`
  - `SELECT * FROM pg_stat_progress_vacuum;` (during activity).

- Insert latency and contention
  - `pg_stat_statements` for `dms.InsertReferences` to capture mean/95p latency.
  - `SELECT * FROM pg_locks WHERE relation::regclass IN ('dms.reference','dms.alias');` during load.
  - Monitor WAL/checkpoints (e.g., `pg_stat_bgwriter`, `pg_stat_io`).

- Index health (optional)
  - `amcheck` and `pageinspect` to sample leaf fullness and split frequency on `Reference` indexes.

## Immediate Mitigations (No Data Migrations)

These actions are compatible with the current schema and can be applied per partition.

- Aggressive autovacuum for `Reference` partitions
  - Lower `autovacuum_vacuum_scale_factor` to ~0.01–0.05 (and adjust `autovacuum_analyze_scale_factor`) on each `dms.reference_*` child to keep up with delete‑then‑insert churn.
  - Consider higher `autovacuum_vacuum_cost_limit` and tuned `autovacuum_vacuum_cost_delay` for sustained progress.
  - Example per partition: `ALTER TABLE dms.reference_00 SET (autovacuum_vacuum_scale_factor=0.02, autovacuum_analyze_scale_factor=0.02);`

- Lower fillfactor on `Reference` B‑tree indexes
  - Reduce to 80–90 for write‑hot indexes, especially the parent and referenced‑document indexes, to leave headroom and reduce page splits.
  - Apply via `ALTER INDEX ... SET (fillfactor=85);` followed by `REINDEX [CONCURRENTLY]` per partitioned index.

- Consider deferring the Alias FK checks for bulk upserts
  - Recreate `FK_Reference_ReferencedAlias` as `DEFERRABLE INITIALLY DEFERRED` to batch validations until commit where workloads use large transactions. Validate behavior in staging.

- Session‑level durability tradeoff for bulk operations (optional)
  - For write‑heavy, redo‑tolerant sessions, use `SET LOCAL synchronous_commit=off;` during bulk upserts to reduce flush latency. Not recommended for general interactive traffic.

## Schema Refinements (Require Planned Changes)

These changes provide larger write‑path improvements but require DDL updates and limited read‑path adjustments.

1) Drop referenced‑document columns and FK from `Reference`

- Columns to remove: `ReferencedDocumentId`, `ReferencedDocumentPartitionKey`.
- Drop FK: `FK_Reference_ReferencedDocument`.
- Drop index: `(ReferencedDocumentPartitionKey, ReferencedDocumentId)`.
- Update `dms.InsertReferences` to stop joining to `Alias`; insert only `(ParentDocumentId, ParentDocumentPartitionKey, ReferentialPartitionKey, ReferentialId)`.
- Update read paths to resolve reverse lookups via `Reference → Alias → Document`:
  - Replace queries at `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:784` and `:826` accordingly.
- Net effect: remove one FK validation, one write‑heavy index, and the join from the hot insert path.

2) Store `AliasId` instead of `ReferentialId` in `Reference`

- Replace `(ReferentialPartitionKey, ReferentialId)` with `(ReferentialPartitionKey, AliasId)` referencing the `Alias` PK. Narrower keys reduce row and index width and speed FK lookups.
- Requires minor changes to insert and read logic and an index on `(ReferentialPartitionKey, AliasId)`.

3) Increase partition breadth for `Reference`

- Expand from 16 to 32/64 partitions to lower per‑partition index sizes, improve cache locality, and reduce contention. Requires creating a new partitioned table, migrating data with `INSERT ... SELECT`, and swapping in a maintenance window.

4) Two‑level partitioning (optional)

- Partition by `ParentDocumentPartitionKey`, subpartition by `ReferentialPartitionKey` to improve pruning for both parent‑ and alias‑driven operations. Higher operational overhead; apply only if both directions are hot.

5) Split write/read responsibilities (optional)

- Keep a minimal, write‑optimized `Reference` (parent + alias FK only), and maintain a separate, read‑optimized reverse‑lookup table by triggers or streaming. Reduces write amplification at the cost of double writes.

## Roadmap

Phase 0 — Measure (one week)

- Enable `pg_stat_statements` and gather baselines for `dms.InsertReferences` latency and call counts.
- Collect per‑partition sizes, index sizes, and `pgstattuple_approx` bloat estimates.
- Track `n_dead_tup`, `vacuum_count`, and `last_autovacuum` across `dms.reference_*` partitions.

Phase 1 — No‑migration mitigations (one to two weeks)

- Apply per‑partition autovacuum settings and monitor dead/live ratios over several cycles.
- Lower fillfactor and reindex `Reference` indexes.
- Trial `DEFERRABLE INITIALLY DEFERRED` Alias FK on a staging environment and validate correctness/performance.
- Optionally apply `synchronous_commit=off` in bulk ingest sessions.

Phase 2 — Reduce write amplification (planned DDL)

- Remove referenced‑document columns, FK, and index; update the insert function and two read queries to go via `Alias`. Validate with representative datasets.

Phase 3 — Partition expansion (planned DDL + data move)

- Rebuild `Reference` with 32–64 partitions. Migrate data and swap in a maintenance window; verify pruning and index sizes.

Phase 4 — Optional compaction and split (planned DDL)

- Switch from `(ReferentialPartitionKey, ReferentialId)` to `(ReferentialPartitionKey, AliasId)` and, if necessary, split write/read tables.

## Risks and Compatibility Notes

- The referenced‑`Document` FK is only safe to drop if `FK_Reference_ReferencedAlias` remains enforced; otherwise, deletes on `Document` may not be blocked when a referential alias still exists.
- Dropping referenced‑document columns requires updating reverse‑lookup queries at `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:784` and `:826` to join via `Alias`.
- Increasing partitions introduces more relations to manage and longer DDL windows; plan operational tooling accordingly.

## Appendix — Pointers and Files

- Document DDL: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0001_Create_Document_Table.sql:1`
- Alias DDL: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0002_Create_Alias_Table.sql:1`
- Reference DDL: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql:1`
- Reference validation FKs: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0015_Create_Reference_Validation_FKs.sql:1`
- InsertReferences procedure: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql:1`
- Application call site for InsertReferences: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:703`
- Reverse‑lookup read queries: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:784`, `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:826`

