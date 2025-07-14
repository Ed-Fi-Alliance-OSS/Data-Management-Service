#!/bin/bash

# Debug version of load test runner
echo "🔍 DMS Load Test Debug Runner"
echo "============================="

# Change to script directory
cd "$(dirname "$0")"

# Load environment variables
if [ -f .env.load-test ]; then
    echo "📄 Loading environment from .env.load-test"
    export $(cat .env.load-test | grep -v '^#' | xargs)
else
    echo "⚠️  Warning: .env.load-test not found. Using existing environment variables."
fi

# Enable debug mode
export DEBUG=true

# Reduce scale for debugging
export SCHOOL_COUNT=2
export STUDENT_COUNT=10
export STAFF_COUNT=5
export VUS_LOAD_PHASE=1
export DURATION_LOAD_PHASE=30s

echo ""
echo "Debug Configuration:"
echo "  API URL: $API_BASE_URL"
echo "  Client: $CLIENT_ID"
echo "  Schools: $SCHOOL_COUNT"
echo "  Students: $STUDENT_COUNT"
echo "  Staff: $STAFF_COUNT"
echo "  VUs: $VUS_LOAD_PHASE"
echo "  Duration: $DURATION_LOAD_PHASE"
echo ""

# Run only the load phase for debugging
echo "🚀 Running load phase with debug logging..."
k6 run src/scenarios/load.js