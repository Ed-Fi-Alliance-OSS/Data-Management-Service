# Running the Application Locally

> [!TIP] This describes command line operations in the immediate "local"
> context, without using Kubernetes and not running the application in Docker.

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
./build.ps1 build
./build.ps1 run
```

## Running the EdFi.DataManagementService.Api.Tests.Integration

> [!NOTE] If using a custom connection string for your integration tests, it's
> highly recommended to add an `appsettings.Test.json` file for your local
> development environment testing. This file should be based on the
> `appsettings.json` file on EdFi.DataManagementService.Api.Tests.Integration
> folder.
## Running Unit Tests and Generate Code Coverage Report
To run the unit tests locally, execute the following command in a PowerShell
terminal:

```shell
# From base directory
./build.ps1 UnitTest -Configuration Debug
```

The previous command should generate two files in the base directory, which
contain all the merged results from the unit tests execution.
```
coverage.cobertura.xml
coverage.json
```

After completing the Unit Tests execution, run the following command to generate
the HTML coverage report.

```shell
# From base directory
reportgenerator -reports:"coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html
```

A Coverage Report folder will be created. Open that folder and look for the
index.html file, which should contain a detailed report with the coverage
results.

### Coverlet Parameters
We are currently evaluating `line` and `branch` metrics from the Total
coverage. If the total coverage is less than our threshold, the build will fail.

Total: Ensures the total combined coverage result of all modules isn't less than
the threshold

```
    --threshold-type line
    --threshold-type branch
    --threshold-stat total
```

To evaluate coverage by modules, we need to remove the `--threshold-stat total`
option. This will compare the threshold value by `line` and `branch` instead.
