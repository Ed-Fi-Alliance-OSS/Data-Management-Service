#\!/bin/bash

# Try Keycloak OAuth endpoint instead
echo "Testing Keycloak OAuth endpoint..."
TOKEN_RESPONSE=$(curl -s -X POST "http://localhost:8045/realms/dms/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials&client_id=b02ebe1d-0a5d-4510-b68b-7c23c8eab77a&client_secret=a3bd055a-a3f0-403f-8ee8-2c5b36b604bb")

echo "Token response: $TOKEN_RESPONSE"

TOKEN=$(echo "$TOKEN_RESPONSE"  < /dev/null |  jq -r '.access_token')
echo -e "\nToken obtained: ${TOKEN:0:50}..."

if [ "$TOKEN" \!= "null" ]; then
  echo -e "\nTesting POST to gradeLevelDescriptors:"
  curl -v -X POST "http://localhost:8080/api/data/ed-fi/gradeLevelDescriptors" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{
      "codeValue": "TestGrade",
      "shortDescription": "Test Grade",
      "description": "Test Grade Description",
      "namespace": "uri://ed-fi.org/gradeLevelDescriptor"
    }'
fi
