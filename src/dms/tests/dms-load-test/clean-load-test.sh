#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.

# DMS Load Test Cleanup Script
# This script removes the load test client configuration

set -e

echo "🧹 Cleaning up DMS Load Test Configuration"
echo "=========================================="

# Remove .env.load-test if it exists
if [ -f ".env.load-test" ]; then
    echo "📄 Removing .env.load-test..."
    rm .env.load-test
    echo "✅ Configuration file removed"
else
    echo "ℹ️  No .env.load-test file found"
fi

# Note about vendor/application cleanup
echo ""
echo "⚠️  Note: This script only removes the local configuration file."
echo "   Vendors and applications created in the Config Service are not removed."
echo "   They can be manually deleted via the Config Service API if needed."
echo ""

echo "✅ Cleanup complete!"