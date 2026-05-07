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

### Setup Data Management Service E2E test Docker environment

1. Navigate to `src/dms/tests/EdFi.DataManagementService.Tests.E2E`
2. Run: `pwsh ./setup-local-dms.ps1`

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

## Testing Guidelines

- Use NUnit with FluentAssertions, and FakeItEasy for mocks when necessary.
- NUnit tests should follow the existing style, which is filenames named like the code area being tested,
  TestFixture classes named with prefix "Given_", a Setup method which does arrange and act, and Test methods with "It_" prefixes for each individual assert.

## Development Artifacts

`tasks.json` and `progress.txt` are committed files used for development tracking while development and QA are in progress.
