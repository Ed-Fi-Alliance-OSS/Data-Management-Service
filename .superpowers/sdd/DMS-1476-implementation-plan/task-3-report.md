# DMS-1476 Task 3 Report

## Delivered Scope

- Added `ManagementEndpointsOptions` with the nested
  `AppSettings:ManagementEndpoints` section, a `[JsonIgnore]` required role, and a
  fail-closed mapping helper backed by `EndpointRequiredRole.IsValid`.
- Added the singleton `IManagementEndpointAuthorizationService`. Its implementation delegates
  bearer-to-role decisions to Task 2's `EndpointRoleAuthorizer` using the configured management
  role and JWT role claim type.
- Bound the options in the ASP.NET Core frontend host and added the shipped empty
  `AppSettings:ManagementEndpoints:RequiredRole` default.
- Added unit coverage for authorized, unauthorized, forbidden, no-client-role-fallback, and
  missing/invalid required-role outcomes; options validation and serialization; and frontend
  configuration binding/default behavior.

## Explicit Constraints Confirmed

| Constraint | Evidence |
| --- | --- |
| Nested configuration | `ManagementEndpointsOptions.SectionName` is `AppSettings:ManagementEndpoints`; frontend binding uses it. |
| Required role stays private | `RequiredRole` has `[JsonIgnore]`; serialization test verifies it is omitted. |
| No startup validation | No `IValidateOptions<ManagementEndpointsOptions>` implementation or registration was added. |
| Singleton authorization service | `AddJwtAuthentication` registers `IManagementEndpointAuthorizationService` with `AddSingleton`. |
| Consume prior tasks | Options use `EndpointRequiredRole.IsValid`; authorization delegates to `EndpointRoleAuthorizer.AuthorizeAsync`. |
| Do not wire endpoint enablement | `EnableManagementEndpoints` remains at its existing default and has no new consumer. |

## Test-Driven Evidence

1. Red: before production code, the focused core test command failed with `CS0234` because
   `EdFi.DataManagementService.Core.Management` did not exist and `CS0246` because
   `ManagementEndpointAuthorizationService` was missing.
2. Green after implementation and formatting:
   - `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~Given_ManagementEndpoint"`
     - Passed: 14, failed: 0, skipped: 0.
   - `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~Given_ManagementEndpointsOptionsBinding"`
     - Passed: 2, failed: 0, skipped: 0.

## Formatting And Review

- Restored the repository-local tools with `dotnet tool restore`.
- Formatted the six touched C# files using the manifest-pinned CSharpier 1.2.5 through
  `dotnet tool run csharpier -- format ...`; the direct `dotnet csharpier` shim was unavailable in
  this session even after restore.
- `git diff --check` completed without whitespace errors.
- Manual self-review checked the complete scoped diff, option binding, singleton registration,
  absence of an options validator, and absence of new endpoint-enablement wiring.

## Concerns

None. The focused tests and formatter completed successfully. The full solution and integration
suites were outside Task 3's focused-test scope.
