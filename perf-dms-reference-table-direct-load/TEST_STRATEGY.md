# DMS Reference Table Performance Testing Strategy

## Overview
This testing suite isolates the database layer from the DMS application to identify and measure performance bottlenecks in the three-table design, specifically focusing on the References table with 20M+ rows.

> ⚠️ **Schema note**: The perf harness provisions Document, Alias, and Reference using local copies of the DDL. It is not wired to the canonical deploy scripts in `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts`, so revisit the setup if those upstream definitions change.

## Key Performance Issues Identified

### 1. DELETE-then-INSERT Pattern (CRITICAL)
**Location**: `InsertReferences` stored procedure (lines 20-23)
**Issue**: Every reference update deletes ALL existing references before inserting new ones
**Impact**:
- Creates ~150-200 dead tuples per operation
- With 20M rows, causes severe table bloat
- Triggers aggressive autovacuum cycles
- Lock contention during bulk operations

### 2. Reverse Lookups via Alias
**Issue**: References partitioned by ParentDocumentPartitionKey but reverse lookups must hop through Alias using ReferentialId
**Impact**: Requires partition-aware index support on (ReferentialPartitionKey, ReferentialId) to avoid scanning all partitions

### 3. Foreign Key Validation Overhead
**Issue**: Multiple FK constraints require validation on every insert
**Impact**: Significant overhead with large batch operations

## Test Scenarios

### Baseline Tests
1. **Current Implementation** (`sql/current/test_current_insert_references.sql`)
   - Measures existing DELETE-then-INSERT pattern performance
   - Establishes baseline metrics for comparison

> **Deterministic fixtures:** Scenario scripts no longer sample random documents. Instead they pull repeatable payloads from `dms.perf_reference_targets`, which is rebuilt automatically by `SELECT dms.build_perf_reference_targets();`. The bundled fixtures include:
>
> - `single_doc_standard` – average document (50 references)
> - `single_doc_heavy` – heavy document (200 references)
> - `batch_100_mixed` – documents 1–100 with up to 20 references each
>
> Refresh fixtures anytime after modifying test data:\
> `psql -c "SELECT dms.build_perf_reference_targets();"`

### Alternative Implementations
1. **Differential Updates** (`sql/alternatives/differential_update.sql`)
   - Only deletes removed references
   - Only inserts new references
   - Expected improvement: 30-50% reduction in latency

2. **MERGE Pattern** (`sql/alternatives/merge_pattern.sql`)
   - Uses INSERT ON CONFLICT for upserts
   - Reduces dead tuple creation
   - Better for concurrent operations

### Stress Tests
1. **Repeated Updates** (`sql/scenarios/test_problematic_patterns.sql`)
   - Same document updated 100 times
   - Demonstrates dead tuple accumulation

2. **Batch Operations** (`sql/scenarios/batch_operations.sql`)
   - Updates 100-1000 documents in single transaction
   - Tests lock contention and scaling

3. **Concurrent Load** (`load-tests/concurrent_load_test.py`)
   - 10-50 concurrent sessions
   - Measures throughput under load

## Quick Start Testing Flow

```bash
# 1. Setup
cd perf-claude
./quick-start.sh

# 2. Run specific test
cd scripts
./test-single-scenario.sh differential_update.sql

# 3. Monitor while testing
MONITOR=true ./test-single-scenario.sh batch_operations.sql

# 4. Full test suite
./run-all-tests.sh

# 5. Generate report
./generate-report.sh
```

### Dataset Generation Options

- **Default CSV pipeline**: `./scripts/generate-test-data.sh --mode csv` (or the Quick Start prompt) generates deterministic CSVs and bulk loads with `COPY`. Fastest option for most tests.
- **SQL pipeline with chunking**: `./scripts/generate-test-data.sh --mode sql [--chunk-size <docs>]` performs the entire build inside Postgres in resumable chunks. Use when external tooling is restricted.
- **Resume support**: rerun the SQL pipeline with `--resume` to continue after interruptions. Each chunk clears and reloads its document range to remain idempotent.
- **Manual CSV flow**: `python ./data/generate_deterministic_data.py --output ./data/out` followed by `./scripts/load-test-data-from-csv.sh ./data/out` if you need to stage files separately.

### Deterministic Sampling Controls

- SQL scripts no longer query live tables with `ORDER BY random()`. Each scenario pulls parent and reference arrays from `dms.perf_reference_targets`, ensuring that “current”, “differential”, and “merge” tests all exercise the exact same payload.
- The problematic-pattern harness reuses fixture slices (`single_doc_standard`, `single_doc_heavy`, `batch_100_mixed`) to build deterministic variations—array lengths vary per iteration, but the underlying rows are repeatable and sourced from the fixture table.
- The async Python load test (`load-tests/concurrent_load_test.py`) selects documents/aliases via ordered scans, shuffles them with a fixed seed (`12345`), and gives each session its own deterministic RNG (`seed = 12345 + sessionId`). Throughput still varies by workload, but the identity mix is stable across runs.
- To change the deterministic sets, rebuild the fixture table or adjust the constants in the script (sample size, seeds). Document any deviations so later runs remain comparable.

## Key Metrics to Monitor

### Performance Metrics
- **Latency**: Average, P95, P99, Max
- **Throughput**: Operations per second
- **Dead Tuples**: Accumulation rate per operation
- **Table Bloat**: Size growth over time

### Resource Metrics
- **Lock Waits**: Frequency and duration
- **Cache Hit Ratio**: Should be >99% for hot data
- **I/O Operations**: Read/write patterns
- **Memory Usage**: Shared buffers, work_mem

## Recommended Solutions

### Immediate (Quick Wins)
1. **Implement Differential Updates**
   - Modify `InsertReferences` to only update changed references
   - Expected improvement: 30-50% performance gain
   - Reduces dead tuples by 80%

### Short-term
1. **Add Alias-Focused Indexes**
   ```sql
   CREATE INDEX IX_Reference_ReferentialId
   ON dms.Reference (ReferentialPartitionKey, ReferentialId);
   ```

2. **Optimize FK Validation**
   - Consider deferring FK checks within transactions
   - Batch validate at commit time

### Long-term
1. **Rethink Partitioning Strategy**
   - Consider composite partitioning
   - Or partition by time + document for better locality

2. **Denormalization Options**
   - Store frequently accessed references in Document JSONB
   - Trade storage for query performance

## Test Data Characteristics

- **Documents**: 100,000 records
- **Aliases**: 100,000 records
- **References**: 20,000,000 records
- **Distribution**: Power law (some documents have 10x more references)
- **Partitions**: 16 (matching production)

## Success Criteria

- Single document update: <50ms average latency
- Batch update (1000 docs): <5s total time
- Concurrent load (10 sessions): >20 ops/sec sustained
- Dead tuple accumulation: <10 per operation
- No lock timeouts under normal load

## Monitoring During Tests

- `run-all-tests.sh` captures a lightweight snapshot from `pg_stat_user_tables` and `pg_stat_database` **before** the suite starts and **after** it completes. Compare `before_tests_pg_stats.txt` and `after_tests_pg_stats.txt` in the results directory to see how dead tuples, table size, and transaction counters changed without injecting extra load.
- Every scenario (including the quick-start flow and Python load tests) now resets key `pg_stat_*` views and writes both `*_before_observability.txt` and `*_after_observability.txt` to the results directory. Use these files to inspect:
  - `pg_stat_statements`: total/average time, shared buffer hits vs reads, and temp usage for each query (look for unusually high `shared_blks_read` or `temp_blks_written` that signal cache misses or spills).
  - `pg_stat_user_functions`: PL/pgSQL cost of `dms.InsertReferences` variants—`total_time` shows end-to-end latency while `self_time` discounts child SQL calls.
  - `pg_statio_user_tables`: heap/index hit ratios for `Document`, `Alias`, and `Reference` tables—low hit ratios reveal buffer churn.
  - `pg_stat_wal`: WAL bytes emitted; spikes highlight write amplification or excessive churn caused by DELETE+INSERT cycles.
  - `pgstattuple_approx`: dead tuple % and free space % for `dms.Reference`; the harness aggregates results across all partitions because the parent table itself has no heap.
  - `pg_stat_io` (if available): time spent waiting on physical IO per backend type—large `read_time` or `write_time` under `client backend` correlates with workload stalls.
  - Wait events for `application_name` values starting with `perf_`, which surfaces lock contention or buffer pin waits without sampling more aggressively.
- To analyse improvements, diff the before/after snapshots for each scenario; the after file already reflects the workload delta because counters are reset up front.
- If you still need continuous sampling (locks, statements, or size deltas), run `scripts/monitor-performance.sh` manually with a coarse `MONITOR_INTERVAL` (default 10 s) so the monitoring queries do not materially affect the workload under test.

> The `pgstattuple_approx` metrics require the `pgstattuple` extension. Install the matching `postgresql-contrib` package for your Postgres version and run `CREATE EXTENSION pgstattuple;` before executing tests; contrib extensions are not loaded automatically. The harness calls the function on each `dms.Reference_*` partition and rolls the results up into a total row.
## Next Steps After Testing

1. **Analyze Results**: Review `results/*/analysis_report.txt`
2. **Compare Implementations**: Identify best performing approach
3. **Implement Changes**: Apply winning strategy to DMS codebase
4. **Validate in Staging**: Test with full application stack
5. **Production Rollout**: Deploy with monitoring

## Important Notes

- Always run `ANALYZE` after bulk data loads
- Monitor `pg_stat_statements` for slow queries
- Check `pg_stat_user_tables` for vacuum statistics
- Use `EXPLAIN (ANALYZE, BUFFERS)` for query analysis
- Consider connection pooling for production workloads
