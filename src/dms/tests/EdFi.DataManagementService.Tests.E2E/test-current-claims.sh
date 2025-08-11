#!/bin/bash

# Test the current-claims endpoint

echo "Testing GET /management/current-claims endpoint..."
echo "=================================="
echo

# Make the request and capture both headers and body
response=$(curl -s -i http://localhost:8081/management/current-claims)

# Extract the status code
status_code=$(echo "$response" | grep "HTTP/1.1" | awk '{print $2}')
echo "Status Code: $status_code"

# Extract the X-Reload-Id header
reload_id=$(echo "$response" | grep -i "X-Reload-Id:" | cut -d' ' -f2 | tr -d '\r')
echo "X-Reload-Id: $reload_id"

# Extract the body (everything after the empty line)
body=$(echo "$response" | sed '1,/^$/d')

# Pretty print the JSON to verify structure
echo
echo "Response Body (first 1000 chars):"
echo "$body" | jq -c . | head -c 1000
echo
echo

# Check if the response has the expected structure
if echo "$body" | jq -e '.claimSets' > /dev/null 2>&1 && \
   echo "$body" | jq -e '.claimsHierarchy' > /dev/null 2>&1; then
    echo "✓ Response has expected structure with 'claimSets' and 'claimsHierarchy'"
else
    echo "✗ Response does not have expected structure"
    exit 1
fi

# Count claim sets
claim_sets_count=$(echo "$body" | jq '.claimSets | length')
echo "✓ Found $claim_sets_count claim sets"

# Count top-level claims in hierarchy
hierarchy_count=$(echo "$body" | jq '.claimsHierarchy | length')
echo "✓ Found $hierarchy_count top-level claims in hierarchy"

echo
echo "Test completed successfully!"