# JWT Authentication Migration Completion Plan

## Overview

This plan outlines the steps to complete the JWT authentication migration from the ASP.NET Core frontend to DMS Core, ensuring the frontend becomes a pure pass-through layer while maintaining all security requirements.

## Current State Assessment

Based on the staging code analysis:

1. **Core**: JWT infrastructure is ready but not fully integrated
2. **Frontend**: Still contains authentication configuration and middleware
3. **Tests**: Broken due to authentication dependencies
4. **Configuration**: Needs proper setup for JWT in Core

## Implementation Plan

### Phase 1: Fix Frontend Tests (Priority: Critical)

#### Step 1.1: Create Test-Specific Configuration
Create a minimal test environment that bypasses authentication concerns.

**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`

Add test environment detection and minimal service configuration:
- Check for "Test" environment
- Skip JWT configuration in test mode
- Add minimal required services

#### Step 1.2: Update Test Base Infrastructure
Create common test utilities to simplify test setup.

**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/TestBase/FrontendTestBase.cs` (new)

Provide:
- Base test class with common setup
- Minimal configuration for tests
- Helper methods for creating test factories

#### Step 1.3: Fix Individual Test Classes
Update each failing test to use the new test infrastructure.

**Files to update**:
- `CoreEndpointModuleTests.cs`
- Any other module tests that depend on authentication

### Phase 2: Remove Frontend Authentication (Priority: High)

#### Step 2.1: Remove JWT Configuration
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`

Remove:
- JWT Bearer authentication setup (lines ~150-178)
- Authorization policy configuration
- Authentication service registrations

#### Step 2.2: Remove Authentication Middleware
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`

Remove:
- `app.UseAuthentication()`
- `app.UseAuthorization()`
- Any auth-related middleware

#### Step 2.3: Update Endpoint Modules
Ensure all endpoint modules have no authentication requirements.

**Files to verify**:
- `MetadataEndpointModule.cs`
- `DiscoveryEndpointModule.cs`
- `TokenEndpointModule.cs`
- `ManagementEndpointModule.cs`

Remove any remaining `.RequireAuthorization()` calls.

#### Step 2.4: Clean Up Dependencies
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj`

Remove package reference:
- `Microsoft.AspNetCore.Authentication.JwtBearer`

### Phase 3: Integrate JWT in Core Pipeline (Priority: High)

#### Step 3.1: Update ApiService Pipeline
**File**: `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`

Integrate JWT middleware into the pipeline for appropriate endpoints:
- Add `JwtAuthenticationMiddleware` for data endpoints
- Add `JwtRoleAuthenticationMiddleware` for metadata/discovery endpoints
- Ensure proper ordering in pipeline

#### Step 3.2: Add Configuration Support
**File**: `src/dms/core/EdFi.DataManagementService.Core/Configuration/AppSettings.cs`

Add JWT configuration section reference if needed.

#### Step 3.3: Update Service Registration
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`

Add Core JWT services registration:
```csharp
services.AddJwtAuthentication(configuration);
```

### Phase 4: Configuration Setup (Priority: Medium)

#### Step 4.1: Update appsettings.json
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json`

Add JWT configuration section as documented in JWT_AUTHENTICATION_CONFIG.md.

#### Step 4.2: Update Development Settings
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json`

Add development-specific overrides (e.g., RequireHttpsMetadata: false).

#### Step 4.3: Environment Variable Mapping
Ensure proper environment variable mapping for Docker/Kubernetes deployments.

### Phase 5: Testing and Validation (Priority: Medium)

#### Step 5.1: Unit Test Coverage
- Verify all Core JWT middleware tests pass
- Ensure frontend tests work with new configuration
- Add any missing test scenarios

#### Step 5.2: Integration Tests
Create end-to-end tests for JWT authentication flow:
- Valid token with proper claims
- Invalid/expired tokens
- Missing authorization header
- Role-based access scenarios

#### Step 5.3: Manual Testing
Test with actual JWT tokens from Keycloak:
- Data endpoint access with ClientAuthorizations
- Metadata/discovery endpoints with role requirements
- Token endpoint (should allow anonymous)

## Implementation Order

1. **Day 1: Fix Frontend Tests**
   - Implement Phase 1 completely
   - Ensure all tests pass before proceeding

2. **Day 2: Remove Frontend Auth**
   - Implement Phase 2 step by step
   - Run tests after each step to ensure nothing breaks

3. **Day 3: Core Integration**
   - Implement Phase 3
   - Add configuration (Phase 4)
   - Basic testing

4. **Day 4: Testing and Refinement**
   - Complete Phase 5
   - Fix any issues discovered
   - Documentation updates

## Rollback Strategy

If issues arise:
1. Git stash changes and return to current state
2. Re-evaluate approach based on specific issues
3. Consider implementing gradual migration using feature flags

## Success Criteria

1. ✅ All frontend tests pass
2. ✅ No authentication code remains in frontend
3. ✅ JWT validation works in Core for all endpoints
4. ✅ Proper 401/403 responses for auth failures
5. ✅ Configuration is clean and well-documented
6. ✅ No performance regression

## Configuration Example

After completion, the configuration should look like:

```json
{
  "JwtAuthentication": {
    "Enabled": true,
    "Authority": "http://localhost:8080/realms/edfi",
    "Audience": "edfi-api",
    "MetadataAddress": "http://localhost:8080/realms/edfi/.well-known/openid-configuration",
    "RequireHttpsMetadata": false,
    "ClientRole": "service"
  }
}
```

## Next Steps

1. Review this plan with the team
2. Create feature branch from current staging
3. Begin implementation following the phases
4. Regular check-ins after each phase
5. Final review before merge