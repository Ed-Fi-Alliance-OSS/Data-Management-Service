#!/bin/bash

export RESULTS_TIMESTAMP="${RESULTS_TIMESTAMP:-$(date +%Y%m%d_%H%M%S)}"

source $(dirname "$0")/config.sh

log_info "Starting comprehensive performance test suite"

log_info "Results will be saved to: $RESULTS_DIR"
log_warn "Reminder: perf-claude uses local DDL for Document/Alias/Reference and does not reflect the canonical deploy scripts."

capture_stats_snapshot() {
    local label="$1"
    local snapshot_file="$RESULTS_DIR/${label}_pg_stats.txt"

    log_info "Capturing ${label//_/ } database statistics"
    psql_exec <<EOF > "$snapshot_file"
-- ${label} snapshot for DMS performance harness
SELECT now() AS captured_at, '${label}'::text AS snapshot_label;

SELECT
    schemaname,
    relname,
    n_live_tup,
    n_dead_tup,
    round(100.0 * n_dead_tup / NULLIF(n_live_tup + n_dead_tup, 0), 2) AS dead_pct,
    last_vacuum,
    last_autovacuum
FROM pg_stat_user_tables
WHERE schemaname = 'dms'
  AND relname IN ('document', 'alias', 'reference')
ORDER BY relname;

SELECT
    datname,
    xact_commit,
    xact_rollback,
    tup_inserted,
    tup_updated,
    tup_deleted,
    blk_read_time,
    blk_write_time
FROM pg_stat_database
WHERE datname = current_database();
EOF
}

# Function to run a SQL test file and capture results
run_sql_test() {
    local test_file=$1
    local test_name=$(basename "$test_file" .sql)

    log_info "Running test: $test_name"

    reset_observability_counters "$test_name"
    capture_observability_snapshot "$test_name" "before"

    PGAPPNAME="perf_${test_name}" psql_timed -v ON_ERROR_STOP=1 -f "$test_file" \
        > "$RESULTS_DIR/${test_name}_output.txt" 2>&1

    local status=$?

    capture_observability_snapshot "$test_name" "after"

    if [ $status -eq 0 ]; then
        log_info "✓ Test $test_name completed successfully"
    else
        log_error "✗ Test $test_name failed"
    fi

    return $status
}

# Capture baseline stats before running any workload
capture_stats_snapshot "before_tests"

# Step 1: Baseline - Test current implementation
log_info "=== Phase 1: Baseline Testing (Current Implementation) ==="

# Run current implementation tests
run_sql_test "../sql/current/test_current_insert_references.sql"
run_sql_test "../sql/scenarios/batch_operations.sql"

# Step 2: Alternative implementations
log_info "=== Phase 2: Alternative Implementations ==="

# Test differential update pattern
run_sql_test "../sql/alternatives/differential_update.sql"

# Test merge pattern
run_sql_test "../sql/alternatives/merge_pattern.sql"

# Step 3: Concurrent load testing
log_info "=== Phase 3: Concurrent Load Testing ==="

# Test with Python load tester
if command -v python3 &> /dev/null; then
    log_info "Running concurrent load test with 10 sessions..."
    reset_observability_counters "concurrent_10_sessions"
    capture_observability_snapshot "concurrent_10_sessions" "before"
    python3 ../load-tests/concurrent_load_test.py \
        --sessions 10 \
        --iterations 100 \
        --test-name "concurrent_10_sessions" \
        > "$RESULTS_DIR/concurrent_load_test.txt" 2>&1
    capture_observability_snapshot "concurrent_10_sessions" "after"

    log_info "Running high-concurrency test with 50 sessions..."
    reset_observability_counters "concurrent_50_sessions"
    capture_observability_snapshot "concurrent_50_sessions" "before"
    python3 ../load-tests/concurrent_load_test.py \
        --sessions 50 \
        --iterations 50 \
        --test-name "concurrent_50_sessions" \
        > "$RESULTS_DIR/concurrent_load_test_high.txt" 2>&1
    capture_observability_snapshot "concurrent_50_sessions" "after"
else
    log_warn "Python3 not found, skipping concurrent load tests"
fi

# Step 4: Stress testing
log_info "=== Phase 4: Stress Testing ==="

# Large batch operation
reset_observability_counters "stress_test_large_batch"
capture_observability_snapshot "stress_test_large_batch" "before"
PGAPPNAME="perf_stress_test_large_batch" psql_exec <<EOF > "$RESULTS_DIR/stress_test_large_batch.txt" 2>&1
-- Large batch test
\timing on
BEGIN;

-- Update references for 1000 documents
WITH docs AS (
    SELECT Id, DocumentPartitionKey,
           row_number() OVER (ORDER BY Id) - 1 AS doc_row
    FROM dms.Document
    ORDER BY Id
    LIMIT 1000
), refs AS (
    SELECT
        d.Id,
        d.DocumentPartitionKey,
        array_agg(d.Id)                   AS parent_ids,
        array_agg(d.DocumentPartitionKey) AS parent_partition_keys,
        array_agg(a.ReferentialId)        AS ref_ids,
        array_agg(a.ReferentialPartitionKey) AS ref_keys
    FROM docs d
    CROSS JOIN LATERAL (
        SELECT ReferentialId, ReferentialPartitionKey
        FROM dms.Alias
        WHERE DocumentId != d.Id
        ORDER BY ReferentialPartitionKey, ReferentialId
        OFFSET (d.doc_row * 20)
        LIMIT 20
    ) a
    GROUP BY d.Id, d.DocumentPartitionKey
)
SELECT count(*) as documents_updated
FROM refs r
WHERE dms.InsertReferences(
    r.parent_ids,
    r.parent_partition_keys,
    r.ref_ids,
    r.ref_keys
) IS NOT NULL;

ROLLBACK;
EOF
capture_observability_snapshot "stress_test_large_batch" "after"

# Capture final stats after workloads complete
capture_stats_snapshot "after_tests"

# Step 5: Generate analysis report
log_info "=== Phase 5: Analysis and Reporting ==="
./generate-report.sh

log_info "Performance test suite complete!"
log_info "Results saved to: $RESULTS_DIR"
log_info "Review the generated report: $RESULTS_DIR/performance_report.html"
