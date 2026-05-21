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

E2E tests require the `relational-backend` NUnit category to work with the relational backend. The Reqnroll feature tag is `@relational-backend`, but the NUnit filter omits the `@`: `--filter "Category=relational-backend"`.

The `relational-backend` test category only selects tests; it does not configure the DMS container. To run relational-backend E2E tests, the dms-local stack must be started with `USE_RELATIONAL_BACKEND=true` by using the relational environment file.

### Setup Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./setup-local-dms.ps1`

### Setup Data Management Service relational-backend E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./teardown-local-dms.ps1`
3. Run: `pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e.relational`
4. Verify the DMS container is using the relational backend:
   - `docker inspect dms-local-dms-1 --format '{{range .Config.Env}}{{println .}}{{end}}' | rg 'UseRelationalBackend|Datastore|QueryHandler'`
   - Expected values include `AppSettings__UseRelationalBackend=true`, `AppSettings__Datastore=postgresql`, and `AppSettings__QueryHandler=postgresql`.
5. Run tests with `env -u NODE_OPTIONS dotnet test EdFi.DataManagementService.Tests.E2E.csproj --configuration Release --filter "Category=relational-backend"`.

The default `pwsh ./setup-local-dms.ps1` command uses `.env.e2e`, which starts DMS with `AppSettings__UseRelationalBackend=false`. A run with `Category=relational-backend` against that default stack is not a valid relational-backend signal.

### Teardown Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./teardown-local-dms.ps1`
3. This will:
   - Stop all containers in the dms-local stack
   - Remove all associated volumes
   - Remove locally-built images (dms-local-dms and dms-local-config)

Use `src/dms/tests/EdFi.InstanceManagement.Tests.E2E` only for the Instance Management E2E suite; it uses route-context settings and a different instance setup.

## Working with DMS Configuration Management Service E2E Tests

The DMS Configuration Management Service E2E tests directory is `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/`.

The DMS Configuration Management Service E2E tests have a similar setup and environment to the Data Management Service E2E tests. They have their own `setup-local-cms.ps1` for setup and `teardown-local-cms.ps1` for teardown.

## Working with MSSQL Backend Integration Tests

Before running MSSQL backend integration tests, verify that a SQL Server instance is listening locally on the expected test port, commonly `localhost,1434`. If no suitable instance is running, start a temporary SQL Server container and pass its admin connection string to the test command with `ConnectionStrings__MssqlAdmin`.

Example local container setup:

1. Start SQL Server:
   - `docker run --rm --name dms-codex-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<StrongPassword>' -p 1434:1433 -d mcr.microsoft.com/mssql/server:2022-latest`
2. Wait until SQL Server is ready before running tests.
3. Run MSSQL integration tests with:
   - `ConnectionStrings__MssqlAdmin='Server=localhost,1434;User Id=sa;Password=<StrongPassword>;TrustServerCertificate=True;Encrypt=True' dotnet test <mssql test project or solution> --filter <filter>`
4. Stop the temporary container after the run:
   - `docker stop dms-codex-mssql`

## Testing Guidelines

- Use NUnit with FluentAssertions, and FakeItEasy for mocks when necessary.
- NUnit tests should follow the existing style, which is filenames named like the code area being tested,
  TestFixture classes named with prefix "Given_", a Setup method which does arrange and act, and Test methods with "It_" prefixes for each individual assert.

## Development Artifacts

`tasks.json` and `progress.txt` are committed files used for development tracking while development and QA are in progress.
