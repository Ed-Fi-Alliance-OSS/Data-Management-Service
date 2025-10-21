#!/bin/bash

echo "DMS Reference Table Performance Testing - Quick Start"
echo "====================================================="
echo ""
echo "This script will set up everything needed for performance testing."
echo ""
echo "WARNING: perf-claude currently provisions Document/Alias/Reference using local DDL and is not sourced from the canonical deploy scripts."
echo ""

# Check prerequisites
echo "Checking prerequisites..."

if ! command -v psql &> /dev/null; then
    echo "ERROR: PostgreSQL client (psql) not found. Please install PostgreSQL."
    exit 1
fi

if ! command -v python3 &> /dev/null; then
    echo "WARNING: Python3 not found. Concurrent load tests will be skipped."
fi

# Navigate to scripts directory
cd scripts || exit 1

source ./config.sh

echo ""
echo "Step 1: Setting up test database"
echo "---------------------------------"
read -p "This will create a new database. Continue? (y/n): " confirm
if [ "$confirm" = "y" ]; then
    ./setup-test-db.sh --force
else
    echo "Skipping database setup"
fi

echo ""
echo "Step 2: Generating test data (this may take a while)"
echo "----------------------------------------------------"
echo "Default pipeline:"
echo "  - Generates deterministic CSVs (100k docs, ~20M refs)"
echo "  - Loads them with COPY for fast, repeatable setup"
echo ""
echo "If you need the legacy in-database generator (chunked with resume support), run:"
echo "  ./generate-test-data.sh --mode sql"
echo ""
read -p "Run the default CSV pipeline now? (y/n): " confirm
if [ "$confirm" = "y" ]; then
    ./generate-test-data.sh --mode csv
else
    echo "Skipping data generation"
fi

echo ""
echo "Step 3: Running performance tests"
echo "---------------------------------"
echo "Available test options:"
echo "  1. Quick test (current implementation only)"
echo "  2. Comparison test (current vs alternatives)"
echo "  3. Full test suite (all tests + load testing)"
echo ""
read -p "Select option (1-3): " option

case $option in
    1)
        echo "Running quick test..."
        label="quick_current_insert"
        reset_observability_counters "$label"
        capture_observability_snapshot "$label" "before"
        PGAPPNAME="perf_${label}" psql_timed -v ON_ERROR_STOP=1 -f ../sql/current/test_current_insert_references.sql \
            > "$RESULTS_DIR/${label}_output.txt" 2>&1
        status=$?
        capture_observability_snapshot "$label" "after"

        if [ $status -eq 0 ]; then
            echo "Quick test completed. Review:"
        else
            echo "Quick test encountered errors. Review output:"
        fi
        echo "  $RESULTS_DIR/${label}_output.txt"
        echo "  $RESULTS_DIR/${label}_before_observability.txt"
        echo "  $RESULTS_DIR/${label}_after_observability.txt"
        ;;
    2)
        echo "Running comparison test..."
        ./run-all-tests.sh
        ;;
    3)
        echo "Running full test suite..."
        ./run-all-tests.sh
        echo "Generating report..."
        ./generate-report.sh
        ;;
    *)
        echo "Invalid option"
        ;;
esac

echo ""
echo "Testing complete! Check the results/ directory for output."
echo ""
echo "To view the latest results:"
echo "  cat results/*/analysis_report.txt"
echo "  ls results/*/*_after_observability.txt"
echo ""
echo "Database statistics snapshots:"
echo "  less results/*/before_tests_pg_stats.txt"
echo "  less results/*/after_tests_pg_stats.txt"
echo ""
echo "To run specific tests:"
echo "  cd scripts"
echo "  psql -f ../sql/current/test_current_insert_references.sql"
