# Alternatives Analysis: Reducing Delete-Then-Insert Churn in `dms.Reference`

This report reviews two database alternatives to the current DELETE-then-INSERT pattern used by `InsertReferences`, with the goal of minimizing churn, dead tuples, WAL volume, and lock contention on the `dms.Reference` table at 20M+ rows.

Sources reviewed:
- `perf-claude/sql/alternatives/differential_update.sql`
- `perf-claude/sql/alternatives/merge_pattern.sql`

---

## Status Quo (DELETE then INSERT)

Behavior
- For a given parent document (or batch of parents), the current implementation deletes all reference rows, then inserts the full new set—even if most references are unchanged.

Observed/expected costs
- High write amplification: unnecessary deletes + inserts on unchanged rows.
- Dead tuple accumulation and bloat; aggressive autovacuum and extra WAL volume.
- Elevated lock contention under repeated updates or concurrent sessions.
- Latency variability; worst on “hot” documents repeatedly updated.

The perf harness (`sql/current/test_current_insert_references.sql`) captures these effects with deterministic fixtures and observability snapshots.

---

## Alternative 1: Differential Update

What it does
- Stage desired references (deduped) for the parents in a CTE.
- Validate `Alias` presence; return invalid referential IDs if missing.
- Delete only rows not present in the staged set for those parents.
- Insert only rows in the staged set that don’t already exist.
- Leaves existing, unchanged rows untouched.

Reference: `perf-claude/sql/alternatives/differential_update.sql`

Pros
- Minimizes churn relative to status quo—no full delete; deletes removed refs only; inserts new refs only.
- Significantly reduces dead tuples and write volume; lowers autovacuum pressure.
- No schema change required; leverages existing indexes. Benefits from a covering parent-side index.
- Particularly effective when updates change a small fraction of references.

Cons / Risks
- Does not refresh existing rows if the `Alias` mapping for a given referential key changes (i.e., target document moves). A small, targeted UPDATE step should be added to keep existing rows in sync when alias targets change.
- Existence checks use `NOT EXISTS`; performance depends on appropriate indexes and can still contend on hot parents.
- Concurrency: still acquires locks/deletes for removed rows; much less invasive than full wipes but not lock-free.

Implementation notes
- Add a covering index to accelerate targeted delete and existence checks:
  ```sql
  -- On the partitioned root; creates per-partition indexes
  CREATE INDEX IF NOT EXISTS ix_reference_covering
  ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId)
  INCLUDE (ReferentialPartitionKey, AliasId, ReferencedDocumentPartitionKey, ReferencedDocumentId);
  ```
- Add a safe refresh step for alias retargets (avoid no-op updates):
  ```sql
  -- Example pattern to refresh only when Alias target changed
  UPDATE dms.Reference r
  SET ReferencedDocumentId = a.DocumentId,
      ReferencedDocumentPartitionKey = a.DocumentPartitionKey
  FROM dms.Alias a
  WHERE r.AliasId = a.Id
    AND r.ReferentialPartitionKey = a.ReferentialPartitionKey
    AND (r.ReferencedDocumentId, r.ReferencedDocumentPartitionKey)
        IS DISTINCT FROM (a.DocumentId, a.DocumentPartitionKey)
    AND (r.ParentDocumentId, r.ParentDocumentPartitionKey) IN (
      SELECT DISTINCT parent_document_id, parent_document_partition_key FROM staged
    );
  ```
- Keep the staged CTE `MATERIALIZED` as written to stabilize planning at larger cardinalities.

Validation
- Use fixtures `single_doc_standard` and `batch_100_mixed` to compare latency, dead tuples, table/index size deltas, and WAL bytes against the baseline.

---

## Alternative 2: MERGE/UPSERT Pattern (INSERT … ON CONFLICT)

What it does
- Introduces a unique constraint on `(ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey)` to enable conflict detection.
- Deletes only rows not present in the new staged set for the parents.
- Performs `INSERT … ON CONFLICT DO UPDATE` to refresh `ReferencedDocument*` fields when the alias mapping changes; unchanged rows upsert without separate existence probes.

Reference: `perf-claude/sql/alternatives/merge_pattern.sql`

Pros
- Correctness on alias retargets is built-in; upsert refreshes target document fields.
- Minimal existence-check overhead; relies on the unique index for fast conflict detection.
- Lowers write amplification vs. status quo; more concurrency-friendly on insert/update paths due to row-level conflicts rather than full-parent wipes.

Cons / Risks
- Requires adding a new unique index/constraint at large scale:
  - On partitioned tables, unique indexes are supported when the partition key is part of the key (it is), but creating the index on the partitioned root cannot be `CONCURRENTLY`. Plan a maintenance window or build per-partition indexes concurrently and then attach.
  - Additional index increases write costs (acceptable tradeoff for correctness and speed).
- As written, `DO UPDATE` runs on every conflict, even if values are unchanged; add a `WHERE` guard to avoid no-op updates.

Implementation notes
- Unique index + constraint (partitioned):
  ```sql
  -- Option A: create on partitioned root (blocks; plan a window)
  CREATE UNIQUE INDEX uk_reference_parent_alias
    ON dms.Reference (ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey);
  ALTER TABLE dms.Reference
    ADD CONSTRAINT uk_reference_parent_alias
    UNIQUE USING INDEX uk_reference_parent_alias;

  -- Option B: per-partition concurrent build, then ATTACH (advanced/operational)
  -- Create partitioned index on root, then for each partition create matching index CONCURRENTLY and ATTACH.
  ```
- Avoid no-op updates during upsert:
  ```sql
  INSERT INTO dms.Reference (...)
  VALUES (...)
  ON CONFLICT (ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey)
  DO UPDATE SET
    ReferencedDocumentId = EXCLUDED.ReferencedDocumentId,
    ReferencedDocumentPartitionKey = EXCLUDED.ReferencedDocumentPartitionKey
  WHERE (dms.Reference.ReferencedDocumentId, dms.Reference.ReferencedDocumentPartitionKey)
        IS DISTINCT FROM (EXCLUDED.ReferencedDocumentId, EXCLUDED.ReferencedDocumentPartitionKey);
  ```
- Keep the targeted delete for removed refs; dead tuples are unavoidable for true removals but far fewer than status quo.
- A covering index (same as Alternative 1) still helps with selective parent operations and diagnostics.

Validation
- Use the supplied scenario which flips between original and mutated payloads across 100 iterations and records latency histogram, dead tuples, and size stats. Compare to the baseline and Differential.

---

## Head-to-Head Comparison

| Aspect | Status Quo (DELETE+INSERT) | Differential Update | MERGE/UPSERT |
|---|---|---|---|
| Write amplification | Highest | Low (delete removed, insert new) | Low–moderate (delete removed, upsert existing) |
| Dead tuples/bloat | Highest | Much lower | Lower; add WHERE to avoid no-op updates |
| Alias retarget correctness | Correct (reinsert) | Needs refresh step | Correct (upsert refresh) |
| Schema change required | None | None | Unique constraint/index required |
| Concurrency on hot parents | Higher contention | Reduced footprint | Best on insert/update (conflict-driven) |
| Operational complexity | Low | Low | Medium (build unique index at scale) |

---

## How to Validate with the Harness

1. Ensure the deterministic fixtures exist (generated by data scripts):
   - `single_doc_standard`, `single_doc_heavy`, `batch_100_mixed` in `dms.perf_reference_targets`.
2. Run scenarios:
   - Baseline: `perf-claude/sql/current/test_current_insert_references.sql`
   - Differential: `perf-claude/sql/alternatives/differential_update.sql`
   - MERGE: `perf-claude/sql/alternatives/merge_pattern.sql`
3. Review results under `perf-claude/results/<timestamp>/*` and the `dms.perf_test_results` table:
   - Latency (avg, P95/P99), dead tuples before/after, table/index sizes, WAL bytes (if enabled), and latency histograms.

---

## Recommendation

Short term (low risk, immediate improvement)
- Adopt the Differential Update approach in `InsertReferences` to eliminate full deletes and inserts. Add the small “refresh existing rows when alias changed” UPDATE to maintain correctness without broad churn.
- Create the covering index on `(ParentDocumentPartitionKey, ParentDocumentId)` with INCLUDE columns to accelerate selective deletes/existence checks.

Medium term (robustness + concurrency)
- Introduce the unique constraint and transition to the MERGE/UPSERT pattern, ensuring the `DO UPDATE` clause is guarded to avoid no-op updates.
- Plan the unique index deployment on the partitioned table to minimize impact (maintenance window on root, or advanced per-partition concurrent builds with ATTACH).

Complementary tuning (from `TEST_STRATEGY.md` and `TUNING_EXPLORATIONS.md`)
- Consider table `fillfactor` and more aggressive autovacuum for `dms.Reference` to manage residual churn.
- Monitor with `pg_stat_statements`, `pg_stat_user_functions`, `pg_statio_user_tables`, and (if enabled) `pgstattuple` for bloat tracking.

---

## Appendix: Key DDL Snippets

Covering index
```sql
CREATE INDEX IF NOT EXISTS ix_reference_covering
ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId)
INCLUDE (ReferentialPartitionKey, AliasId, ReferencedDocumentPartitionKey, ReferencedDocumentId);
```

Unique index + constraint for MERGE
```sql
-- Partitioned root (cannot use CONCURRENTLY here)
CREATE UNIQUE INDEX uk_reference_parent_alias
  ON dms.Reference (ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey);
ALTER TABLE dms.Reference
  ADD CONSTRAINT uk_reference_parent_alias
  UNIQUE USING INDEX uk_reference_parent_alias;
```

No-op update guard in upsert
```sql
ON CONFLICT (ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey)
DO UPDATE SET
  ReferencedDocumentId = EXCLUDED.ReferencedDocumentId,
  ReferencedDocumentPartitionKey = EXCLUDED.ReferencedDocumentPartitionKey
WHERE (dms.Reference.ReferencedDocumentId, dms.Reference.ReferencedDocumentPartitionKey)
      IS DISTINCT FROM (EXCLUDED.ReferencedDocumentId, EXCLUDED.ReferencedDocumentPartitionKey);
```

Refresh step for Differential when alias targets change
```sql
UPDATE dms.Reference r
SET ReferencedDocumentId = a.DocumentId,
    ReferencedDocumentPartitionKey = a.DocumentPartitionKey
FROM dms.Alias a
WHERE r.AliasId = a.Id
  AND r.ReferentialPartitionKey = a.ReferentialPartitionKey
  AND (r.ReferencedDocumentId, r.ReferencedDocumentPartitionKey)
      IS DISTINCT FROM (a.DocumentId, a.DocumentPartitionKey)
  AND (r.ParentDocumentId, r.ParentDocumentPartitionKey) IN (
    SELECT DISTINCT parent_document_id, parent_document_partition_key FROM staged
  );
```

---

Prepared by: perf-claude analysis
Date: 2025-10-20
