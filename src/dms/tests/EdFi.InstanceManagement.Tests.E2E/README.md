# Ed-Fi Instance Management E2E Tests

This project contains End-to-End tests for the Data Management Service instance management and route segment functionality, focusing on data segregation among instances.

## Purpose

These tests verify that:

- Multiple instances can be configured with route qualifiers (e.g., districtId, schoolYear)
- Data is properly segregated between instances
- Route segments correctly isolate data access
- Instance context is properly maintained throughout request processing
- Error handling for invalid route qualifiers

## Test Implementation

The tests are based on the workflow defined in `src/dms/tests/RestClient/multi-instance-route-qualifiers.http` and implement:

1. **Instance Setup**: Creating vendors, instances with route contexts, and applications via Configuration Service API
2. **Route Qualifier Segregation**: Testing data isolation across different instance/route combinations
3. **Error Handling**: Verifying appropriate error responses for invalid route qualifiers

## Prerequisites

- Docker Desktop running
- PowerShell Core (pwsh) 7.0 or higher
- .NET 8.0 SDK

## Running the Tests

**IMPORTANT**: These tests require a comprehensive setup involving Docker containers, database configuration, and route qualifier settings. Always use the build script from the repository root:

```powershell
./build-dms.ps1 InstanceE2ETest -Configuration Release
```

This script handles:
- Docker environment setup with proper configuration
- Building the DMS and Configuration Service
- Running the tests
- Cleanup of the Docker environment

**Do not attempt to run these tests directly with `dotnet test`** - the setup is too complex and requires specific environment configuration that is managed by the build script.

## Test Structure

Tests are organized using Reqnroll (SpecFlow successor) with Gherkin feature files:

### Features

- `Features/InstanceManagement/InstanceSetup.feature` - Vendor, instance, and application creation
- `Features/InstanceManagement/RouteQualifierSegregation.feature` - Data isolation testing
- `Features/InstanceManagement/RouteQualifierErrors.feature` - Error handling validation

### Step Definitions

- `StepDefinitions/InstanceSetupStepDefinitions.cs` - Instance setup steps
- `StepDefinitions/RouteQualifierStepDefinitions.cs` - Data segregation steps
- `StepDefinitions/ErrorHandlingStepDefinitions.cs` - Error handling steps

### Management (Test Infrastructure)

- `Management/ConfigServiceClient.cs` - Configuration Service API client
- `Management/DmsApiClient.cs` - DMS API client with route qualifier support
- `Management/TokenHelper.cs` - Authentication token management
- `Management/InstanceManagementContext.cs` - Test data tracking across scenarios
- `Management/TestConfiguration.cs` - Test configuration constants

### Models

- Request/Response models for Configuration Service APIs (Vendor, Instance, RouteContext, Application)

### Hooks

- `Hooks/SetupHooks.cs` - Test run initialization and logging
- `Hooks/InstanceManagementCleanupHooks.cs` - Cleanup after each scenario

## Configuration

### Test Configuration (`appsettings.json`)

- `QueryHandler` - Database type (postgresql)
- `AuthenticationService` - URL for authentication endpoint
- `EnableClaimsetReload` - Whether to reload claimsets during tests

### Environment Configuration (`.env.routeContext.e2e`)

Key setting: `ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear`

This enables route-based instance resolution in DMS.

## Test Data

Tests create the following instances:

- Instance 1: District 255901, School Year 2024 → Database: `edfi_datamanagementservice_d255901_sy2024`
- Instance 2: District 255901, School Year 2025 → Database: `edfi_datamanagementservice_d255901_sy2025`
- Instance 3: District 255902, School Year 2024 → Database: `edfi_datamanagementservice_d255902_sy2024`

URL Pattern: `http://localhost:8080/{districtId}/{schoolYear}/data/ed-fi/{resource}`

Example: `http://localhost:8080/255901/2024/data/ed-fi/contentClassDescriptors`

## Cleanup

Tests tagged with `@InstanceCleanup` automatically clean up:

- Applications
- Instances (including route contexts)
- Vendors

Cleanup is performed after each scenario to ensure test isolation.

## Important Notes

- The build script handles all setup automatically
- Tests interact with Docker containers, so Docker must be running
- Route qualifiers must be enabled in the environment configuration (handled by build script)
- Check Docker logs if tests fail: `docker logs <container-name>`
- Tests use self-contained identity provider (no Keycloak required)

## Troubleshooting

**Tests fail with 404 for all requests:**

- Re-run the build script: `./build-dms.ps1 InstanceE2ETest -Configuration Release`
- The build script ensures route qualifiers are properly configured

**Setup issues:**

- Re-run the build script - it handles full setup
- Check Configuration Service logs: `docker logs dms-config-service`
- Check DMS logs: `docker logs dms-dms-1`

**Instance creation fails:**

- Verify PostgreSQL is running: `docker ps | grep postgresql`
- Check database connection string in test data
- Re-run the build script to reset the environment
