#!/bin/bash

# Test authentication only
echo "🔐 Running authentication test..."

# Change to script directory
cd "$(dirname "$0")"

# Load environment variables
if [ -f .env.load-test ]; then
    echo "📄 Loading environment from .env.load-test"
    export $(cat .env.load-test | grep -v '^#' | xargs)
else
    echo "⚠️  Warning: .env.load-test not found. Using existing environment variables."
fi

# Add DEBUG flag
export DEBUG=true

# Run auth test
k6 run src/test-auth.js