# DMS-1476 Task 7 Report

## Scope

- Base commit: `7934010e feat: default claimset reload off and propagate the management required role`
- Modified the two Task 7 E2E environment files, the Instance Management E2E DMS client and management step definitions, and the management claimset feature.

## Docker Token Probe

The required SystemAdministrator and `DmsConfigurationService` token probe was not completed.

- Blocker: Docker access is denied in this sandbox, as reported while the mandated `setup-local-dms.ps1 -EnvironmentFile ./.env.e2e` setup was in progress.
- Action: stopped the setup and did not issue or decode either token.
- Outcome: the `cms-client` role claim was not empirically confirmed. The E2E environment files use the Task 7 brief's documented expected value, `cms-client`.

## Implementation

- Added `DMS_MANAGEMENT_REQUIRED_ROLE=cms-client` to both E2E environment files.
- Changed `DmsApiClient` management requests to use authenticated `_httpClient`, which carries the supplied bearer token.
- Kept unscoped management requests deliberately anonymous for multi-tenant `404` coverage.
- Made all tenant-scoped requests, including invalid-tenant scenarios, acquire the seeded `DmsConfigurationService` token.
- Added tenant-scoped anonymous `401` and ordinary DMS application-token `403` scenarios for both management endpoints.
- The wrong-role token is minted with fixture application credentials, rather than the seeded configuration-service client credentials that carry the management role.

## Verification

- Formatted the two changed C# files with:

  ```powershell
  dotnet tool run csharpier -- format src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/DmsApiClient.cs src/dms/tests/EdFi.InstanceManagement.Tests.E2E/StepDefinitions/ManagementEndpointStepDefinitions.cs
  ```

- Compile-only check passed:

  ```powershell
  dotnet build src/dms/tests/EdFi.InstanceManagement.Tests.E2E/EdFi.InstanceManagement.Tests.E2E.csproj --configuration Release --no-restore
  ```

  Result: `0 Warning(s)`, `0 Error(s)`.

- `git diff --check` passed.
- No Docker E2E suite was run.

## Self-Review

- Confirmed the final implementation changes are confined to the five Task 7 file groups plus this required report.
- Confirmed feature bindings exist for all ten scenarios: unscoped `404`, authorized valid tenant `200`, authorized invalid tenant `404`, case-insensitive valid tenant `200`, anonymous tenant `401`, and wrong-role tenant `403` for both GET and POST endpoints.
- Runtime role-claim contents and live endpoint responses remain unverified until Docker access is available.
