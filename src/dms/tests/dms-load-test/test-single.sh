#!/bin/bash

# Test single resource creation
echo "🧪 Running single resource test..."

# Change to script directory
cd "$(dirname "$0")"

# Load environment variables
if [ -f .env.load-test ]; then
    echo "📄 Loading environment from .env.load-test"
    export $(cat .env.load-test | grep -v '^#' | xargs)
else
    echo "⚠️  Warning: .env.load-test not found."
fi

# Run single resource test
k6 run src/test-single-resource.js