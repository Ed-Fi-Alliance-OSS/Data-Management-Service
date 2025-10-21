#!/bin/bash

export RESULTS_TIMESTAMP="${RESULTS_TIMESTAMP:-$(date +%Y%m%d_%H%M%S)}"

source $(dirname "$0")/config.sh

# Check if a test file is provided
if [ $# -eq 0 ]; then
    echo "Usage: $0 <test-sql-file>"
    echo ""
    echo "Available tests:"
    echo "  Current Implementation:"
    ls -1 ../sql/current/*.sql 2>/dev/null | sed 's/^/    /'
    echo ""
    echo "  Alternative Implementations:"
    ls -1 ../sql/alternatives/*.sql 2>/dev/null | sed 's/^/    /'
    echo ""
    echo "  Test Scenarios:"
    ls -1 ../sql/scenarios/*.sql 2>/dev/null | sed 's/^/    /'
    exit 1
fi

TEST_FILE="$1"

# Check if file exists
if [ ! -f "$TEST_FILE" ]; then
    # Try to find the file in common locations
    if [ -f "../sql/current/$TEST_FILE" ]; then
        TEST_FILE="../sql/current/$TEST_FILE"
    elif [ -f "../sql/alternatives/$TEST_FILE" ]; then
        TEST_FILE="../sql/alternatives/$TEST_FILE"
    elif [ -f "../sql/scenarios/$TEST_FILE" ]; then
        TEST_FILE="../sql/scenarios/$TEST_FILE"
    else
        log_error "Test file not found: $TEST_FILE"
        exit 1
    fi
fi

TEST_NAME=$(basename "$TEST_FILE" .sql)
export RESULTS_DIR="${RESULTS_ROOT}/${RESULTS_TIMESTAMP}_${TEST_NAME}"
mkdir -p "$RESULTS_DIR"

log_info "Running test: $TEST_NAME"
log_info "Test file: $TEST_FILE"
log_info "Results will be saved to: $RESULTS_DIR"

# Start monitoring if requested
if [ "${MONITOR:-false}" = "true" ]; then
    log_info "Starting performance monitoring..."
    ../scripts/monitor-performance.sh "$TEST_NAME" &
    MONITOR_PID=$!
    sleep 2
fi

# Run the test
log_info "Executing test..."
reset_observability_counters "$TEST_NAME"
capture_observability_snapshot "$TEST_NAME" "before"

PGAPPNAME="perf_${TEST_NAME}" psql_timed -v ON_ERROR_STOP=1 -f "$TEST_FILE" \
    > "$RESULTS_DIR/test_output.txt" 2>&1
STATUS=$?

capture_observability_snapshot "$TEST_NAME" "after"

if [ $STATUS -eq 0 ]; then
    log_info "✓ Test completed successfully"

    # Show summary
    echo ""
    echo "Test Results Summary:"
    echo "===================="
    psql_exec -t <<EOF
SELECT
    test_name,
    round(avg_latency_ms::numeric, 2) || ' ms' as avg_latency,
    rows_affected,
    round(duration_ms::numeric, 2) || ' ms' as total_duration
FROM dms.perf_test_results
WHERE test_name LIKE '%${TEST_NAME}%'
  AND created_at > NOW() - INTERVAL '5 minutes'
ORDER BY created_at DESC
LIMIT 1;
EOF

else
    log_error "✗ Test failed. Check output for errors."
fi

# Stop monitoring if it was started
if [ -n "$MONITOR_PID" ]; then
    log_info "Stopping monitoring..."
    kill $MONITOR_PID 2>/dev/null
fi

echo ""
echo "Full output saved to: $RESULTS_DIR/test_output.txt"
echo "Observability snapshots:"
echo "  $RESULTS_DIR/${TEST_NAME}_before_observability.txt"
echo "  $RESULTS_DIR/${TEST_NAME}_after_observability.txt"
echo ""
echo "To view detailed results:"
echo "  cat $RESULTS_DIR/test_output.txt"
echo ""
echo "To compare with other tests:"
echo "  psql -c \"SELECT * FROM dms.perf_test_results ORDER BY created_at DESC LIMIT 10\""
