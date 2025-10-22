# DMS Reference Table Performance Tuning Explorations

This document outlines targeted performance optimization experiments to systematically explore solutions for the 20M+ row Reference table performance issues.

## Overview

The baseline performance harness establishes current behavior. These explorations test specific hypotheses about how to improve performance through indexing strategies, partition tuning, PostgreSQL configuration, and alternative query patterns.

---

## Exploration 1: Index Strategy Optimization

### Goal
Determine optimal indexing strategy to minimize heap access and improve query selectivity at 20M row scale.

### Current Index Configuration
```sql
-- Existing indexes from 0003_Create_Reference_Table.sql
CREATE INDEX UX_Reference_ParentDocumentId
  ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId);

CREATE INDEX UX_Reference_ReferencedDocumentId
  ON dms.Reference (ReferencedDocumentPartitionKey, ReferencedDocumentId);
```

### Experiments to Run

#### 1.1: Covering Index for InsertReferences DELETE Operation
**Hypothesis**: A covering index can eliminate heap access during the bulk DELETE phase.

```sql
-- Create covering index that includes all columns needed by DELETE
CREATE INDEX idx_reference_covering_delete
ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId)
INCLUDE (ReferentialId, ReferentialPartitionKey);

-- Test query plan
EXPLAIN (ANALYZE, BUFFERS)
DELETE FROM dms.Reference
WHERE ParentDocumentPartitionKey = 5 AND ParentDocumentId = 12345;
```

**Metrics to Capture**:
- Shared blocks hit vs read
- Index-only scan vs index scan
- Execution time comparison
- Dead tuple accumulation rate

#### 1.2: Partial Index for Unvalidated References
**Hypothesis**: If many references have NULL ReferencedDocumentId (when validation is off), a partial index reduces bloat.

```sql
-- Index only validated references
CREATE INDEX idx_reference_validated
ON dms.Reference (ReferencedDocumentPartitionKey, ReferencedDocumentId)
WHERE ReferencedDocumentId IS NOT NULL;

-- Index only unvalidated references
CREATE INDEX idx_reference_unvalidated
ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId)
WHERE ReferencedDocumentId IS NULL;
```

**Metrics to Capture**:
- Index size reduction
- Query plan changes
- Insertion performance impact

#### 1.3: BRIN Index for Sequential/Temporal Access
**Hypothesis**: If references are typically accessed in creation order, BRIN indexes provide massive space savings.

```sql
-- BRIN index on identity column
CREATE INDEX idx_reference_brin_id
ON dms.Reference USING BRIN (Id);

-- BRIN index on partition key + parent document (if correlated)
CREATE INDEX idx_reference_brin_parent
ON dms.Reference USING BRIN (ParentDocumentPartitionKey, ParentDocumentId);
```

**Metrics to Capture**:
- Index size (BRIN vs B-tree)
- Sequential scan vs BRIN scan timing
- Effectiveness on large bulk deletes

#### 1.4: Fillfactor Tuning for High-Update Table
**Hypothesis**: Reducing fillfactor allows HOT (Heap-Only Tuple) updates, reducing index bloat.

```sql
-- Test different fillfactor values
ALTER TABLE dms.Reference SET (fillfactor = 70);  -- Leave 30% free space
VACUUM FULL dms.Reference;

-- Alternative: more aggressive
ALTER TABLE dms.Reference SET (fillfactor = 60);
```

**Metrics to Capture**:
- HOT update percentage (from `pg_stat_user_tables.n_tup_hot_upd`)
- Table bloat growth rate
- Update performance with/without HOT

### Test Script Template
```sql
-- sql/explorations/test_index_strategies.sql
\timing on
SET application_name = 'perf_index_exploration';

-- Baseline measurement
INSERT INTO dms.perf_test_results (test_name, test_type, start_time)
VALUES ('index_baseline', 'index_exploration', NOW())
RETURNING test_id AS baseline_id \gset

-- Test current index performance
\set fixture_target 'single_doc_heavy'
-- Run standard InsertReferences test...

-- Create covering index
CREATE INDEX CONCURRENTLY idx_reference_covering_delete...

-- Re-run same test
INSERT INTO dms.perf_test_results (test_name, test_type, start_time)
VALUES ('index_covering', 'index_exploration', NOW())
RETURNING test_id AS covering_id \gset

-- Compare results
SELECT
    test_name,
    avg_latency_ms,
    avg_latency_ms - LAG(avg_latency_ms) OVER (ORDER BY test_id) AS improvement_ms,
    dead_tuples_after - dead_tuples_before AS dead_tuples_created
FROM dms.perf_test_results
WHERE test_id IN (:baseline_id, :covering_id);
```

---

## Exploration 2: Partition Strategy Analysis

### Goal
Determine if the current 16-partition hash strategy is optimal, or if partitioning is helping/hurting performance.

### Current Configuration
- Hash partitioning on `ParentDocumentPartitionKey` (16 partitions)
- Cross-partition queries required for `ReferencedDocumentId` lookups

### Experiments to Run

#### 2.1: Compare Partition Counts (8 vs 16 vs 32 vs 64)
**Hypothesis**: Different partition counts affect lock granularity and query planning.

```sql
-- Create test schema with different partition counts
CREATE TABLE dms.reference_8part PARTITION BY HASH(ParentDocumentPartitionKey);
-- Create 8 partitions...

CREATE TABLE dms.reference_32part PARTITION BY HASH(ParentDocumentPartitionKey);
-- Create 32 partitions...
```

**Metrics to Capture**:
- Insertion throughput
- DELETE performance across partitions
- Lock wait times during concurrent operations
- Query planner partition pruning effectiveness

#### 2.2: Partitioned vs Non-Partitioned at 20M Scale
**Hypothesis**: At 20M rows, partitioning overhead may exceed benefits.

```sql
-- Create non-partitioned version
CREATE TABLE dms.reference_nopart (
    -- Same schema as dms.Reference but without PARTITION BY
);

-- Load same 20M rows
-- Run identical tests on both tables
```

**Metrics to Capture**:
- Table and index sizes
- Query performance comparison
- VACUUM performance
- Concurrent update throughput

#### 2.3: Cross-Partition Query Impact Analysis
**Hypothesis**: The `ReferencedDocumentId` index scanning all 16 partitions is a hidden bottleneck.

```sql
-- Analyze cross-partition lookup cost
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM dms.Reference
WHERE ReferencedDocumentPartitionKey = 5
  AND ReferencedDocumentId = 12345;

-- Compare: single partition lookup
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM dms.Reference
WHERE ParentDocumentPartitionKey = 5
  AND ParentDocumentId = 12345;
```

**Metrics to Capture**:
- Number of partitions scanned
- Shared blocks read across partitions
- Planning time vs execution time ratio

#### 2.4: Alternative Partition Key (ReferentialPartitionKey)
**Hypothesis**: Partitioning by ReferentialPartitionKey instead of ParentDocumentPartitionKey might improve reference lookups.

```sql
-- Test alternative partitioning scheme
CREATE TABLE dms.reference_alt_partition
PARTITION BY HASH(ReferentialPartitionKey);

-- Measure impact on both insert and lookup patterns
```

### Test Script Template
```bash
# scripts/test-partition-strategies.sh
for PARTITION_COUNT in 0 8 16 32 64; do
    log_info "Testing with $PARTITION_COUNT partitions"
    ./setup-test-db-with-partitions.sh $PARTITION_COUNT
    ./generate-test-data.sh --mode sql --documents 1000000 --references 20000000
    ./test-single-scenario.sh ../sql/current/test_current_insert_references.sql
    # Capture results...
done
```

---

## Exploration 3: DELETE Operation Optimization

### Goal
Optimize the bulk DELETE that occurs in `InsertReferences` (lines 20-23 of stored procedure).

### Current DELETE Pattern
```sql
DELETE from dms.Reference r
USING unnest(parentDocumentIds, parentDocumentPartitionKeys) as d (Id, DocumentPartitionKey)
WHERE d.Id = r.ParentDocumentId AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey;
```

### Experiments to Run

#### 3.1: CTE-Based DELETE with Statistics
**Hypothesis**: Using a CTE allows better statistics and query planning.

```sql
CREATE OR REPLACE FUNCTION dms.InsertReferences_CTEDelete(...)
AS $$
BEGIN
    WITH target_parents AS (
        SELECT DISTINCT parent_id, parent_key
        FROM unnest(parentDocumentIds, parentDocumentPartitionKeys)
          AS t(parent_id, parent_key)
    ),
    deleted AS (
        DELETE FROM dms.Reference r
        USING target_parents tp
        WHERE r.ParentDocumentId = tp.parent_id
          AND r.ParentDocumentPartitionKey = tp.parent_key
        RETURNING r.Id
    )
    SELECT COUNT(*) FROM deleted;
    -- ... rest of function
END;
$$;
```

**Metrics to Capture**:
- DELETE execution time
- Query plan differences
- Lock duration

#### 3.2: Temporary Table Staging
**Hypothesis**: Staging deletions in a temp table allows batch optimization.

```sql
CREATE OR REPLACE FUNCTION dms.InsertReferences_TempStaging(...)
AS $$
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS refs_to_delete (
        parent_id BIGINT,
        parent_key SMALLINT
    ) ON COMMIT DROP;

    INSERT INTO refs_to_delete
    SELECT DISTINCT * FROM unnest(parentDocumentIds, parentDocumentPartitionKeys);

    ANALYZE refs_to_delete;  -- Let planner know cardinality

    DELETE FROM dms.Reference r
    USING refs_to_delete rtd
    WHERE r.ParentDocumentId = rtd.parent_id
      AND r.ParentDocumentPartitionKey = rtd.parent_key;
END;
$$;
```

**Metrics to Capture**:
- ANALYZE effectiveness
- Temp table overhead
- Overall performance improvement

#### 3.3: TRUNCATE + COPY for Bulk Rewrites
**Hypothesis**: For documents with many references, TRUNCATE partition + bulk INSERT is faster than DELETE.

```sql
-- For single-document updates with >100 references
IF array_length(referentialIds) > 100 AND <single document> THEN
    -- Detach partition, truncate, reload
    ALTER TABLE dms.Reference DETACH PARTITION reference_05;
    TRUNCATE reference_05;
    -- Bulk insert...
    ALTER TABLE dms.Reference ATTACH PARTITION reference_05...
END IF;
```

**Metrics to Capture**:
- Threshold where TRUNCATE+INSERT beats DELETE+INSERT
- Downtime/lock duration

#### 3.4: FK Constraint Impact Analysis
**Hypothesis**: FK constraint validation during DELETE is a bottleneck.

```sql
-- Test with FK disabled (NOT FOR PRODUCTION)
ALTER TABLE dms.Reference DROP CONSTRAINT FK_Reference_ParentDocument;

-- Run delete test
-- Measure performance improvement

-- Re-enable
ALTER TABLE dms.Reference ADD CONSTRAINT FK_Reference_ParentDocument ...;
```

**Metrics to Capture**:
- FK validation overhead
- Whether deferrable constraints help (`DEFERRABLE INITIALLY DEFERRED`)

---

## Exploration 4: Real-World Workload Patterns

### Goal
Simulate production access patterns beyond uniform random distribution.

### Experiments to Run

#### 4.1: Hot Document Pattern (Pareto Distribution)
**Hypothesis**: 20% of documents receive 80% of updates in production.

```python
# load-tests/hot_document_test.py
class HotDocumentTester:
    def __init__(self):
        self.hot_docs = random.sample(self.document_ids, len(self.document_ids) // 5)
        self.cold_docs = [d for d in self.document_ids if d not in self.hot_docs]

    async def update_with_hotspot(self):
        if random.random() < 0.8:  # 80% hit hot documents
            doc = random.choice(self.hot_docs)
        else:
            doc = random.choice(self.cold_docs)
        # Perform update...
```

**Metrics to Capture**:
- Cache hit rates for hot vs cold documents
- Lock contention on hot partitions
- Dead tuple accumulation in hot partitions

#### 4.2: Temporal Batch Pattern
**Hypothesis**: Production has batch load windows with different characteristics.

```python
# Simulate batch load (high throughput, low latency tolerance)
async def batch_load_window():
    for i in range(10000):
        await update_references(...)  # No delay

# Simulate interactive window (low throughput, low latency required)
async def interactive_window():
    for i in range(100):
        await update_references(...)
        await asyncio.sleep(random.uniform(0.1, 2.0))
```

**Metrics to Capture**:
- Latency percentiles during batch vs interactive
- Autovacuum interference during batch loads

#### 4.3: Mixed Read/Write Workload
**Hypothesis**: Concurrent reads during heavy writes affect both operations.

```python
# Run simultaneous read and write workloads
async def mixed_workload():
    writers = [update_references() for _ in range(20)]
    readers = [query_references() for _ in range(50)]
    await asyncio.gather(*writers, *readers)
```

**Metrics to Capture**:
- Read latency during write spikes
- Lock wait events
- MVCC snapshot age

---

## Exploration 5: PostgreSQL Configuration Tuning

### Goal
Identify configuration parameters that significantly impact bulk reference operations.

### Experiments to Run

#### 5.1: Work Memory Tuning
**Hypothesis**: Larger work_mem improves hash joins and sorts in DELETE operations.

```sql
-- Test different work_mem values
SET work_mem = '4MB';   -- Default
-- Run test, capture metrics

SET work_mem = '64MB';
-- Run test, capture metrics

SET work_mem = '256MB';
-- Run test, capture metrics
```

**Metrics to Capture**:
- Temp file usage (should decrease)
- Query execution time
- Memory usage per connection

#### 5.2: Maintenance Work Memory for Vacuum
**Hypothesis**: Larger maintenance_work_mem improves VACUUM efficiency on high-churn tables.

```sql
SET maintenance_work_mem = '1GB';
VACUUM ANALYZE dms.Reference;
```

**Metrics to Capture**:
- VACUUM duration
- Dead tuple removal rate
- Index bloat reduction

#### 5.3: Random Page Cost for SSD
**Hypothesis**: Default random_page_cost=4 is too high for SSDs.

```sql
SET random_page_cost = 1.1;  -- SSD-optimized
-- Re-run query plans

SET random_page_cost = 4.0;  -- Default (HDD assumption)
-- Compare
```

**Metrics to Capture**:
- Query plan changes (seq scan vs index scan)
- Actual execution time differences

#### 5.4: Autovacuum Aggressiveness for Reference Table
**Hypothesis**: More aggressive autovacuum prevents bloat accumulation.

```sql
ALTER TABLE dms.Reference SET (
    autovacuum_vacuum_scale_factor = 0.05,    -- Default: 0.2
    autovacuum_analyze_scale_factor = 0.02,   -- Default: 0.1
    autovacuum_vacuum_cost_delay = 10,        -- Default: 20
    autovacuum_vacuum_cost_limit = 500        -- Default: 200
);
```

**Metrics to Capture**:
- Autovacuum frequency
- Average dead tuple count over time
- Table bloat percentage

#### 5.5: Checkpoint and WAL Tuning
**Hypothesis**: Checkpoint frequency affects bulk write performance.

```sql
-- Test different checkpoint intervals
ALTER SYSTEM SET checkpoint_timeout = '30min';  -- Default: 5min
ALTER SYSTEM SET max_wal_size = '4GB';          -- Default: 1GB
SELECT pg_reload_conf();
```

**Metrics to Capture**:
- WAL bytes generated
- Checkpoint write time
- Sustained write throughput

### Test Script Template
```bash
# scripts/test-pg-config.sh
declare -A WORK_MEM_VALUES=( ["4MB"]=4 ["64MB"]=64 ["256MB"]=256 )

for mem_size in "${!WORK_MEM_VALUES[@]}"; do
    log_info "Testing with work_mem=$mem_size"
    psql_exec "SET work_mem = '$mem_size';"
    ./test-single-scenario.sh ../sql/current/test_current_insert_references.sql
    # Log results with work_mem tag
done
```

---

## Exploration 6: Bloat Analysis and Management

### Goal
Quantify bloat accumulation and test different mitigation strategies.

### Experiments to Run

#### 6.1: Install pgstattuple for Detailed Bloat Metrics

```sql
CREATE EXTENSION IF NOT EXISTS pgstattuple;

-- Capture detailed bloat statistics
SELECT
    pg_size_pretty(pg_relation_size('dms.reference')) AS table_size,
    pg_size_pretty(pg_total_relation_size('dms.reference')) AS total_with_indexes,
    (pgstattuple('dms.reference')).dead_tuple_percent,
    (pgstattuple('dms.reference')).free_percent,
    (pgstattuple('dms.reference')).tuple_len AS live_tuple_bytes,
    (pgstattuple('dms.reference')).dead_tuple_len AS dead_tuple_bytes;
```

**Integrate into Monitoring**:
```sql
-- Add to capture_observability_snapshot
CREATE OR REPLACE FUNCTION dms.get_table_bloat_stats()
RETURNS TABLE (
    tablename TEXT,
    dead_pct NUMERIC,
    free_pct NUMERIC,
    bloat_mb NUMERIC
) AS $$
    SELECT
        'reference'::TEXT,
        (pgstattuple('dms.reference')).dead_tuple_percent,
        (pgstattuple('dms.reference')).free_percent,
        pg_relation_size('dms.reference') / 1024.0 / 1024.0 *
            (pgstattuple('dms.reference')).dead_tuple_percent / 100.0;
$$ LANGUAGE sql;
```

#### 6.2: HOT Update Percentage Tracking
**Hypothesis**: Low HOT update percentage indicates index bloat.

```sql
-- Monitor HOT updates over time
SELECT
    schemaname,
    tablename,
    n_tup_upd AS total_updates,
    n_tup_hot_upd AS hot_updates,
    ROUND(100.0 * n_tup_hot_upd / NULLIF(n_tup_upd, 0), 2) AS hot_update_pct,
    last_autovacuum
FROM pg_stat_user_tables
WHERE tablename = 'reference';
```

**Metrics to Capture**:
- HOT update percentage before/after fillfactor changes
- Correlation with dead tuple accumulation

#### 6.3: VACUUM FULL vs Regular VACUUM Timing
**Hypothesis**: Periodic VACUUM FULL is necessary but expensive.

```sql
-- Test regular vacuum
\timing on
VACUUM ANALYZE dms.Reference;

-- Test VACUUM FULL
VACUUM FULL ANALYZE dms.Reference;

-- Compare table sizes before/after
```

**Metrics to Capture**:
- VACUUM duration
- Downtime requirements (VACUUM FULL locks table)
- Space reclaimed
- Query performance improvement

#### 6.4: Bloat Prevention Strategy Comparison
**Hypothesis**: Combining fillfactor + aggressive autovacuum prevents bloat better than either alone.

```sql
-- Strategy A: Default settings
-- Run workload, measure bloat

-- Strategy B: Fillfactor only
ALTER TABLE dms.Reference SET (fillfactor = 70);
-- Run workload, measure bloat

-- Strategy C: Aggressive autovacuum only
ALTER TABLE dms.Reference SET (
    autovacuum_vacuum_scale_factor = 0.05
);
-- Run workload, measure bloat

-- Strategy D: Both
-- Combine B + C, measure bloat
```

**Metrics to Capture**:
- Dead tuple percentage over time
- Autovacuum frequency
- Query performance stability

---

## Recommended Exploration Sequence

1. **Start with Configuration** (Exploration 5): Quick wins, no code changes
   - Test work_mem and random_page_cost immediately
   - Tune autovacuum settings for Reference table

2. **Index Optimization** (Exploration 1): High impact, low risk
   - Add covering indexes
   - Test fillfactor tuning

3. **Bloat Monitoring** (Exploration 6): Establish baseline
   - Install pgstattuple
   - Track bloat metrics during tests

4. **DELETE Optimization** (Exploration 3): Code changes required
   - Test CTE-based DELETE
   - Evaluate temp table approach

5. **Partition Strategy** (Exploration 2): Requires data reload
   - Test different partition counts
   - Compare partitioned vs non-partitioned

6. **Workload Patterns** (Exploration 4): Validation
   - Ensure solutions work under production patterns
   - Test hot document scenarios

---

## Metrics Collection Framework

Add to `scripts/config.sh`:

```bash
# Exploration-specific metrics
export ENABLE_PGSTATTUPLE="${ENABLE_PGSTATTUPLE:-true}"
export ENABLE_INDEX_ANALYSIS="${ENABLE_INDEX_ANALYSIS:-true}"
export ENABLE_BLOAT_TRACKING="${ENABLE_BLOAT_TRACKING:-true}"
```

Add to `capture_observability_snapshot`:

```sql
\echo '-- Index Usage Statistics'
SELECT
    schemaname, tablename, indexname,
    idx_scan, idx_tup_read, idx_tup_fetch,
    pg_size_pretty(pg_relation_size(indexrelid)) AS index_size
FROM pg_stat_user_indexes
WHERE schemaname = 'dms' AND tablename IN ('document', 'alias', 'reference')
ORDER BY idx_scan DESC;

\echo '-- Table Bloat Statistics (requires pgstattuple)'
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgstattuple')
    THEN 1 ELSE 0 END AS has_pgstattuple \gset
\if :has_pgstattuple
SELECT
    'reference' AS tablename,
    (pgstattuple('dms.reference')).dead_tuple_percent,
    (pgstattuple('dms.reference')).free_percent,
    pg_size_pretty((pgstattuple('dms.reference')).table_len) AS total_size;
\else
\echo 'pgstattuple extension not available. Install for detailed bloat analysis.'
\endif

\echo '-- HOT Update Statistics'
SELECT
    tablename,
    n_tup_upd AS total_updates,
    n_tup_hot_upd AS hot_updates,
    ROUND(100.0 * n_tup_hot_upd / NULLIF(n_tup_upd, 0), 2) AS hot_update_pct
FROM pg_stat_user_tables
WHERE schemaname = 'dms' AND tablename IN ('document', 'alias', 'reference');
```

---

## Next Steps

1. **Prioritize** based on expected impact and implementation complexity
2. **Create dedicated test scripts** for each exploration in `sql/explorations/`
3. **Run explorations systematically**, documenting results in `results/explorations/`
4. **Analyze trade-offs** between different approaches
5. **Combine winning strategies** into an optimized implementation
6. **Validate** against production workload characteristics

Each exploration should be run at **production scale (20M references)** to ensure results are representative.
