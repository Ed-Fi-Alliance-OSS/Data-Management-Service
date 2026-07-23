This repository contains the **Ed-Fi Data Management Service (DMS) Platform**, which consists of two main applications:

1. **Ed-Fi Data Management Service (DMS)** - A functional implementation of Ed-Fi Resources API, Ed-Fi Descriptors API, and Ed-Fi Discovery API
 - Code and solution file in `./src/dms`
2. **Ed-Fi DMS Configuration Service (CMS)** - A functional implementation of the Ed-Fi Management API specification
 - Code and solution file in `./src/config`

### Code Style

- Only use .NET 10 code style, including modern C# language features (e.g., primary constructors, pattern matching, records, target-typed new, collection expressions, and file-scoped namespaces).
- Declare variables non-nullable.
- Always use `is null` or `is not null` instead of `== null` or `!= null`.

### Format Code

- `dotnet csharpier format <directory or file>`

## Working with Data Management Service E2E Tests

The Data Management Service E2E tests directory is `src/dms/tests/EdFi.DataManagementService.Tests.E2E/`.

The Data Management Service E2E tests interact with a Docker stack named dms-local. Examine the docker log files to assist in debugging E2E tests.

If docker is not running, on Linux start it with `systemctl --user start docker-desktop`

You must teardown and setup when you switch branches or change debugging code in DMS.

Data Management Service E2E tests do not always clean up after themselves. Teardown and setup between test runs if you see inconsistent behavior.

If local E2E tests fail before issuing API requests, check for environment/runtime issues such as unsupported `NODE_OPTIONS` values or container health checks failing because required shell tools are missing.

When running E2E tests from a shell that has `NODE_OPTIONS` set, clear it for the test command if Playwright reports an unsupported Node option, for example: `env -u NODE_OPTIONS dotnet test ...`.

### Setup Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./setup-local-dms.ps1`

### Run Data Management Service E2E tests

For DMS E2E tests, prefer the repo-root `build-dms.ps1 E2ETest` path when you want a complete test run.

The build script performs the Docker setup, provisions `E2E_DATABASE_NAME` with generated DDL including `dms."EffectiveSchema"`, starts or restarts DMS against the provisioned database, clears unsupported `NODE_OPTIONS`, and sets `AppSettings__DataStoreDatabaseName` for the E2E test process.

Example shard run from the repository root:

```powershell
./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-3'
```

The direct setup path is also valid for local relational E2E testing. `pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e` configures the CMS data store to use `E2E_DATABASE_NAME`, provisions that database with generated DDL including `dms."EffectiveSchema"`, and starts DMS after provisioning. Direct `dotnet test` is valid after this setup when the test process is configured for the same database; the default `.env.e2e` and E2E `appsettings.json` both use `edfi_datamanagementservice_e2e`. If a custom environment file changes `E2E_DATABASE_NAME`, also set `AppSettings__DataStoreDatabaseName` to that value for direct `dotnet test` runs.

If you only need to inspect the relational Docker environment manually, you can use this setup path:

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./teardown-local-dms.ps1`
3. Run: `pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e`
4. Verify the DMS container is using PostgreSQL:
   - `docker inspect ed-fi-api --format '{{range .Config.Env}}{{println .}}{{end}}' | rg 'Datastore'`
   - Expected values include `AppSettings__Datastore=postgresql`.

### Teardown Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./teardown-local-dms.ps1`
3. This will:
   - Stop all containers in the dms-local stack
   - Remove all associated volumes
   - Remove locally-built images (ed-fi-api-local and ed-fi-api-config-local)

Use `src/dms/tests/EdFi.InstanceManagement.Tests.E2E` only for the Instance Management E2E suite; it uses route-context settings and a different instance setup.

## Working with DMS API-Level Integration Tests

The API-level integration tests directory is `src/dms/tests/EdFi.DataManagementService.Tests.Integration/`. These are not Docker-stack E2E tests: they boot the DMS HTTP pipeline in-process and lease real PostgreSQL or SQL Server databases from configured admin connection strings.

For SQL Server/MSSQL tests, `ConnectionStrings__MssqlAdmin` must point to a running SQL Server. If the variable is absent, MSSQL tests skip. If the variable is present but stale, tests fail during fixture database setup with a SQL Server connection error before any API request is issued. Troubleshoot that as environment setup, not as a test result:

```powershell
Get-ChildItem Env:ConnectionStrings__MssqlAdmin -ErrorAction SilentlyContinue
Test-NetConnection -ComputerName localhost -Port 14333
docker ps --filter name=dms-mssql-integration
docker logs dms-mssql-integration --tail 80
```

Known-good local MSSQL setup:

```powershell
docker run --name dms-mssql-integration -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='EdFi_Dms1!' -p 14333:1433 -d mcr.microsoft.com/mssql/server:2025-latest
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,14333;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true"
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj --filter "Category=MssqlIntegration"
```

If the container already exists, use `docker start dms-mssql-integration`. If port `14333` is busy, map another host port and use the same port in `ConnectionStrings__MssqlAdmin`.

## Working with DMS Configuration Management Service E2E Tests

The DMS Configuration Management Service E2E tests directory is `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/`.

The DMS Configuration Management Service E2E tests have a similar setup and environment to the Data Management Service E2E tests. They have their own `setup-local-cms.ps1` for setup and `teardown-local-cms.ps1` for teardown.

## Working with MSSQL Backend Integration Tests

Before running MSSQL backend integration tests, verify that a SQL Server instance is listening locally on the expected test port, commonly `localhost,1434`. If no suitable instance is running, start a temporary SQL Server container and pass its admin connection string to the test command with `ConnectionStrings__MssqlAdmin`.

Example local container setup:

1. Start SQL Server:
   - `docker run --rm --name dms-codex-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<StrongPassword>' -p 1434:1433 -d mcr.microsoft.com/mssql/server:2025-latest`
2. Wait until SQL Server is ready before running tests.
3. Run MSSQL integration tests with:
   - `ConnectionStrings__MssqlAdmin='Server=localhost,1434;User Id=sa;Password=<StrongPassword>;TrustServerCertificate=True;Encrypt=True' dotnet test <mssql test project or solution> --filter <filter>`
4. Stop the temporary container after the run:
   - `docker stop dms-codex-mssql`

## Testing Guidelines

- Use NUnit with FluentAssertions, and FakeItEasy for mocks when necessary.
- NUnit tests should follow the existing style, which is filenames named like the code area being tested,
  TestFixture classes named with prefix "Given_", a Setup method which does arrange and act, and Test methods with "It_" prefixes for each individual assert.

