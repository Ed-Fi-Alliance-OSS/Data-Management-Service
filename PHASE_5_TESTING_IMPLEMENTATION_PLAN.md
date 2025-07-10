# Phase 5: JWT Authentication Testing Implementation Plan

## Overview

This plan details the implementation of comprehensive testing for the JWT authentication migration, including unit tests, integration tests, and manual testing using VS Code REST Client .http files.

## Step 5.1: Unit Test Coverage

### 5.1.1 Core JWT Middleware Tests

**File**: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/JwtAuthenticationMiddlewareTests.cs`

Create comprehensive tests for JWT authentication middleware:

```csharp
[TestFixture]
public class JwtAuthenticationMiddlewareTests
{
    // Test scenarios:
    // 1. Valid token with proper claims - should extract ClientAuthorizations
    // 2. Expired token - should return 401
    // 3. Invalid signature - should return 401
    // 4. Missing authorization header - should skip middleware
    // 5. Invalid header format - should return 401
    // 6. JWT disabled - should skip middleware
    // 7. Token with custom claims - should extract properly
}
```

### 5.1.2 JWT Role Authentication Middleware Tests

**File**: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/JwtRoleAuthenticationMiddlewareTests.cs`

Test role-based authentication:

```csharp
[TestFixture]
public class JwtRoleAuthenticationMiddlewareTests
{
    // Test scenarios:
    // 1. Token with required role - should pass through
    // 2. Token without required role - should return 403
    // 3. Multiple roles - should validate properly
    // 4. Role claim type configuration - should use correct claim
}
```

### 5.1.3 JWT Service Registration Tests

**File**: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DmsCoreServiceExtensionsTests.cs`

Test service registration and configuration:

```csharp
[TestFixture]
public class DmsCoreServiceExtensionsTests
{
    // Test scenarios:
    // 1. AddJwtAuthentication registers all required services
    // 2. Configuration binding works correctly
    // 3. Feature flags are respected
    // 4. OIDC metadata caching is configured
}
```

## Step 5.2: Integration Tests

### 5.2.1 End-to-End JWT Flow Tests

**File**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/JwtAuthenticationTests.cs`

Create integration tests that simulate real JWT authentication flows:

```csharp
[TestFixture]
public class JwtAuthenticationTests : E2ETestBase
{
    [Test]
    public async Task DataEndpoint_WithValidToken_ReturnsSuccess()
    {
        // Arrange
        var token = await GetValidTokenFromKeycloak();
        
        // Act
        var response = await HttpClient.GetAsync("/data/ed-fi/students", 
            headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", token));
        
        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
    
    [Test]
    public async Task DataEndpoint_WithoutToken_Returns401()
    {
        // Act
        var response = await HttpClient.GetAsync("/data/ed-fi/students");
        
        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
    
    [Test]
    public async Task MetadataEndpoint_WithServiceRole_ReturnsSuccess()
    {
        // Test role-based access for metadata endpoints
    }
    
    [Test]
    public async Task TokenEndpoint_AllowsAnonymous_ReturnsSuccess()
    {
        // Verify token endpoint works without authentication
    }
}
```

### 5.2.2 Client Authorization Tests

**File**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/ClientAuthorizationTests.cs`

Test that JWT claims are properly extracted and used for authorization:

```csharp
[TestFixture]
public class ClientAuthorizationTests : E2ETestBase
{
    [Test]
    public async Task Token_WithNamespacePrefix_RestrictsAccess()
    {
        // Test namespace-based authorization from JWT claims
    }
    
    [Test]
    public async Task Token_WithEducationOrganizations_RestrictsAccess()
    {
        // Test education organization-based authorization
    }
}
```

## Step 5.3: Manual Testing with .http Files

### 5.3.1 JWT Authentication Flow Test File

**File**: `src/dms/tests/RestClient/jwt-authentication-flow.http`

Create comprehensive JWT testing scenarios:

```http
### Variables
@keycloakUrl = http://localhost:8045
@dmsPort = 5198
@configPort = 5126
@realm = edfi
@clientId = dms-test-client
@clientSecret = test-secret-123

### 1. Get JWT Token from Keycloak
# @name getJwtToken
POST {{keycloakUrl}}/realms/{{realm}}/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id={{clientId}}
&client_secret={{clientSecret}}

###
@jwtToken = {{getJwtToken.response.body.access_token}}

### 2. Test Data Endpoint with Valid JWT
GET http://localhost:{{dmsPort}}/data/ed-fi/students
Authorization: Bearer {{jwtToken}}

### 3. Test Data Endpoint without Token (Should Fail)
GET http://localhost:{{dmsPort}}/data/ed-fi/students

### 4. Test Metadata Endpoint with JWT
GET http://localhost:{{dmsPort}}/metadata
Authorization: Bearer {{jwtToken}}

### 5. Test Discovery Endpoint (Should Allow Anonymous)
GET http://localhost:{{dmsPort}}/

### 6. Create Descriptor with JWT
POST http://localhost:{{dmsPort}}/data/ed-fi/gradeLevelDescriptors
Authorization: Bearer {{jwtToken}}
Content-Type: application/json

{
    "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
    "codeValue": "First Grade",
    "shortDescription": "First Grade"
}

### 7. Test with Expired Token
# Use a token that's been saved and is now expired
@expiredToken = eyJhbGciOiJSUzI1NiIsInR5cCIgOiAiSldUIiwia2lkIiA6ICJmNEV4...
GET http://localhost:{{dmsPort}}/data/ed-fi/students
Authorization: Bearer {{expiredToken}}

### 8. Test with Invalid Token
GET http://localhost:{{dmsPort}}/data/ed-fi/students
Authorization: Bearer invalid-token-here
```

### 5.3.2 Role-Based Access Test File

**File**: `src/dms/tests/RestClient/jwt-role-based-access.http`

Test role-based authorization scenarios:

```http
### Variables
@keycloakUrl = http://localhost:8045
@dmsPort = 5198
@realm = edfi

### 1. Get Token with Service Role
# @name getServiceRoleToken
POST {{keycloakUrl}}/realms/{{realm}}/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id=service-client
&client_secret=service-secret

###
@serviceToken = {{getServiceRoleToken.response.body.access_token}}

### 2. Get Token without Service Role
# @name getBasicToken
POST {{keycloakUrl}}/realms/{{realm}}/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id=basic-client
&client_secret=basic-secret

###
@basicToken = {{getBasicToken.response.body.access_token}}

### 3. Test Metadata with Service Role (Should Succeed)
GET http://localhost:{{dmsPort}}/metadata
Authorization: Bearer {{serviceToken}}

### 4. Test Metadata without Service Role (Should Fail)
GET http://localhost:{{dmsPort}}/metadata
Authorization: Bearer {{basicToken}}

### 5. Test Management Endpoints with Different Roles
GET http://localhost:{{dmsPort}}/management/health
Authorization: Bearer {{serviceToken}}

### 6. Test Data Endpoints (Should Work with Any Valid Token)
GET http://localhost:{{dmsPort}}/data/ed-fi/students
Authorization: Bearer {{basicToken}}
```

### 5.3.3 Client Authorization Test File

**File**: `src/dms/tests/RestClient/jwt-client-authorizations.http`

Test namespace and education organization restrictions:

```http
### Variables
@keycloakUrl = http://localhost:8045
@dmsPort = 5198
@realm = edfi

### 1. Get Token with Namespace Restrictions
# @name getNamespaceToken
POST {{keycloakUrl}}/realms/{{realm}}/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id=namespace-restricted-client
&client_secret=namespace-secret

###
@namespaceToken = {{getNamespaceToken.response.body.access_token}}

### 2. Test Creating Resource in Allowed Namespace
POST http://localhost:{{dmsPort}}/data/ed-fi/gradeLevelDescriptors
Authorization: Bearer {{namespaceToken}}
Content-Type: application/json

{
    "namespace": "uri://allowed-namespace.org/GradeLevelDescriptor",
    "codeValue": "Grade 1",
    "shortDescription": "First Grade"
}

### 3. Test Creating Resource in Disallowed Namespace (Should Fail)
POST http://localhost:{{dmsPort}}/data/ed-fi/gradeLevelDescriptors
Authorization: Bearer {{namespaceToken}}
Content-Type: application/json

{
    "namespace": "uri://disallowed-namespace.org/GradeLevelDescriptor",
    "codeValue": "Grade 1",
    "shortDescription": "First Grade"
}

### 4. Get Token with Education Organization Restrictions
# @name getEdOrgToken
POST {{keycloakUrl}}/realms/{{realm}}/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id=edorg-restricted-client
&client_secret=edorg-secret

###
@edOrgToken = {{getEdOrgToken.response.body.access_token}}

### 5. Test Accessing Allowed Education Organization
GET http://localhost:{{dmsPort}}/data/ed-fi/schools/123
Authorization: Bearer {{edOrgToken}}

### 6. Test Accessing Disallowed Education Organization
GET http://localhost:{{dmsPort}}/data/ed-fi/schools/999
Authorization: Bearer {{edOrgToken}}
```

### 5.3.4 JWT Configuration Test File

**File**: `src/dms/tests/RestClient/jwt-configuration-test.http`

Test different JWT configuration scenarios:

```http
### Test JWT with Different Configurations

### 1. Test with HTTPS Metadata (Production-like)
# This requires proper SSL configuration

### 2. Test Clock Skew Tolerance
# Use a token with slight time difference

### 3. Test Metadata Refresh
# Long-running test to verify metadata refresh works

### 4. Test Multiple Audiences
# If configured to accept multiple audiences

### 5. Test Custom Role Claim Types
# Verify role extraction with different claim types
```

## Implementation Tasks

### Task 1: Create Unit Tests (4 hours)
1. ✅ Mark existing test item as in_progress
2. Create JwtAuthenticationMiddlewareTests.cs
3. Create JwtRoleAuthenticationMiddlewareTests.cs  
4. Update DmsCoreServiceExtensionsTests.cs
5. Run all unit tests and fix any issues

### Task 2: Create Integration Tests (3 hours)
1. Create JwtAuthenticationTests.cs
2. Create ClientAuthorizationTests.cs
3. Set up test fixtures with Keycloak
4. Run integration tests in E2E environment

### Task 3: Create .http Files (2 hours)
1. Create jwt-authentication-flow.http
2. Create jwt-role-based-access.http
3. Create jwt-client-authorizations.http
4. Create jwt-configuration-test.http
5. Test all scenarios manually

### Task 4: Documentation (1 hour)
1. Update TEST_JWT_FLOW.md with new test information
2. Create JWT_TESTING_GUIDE.md for developers
3. Update main README with JWT testing section

## Success Criteria

1. **Unit Tests**: All JWT middleware tests pass with >90% coverage
2. **Integration Tests**: E2E tests verify complete JWT flow
3. **.http Files**: Manual testing scenarios are documented and runnable
4. **Documentation**: Clear testing guide for developers
5. **CI/CD**: Tests integrated into build pipeline

## Troubleshooting Guide

### Common Test Issues

1. **Keycloak Connection Failed**
   - Ensure Keycloak is running on port 8045
   - Check realm configuration
   - Verify client credentials

2. **Token Validation Errors**
   - Check Authority URL matches issuer
   - Verify audience configuration
   - Ensure clock synchronization

3. **Role Claims Not Found**
   - Verify RoleClaimType configuration
   - Check token contains expected claims
   - Ensure Keycloak client has roles assigned

## Next Steps

After completing Phase 5:
1. Run full regression test suite
2. Performance test JWT validation overhead
3. Security review of implementation
4. Deploy to staging environment
5. Monitor for any authentication issues