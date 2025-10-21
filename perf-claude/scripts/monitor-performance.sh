#!/bin/bash

source $(dirname "$0")/config.sh

usage() {
    cat <<'EOF'
Usage: monitor-performance.sh [test_name] [options]

Options:
  --locks             Enable periodic lock snapshots (heavy)
  --statements        Enable periodic pg_stat_statements snapshots (heavy)
  --sizes             Enable periodic table/index size snapshots (heavy)
  --core-interval N   Seconds between lightweight metric samples (default: MONITOR_INTERVAL)
  --heavy-interval N  Seconds between heavy snapshots (default: HEAVY_SNAPSHOT_INTERVAL)
  --help              Show this help

Examples:
  # Basic monitoring with defaults
  ./monitor-performance.sh baseline

  # Full monitoring with all heavy snapshots enabled
  ./monitor-performance.sh mytest --locks --statements --sizes

  # Custom intervals for a long-running stress test
  ./monitor-performance.sh stress --core-interval 5 --heavy-interval 120
EOF
}

TEST_NAME="monitoring"
RUN_LOCK_SNAP=false
RUN_STATEMENT_SNAP=false
RUN_SIZE_SNAP=false
CORE_INTERVAL="$MONITOR_INTERVAL"
HEAVY_INTERVAL="$HEAVY_SNAPSHOT_INTERVAL"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --locks)
            RUN_LOCK_SNAP=true
            ;;
        --statements)
            RUN_STATEMENT_SNAP=true
            ;;
        --sizes)
            RUN_SIZE_SNAP=true
            ;;
        --core-interval)
            shift
            CORE_INTERVAL="$1"
            ;;
        --heavy-interval)
            shift
            HEAVY_INTERVAL="$1"
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            if [ "$TEST_NAME" = "monitoring" ]; then
                TEST_NAME="$1"
            else
                usage
                exit 1
            fi
            ;;
    esac
    shift
done

# Respect defaults from config if user did not pass explicit flags
if [ "$RUN_LOCK_SNAP" = false ] && [ "$ENABLE_LOCK_MONITORING" = "true" ]; then
    RUN_LOCK_SNAP=true
fi
if [ "$RUN_STATEMENT_SNAP" = false ] && [ "$ENABLE_STATEMENT_TRACKING" = "true" ]; then
    RUN_STATEMENT_SNAP=true
fi

log_info "Starting performance monitoring for test: $TEST_NAME"
log_info "Results will be saved to: $RESULTS_DIR"

# Core metrics CSV (lightweight)
MONITOR_FILE="$RESULTS_DIR/${TEST_NAME}_monitoring_core.csv"
echo "Timestamp,Active_Connections,Blocked_Queries,Dead_Tuples_Reference,Cache_Hit_Ratio,TX_Per_Sec" > "$MONITOR_FILE"

# Heavy snapshot files
if [ "$RUN_SIZE_SNAP" = true ]; then
    SIZE_FILE="$RESULTS_DIR/${TEST_NAME}_size_snapshots.csv"
    echo "Timestamp,Table_Size_MB,Index_Size_MB" > "$SIZE_FILE"
fi

core_metrics_loop() {
    while true; do
        local timestamp metrics
        timestamp=$(date +"%Y-%m-%d %H:%M:%S")
        if ! metrics=$(psql_exec -At <<'EOF'
WITH activity AS (
    SELECT count(*)::int AS active_connections
    FROM pg_stat_activity
    WHERE state = 'active'
),
locks AS (
    SELECT count(*)::int AS blocked_queries
    FROM pg_locks
    WHERE NOT granted
),
dead AS (
    SELECT COALESCE(n_dead_tup, 0)::bigint AS dead_tuples
    FROM pg_stat_user_tables
    WHERE schemaname = 'dms' AND relname = 'reference'
),
cache AS (
    SELECT round(
        100.0 * sum(heap_blks_hit) / NULLIF(sum(heap_blks_hit) + sum(heap_blks_read), 0),
        2
    ) AS cache_hit_ratio
    FROM pg_statio_user_tables
),
tx AS (
    SELECT round(
        xact_commit::numeric / GREATEST(EXTRACT(EPOCH FROM (now() - stats_reset)), 1),
        2
    ) AS tx_per_sec
    FROM pg_stat_database
    WHERE datname = current_database()
)
SELECT
    activity.active_connections,
    locks.blocked_queries,
    dead.dead_tuples,
    COALESCE(cache.cache_hit_ratio, 100.0),
    COALESCE(tx.tx_per_sec, 0.0)
FROM activity, locks, dead, cache, tx;
EOF
        ); then
            log_warn "Failed to capture core metrics snapshot"
            echo "$timestamp,ERROR,ERROR,ERROR,ERROR,ERROR" >> "$MONITOR_FILE"
        else
            metrics=${metrics//$'\n'/}
            metrics=${metrics//|/,}
            echo "$timestamp,$metrics" >> "$MONITOR_FILE"
        fi
        sleep "$CORE_INTERVAL"
    done
}

size_snapshot_loop() {
    while true; do
        local timestamp sizes
        timestamp=$(date +"%Y-%m-%d %H:%M:%S")
        sizes=$(psql_exec -At <<'EOF'
SELECT
    round(pg_relation_size('dms.reference') / 1024.0 / 1024.0, 2),
    round(pg_indexes_size('dms.reference') / 1024.0 / 1024.0, 2);
EOF
)
        sizes=${sizes//$'\n'/}
        sizes=${sizes//|/,}
        echo "$timestamp,$sizes" >> "$SIZE_FILE"
        sleep "$HEAVY_INTERVAL"
    done
}

lock_snapshot_loop() {
    local lock_file header_written=false
    lock_file="$RESULTS_DIR/${TEST_NAME}_locks.log"
    if [ ! -f "$lock_file" ]; then
        echo "Timestamp|Blocked_PID|Blocked_User|Blocking_PID|Blocking_User|Blocked_Statement|Blocking_Statement" > "$lock_file"
        header_written=true
    fi
    while true; do
        local snapshot
        log_info "Capturing lock snapshot -> $lock_file"
        snapshot=$(psql_exec -At <<'EOF'
SELECT
    now(),
    blocked_locks.pid,
    blocked_activity.usename,
    blocking_locks.pid,
    blocking_activity.usename,
    regexp_replace(blocked_activity.query, E'[\\n\\r]+', ' ', 'g'),
    regexp_replace(blocking_activity.query, E'[\\n\\r]+', ' ', 'g')
FROM pg_catalog.pg_locks blocked_locks
JOIN pg_catalog.pg_stat_activity blocked_activity ON blocked_activity.pid = blocked_locks.pid
JOIN pg_catalog.pg_locks blocking_locks
    ON blocking_locks.locktype = blocked_locks.locktype
    AND blocking_locks.database IS NOT DISTINCT FROM blocked_locks.database
    AND blocking_locks.relation IS NOT DISTINCT FROM blocked_locks.relation
    AND blocking_locks.page IS NOT DISTINCT FROM blocked_locks.page
    AND blocking_locks.tuple IS NOT DISTINCT FROM blocked_locks.tuple
    AND blocking_locks.virtualxid IS NOT DISTINCT FROM blocked_locks.virtualxid
    AND blocking_locks.transactionid IS NOT DISTINCT FROM blocked_locks.transactionid
    AND blocking_locks.classid IS NOT DISTINCT FROM blocked_locks.classid
    AND blocking_locks.objid IS NOT DISTINCT FROM blocked_locks.objid
    AND blocking_locks.objsubid IS NOT DISTINCT FROM blocked_locks.objsubid
    AND blocking_locks.pid != blocked_locks.pid
JOIN pg_catalog.pg_stat_activity blocking_activity ON blocking_activity.pid = blocking_locks.pid
WHERE NOT blocked_locks.granted;
EOF
)
        if [ -z "$snapshot" ]; then
            echo "$(date +"%Y-%m-%d %H:%M:%S")| | | | | | " >> "$lock_file"
        else
            echo "$snapshot" >> "$lock_file"
        fi
        sleep "$HEAVY_INTERVAL"
    done
}

statement_snapshot_loop() {
    local stmt_file
    stmt_file="$RESULTS_DIR/${TEST_NAME}_slow_queries.log"
    if [ ! -f "$stmt_file" ]; then
        echo "Timestamp|Total_Exec_Time_ms|Mean_Exec_Time_ms|StdDev_Exec_Time_ms|Calls|Query" > "$stmt_file"
    fi
    while true; do
        local snapshot
        log_info "Capturing statement snapshot -> $stmt_file"
        snapshot=$(psql_exec -At <<'EOF'
SELECT
    now(),
    round(total_exec_time::numeric, 2),
    round(mean_exec_time::numeric, 2),
    round(stddev_exec_time::numeric, 2),
    calls,
    regexp_replace(query, E'[\\n\\r]+', ' ', 'g')
FROM pg_stat_statements
WHERE query NOT LIKE '%pg_stat%'
ORDER BY mean_exec_time DESC
LIMIT 20;
EOF
)
        if [ -z "$snapshot" ]; then
            echo "$(date +"%Y-%m-%d %H:%M:%S")|0|0|0|0|" >> "$stmt_file"
        else
            echo "$snapshot" >> "$stmt_file"
        fi
        sleep "$HEAVY_INTERVAL"
  	done
}

core_metrics_loop &
CORE_PID=$!

if [ "$RUN_SIZE_SNAP" = true ]; then
    size_snapshot_loop &
    SIZE_PID=$!
fi

if [ "$RUN_LOCK_SNAP" = true ]; then
    lock_snapshot_loop &
    LOCK_PID=$!
fi

if [ "$RUN_STATEMENT_SNAP" = true ]; then
    statement_snapshot_loop &
    STATEMENT_PID=$!
fi

cleanup() {
    log_info "Stopping monitoring processes..."
    kill "$CORE_PID" 2>/dev/null
    [ -n "$SIZE_PID" ] && kill "$SIZE_PID" 2>/dev/null
    [ -n "$LOCK_PID" ] && kill "$LOCK_PID" 2>/dev/null
    [ -n "$STATEMENT_PID" ] && kill "$STATEMENT_PID" 2>/dev/null
    log_info "Monitoring stopped"
}

trap cleanup EXIT

log_info "Core metrics sampling every ${CORE_INTERVAL}s -> $MONITOR_FILE"
if [ "$RUN_SIZE_SNAP" = true ]; then
    log_info "Size snapshots every ${HEAVY_INTERVAL}s -> $SIZE_FILE"
fi
[ "$RUN_LOCK_SNAP" = true ] && log_info "Lock snapshots enabled (interval ${HEAVY_INTERVAL}s)"
[ "$RUN_STATEMENT_SNAP" = true ] && log_info "Statement snapshots enabled (interval ${HEAVY_INTERVAL}s)"

wait
