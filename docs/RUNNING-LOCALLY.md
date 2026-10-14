# Running the Application Locally

> [!TIP]
> This describes command line operations in the immediate "local"
> context, without using Docker and not running the application in Docker.

> [!NOTE] If using a custom connection string or custom app settings in your
> .NET application, it's highly recommended to add an
> `appsettings.Development.json` file for your local development environment.
> This file should be based on the `appsettings.json` file and can override
> settings specifically for local development.

1. Start a PostgreSQL instance on port 5432, if not already running, with
   username `postgres` and password configured in a `pgpass.conf` file.
   1. If using an alternate port or location, edit the connection string in
      `appSettings.Development.json`.
   2. Option: use Docker Compose to run
      [postgresql-compose.yml](../eng/postgresql-compose.yml)
2. Adjust `appSettings.Development.json` if needed.
3. Build the AspNetCore project.
4. Run the AspNetCore project.

Sample commands:

```shell
# From base directory
cd eng
docker compose -f postgresql-compose.yml up -d
cd ../
./build-dms.ps1 build
./build-dms.ps1 run
```

## Selecting a Data Standard version

The local start/build scripts run **Data Standard 5.2 by default**. To run a
different supported version, pass `-DataStandardVersion` (e.g. `6.1`) to
`build-dms.ps1`, `eng/docker-compose/start-local-dms.ps1`, or
`start-published-dms.ps1`; with no parameter the behavior is unchanged. See
[Data Standard Versions](./DATA-STANDARD-VERSIONS.md) for how version selection
works and how to add or drop a version.

## Running the Configuration Service

The shipped `appsettings.json` for the Configuration Service leaves
`DatabaseSettings:EncryptionKey` blank, and the service refuses to start until it
is supplied. `IdentitySettings:ClientSecret` and `IdentitySettings:EncryptionKey`
are blank as well; the service starts without those, but a blank `ClientSecret`
makes it answer every request with a configuration report, and a blank
`IdentitySettings:EncryptionKey` fails the first token request. Copy the starter
file and adjust it for your machine:

```shell
# From base directory
cd src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore
cp appsettings.Development.json.example appsettings.Development.json
cd ../../../../
./build-config.ps1 build
./build-config.ps1 run
```

If you already have an `appsettings.Development.json`, add the missing values to
it instead of copying over it. Replacing the file changes the derived encryption
key, which orphans any connection strings already stored in your local
Configuration Service database.

`appsettings.Development.json` is gitignored, so the values stay on your
machine. `DatabaseSettings:EncryptionKey` must be at least 32 ASCII characters;
only the first 32 contribute to the AES-256 key that protects stored connection
strings. The starter file uses the same value as the `eng/docker-compose`
environment files, so a locally run
Configuration Service and a Compose stack can read each other's encrypted
connection strings. Replace it for any real deployment — see
[Configuration](./CONFIGURATION.md#configurationservicesettings) for the full
rules, and for what to do after changing a key that already has data encrypted
under it.

## Running the EdFi.DataManagementService.Backend.Postgresql.Test.Integration

To run the integration tests locally, execute the following command in a PowerShell
terminal:

```shell
# From base directory
./build-dms.ps1 IntegrationTest -Configuration Debug
```

You can see the result in the `TestResults` folder (located in the base
directory). The file containing the result is of the type `Test Result File
(.trx)`. To better understand the format, is recommended that you open it using
Visual Studio. The file name is
`EdFi.DataManagementService.Backend.Postgresql.Test.Integration.trx`.

## Running the EdFi.DataManagementService.Api.Tests.Integration

> [!NOTE] If using a custom connection string for your integration tests, it's
> highly recommended to add an `appsettings.Test.json` file for your local
> development environment testing. This file should be based on the
> `appsettings.json` file on EdFi.DataManagementService.Api.Tests.Integration
> folder.

## API-level integration tests

The `EdFi.DataManagementService.Tests.Integration` project boots the real DMS
HTTP pipeline against a provisioned PostgreSQL and/or SQL Server database,
with auth/CMS/profile-catalog/application-context faked. See
[`src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md`](../src/dms/tests/EdFi.DataManagementService.Tests.Integration/README.md)
for the fixture map, runtime-compatibility materialization details, and the
recipe for adding a scenario.

### Prerequisites

Start PostgreSQL via the repo docker compose, then set the admin
connection string:

```powershell
cd eng/docker-compose
pwsh ./start-postgresql.ps1
$env:ConnectionStrings__DatabaseConnection = "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_dms_backend_integration;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DataManagementService;NoResetOnClose=true;"
```

Start SQL Server in a container, then set the admin connection string. Using
host port `14333` avoids collisions with a developer SQL Server on the default
port:

```powershell
docker run --name dms-mssql-integration-2025 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='EdFi_Dms1!' -p 14333:1433 -d mcr.microsoft.com/mssql/server:2025-latest
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,14333;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true"
```

If `ConnectionStrings__MssqlAdmin` is present but points at a stopped or
missing SQL Server, MSSQL tests fail during fixture database setup instead of
skipping. Check the endpoint before treating the test failure as product code:

```powershell
Get-ChildItem Env:ConnectionStrings__MssqlAdmin -ErrorAction SilentlyContinue
Test-NetConnection -ComputerName localhost -Port 14333
docker ps --filter name=dms-mssql-integration-2025
docker logs dms-mssql-integration-2025 --tail 80
```

If the named container already exists, restart it with
`docker start dms-mssql-integration-2025`.
The version-suffixed name keeps SQL Server 2025 separate from any `dms-mssql-integration` container created from the earlier SQL Server 2022 instructions.
Do not reuse that legacy container: it still runs SQL Server 2022, so tests gated on SQL Server 2025 (such as the native-json evaluation fixture) silently skip.
Remove it with `docker rm -f dms-mssql-integration` once anything you need from it is saved.
If `14333` is busy, map another host port
and use that port in `ConnectionStrings__MssqlAdmin`.

### Run

```powershell
# All dialects
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration

# PostgreSQL only
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "Category=PostgresqlIntegration"

# SQL Server only
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "Category=MssqlIntegration"
```

Tests whose dialect is unconfigured cleanly `Assert.Ignore`, so partial
local setups still produce a useful test run.

## MSSQL backend integration provisioning diagnostics

The backend MSSQL integration project creates real SQL Server databases during
fixture setup. These tests use `ConnectionStrings__MssqlAdmin` as described
above; they are separate from the API-level integration project and from the
Docker-stack E2E suite.

Run the backend project without a fixture or category filter to see the same
setup failures that occur in an unfiltered test host:

```powershell
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj
```

Generated-DDL admission is unlimited when
`MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY` is unset or blank. To cap the
number of admitted provisioning operations in a new test process, set a
positive integer before starting it:

```powershell
$env:MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY = '1'
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj
```

The setting is captured lazily once per process, so change it and restart the
test process to apply a different value. It only coordinates tests inside that
process; it does not limit other processes or SQL Server activity. The existing
300-second generated-DDL command timeout remains unchanged. A timeout message
reports process-wide admitted-operation samples at batch start and timeout,
including the failing operation and excluding queued entrants. Those samples
are not executing-query counts and do not prove an engine wait. The message
labels the cause as suspected provisioning contention. If the effective limit
is already 1, investigate other processes and server waits; reducing it further
cannot help.

Optional per-fixture CSV timing output can be enabled with
`MSSQL_FIXTURE_TIMINGS_PATH`. Output failures are best-effort and do not replace
test failures or interrupt cleanup. Use the existing MSSQL shard categories as
a fallback when isolating a failure; a filtered run does not replace the
unfiltered host check.

## Running Unit Tests and Generate Code Coverage Report

> [!NOTE]
> The coverage tooling (ReportGenerator) is pinned in the repository tool
> manifest (`.config/dotnet-tools.json`), so restore it once per clone:
>
> ```shell
> dotnet tool restore
> ```

The unit test command runs the compiled assemblies without building them, so
build first, then run the tests, in a PowerShell terminal:

```shell
# From base directory
./build-dms.ps1 Build -Configuration Debug
./build-dms.ps1 UnitTest -Configuration Debug
```

`UnitTest` runs every `*.Tests.Unit` project in a single `dotnet test` pass
with the XPlat Code Coverage collector, merges the per-project results with
ReportGenerator, and writes the merged Cobertura report to
`coverage.cobertura.xml` in the base directory. The command fails if the merged
`line` or `branch` coverage total falls below the threshold (currently 58%).

After completing the Unit Tests execution, run the following command to generate
the HTML coverage report from the merged file.

```shell
# From base directory
./build-dms.ps1 Coverage
```

A `coveragereport` folder will be created. Open that folder and look for the
index.html file, which should contain a detailed report with the coverage
results.
