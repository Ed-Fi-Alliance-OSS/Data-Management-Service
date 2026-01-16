# Agent Reference for Ed-Fi Data Management Service

## Project Overview

This repository contains the **Ed-Fi Data Management Service (DMS) Platform**, which consists of two main applications:

1. **Ed-Fi Data Management Service (DMS)** - A functional implementation of Ed-Fi Resources API, Ed-Fi Descriptors API, and Ed-Fi Discovery API
2. **Ed-Fi DMS Configuration Service (CMS)** - A functional implementation of the Ed-Fi Management API specification

## Project Structure

```
Data-Management-Service/
├── src/
│   ├── dms/                          # Main Data Management Service
│   │   ├── frontend/                 # AspNetCore web API frontend
│   │   ├── backend/                  # Database backends (PostgreSQL, MSSQL, OpenSearch)
│   │   ├── core/                     # Core business logic and services
│   │   ├── clis/                     # Command-line tools (ApiSchemaDownloader, etc.)
│   │   └── tests/                    # E2E tests
│   └── config/                       # DMS Configuration Service
│       ├── frontend/                 # AspNetCore web API frontend
│       ├── backend/                  # Database backends and identity providers
│       ├── datamodel/                # Shared data models
│       └── tests/                    # E2E tests
├── eng/                              # Engineering tools and scripts
│   ├── docker-compose/               # Docker composition files and startup scripts
│   ├── bulkLoad/                     # Performance testing and bulk loading
│   └── smoke_test/                   # Smoke testing scripts
└── docs/                             # Developer documentation

```

## DMS Core and backends

- `src/dms/core/EdFi.DataManagementService.Core/`: core runtime wired into the DMS `frontend` via `DmsCoreServiceExtensions` (request pipeline, middlewares, handlers, security, validation, API schema/OpenAPI/OAuth).
- `src/dms/core/EdFi.DataManagementService.Core.External/`: shared contracts/models used across `frontend` + backend implementations (notably `Backend/` request/result types like `IGetRequest`/`GetResult`).
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/`: relational backend implementation plus schema deploy code under `Deploy/`
- Tests: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/` and `src/dms/backend/*Tests*/`.

## Development workflow steps

* **Build**
  * dotnet build <projectfile or solutionfile>
* **Unit Test**
  * dotnet test <projectfile or solutionfile>
* **Format Code**
  * dotnet csharpier format <directory or file>

## Working with Data Management Service E2E tests
The Data Management Service E2E tests directory is `src/dms/tests/EdFi.DataManagementService.Tests.E2E/`.

The Data Management Service E2E tests interact with a Docker stack named dms-local. Examine the docker log files to assist in debugging E2E tests.

Testcontainers is obsolete, DO NOT use them when working with the E2E tests.

If docker is not running, on Linux start it with `systemctl --user start docker-desktop`

You must teardown and setup when you switch branches or change debugging code in DMS.

Data Management Service E2E tests do not always clean up after themselves. Teardown and setup between test runs if you see inconsistent behavior.

* **Setup Data Management Service E2E test Docker environment**
  1. Navigate to `src/dms/tests/EdFi.InstanceManagement.Tests.E2E`
  2. Run: `pwsh ./setup-local-dms.ps1`

* **Teardown Data Management Service E2E test Docker environment**
  1. Navigate to `src/dms/tests/EdFi.InstanceManagement.Tests.E2E`
  2. Run: `pwsh ./teardown-local-dms.ps1`
  3. This will:
     - Stop all containers in the dms-local stack
     - Remove all associated volumes
     - Remove locally-built images (dms-local-dms and dms-local-config)

## Working with DMS Configuration Management Service E2E tests
The DMS Configuration Management Service E2E tests directory is `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/`.

The DMS Configuration Management Service E2E tests have a similar setup and environment to the Data Management Service E2E tests. They have their own `setup-local-cms.ps1` for setup and `teardown-local-cms.ps1` for teardown.

## Development Practices
- NUnit tests should follow the existing style, which is TestFixture classes named with prefix "Given_", a Setup method which does arrange and act, and Test methods with "It_" prefixes for each individual assert

- **Always** use the most modern C# features whenever possible, including but not limited to:
  - Primary constructors
  - Pattern matching
  - Record types
  - Target-typed new
  - Collection expressions
  - File scoped namespace declarations
