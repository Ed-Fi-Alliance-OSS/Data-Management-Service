# DMS Reference Table Insert Performance: Findings and Proposed Roadmap

## Executive Summary

- Insert performance issues in the `Reference` table are primarily driven by write amplification (delete-then-insert churn), redundant foreign key checks, large and fragmented indexes, and an under-partitioned layout for the workload’s scale.
- Low-risk, high-impact actions: tune autovacuum per partition, rebuild indexes with a lower fillfactor, and make the Alias foreign key deferrable for batch inserts. These reduce bloat and page splits, stabilizing write latency.
- Moderate changes with larger gains: simplify the `Reference` schema by dropping referenced-document columns/FK and their index; update the insert path to avoid a per-row join; increase partition count to 32–64. These reduce per-insert work and index width.
- Optional, heavier changes: store `AliasId` instead of a UUID in `Reference`, consider two‑level partitioning, or split write and read paths. These deliver additional space and write savings at higher migration complexity.
- Recommended phased plan: Measure → Quick Wins → Reduce Write Amplification → Partition Expansion → Optional Compaction.

## Background and Scope

This report reevaluates the three-table storage design used by the Data Management Service (DMS) with a focus on insert performance into the `Reference` table. It merges two internal analyses and summarizes actions for project management and leadership.

- Platform: PostgreSQL 16
- Core tables: `Document`, `Alias`, `Reference` (all partitioned)
- Insert path: application calls `dms.InsertReferences` per upsert, which deletes existing rows and bulk-inserts new ones, joining to `Alias` to backfill referenced-document columns.
- Current partition counts: `Document` 16, `Alias` 16, `Reference` 16 (original design targeted 64 for `Reference`).
- Source references:
  - Design overview: `/home/brad/work/dms-root/Project-Tanager/docs/DMS/PRIMARY-DATA-STORAGE/README.md`
  - Deployed DDL: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts`
  - Insert/query code path: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs`

## Observed Symptoms

- Insert latency into `Reference` grows over time, with increased WAL generation and dead tuples.
- Indexes involving UUID keys fragment quickly (random distribution), causing page splits and buffer churn under concurrency.
- Reverse-lookup index on referenced document is particularly write-heavy and large.

## Likely Root Causes

- Delete-then-insert pattern: Every upsert deletes prior references and reinserts, creating dead tuples and index churn; if autovacuum thresholds are not tuned per partition, bloat accumulates and slows new inserts.
- Redundant foreign key checks: `Reference` validates three FKs (parent `Document`, referenced `Document`, referenced `Alias`). The referenced `Document` FK is redundant because `Alias` already FKs to `Document`.
- Join during inserts: `InsertReferences` LEFT JOINs to `Alias` only to populate referenced-document id/partition columns; this adds CPU and I/O on the hot path and forces an extra FK/index to maintain.
- Under-partitioning: With only 16 partitions, per-partition tables and indexes are large, concentrating contention and increasing split/bloat rates.
- Index maintenance overhead: Four index structures are updated per insert (PK plus three additional indexes), several on wide/random keys.

## Options and Proposed Solutions

### Immediate Operational Tunings (Low Risk, High ROI)

- Autovacuum per partition: Lower `autovacuum_vacuum_scale_factor` (e.g., 0.01–0.05) and increase cost limits on `Reference` partitions to keep up with churn.
- Index fillfactor: Rebuild `Reference` indexes with a lower `FILLFACTOR` (e.g., 80–90) to reduce page splits during sustained inserts.
- Deferrable FK to Alias: Make `FK_Reference_ReferencedAlias` `DEFERRABLE INITIALLY DEFERRED` to batch FK checks at commit in larger transactions (validate in staging).
- Optional session tuning for bulk loads: Use `synchronous_commit=off` for controlled bulk-write sessions if acceptable for durability.

### Schema Simplifications (Moderate Effort, Larger Gains)

- Drop referenced-document columns and redundant FK:
  - Remove `ReferencedDocumentId` and `ReferencedDocumentPartitionKey` from `Reference`.
  - Drop the FK to the referenced `Document` and its supporting index.
  - Update `InsertReferences` to stop joining to `Alias`; insert only parent and referential keys.
  - Update the limited read paths to join `Reference → Alias → Document` when needed.
- Increase `Reference` partitions from 16 → 32 or 64:
  - Produces smaller per-partition indexes, better insert locality, and lower contention.
  - Requires migration to a new partitioned table and a data move/swap in a maintenance window.

### Heavier Alternatives (Optional, Highest Savings/Complexity)

- Alias-ID bridge: Store `(ReferentialPartitionKey, AliasId)` in `Reference` instead of `(ReferentialPartitionKey, ReferentialId)` (UUID). Saves ~8 bytes/row and narrows indexes while keeping FK validation via `Alias`.
- Two-level partitioning: Partition by parent partition key and subpartition by referential partition key (e.g., 16×16 = 256). Improves pruning and parallel maintenance at the cost of operational complexity.
- Split write vs read paths: Keep a lean, write-optimized `Reference` and maintain an optional reverse-lookup table asynchronously (trigger/Kafka) if read-side reverse lookups are frequent and latency-sensitive.

## Impact and Tradeoffs

- Expected impact: Fewer per-insert index updates and FK checks; reduced random I/O and bloat; smaller indexes; improved insert throughput and more stable latency.
- Tradeoffs: Some read paths will add a join (via `Alias`) after dropping referenced-document columns. These reads are infrequent today; monitor and optimize if they grow.
- Migration complexity: Partition expansion and column/FK changes require planned windows and rollback plans; use staging validation first.

## Phased Roadmap

1. Phase 0 — Measure
   - Enable/collect query stats; quantify partition/index sizes and bloat; measure `dms.InsertReferences` latency and autovacuum cadence per partition.
2. Phase 1 — Quick Wins (Operational)
   - Apply per-partition autovacuum reloptions; rebuild indexes with tuned fillfactor; test making the Alias FK deferrable; optionally apply `synchronous_commit=off` in controlled bulk sessions.
3. Phase 2 — Reduce Write Amplification (Schema)
   - Drop referenced-document columns/FK/index; simplify `InsertReferences`; adjust read queries to join via `Alias → Document`. Validate throughput/latency on staging.
4. Phase 3 — Partition Expansion
   - Rebuild `Reference` with 32–64 partitions; migrate data with `INSERT ... SELECT`; swap tables in a maintenance window; rebaseline autovacuum and performance.
5. Phase 4 — Optional Compaction
   - Switch from UUID referential key to `AliasId` in `Reference` for further heap/index size reductions. Reassess read/query plans.

## Success Criteria (KPIs)

- Higher insert throughput and lower variance for `dms.InsertReferences`.
- Sustained low dead-tuple ratios per partition; reduced bloat levels.
- Lower index split rates and stable cache hit ratios; decreased WAL per batch.
- No material regression in the small set of reverse-lookup reads.

## Effort, Dependencies, and Ownership

- Skills: PostgreSQL 16 operations and DDL migrations; application updates in `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs` for insert and read paths.
- Ops prerequisites: Ability to enable/confirm `pg_stat_statements`/`pgstattuple`, schedule index rebuilds and partition migrations, and conduct maintenance windows.
- Environments: Staging environment with representative data to validate changes and benchmark before production rollout.

## Decision Requests

- Approve Phase 0–2 now to address immediate performance risks while preserving API behavior.
- Allocate a maintenance window for Phase 3 (partition expansion) after Phase 2 validation.
- Defer Phase 4 (optional compaction) until post-Phase 3 results are reviewed.

## Appendix A — Storage Outlook (Order-of-Magnitude)

- Current `Reference` row payload is roughly 60–70 bytes (excluding indexes). With four indexes, total storage at 500M rows is plausibly 70–100+ GB due to index width and bloat.
- Dropping referenced-document columns and the reverse-lookup index removes a write-heavy index and narrows rows, saving tens of GB at large scale.
- Moving from UUID to `AliasId` for the referential key saves an additional ~8 bytes/row and narrows related indexes.

