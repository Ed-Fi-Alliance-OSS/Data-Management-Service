#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.

# DMS Load Test Smoke Test Runner
# This script runs a minimal smoke test against a local DMS instance

set -e

echo "🚀 Running DMS Load Test Smoke Test"
echo "====================================="

# Check if k6 is installed
if ! command -v k6 &> /dev/null; then
    echo "❌ k6 is not installed. Please install k6 first."
    echo "Visit: https://k6.io/docs/getting-started/installation/"
    exit 1
fi

# Check if npm dependencies are installed
if [ ! -d "node_modules" ]; then
    echo "📦 Installing npm dependencies..."
    npm install
fi

# Set minimal environment variables for smoke test
export API_BASE_URL="http://localhost:8080/api"
export OAUTH_TOKEN_URL="http://localhost:8080/api/oauth/token"
export CLIENT_ID="DmsConfigurationService"
export CLIENT_SECRET="s3creT@09"
export SCHOOL_COUNT=2
export STUDENT_COUNT=2
export STAFF_COUNT=2
export COURSES_PER_SCHOOL=2
export SECTIONS_PER_COURSE=2

echo ""
echo "Configuration:"
echo "  API URL: $API_BASE_URL"
echo "  Client: $CLIENT_ID"
echo "  Scale: $SCHOOL_COUNT schools, $STUDENT_COUNT students"
echo ""

# Run the smoke test
echo "🧪 Starting smoke test..."
k6 run src/scenarios/smoke.js

echo ""
echo "✅ Smoke test completed!"