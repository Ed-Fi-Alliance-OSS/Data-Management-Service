# Ed-Fi DMS Platform

- `src/dms`: Data Management Service (DMS), implementing the Ed-Fi Resources, Descriptors,
  and Discovery APIs. Official product documentation calls this the **Ed-Fi API**.
- `src/config`: DMS Configuration Service (CMS), implementing the Ed-Fi Management API.
  See [CMS instructions](src/config/AGENTS.md) when working in that service.

## Code Style

- Follow the project's configured C# version, `.editorconfig`, and surrounding conventions.
- Prefer non-nullable declarations; use nullable types when absence is a valid state.
- Use `is null` / `is not null` instead of `== null` / `!= null`.
- Use `System.Text.Json` for JSON in .NET application code and build tooling; do not add
  `Newtonsoft.Json` dependencies or usages.
- Format changed C# files with `dotnet csharpier format <directory or file>`.

## RelationalMappingVersion Release Cadence

`SchemaHashConstants.RelationalMappingVersion` tracks the DMS release line.

- Bump at most once per release, only when relational mapping changes; never lower the value.
- Keep `v3` throughout DMS 8.1. The next eligible bump is `v4` at the first qualifying mapping
  change after 8.1 ships. Do not infer the release line from `src/dms/Directory.Build.props`.
- Each bump changes `EffectiveSchemaHash` for every dialect and forces database reprovisioning.
  Mapping-only physical changes can require reprovisioning even when the hash is unchanged,
  because generated DDL and mapping-set output are not hash inputs.

For mapping changes, consult the [release history](docs/RELATIONAL-BACKEND.md#relationalmappingversion-release-cadence)
and [provisioning guide](docs/RELATIONAL-BACKEND.md#reprovisioning-and-legacy-data-carry-forward).

## Testing Conventions

- Use NUnit, FluentAssertions, and FakeItEasy for mocks when needed. Name test files after
  the area tested, fixtures with `Given_`, and test methods with `It_`.
- Add regression tests that fail without the fix, including observable operation-order
  differences. Assertions should establish the required behavior.
- Synchronize asynchronous tests with explicit signals or fake time where supported;
  do not repair races by increasing delays.

## Running Tests

- **DMS E2E:** Prefer repo-root `build-dms.ps1 E2ETest`. Use the
  [E2E README](src/dms/tests/EdFi.DataManagementService.Tests.E2E/README.md) for engine filters,
  setup, and troubleshooting. Use `-SkipDockerBuild` only when existing images contain the
  changes being tested; rebuild after changing containerized code.
- **API integration:** These tests host the HTTP pipeline in-process against real databases.
  Use [local setup](docs/RUNNING-LOCALLY.md#api-level-integration-tests) and the
  [fixture guide](src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md).
- **MSSQL backend integration:** Use the
  [provisioning diagnostics](docs/RUNNING-LOCALLY.md#mssql-backend-integration-provisioning-diagnostics)
  for database setup, concurrency, and shard filters.
- **Instance Management E2E:** Use its [own suite](src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md);
  it requires route-context settings and a different instance setup from DMS E2E.
- **CMS E2E:** Use the suite's
  [setup](src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/setup-local-cms.ps1) and
  [teardown](src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1) scripts.

## Logging

- Normalize / sanitize client-derived strings before logging them.
- Log the already-normalized string (`someId.Value`), never the wrapping record/struct itself.
