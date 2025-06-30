# Test Plan: Core Tests for Authentication/Authorization After JWT Refactoring

## Overview

This document outlines the comprehensive test plan for authentication and authorization in the DMS Core project after refactoring JWT processing from the ASP.NET Core frontend to the Core middleware pipeline.

## Analysis of Frontend Tests

After analyzing the frontend tests, I identified the following authentication/authorization behaviors that were previously tested:

### 1. **CoreEndpointModuleTests.cs**
- Tests authorization with valid client token → expects 200 OK
- Tests authorization with invalid client token → expects 403 Forbidden
- Uses TestAuthHandler to simulate authentication in ASP.NET Core

### 2. **TokenEndpointModuleTests.cs**
- Tests OAuth token endpoint with JSON content
- Tests OAuth token endpoint with form-encoded content
- Tests token response format (access_token, expires_in, token_type)
- Verifies proper token endpoint behavior

### 3. **ApiClientDetailsProviderTests.cs**
- Tests extraction of scopes from JWT claims
- Tests extraction of namespace prefixes from JWT claims
- Tests fallback to token hash when no JTI claim exists
- Verifies proper claim parsing

### 4. **MockTokenProvider.cs**
- Utility for generating test JWT tokens
- Used across multiple test files

## Current Core Test Coverage

We've already created comprehensive tests in Core:

### ✅ Completed Tests

1. **DecodeJwtToClientAuthorizationsMiddlewareTests.cs**
   - Missing Authorization header → 401 Unauthorized
   - Invalid Authorization format → 401 Unauthorized
   - Invalid JWT token → 401 Unauthorized
   - Token validation failure → 401 Unauthorized
   - Missing required role → 401 Unauthorized
   - Valid token → Success with ClientAuthorizations
   - Exception handling → 401 Unauthorized
   - Case-insensitive Bearer prefix → Success

2. **JwtTokenValidatorTests.cs**
   - Valid token validation
   - Expired token handling
   - Invalid audience/issuer
   - Invalid signature
   - Malformed tokens
   - Configuration caching

3. **ResourceActionAuthorizationMiddlewareTests.cs**
   - Null ClientAuthorizations → 403 Forbidden
   - Valid claim set → Success
   - No matching claim set → 403 Forbidden
   - Authorization strategy validation

4. **JwtAuthenticationFlowIntegrationTests.cs**
   - End-to-end JWT flow
   - Missing auth header → 401
   - Invalid token → 401
   - No permissions → 403
   - Education org filtering
   - Namespace prefix filtering

## Additional Tests Needed in Core

### 1. **Enhanced ApiClientDetailsProvider Tests**

The current implementation in `DecodeJwtToClientAuthorizationsMiddleware` handles claim extraction, but we need additional edge case tests:

#### Test Scenarios to Add:
- **Multiple education organization IDs**: Verify correct parsing of multiple org IDs
- **Empty namespace prefixes**: Handle empty or whitespace-only prefixes
- **Missing scope claim**: Default behavior when scope is missing
- **Invalid claim formats**: Non-numeric education org IDs, malformed data
- **Special characters in claims**: Unicode, escaped characters
- **Very long claim values**: Performance and boundary testing
- **Duplicate claims**: How duplicates are handled

### 2. **OAuth Manager Tests**

If OAuth token management moves to Core:
- Token generation with various grant types
- Token refresh scenarios
- Invalid client credentials
- Token expiration handling
- Concurrent token requests

### 3. **Authorization Policy Tests**

Test various authorization scenarios at the Core level:
- **Role-based authorization**: Different role combinations
- **Claim-based authorization**: Complex claim requirements
- **Combined role and claim requirements**
- **Hierarchical permissions**: Parent/child relationships
- **Dynamic permission changes**: Runtime permission updates

### 4. **Enhanced Integration Tests**

Additional scenarios for `JwtAuthenticationFlowIntegrationTests.cs`:
- **Token refresh flow**: Complete refresh cycle
- **Multiple concurrent requests**: Thread safety
- **Different token issuers**: Multi-tenant scenarios
- **Token revocation**: Handling revoked tokens
- **Performance under load**: Many simultaneous auth requests
- **Caching behavior**: Token validation caching

## Implementation Details

### Phase 1: Edge Case Tests for Claim Extraction

Create additional tests in `DecodeJwtToClientAuthorizationsMiddlewareTests.cs`:

```csharp
[Test]
public async Task Execute_MultipleEducationOrganizationIds_ExtractsAll()
{
    // Test with multiple education org ID claims
}

[Test]
public async Task Execute_EmptyNamespacePrefixes_HandledGracefully()
{
    // Test with empty or whitespace namespace prefixes
}

[Test]
public async Task Execute_InvalidEducationOrgIdFormat_Returns401()
{
    // Test with non-numeric education org IDs
}

[Test]
public async Task Execute_VeryLongClaimValues_HandledCorrectly()
{
    // Test with extremely long claim values
}
```

### Phase 2: Authorization Enhancement Tests

Update `ResourceActionAuthorizationMiddlewareTests.cs`:

```csharp
[Test]
public async Task Execute_ComplexClaimRequirements_ValidatesCorrectly()
{
    // Test complex authorization scenarios
}

[Test]
public async Task Execute_HierarchicalPermissions_InheritsCorrectly()
{
    // Test permission inheritance
}
```

### Phase 3: Integration Test Enhancements

Add to `JwtAuthenticationFlowIntegrationTests.cs`:

```csharp
[Test]
public async Task CompleteJwtFlow_ConcurrentRequests_AllSucceed()
{
    // Test thread safety with multiple simultaneous requests
}

[Test]
public async Task CompleteJwtFlow_TokenNearExpiration_HandledGracefully()
{
    // Test tokens that are about to expire
}

[Test]
public async Task CompleteJwtFlow_DifferentTokenVersions_CompatibilityMaintained()
{
    // Test backward compatibility with different token formats
}
```

## Test Data Requirements

### JWT Token Test Data
- Valid tokens with various claim combinations
- Expired tokens with different expiration times
- Tokens with invalid signatures
- Tokens from different issuers
- Tokens with special characters in claims

### Claim Set Test Data
- Standard claim sets (SIS-Vendor, Assessment-Vendor)
- Empty claim sets
- Claim sets with complex authorization rules
- Overlapping claim sets

## Success Criteria

1. **All authentication failures return 401 Unauthorized**
   - Consistent error responses
   - Appropriate error messages
   - No information leakage

2. **All authorization failures return 403 Forbidden**
   - Clear distinction from authentication failures
   - Detailed error messages for debugging
   - Proper audit logging

3. **100% code coverage for JWT-related code**
   - All branches tested
   - All edge cases covered
   - Performance benchmarks met

4. **Integration tests pass consistently**
   - No flaky tests
   - Reasonable execution time
   - Clear failure messages

## Testing Strategy

### Unit Tests
- Fast execution (< 100ms per test)
- No external dependencies
- Focused on single responsibility
- Comprehensive edge case coverage

### Integration Tests
- Complete scenario testing
- Real JWT token validation
- Database integration where needed
- Performance validation

### Manual Testing Checklist
- [ ] Test with real Keycloak instance
- [ ] Test with Azure AD tokens
- [ ] Test with custom identity providers
- [ ] Load testing with JMeter/K6
- [ ] Security scanning with OWASP tools

## Maintenance

### Regular Review
- Monthly review of test coverage
- Quarterly security assessment
- Performance baseline updates
- Documentation updates

### Test Data Management
- Automated test data generation
- Secure storage of test credentials
- Regular rotation of test keys
- Version control for test data

## Conclusion

This comprehensive test plan ensures that the JWT refactoring maintains security, performance, and reliability while moving authentication logic from the frontend to the Core middleware pipeline. All critical paths are tested, edge cases are covered, and the system remains maintainable and extensible.