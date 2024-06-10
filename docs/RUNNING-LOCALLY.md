# Running the Application Locally

> [!TIP]
> This describes command line operations in the immediate "local" context,
> without using Kubernetes and not running the application in Docker.

> Note: If using a custom connection string or custom app settings in your .NET application, it's highly recommended to add an `appsettings.Development.json` file for your local development environment. This file should be based on the `appsettings.json` file and can override settings specifically for local development.

1. Start a PostgreSQL instance on port 5432, if not already running, with username `postgres` and password configured in a `pgpass.conf` file.
   1. If using an alternate port or location, edit the connection string in `appSettings.Development.json`.
   2. Option: use Docker Compose to run [postgresql-compose.yml](../eng/postgresql-compose.yml)
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
> Note: If using a custom connection string for your integration tests, it's highly recommended to add an `appsettings.Test.json` file for your local development environment testing. This file should be based on the `appsettings.json` file on EdFi.DataManagementService.Api.Tests.Integration folder.
