#!/bin/bash

# Run full test suite for Ed-Fi DMS Load Testing

echo "🎯 Running Full Ed-Fi DMS Load Test Suite"
echo "========================================"

# Check if k6 is installed
if ! command -v k6 &> /dev/null; then
    echo "❌ k6 is not installed. Please install k6 first."
    echo "Visit: https://k6.io/docs/getting-started/installation/"
    exit 1
fi

# Check if .env file exists
if [ ! -f .env ]; then
    echo "⚠️  .env file not found. Copying from .env.example..."
    cp .env.example .env
    echo "Please configure .env with your API credentials."
    exit 1
fi

# Load environment variables
export $(cat .env | grep -v '^#' | xargs)

# Create results directory if it doesn't exist
mkdir -p results

# Timestamp for this test run
TIMESTAMP=$(date +%Y%m%d-%H%M%S)

echo "📋 Full Test Configuration:"
echo "  - API URL: $API_BASE_URL"
echo "  - Scale: $SCHOOL_COUNT schools, $STUDENT_COUNT students, $STAFF_COUNT staff"
echo "  - Load Phase: $VUS_LOAD_PHASE VUs for $DURATION_LOAD_PHASE"
echo "  - Read-Write Phase: $VUS_READWRITE_PHASE VUs for $DURATION_READWRITE_PHASE"
echo ""

# Ask for confirmation
read -p "Start full test suite? This will take ~1 hour. (y/n) " -n 1 -r
echo ""
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Test cancelled."
    exit 1
fi

# Phase 1: Smoke Test
echo ""
echo "🔧 Phase 1: Smoke Test"
echo "---------------------"
k6 run \
    --out json=results/smoke-$TIMESTAMP.json \
    src/scenarios/smoke.js

if [ $? -ne 0 ]; then
    echo "❌ Smoke test failed. Aborting test suite."
    exit 1
fi

echo "✅ Smoke test passed!"
sleep 5

# Phase 2: Load Test
echo ""
echo "📊 Phase 2: Load Test (POST operations)"
echo "--------------------------------------"
k6 run \
    --out json=results/load-$TIMESTAMP.json \
    src/scenarios/load.js

if [ $? -ne 0 ]; then
    echo "⚠️  Load test encountered errors. Check results/load-$TIMESTAMP.json"
fi

echo "✅ Load test completed!"
sleep 5

# Phase 3: Read-Write Test
echo ""
echo "🔄 Phase 3: Read-Write Test (Mixed CRUD)"
echo "---------------------------------------"
k6 run \
    --out json=results/readwrite-$TIMESTAMP.json \
    src/scenarios/readwrite.js

if [ $? -ne 0 ]; then
    echo "⚠️  Read-write test encountered errors. Check results/readwrite-$TIMESTAMP.json"
fi

echo ""
echo "🎉 Full test suite completed!"
echo "📁 Results saved in: results/*-$TIMESTAMP.json"
echo ""
echo "To analyze results, use k6's built-in tools or export to your monitoring platform."