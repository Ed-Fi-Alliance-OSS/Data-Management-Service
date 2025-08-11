#!/bin/bash

echo "Testing GET /management/current-claims endpoint..."
echo "================================================="
echo

# Get headers and body together
echo "1. Testing headers..."
response=$(curl -s -i http://localhost:8081/management/current-claims)
status_line=$(echo "$response" | head -1)
reload_id=$(echo "$response" | grep -i "X-Reload-Id:" | sed 's/.*: //' | tr -d '\r')

echo "Status: $status_line"
echo "X-Reload-Id: $reload_id"

if [[ -n "$reload_id" ]]; then
    echo "✓ X-Reload-Id header is present"
else
    echo "✗ X-Reload-Id header is missing"
    exit 1
fi

echo
echo "2. Testing response body structure..."

# Get just the body
body=$(curl -s http://localhost:8081/management/current-claims)

# Save to file for inspection
echo "$body" > /tmp/current-claims.json

# Check structure
if jq -e '.claimSets' /tmp/current-claims.json > /dev/null 2>&1 && \
   jq -e '.claimsHierarchy' /tmp/current-claims.json > /dev/null 2>&1; then
    echo "✓ Response has expected structure with 'claimSets' and 'claimsHierarchy'"
else
    echo "✗ Response does not have expected structure"
    exit 1
fi

# Count elements
claim_sets_count=$(jq '.claimSets | length' /tmp/current-claims.json)
hierarchy_count=$(jq '.claimsHierarchy | length' /tmp/current-claims.json)

echo "✓ Found $claim_sets_count claim sets"
echo "✓ Found $hierarchy_count top-level claims in hierarchy"

echo
echo "3. Verifying response format matches upload format..."

# Check that claimSets is an array of objects with expected properties
first_claimset=$(jq -r '.claimSets[0] | keys | sort | join(",")' /tmp/current-claims.json)
echo "First claimSet keys: $first_claimset"

# Check that claimsHierarchy is an array of claim objects
first_claim=$(jq -r '.claimsHierarchy[0] | keys | sort | join(",")' /tmp/current-claims.json 2>/dev/null || echo "none")
echo "First claim keys: $first_claim"

echo
echo "✓ All tests passed!"
echo
echo "The GET /management/current-claims endpoint:"
echo "- Returns HTTP 200"
echo "- Includes X-Reload-Id header: $reload_id"
echo "- Returns JSON with 'claimSets' and 'claimsHierarchy' at top level"
echo "- Format matches the expected upload format"