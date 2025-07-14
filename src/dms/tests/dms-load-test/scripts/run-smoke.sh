#!/bin/bash

# Run smoke test for Ed-Fi DMS Load Testing

echo "🔧 Running Ed-Fi DMS Smoke Test..."
echo "================================"

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

# Run smoke test
echo "🚀 Starting smoke test..."
k6 run src/scenarios/smoke.js

echo "✅ Smoke test completed!"