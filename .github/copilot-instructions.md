# Copilot Instructions for Ed-Fi Data Management Service

These instructions apply to the entire repository. Follow them when generating code, tests,
documentation, scripts, or review comments for this project.

## Repository Purpose

This repository contains the Ed-Fi API platform, internally called the Data Management Service
(DMS) platform. It has two primary applications:

- Data Management Service (DMS): implementation of the Ed-Fi Resources API, Descriptors API,
  and Discovery API.
- DMS Configuration Service (CMS): implementation of the Ed-Fi Management API specification.

The platform replaces the legacy Ed-Fi ODS/API and ODS Admin API applications.

## Repository Layout

- `src/dms/`: DMS solution and projects.
  - `EdFi.DataManagementService.sln`: main DMS solution.
  - `EdFi.DataManagementService-Docker.sln`: Docker-oriented DMS solution.
  - `core/`: DMS core request pipeline, middleware, schema handling, and shared contracts.
  - `backend/`: backend abstractions, relational backend, DDL generation, PostgreSQL, MSSQL,
    plans, and backend tests.
  - `frontend/`: ASP.NET Core frontend host and HTTP modules.
  - `clis/`: schema tools, OpenAPI generator, and schema downloader projects.
  - `tests/`: DMS API-level integration, E2E, unit, and instance management tests.
- `src/config/`: CMS solution and projects.
  - `EdFi.DmsConfigurationService.sln`: CMS solution.
  - `backend/`: CMS backend, PostgreSQL, MSSQL, Keycloak, OpenIddict, installer, and tests.
  - `frontend/`: CMS ASP.NET Core frontend and unit tests.
  - `datamodel/`: CMS data model.
  - `tests/`: CMS E2E tests.
- `src/Directory.Packages.props`: centralized NuGet package versions.
- `.config/dotnet-tools.json`: local .NET tools, including CSharpier and Husky.
- `docs/`: developer-oriented documentation.
- `reference/`: design documents and examples.
- `eng/docker-compose/`: local Docker Compose environments and helper scripts.

## General Working Rules

- Prefer small, targeted changes that match existing patterns in the touched project.
- Read nearby code before introducing a new abstraction or dependency.
- Do not perform unrelated refactors while implementing a requested change.
- Do not rewrite formatting manually; use CSharpier for C# formatting.
- Preserve existing public contracts unless the change explicitly requires a contract change.
- Treat generated artifacts, snapshots, fixtures, and lock files carefully. Update them only when
  the change requires it and the existing test pattern expects it.
- Do not add secrets, production credentials, private keys, or machine-specific absolute paths to
  committed files.
- When suggesting shell commands, prefer PowerShell examples because the repository scripts are
  PowerShell-first.

## .NET and C# Style

- Target .NET 10 style throughout the repository.
- Use modern C# features when they improve clarity and match local style:
  - file-scoped namespaces
  - primary constructors
  - records
  - target-typed `new`
  - collection expressions
  - pattern matching
  - switch expressions
- Keep variables non-nullable by default. Avoid nullable types unless `null` is part of the
  domain model or API contract.
- Use `is null` and `is not null` for null checks. Do not use `== null` or `!= null`.
- Prefer explicit domain names over abbreviations in production code.
- Prefer immutable values and records for value-like data.
- Prefer constructor injection and existing DI registration patterns.
- Keep exception messages and problem details specific enough to diagnose the failure.
- Avoid broad catch blocks unless the existing code path intentionally translates failures.
- Keep async flows async. Do not block on tasks with `.Result`, `.Wait()`, or sync-over-async
  patterns.
- Use cancellation tokens when surrounding code already passes them through.

## Formatting

- The repository uses spaces, four-space indentation, UTF-8, final newlines, and a max line length
  of 110 from `.editorconfig`.
- JSON and YAML use two-space indentation.
- Markdown may preserve trailing whitespace when intentional.
- Format C# files with:

```powershell
dotnet csharpier format <directory-or-file>
```

- Restore local tools after cloning or when `.config/dotnet-tools.json` changes:

```powershell
./setup-dev-environment.ps1
```

## Dependency Guidelines

- Use `src/Directory.Packages.props` for package versions.
- Do not add package versions directly to individual `.csproj` files unless the repository has an
  established exception in that area.
- Avoid new dependencies when the standard library or existing project dependency is sufficient.
- Keep package choices consistent across DMS and CMS when the same concern appears in both.

## Build Commands

Use focused project or solution builds where possible:

```powershell
dotnet build ./src/dms/EdFi.DataManagementService.sln
dotnet build ./src/config/EdFi.DmsConfigurationService.sln
```

For DMS build-script workflows:

```powershell
./build-dms.ps1 Build -Configuration Debug
./build-dms.ps1 UnitTest -Configuration Debug
./build-dms.ps1 IntegrationTest -Configuration Debug
```

For CMS build-script workflows:

```powershell
./build-config.ps1 Build -Configuration Debug
./build-config.ps1 UnitTest -Configuration Debug
```

## Testing Frameworks and Style

- Use NUnit for tests.
- Use FluentAssertions for assertions.
- Use FakeItEasy for mocks when mocks are necessary.
- Follow the existing NUnit naming style:
  - test files are named after the code area under test
  - test fixture classes start with `Given_`
  - `SetUp` methods arrange and act when that is the local style
  - test methods start with `It_`
- Prefer focused unit tests for pure logic.
- Prefer integration tests when behavior depends on HTTP pipeline wiring, database behavior,
  generated DDL, schema loading, or serialization contracts.
- Do not silently skip meaningful assertions. If a test is conditional on environment setup, follow
  the existing `Assert.Ignore` or category pattern in nearby tests.

## Running Unit Tests

Run a focused project when changing a single area:

```powershell
dotnet test ./src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj
dotnet test ./src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj
dotnet test ./src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj
```

Use filters for narrow verification:

```powershell
dotnet test <test-project.csproj> --filter "FullyQualifiedName~Given_SomeFixture"
```

## DMS API-Level Integration Tests

The API-level integration tests are under:

```text
src/dms/tests/EdFi.DataManagementService.Tests.Integration/
```

These are in-process HTTP pipeline tests against real provisioned PostgreSQL or SQL Server
databases. They are not Docker-stack E2E tests.

Run all configured dialects:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration
```

Run PostgreSQL only:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "Category=PostgresqlIntegration"
```

Run SQL Server only:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "Category=MssqlIntegration"
```

PostgreSQL admin connection setup:

```powershell
cd eng/docker-compose
pwsh ./start-postgresql.ps1
$env:ConnectionStrings__DatabaseConnection = "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_dms_backend_integration;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DataManagementService;NoResetOnClose=true;"
```

SQL Server admin connection setup:

```powershell
docker run --name dms-mssql-integration -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='EdFi_Dms1!' -p 14333:1433 -d mcr.microsoft.com/mssql/server:2022-latest
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,14333;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true"
```

If the SQL Server container already exists:

```powershell
docker start dms-mssql-integration
```

For SQL Server setup failures, distinguish "unconfigured" from "configured but unreachable":

```powershell
Get-ChildItem Env:ConnectionStrings__MssqlAdmin -ErrorAction SilentlyContinue
Test-NetConnection -ComputerName localhost -Port 14333
docker ps --filter name=dms-mssql-integration
docker logs dms-mssql-integration --tail 80
```

## DMS Docker-Stack E2E Tests

The DMS E2E suite is under:

```text
src/dms/tests/EdFi.DataManagementService.Tests.E2E/
```

These tests use the local Docker stack named `dms-local`. They are different from the API-level
integration tests.

Prefer the repository-root build target for a complete E2E run:

```powershell
./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './eng/docker-compose/.env.e2e'
```

Run a shard or focused category:

```powershell
./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './eng/docker-compose/.env.e2e' -TestFilter 'Category=@e2e-ci-shard-3'
```

The build target starts the Docker stack, provisions the E2E database, restarts DMS, clears
unsupported `NODE_OPTIONS`, and sets the E2E data-store database name for the test process.

For direct local setup:

```powershell
cd src/dms/tests/EdFi.DataManagementService.Tests.E2E
pwsh ./teardown-local-dms.ps1
pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e
```

After direct setup, direct `dotnet test` is valid when the test process uses the same database:

```powershell
dotnet test ./EdFi.DataManagementService.Tests.E2E.csproj
```

If a custom environment file changes `E2E_DATABASE_NAME`, set
`AppSettings__DataStoreDatabaseName` to the same value for direct `dotnet test` runs.

If Playwright reports unsupported Node options, clear `NODE_OPTIONS` for the test command.

## CMS E2E Tests

The CMS E2E suite is under:

```text
src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/
```

Use the CMS-local setup and teardown scripts for this suite:

```powershell
cd src/config/tests/EdFi.DmsConfigurationService.Tests.E2E
pwsh ./teardown-local-cms.ps1
pwsh ./setup-local-cms.ps1
dotnet test ./EdFi.DmsConfigurationService.Tests.E2E.csproj
```

Do not use the DMS E2E setup scripts for CMS E2E tests.

## Instance Management E2E Tests

Use this directory only for the Instance Management E2E suite:

```text
src/dms/tests/EdFi.InstanceManagement.Tests.E2E/
```

It uses route-context settings and a different instance setup than the DMS E2E suite.

## Docker and Local Runtime

Local Docker scripts live under:

```text
eng/docker-compose/
```

Common commands:

```powershell
cd eng/docker-compose
./start-local-dms.ps1
./start-local-dms.ps1 -d
./start-local-dms.ps1 -d -v
./start-postgresql.ps1
./bootstrap-local-dms.ps1
```

Use `-d` to stop services and `-v` to remove volumes. When switching branches or changing schema
inputs, also remove stale bootstrap state where appropriate:

```powershell
./start-local-dms.ps1 -d -v -RemoveBootstrap
```

Default local URLs:

- DMS API: `http://localhost:8080`
- CMS/OpenIddict: `http://localhost:8081`
- Swagger UI: `http://localhost:8082`
- Kafka UI: `http://localhost:8088`

Self-contained OpenIddict mode is the default identity-provider path. Use Keycloak only when the
task explicitly needs Keycloak behavior.

## Relational Backend Rules

The relational backend derives tables, views, constraints, and triggers from an effective schema.
Read `docs/RELATIONAL-BACKEND.md` for relational backend work.

Important invariants:

- DMS validates the database fingerprint on first use.
- The database must contain `dms."EffectiveSchema"` for the effective schema loaded by the running
  service.
- A schema mismatch returns HTTP 503.
- First-use validation failures are cached for the process lifetime.
- Reprovisioning alone is not enough after a mismatch; restart DMS after provisioning.
- There is no hot reload for schema changes.

After any ApiSchema or schema package change:

1. Reprovision a fresh database for the new effective schema.
2. Restart DMS so it reloads schema and clears cached validation state.

For DDL and schema tooling, start with:

```text
src/dms/clis/EdFi.DataManagementService.SchemaTools/
```

Useful schema tool operations include:

```powershell
dms-schema hash <core-ApiSchema.json> [extension-ApiSchema.json ...]
dms-schema ddl emit --schema <core-ApiSchema.json> --output ./ddl-output --dialect both
dms-schema ddl provision --schema <core-ApiSchema.json> --connection-string "<connection>" --dialect pgsql --create-database
dms-schema ddl provision --schema <core-ApiSchema.json> --connection-string "<connection>" --dialect mssql --create-database
```

## PostgreSQL and MSSQL Differences

- PostgreSQL integration commonly uses host port `5435` in the local compose environment.
- DMS API-level MSSQL integration commonly uses host port `14333`.
- Backend MSSQL integration commonly uses host port `1434` or a test-specific configured port.
- SQL Server connection strings should include `TrustServerCertificate=true` for local test
  containers.
- Do not assume a SQL Server test failure is a product failure when the admin connection string is
  configured but the container is stopped. Check connectivity and logs first.

Example temporary MSSQL backend integration container:

```powershell
docker run --rm --name dms-codex-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<StrongPassword>' -p 1434:1433 -d mcr.microsoft.com/mssql/server:2022-latest
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,1434;User Id=sa;Password=<StrongPassword>;TrustServerCertificate=True;Encrypt=True"
dotnet test <mssql-test-project.csproj> --filter <filter>
docker stop dms-codex-mssql
```

## HTTP Pipeline and API Behavior

- Preserve established HTTP status codes, headers, and ProblemDetails shapes.
- Be careful with Ed-Fi API compatibility behavior, including descriptors, references, profiles,
  authorization, pagination, total count, change versions, deletes, and key changes.
- Data strictness matters. Request-body property names are case-sensitive.
- When touching profile behavior, test profile-constrained create/read/update paths and hidden-field
  preservation if applicable.
- When touching descriptor behavior, test both descriptor identity and descriptor reference
  resolution paths.
- When touching change-version behavior, verify `minChangeVersion`, `maxChangeVersion`, and
  newest-change-version behavior as applicable.

## Configuration Service Guidelines

- CMS code lives under `src/config`.
- Keep CMS authorization, client secret validation, identity-provider, and datastore behavior
  aligned across PostgreSQL, MSSQL, Keycloak, and OpenIddict implementations.
- Client secrets in examples must satisfy configured minimum and maximum length rules and the
  complexity rule when the runtime enforces it.
- Do not use DMS-specific E2E setup when validating CMS behavior.

## Scripts

- Repository scripts are PowerShell scripts and should remain compatible with PowerShell.
- Keep script parameters, defaults, and examples aligned with existing scripts.
- Use PSScriptAnalyzer-friendly style in PowerShell changes.
- Avoid embedding local absolute paths in scripts or documentation unless an example explicitly
  requires a placeholder.

## Documentation

- Keep docs factual and command-oriented.
- Link to existing docs instead of duplicating long explanations when possible.
- Update docs when changing developer workflows, commands, configuration names, or test setup.
- Prefer repository-relative paths in documentation.

## Review and Debugging Expectations

When reviewing or debugging changes:

- Start with the smallest relevant test project or test filter.
- If E2E tests fail before issuing API requests, check environment and runtime setup before assuming
  product behavior is wrong.
- For Docker E2E failures, inspect container logs.
- For API-level integration failures, inspect test logs under the relevant `bin/Debug/net10.0/logs`
  directory when console output is insufficient.
- For MSSQL failures, verify the container is running and the configured port matches the connection
  string.
- For fingerprint or HTTP 503 failures after schema changes, reprovision a clean database and
  restart DMS.

## Before Finishing a Change

- Format touched C# files with CSharpier.
- Run the most focused relevant tests.
- Broaden tests when the change affects shared behavior, the HTTP pipeline, database schemas,
  generated DDL, authorization, profiles, or public API behavior.
- Report any tests that were not run and why.
