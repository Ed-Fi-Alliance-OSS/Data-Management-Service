#!/bin/bash

# Run load phase test for Ed-Fi DMS Load Testing

echo "📊 Running Ed-Fi DMS Load Phase Test..."
echo "======================================"

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

# Display test configuration
echo "📋 Test Configuration:"
echo "  - API URL: $API_BASE_URL"
echo "  - Schools: $SCHOOL_COUNT"
echo "  - Students: $STUDENT_COUNT"
echo "  - Staff: $STAFF_COUNT"
echo "  - Virtual Users: $VUS_LOAD_PHASE"
echo "  - Duration: $DURATION_LOAD_PHASE"
echo ""

# Ask for confirmation
read -p "Start load test? (y/n) " -n 1 -r
echo ""
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Test cancelled."
    exit 1
fi

# Run load test with output
echo "🚀 Starting load phase test..."
k6 run \
    --out json=results/load-phase-$(date +%Y%m%d-%H%M%S).json \
    src/scenarios/load.js

echo "✅ Load phase test completed!"