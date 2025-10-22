#!/bin/bash

# Resolve repository root (one level above this scripts directory)
SCRIPT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Database Configuration
export DB_HOST="${DB_HOST:-localhost}"
export DB_PORT="${DB_PORT:-5435}"
export DB_NAME="${DB_NAME:-dms_perf_test}"
export DB_USER="${DB_USER:-postgres}"
export DB_PASSWORD="${DB_PASSWORD:-abcdefgh1!}"

# Connection string
export PGCONNSTRING="postgresql://${DB_USER}:${DB_PASSWORD}@${DB_HOST}:${DB_PORT}/${DB_NAME}"

# Test Data Configuration
export NUM_DOCUMENTS="${NUM_DOCUMENTS:-100000}"          # Number of documents to generate
export NUM_REFERENCES="${NUM_REFERENCES:-20000000}"     # Target number of references (20M)
export AVG_REFS_PER_DOC="${AVG_REFS_PER_DOC:-200}"      # Average references per document
export NUM_PARTITIONS="${NUM_PARTITIONS:-16}"           # Number of partitions (matching production)

# Test Execution Configuration
export CONCURRENT_SESSIONS="${CONCURRENT_SESSIONS:-10}"  # Number of concurrent sessions for load tests
export TEST_DURATION_SECONDS="${TEST_DURATION_SECONDS:-300}" # Duration for sustained load tests
export BATCH_SIZE="${BATCH_SIZE:-1000}"                 # Batch size for bulk operations

# Performance Thresholds (in milliseconds)
export THRESHOLD_SINGLE_INSERT="${THRESHOLD_SINGLE_INSERT:-50}"
export THRESHOLD_BULK_INSERT="${THRESHOLD_BULK_INSERT:-5000}"
export THRESHOLD_QUERY="${THRESHOLD_QUERY:-100}"

# Monitoring Configuration
export MONITOR_INTERVAL="${MONITOR_INTERVAL:-10}"        # Seconds between monitoring snapshots (used only by optional monitor script)
export ENABLE_STATEMENT_TRACKING="${ENABLE_STATEMENT_TRACKING:-false}"
export ENABLE_LOCK_MONITORING="${ENABLE_LOCK_MONITORING:-false}"
export HEAVY_SNAPSHOT_INTERVAL="${HEAVY_SNAPSHOT_INTERVAL:-30}"

# Results Directory
export RESULTS_ROOT="${RESULTS_ROOT:-${SCRIPT_ROOT}/results}"
export RESULTS_TIMESTAMP="${RESULTS_TIMESTAMP:-$(date +%Y%m%d_%H%M%S)}"
export RESULTS_DIR="${RESULTS_DIR:-${RESULTS_ROOT}/${RESULTS_TIMESTAMP}}"

# Color output for better readability
export RED='\033[0;31m'
export GREEN='\033[0;32m'
export YELLOW='\033[1;33m'
export NC='\033[0m' # No Color

# Utility Functions
log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# PostgreSQL execution wrapper
psql_exec() {
    PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME "$@"
}

# PostgreSQL execution with timing
psql_timed() {
    PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME \
        -c "\\timing on" \
        "$@"
}

# Reset Postgres statistics so each scenario starts from a clean slate.
reset_observability_counters() {
    local label="$1"
    log_info "Resetting Postgres statistics for ${label}"

    psql_exec <<'SQL'
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') THEN
        PERFORM pg_stat_statements_reset();
    END IF;
END
$$;
SELECT pg_stat_reset();
DO $$
DECLARE
    func_oid oid;
BEGIN
    FOR func_oid IN
        SELECT p.oid
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'dms'
    LOOP
        PERFORM pg_stat_reset_single_function_counters(func_oid);
    END LOOP;
EXCEPTION
    WHEN undefined_function THEN
        RAISE NOTICE 'pg_stat_reset_single_function_counters not available on this server, skipping function reset.';
END
$$;
DO $$
BEGIN
    BEGIN
        PERFORM pg_stat_reset_shared('wal');
    EXCEPTION
        WHEN undefined_function OR invalid_parameter_value THEN
            RAISE NOTICE 'pg_stat_reset_shared(wal) not supported on this server, skipping reset.';
    END;

    BEGIN
        PERFORM pg_stat_reset_shared('io');
    EXCEPTION
        WHEN undefined_function OR invalid_parameter_value THEN
            RAISE NOTICE 'pg_stat_reset_shared(io) not supported on this server, skipping reset.';
    END;
END
$$;
SQL
}

# Capture Postgres statistics that describe how a scenario behaved.
capture_observability_snapshot() {
    local label="$1"
    local stage="$2"
    local safe_label="${label//[^A-Za-z0-9_-]/_}"
    local safe_stage="${stage//[^A-Za-z0-9_-]/_}"
    local output_file="${RESULTS_DIR}/${safe_label}_${safe_stage}_observability.txt"

    log_info "Collecting observability snapshot (${label} - ${stage}) -> ${output_file}"

    local sql_label
    sql_label=$(printf "%s" "$label" | sed "s/'/''/g")
    local sql_stage
    sql_stage=$(printf "%s" "$stage" | sed "s/'/''/g")

    psql_exec <<SQL > "$output_file"
\pset format aligned
\pset pager off

SELECT now() AS captured_at,
       '${sql_label}'::text AS test_name,
       '${sql_stage}'::text AS snapshot_stage;

\echo ''
\echo '-- pg_stat_statements (calls ordered by total_exec_time)'
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') THEN 1 ELSE 0 END AS has_pg_stat_statements \gset
\if :has_pg_stat_statements
SELECT query,
       calls,
       total_exec_time,
       mean_exec_time,
       rows,
       shared_blks_hit,
       shared_blks_read,
       shared_blks_dirtied,
       shared_blks_written,
       temp_blks_read,
       temp_blks_written
FROM pg_stat_statements
WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
ORDER BY total_exec_time DESC;
\else
\echo 'pg_stat_statements extension not available in this database.'
\endif

\echo ''
\echo '-- pg_stat_user_functions (dms schema only)'
SELECT schemaname,
       funcname,
       calls,
       total_time,
       self_time
FROM pg_stat_user_functions
WHERE schemaname = 'dms'
ORDER BY total_time DESC;

\echo ''
\echo '-- pg_stat_statio_user_tables (Document/Alias/Reference)'
SELECT relname,
       heap_blks_hit,
       heap_blks_read,
       idx_blks_hit,
       idx_blks_read,
       toast_blks_hit,
       toast_blks_read
FROM pg_statio_user_tables
WHERE schemaname = 'dms'
  AND relname IN ('document', 'alias', 'reference')
ORDER BY relname;

\echo ''
\echo '-- pg_stat_wal (if available)'
SELECT CASE WHEN to_regclass('pg_catalog.pg_stat_wal') IS NULL THEN 0 ELSE 1 END AS has_pg_stat_wal \gset
\if :has_pg_stat_wal
SELECT
    wal_records,
    wal_fpi,
    wal_bytes,
    wal_buffers_full,
    wal_write,
    wal_sync,
    wal_write_time,
    wal_sync_time
FROM pg_stat_wal;
\else
\echo 'pg_stat_wal view not available on this PostgreSQL version.'
\endif

\echo ''
\echo '-- pgstattuple_approx (Reference table)'
SELECT CASE WHEN EXISTS (
           SELECT 1
           FROM pg_proc
           WHERE proname = 'pgstattuple_approx'
       ) THEN 1 ELSE 0 END AS has_pgstattuple \gset
\if :has_pgstattuple
WITH partitions AS (
    SELECT
        c.relname AS partition_name,
        format('%I.%I', n.nspname, c.relname) AS qualified_name
    FROM pg_class c
    INNER JOIN pg_inherits i ON c.oid = i.inhrelid
    INNER JOIN pg_class parent ON i.inhparent = parent.oid
    INNER JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'dms'
      AND parent.relname = 'reference'
),
partition_stats AS (
    SELECT
        p.partition_name,
        stats.table_len,
        stats.dead_tuple_len,
        stats.approx_free_space
    FROM partitions p
    CROSS JOIN LATERAL pgstattuple_approx(p.qualified_name::regclass) AS stats(
        table_len,
        scanned_percent,
        approx_tuple_count,
        approx_tuple_len,
        approx_tuple_percent,
        dead_tuple_count,
        dead_tuple_len,
        dead_tuple_percent,
        approx_free_space,
        approx_free_percent
    )
),
totals AS (
    SELECT
        'TOTAL'::text AS partition_name,
        COALESCE(SUM(table_len), 0) AS table_len,
        COALESCE(SUM(dead_tuple_len), 0) AS dead_tuple_len,
        COALESCE(SUM(approx_free_space), 0) AS approx_free_space,
        pg_total_relation_size('dms.reference') AS total_relation_size
    FROM partition_stats
)
SELECT
    partition_name AS partition_or_total,
    pg_size_pretty(table_len) AS table_size,
    CASE
        WHEN partition_name = 'TOTAL' THEN pg_size_pretty(total_relation_size)
        ELSE NULL
    END AS total_size_with_indexes,
    ROUND(dead_tuple_len * 100.0 / NULLIF(table_len, 0), 2) AS dead_tuple_percent,
    ROUND(approx_free_space * 100.0 / NULLIF(table_len, 0), 2) AS free_percent
FROM (
    SELECT partition_name, table_len, dead_tuple_len, approx_free_space, total_relation_size
    FROM totals
    UNION ALL
    SELECT partition_name, table_len, dead_tuple_len, approx_free_space, NULL::bigint
    FROM partition_stats
) stats
ORDER BY CASE WHEN partition_name = 'TOTAL' THEN 0 ELSE 1 END, partition_name;
\else
\echo 'pgstattuple extension not available on this PostgreSQL server.'
\endif

\echo ''
\echo '-- pg_stat_io (if available)'
SELECT CASE WHEN to_regclass('pg_catalog.pg_stat_io') IS NULL THEN 0 ELSE 1 END AS has_pg_stat_io \gset
\if :has_pg_stat_io
SELECT backend_type,
       object,
       context,
       reads,
       read_time,
       writes,
       write_time,
       extends,
        extend_time,
        fsyncs,
        fsync_time
FROM pg_stat_io
WHERE backend_type IN ('client backend', 'checkpointer', 'bgwriter')
ORDER BY backend_type, object, context;
\else
\echo 'pg_stat_io view not available on this PostgreSQL version.'
\endif

\echo ''
\echo '-- Wait events (sessions tagged with perf_ application_name)'
SELECT pid,
       application_name,
       state,
       wait_event_type,
       wait_event
FROM pg_stat_activity
WHERE datname = current_database()
  AND application_name LIKE 'perf_%'
ORDER BY pid;
SQL
}

# Create results directory if it doesn't exist
mkdir -p "$RESULTS_DIR"
