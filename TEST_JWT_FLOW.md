# Testing JWT Authentication Flow

## Prerequisites

1. Keycloak running at http://localhost:8045
2. DMS and Configuration Service running
3. Valid client credentials configured in Keycloak

## Test Steps

### 1. Obtain JWT Token from Keycloak

```bash
# Get token from Keycloak
TOKEN=$(curl -s -X POST http://localhost:8045/realms/edfi/protocol/openid-connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=your-client-id" \
  -d "client_secret=your-client-secret" \
  | jq -r '.access_token')

echo "Token: $TOKEN"
```

### 2. Test Data Endpoint with JWT

```bash
# Test GET request to data endpoint
curl -v http://localhost:5198/data/ed-fi/students \
  -H "Authorization: Bearer $TOKEN" \
  -H "Accept: application/json"
```

### 3. Test Without Token (Should Fail)

```bash
# This should return 401 Unauthorized when JWT is enabled
curl -v http://localhost:5198/data/ed-fi/students \
  -H "Accept: application/json"
```

## Expected Behavior

### When JWT is Disabled (default):
- All requests pass through without authentication
- No 401 errors

### When JWT is Enabled:
- Requests without valid Bearer token return 401
- Requests with valid token are processed
- Token validation happens in Core
- ClientAuthorizations extracted from token

## Troubleshooting

### Common Issues:

1. **500 Internal Server Error**
   - Check if Keycloak is running
   - Verify MetadataAddress is accessible
   - Check logs for OIDC metadata retrieval errors

2. **401 Unauthorized**
   - Verify token is not expired
   - Check audience and issuer match configuration
   - Ensure client has proper roles

3. **Configuration Issues**
   - Verify JwtAuthentication:Enabled is true
   - Check Authority URL is correct
   - Ensure MetadataAddress points to valid OIDC endpoint

## Configuration Verification

Check current configuration:
```bash
# View effective configuration
cat appsettings.json | jq '.JwtAuthentication'
cat appsettings.Development.json | jq '.JwtAuthentication'
```

## Logs to Monitor

Watch for these log messages:
- "JWT authentication successful for TokenId: {TokenId}"
- "Token validation failed"
- "JWT authentication is disabled, skipping middleware"

## Comprehensive Testing Resources

For detailed testing procedures, see:
- **JWT Testing Guide**: [JWT_TESTING_GUIDE.md](JWT_TESTING_GUIDE.md)
- **Manual Test Files**: `src/dms/tests/RestClient/jwt-*.http`
- **Integration Tests**: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/JwtAuthenticationIntegrationTests.cs`
- **E2E Tests**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/JwtAuthentication.feature`