# Task 3 brief — Advertise the DMS OAuth proxy in every served OpenAPI document

## Scope

- Modify only the frontend Discovery/Metadata modules and their listed unit/integration tests:
  - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/DiscoveryEndpointModule.cs`
  - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/MetadataEndpointModule.cs`
  - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/MetaDataModuleTests.cs`
  - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ChangeQueriesMetadataIntegrationTests.cs`
- Derive the public OAuth proxy URL from the request-visible root URL, PathBase, tenant, and route qualifier segments using established Discovery route logic.
- Apply the public URL to response-local OpenAPI JSON clones for Resource, Descriptor, Change-Queries, Profiles, and file-backed Discovery responses.
- Preserve `IApiService` signatures, `ApiService.AddOAuth2SecuritySection` behavior, internal `AppSettings.AuthenticationService`, and cached document isolation.
- Add exact tests for unqualified and PathBase/tenant/districtId/schoolYear-qualified routes, file-backed Discovery provider input, and Change-Queries integration.

## Constraints

- Do not change CMS health, add form credentials, alter authentication service configuration, or introduce migration/compatibility behavior.
- Use System.Text.Json/JsonNode and modern .NET 10 style. Avoid mutating cached/shared documents; mutate only response-local clones.
- Reuse existing route/path helpers; avoid a duplicate route algorithm or unrelated refactor.

## Verification

Run focused tests:
`dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~MetadataModuleTests|FullyQualifiedName~Given_real_ApiSchema_change_queries_metadata"`

Commit the implementation and tests and write `task-3-report.md` with exact test result and concerns. Do not dispatch subagents or reviewers.
