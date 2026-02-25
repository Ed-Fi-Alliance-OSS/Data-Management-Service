This repository contains the **Ed-Fi Data Management Service (DMS) Platform**, which consists of two main applications:

1. **Ed-Fi Data Management Service (DMS)** - A functional implementation of Ed-Fi Resources API, Ed-Fi Descriptors API, and Ed-Fi Discovery API
2. **Ed-Fi DMS Configuration Service (CMS)** - A functional implementation of the Ed-Fi Management API specification

## DMS Core and Backends

- `src/dms/core/EdFi.DataManagementService.Core/`: core runtime wired into the DMS `frontend` via `DmsCoreServiceExtensions` (request pipeline, middlewares, handlers security, validation, API schema/OpenAPI/OAuth).
- `src/dms/core/EdFi.DataManagementService.Core.External/`: shared contracts/models used across `frontend` + backend implementations (notably `Backend/` request/result types like `IGetRequest`/`GetResult`).
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/`: relational backend implementation plus schema deploy code under `Deploy/`
- Tests: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/` and `src/dms/backend/*Tests*/`.

### Code Quality

- **Required**: Obey the `.editorconfig` file settings at all times. The project uses:
  - UTF-8 character encoding
  - LF line endings
  - Spaces for indentation style
  - Final newlines required
  - Trailing whitespace must be trimmed
- **Required**: run the appropriate build process and correct any build errors with the following scripts:
  - If modifying code in `./src/dms` then run `dotnet build --no-restore ./src/dms/EdFi.DataManagementService.sln`
  - If modifying code in `./src/config` then run `dotnet build --no-restore ./src/config/EdFi.DmsConfigurationService.sln`

## Formatting and Code Style

- Apply code-formatting style defined in `.editorconfig`.
- Use pattern matching and switch expressions wherever possible.
- Use `nameof` instead of string literals when referring to member names.
- Only use .NET 10 code style, including modern C# language features (e.g., primary constructors, pattern matching, records, target-typed new, collection expressions, and file-scoped namespaces).

### Nullable Reference Types

- Declare variables non-nullable.
- Always use `is null` or `is not null` instead of `== null` or `!= null`.

## Development Workflow

### Build

- `dotnet build <projectfile or solutionfile>`

### Unit Test

- `dotnet test <projectfile or solutionfile>`

### Format Code

- `dotnet csharpier format <directory or file>`

## Working with Data Management Service E2E Tests

The Data Management Service E2E tests directory is `src/dms/tests/EdFi.DataManagementService.Tests.E2E/`.

The Data Management Service E2E tests interact with a Docker stack named dms-local. Examine the docker log files to assist in debugging E2E tests.

If docker is not running, on Linux start it with `systemctl --user start docker-desktop`

You must teardown and setup when you switch branches or change debugging code in DMS.

Data Management Service E2E tests do not always clean up after themselves. Teardown and setup between test runs if you see inconsistent behavior.

### Setup Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.InstanceManagement.Tests.E2E`
2. Run: `pwsh ./setup-local-dms.ps1`

### Teardown Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.InstanceManagement.Tests.E2E`
2. Run: `pwsh ./teardown-local-dms.ps1`
3. This will:
   - Stop all containers in the dms-local stack
   - Remove all associated volumes
   - Remove locally-built images (dms-local-dms and dms-local-config)

## Working with DMS Configuration Management Service E2E Tests

The DMS Configuration Management Service E2E tests directory is `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/`.

The DMS Configuration Management Service E2E tests have a similar setup and environment to the Data Management Service E2E tests. They have their own `setup-local-cms.ps1` for setup and `teardown-local-cms.ps1` for teardown.

## Testing Guidelines

- Use NUnit with FluentAssertions, and FakeItEasy for mocks when necessary.
- NUnit tests should follow the existing style, which is filenames named like the code area being tested,
  TestFixture classes named with prefix "Given_", a Setup method which does arrange and act, and Test methods with "It_" prefixes for each individual assert.
