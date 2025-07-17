#\!/bin/bash

# Get token
TOKEN=$(curl -s -X POST "http://localhost:8080/api/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials&client_id=b02ebe1d-0a5d-4510-b68b-7c23c8eab77a&client_secret=a3bd055a-a3f0-403f-8ee8-2c5b36b604bb"  < /dev/null |  jq -r '.access_token')

echo "Token obtained: ${TOKEN:0:20}..."

# Test POST to academicSubjectDescriptor
echo -e "\nTesting POST to academicSubjectDescriptors:"
curl -v -X POST "http://localhost:8080/api/data/ed-fi/academicSubjectDescriptors" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "codeValue": "TestSubject",
    "shortDescription": "Test Subject",
    "description": "Test Subject Description",
    "namespace": "uri://ed-fi.org/academicSubjectDescriptor"
  }'
