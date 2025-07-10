# JWT Authentication Testing Guide

## Overview

This guide provides comprehensive instructions for testing JWT authentication in the Ed-Fi Data Management Service (DMS). It covers unit tests, integration tests, and manual testing procedures.

## Test Categories

### 1. Unit Tests

Unit tests for JWT components are located in:
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/JwtAuthenticationMiddlewareTests.cs`
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/JwtRoleAuthenticationMiddlewareTests.cs`

**Run unit tests:**
```bash
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit
```

**Key test scenarios:**
- Valid token authentication
- Expired token handling
- Invalid signature detection
- Missing authorization header
- Role-based access control
- Client-specific JWT rollout

### 2. Integration Tests

Integration tests are available in two formats:

#### SpecFlow/Reqnroll Tests
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/JwtAuthentication.feature`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/StepDefinitions/JwtAuthenticationStepDefinitions.cs`

**Run E2E tests:**
```bash
cd src/dms/tests/EdFi.DataManagementService.Tests.E2E
dotnet test
```

#### Direct Integration Tests
- `src/dms/tests/EdFi.DataManagementService.Tests.Integration/JwtAuthenticationIntegrationTests.cs`

**Run integration tests:**
```bash
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration
```

### 3. Manual Testing with .http Files

Manual test files using VS Code REST Client extension are located in `src/dms/tests/RestClient/`:

#### JWT Test Files

1. **jwt-authentication-flow.http**
   - Basic JWT authentication flow
   - Token acquisition from Keycloak
   - Success and failure scenarios
   - Different authorization schemes

2. **jwt-role-based-access.http**
   - Role-based authorization testing
   - Service role requirements
   - Access control verification

3. **jwt-client-authorizations.http**
   - Namespace prefix restrictions
   - Education organization restrictions
   - Combined authorization scenarios

4. **jwt-configuration-test.http**
   - Configuration variations
   - HTTPS metadata testing
   - Clock skew tolerance
   - Client-specific rollout

## Test Environment Setup

### Prerequisites

1. **Keycloak Running**
   ```powershell
   cd eng/docker-compose
   ./start-keycloak.ps1
   ./setup-keycloak.ps1
   ```

2. **DMS Running with JWT Enabled**
   ```powershell
   # Ensure appsettings.Development.json has:
   # "JwtAuthentication": { "Enabled": true }
   
   # Run DMS from Visual Studio or:
   dotnet run --project src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore
   ```

3. **VS Code REST Client Extension**
   - Install: `ext install humao.rest-client`
   - Open any `.http` file
   - Click "Send Request" above each request

### Configuration for Testing

#### Development Configuration (JWT Enabled)
```json
{
  "JwtAuthentication": {
    "Enabled": true,
    "Authority": "http://localhost:8045/realms/edfi",
    "Audience": "edfi-api",
    "MetadataAddress": "http://localhost:8045/realms/edfi/.well-known/openid-configuration",
    "RequireHttpsMetadata": false,
    "RoleClaimType": "role",
    "ClientRole": "service"
  }
}
```

#### Test Configuration (JWT Disabled)
```json
{
  "JwtAuthentication": {
    "Enabled": false
  }
}
```

## Test Execution Workflow

### 1. Verify JWT is Working

```bash
# Get a token from Keycloak
curl -X POST http://localhost:8045/realms/edfi/protocol/openid-connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=DmsConfigurationService" \
  -d "client_secret=s3creT@09"

# Use the token to access DMS
curl http://localhost:5198/data/ed-fi/students \
  -H "Authorization: Bearer <token-from-above>"
```

### 2. Run Automated Tests

```bash
# Run all JWT-related tests
dotnet test --filter "FullyQualifiedName~Jwt"

# Run with detailed output
dotnet test --filter "FullyQualifiedName~Jwt" --logger "console;verbosity=detailed"
```

### 3. Execute Manual Tests

1. Open VS Code in `src/dms/tests/RestClient/`
2. Start with `jwt-authentication-flow.http`
3. Execute requests sequentially
4. Verify expected responses

## Common Test Scenarios

### Scenario 1: Valid Authentication Flow
1. Obtain JWT token from Keycloak
2. Use token to access data endpoints
3. Verify successful responses

### Scenario 2: Authorization Failures
1. Test without token → Expect 401
2. Test with expired token → Expect 401
3. Test with invalid signature → Expect 401
4. Test with wrong auth scheme → Expect 401

### Scenario 3: Role-Based Access
1. Get token with service role
2. Access metadata endpoints → Expect 200
3. Get token without service role
4. Access metadata endpoints → Expect 403

### Scenario 4: Client Authorizations
1. Test namespace restrictions
2. Test education organization restrictions
3. Verify data filtering based on claims

## Troubleshooting

### Common Issues

1. **"Unable to retrieve metadata" error**
   - Ensure Keycloak is running
   - Check Authority URL is correct
   - Verify network connectivity

2. **All requests return 401**
   - Check JWT is enabled in configuration
   - Verify token format is correct
   - Check audience and issuer match

3. **Tests pass but manual testing fails**
   - Ensure using correct environment
   - Check for configuration differences
   - Verify Keycloak client setup

### Debug Tips

1. **Enable detailed logging:**
   ```json
   {
     "Serilog": {
       "MinimumLevel": {
         "Default": "DEBUG"
       }
     }
   }
   ```

2. **Inspect JWT tokens:**
   - Use https://jwt.io to decode tokens
   - Verify claims structure
   - Check expiration times

3. **Monitor Keycloak logs:**
   ```bash
   docker logs dms-keycloak -f
   ```

## CI/CD Integration

### GitHub Actions
```yaml
- name: Run JWT Tests
  run: |
    dotnet test --filter "FullyQualifiedName~Jwt" \
      --logger "trx;LogFileName=jwt-test-results.trx" \
      --results-directory ./TestResults
```

### Test Reports
- Unit test coverage: `dotnet test --collect:"XPlat Code Coverage"`
- Integration test results: Check `TestResults/*.trx` files

## Performance Considerations

1. **Token Validation Caching**
   - First request with new token: ~50-100ms
   - Subsequent requests: ~5-10ms

2. **Metadata Refresh**
   - Default refresh: Every 60 minutes
   - Automatic refresh: Every 24 hours

3. **Concurrent Requests**
   - Token validation is thread-safe
   - No performance degradation under load

## Security Best Practices

1. **Production Configuration**
   - Always set `RequireHttpsMetadata: true`
   - Use short token expiration times
   - Implement proper key rotation

2. **Test Environment**
   - Use separate Keycloak realm for testing
   - Don't use production secrets in tests
   - Clear test data after runs

3. **Monitoring**
   - Log authentication failures
   - Monitor for unusual patterns
   - Track token usage metrics