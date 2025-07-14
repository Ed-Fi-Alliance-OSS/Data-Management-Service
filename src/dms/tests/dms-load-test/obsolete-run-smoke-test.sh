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

# Check if .env.load-test exists, if not create a client
if [ ! -f ".env.load-test" ]; then
    echo "🔧 No load test client found. Setting up authorized client..."
    node src/utils/setupLoadTestClient.js
    if [ $? -ne 0 ]; then
        echo "❌ Failed to set up load test client"
        exit 1
    fi
    echo ""
fi

# Load environment variables from .env.load-test
if [ -f ".env.load-test" ]; then
    echo "📋 Loading configuration from .env.load-test"
    export $(grep -v '^#' .env.load-test | xargs)
else
    echo "❌ .env.load-test not found after setup"
    exit 1
fi

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