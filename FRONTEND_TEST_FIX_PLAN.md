# Frontend Test Fix Plan

## Problem Analysis

The frontend tests are failing with 500 Internal Server Error responses after JWT authentication was moved from the frontend to Core middleware. The root cause is that the tests use `WebApplicationFactory<Program>` which triggers the full application startup, including JWT authentication configuration that expects various services and settings.

### Key Issues:

1. **Configuration Dependencies**: The application startup expects numerous configuration sections:
   - `AppSettings` with AuthenticationService URL
   - `IdentitySettings` with Authority, Audience, etc.
   - `ConfigurationServiceSettings` with BaseUrl, ClientId, etc.
   - `ConnectionStrings` for database connections

2. **Service Dependencies**: JWT setup tries to:
   - Fetch OIDC metadata from Authority URL
   - Configure token validation
   - Set up authentication schemes

3. **Test Isolation**: Tests are not properly isolated from production startup logic

## Solution Strategy

### Option 1: Refactor Frontend Architecture

Separate authentication concerns from the main application startup.

#### Implementation Steps:

1. **Extract Authentication Setup**
   - Move JWT configuration to a separate extension method
   - Make it conditional based on a feature flag

2. **Create Minimal Frontend**
   - Frontend only handles HTTP request/response conversion
   - No authentication processing at all

3. **Update Tests**
   - Tests focus only on request/response transformation
   - No authentication concerns in frontend tests

