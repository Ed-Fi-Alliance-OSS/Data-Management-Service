# Docker Compose Test and Demonstration Configurations

> [!WARNING]
> **NOT FOR PRODUCTION USE!** Includes passwords in the default
> configuration that are visible in this repo and should never be used in real
> life. Be very careful!

> [!NOTE]
> This document describes a reference architecture intended to assist in
> building production deployments. This reference architecture will not be tuned
> for real-world production usage. For example, it will not include service
> clustering, may not be well secured, and it will not utilize cloud providers'
> manage services. Also see [Debezium Connector for
> PostgreSQL](https://debezium.io/documentation/reference/2.7/connectors/postgresql.html)
> for additional notes on securely configuring replication.

## Starting Services with Docker Compose

This directory contains several Docker Compose files, which can be combined to
start up different configurations:

1. `kafka.yml` covers Kafka
2. `kafka-ui.yml` covers KafkaUI
3. `postgresql.yml` starts only PostgreSQL
4. `local-dms.yml` runs the DMS from local source code.
5. `published-dms.yml` runs the latest DMS `pre` tag as published to Docker Hub.
6. `keycloak.yml` runs KeyCloak (identity provider).
7. `swagger-ui.yml` covers SwaggerUI

Before running these, create a `.env` file. The `.env.example` is a good
starting point.

After starting the environment, you’ll need to install the Kafka connector
configuration. The file `setup-connectors.ps1` will do this for you.

> [!IMPORTANT]
> Register the connector only after DMS has started and deployed the schema.
> The Debezium PostgreSQL source connector snapshots `dms.document` the moment it
> is registered, so registering it against a missing or empty table leaves the
> connector silently not streaming changes. The `start-local-dms.ps1` and
> `start-published-dms.ps1` scripts already defer connector registration until
> after schema provisioning. For the `start-all-services.ps1` local-debugger
> workflow you must run it yourself once DMS is up (see below).

```pwsh
./setup-connectors.ps1
```

> [!NOTE]
> `setup-connectors.ps1` waits for the Kafka Connect REST API to become
> available, replaces any existing connector, and then verifies the connector and
> all of its tasks reach the `RUNNING` state before returning. A failed
> registration is reported as an error rather than as silent success, and a
> not-yet-present connector on first run is handled without surfacing a spurious
> 404.

Convenience PowerShell scripts have been included in the directory, which
startup the appropriate services and inject the Kafka connectors (where
relevant).

* `start-all-services.ps1` launches both `postgresql.yml` and
  `kafka.yml`, without starting the DMS. Useful for running DMS in a
  local debugger. Because the DMS schema does not exist until you start DMS
  from your debugger, this script does **not** register the Kafka connector;
  run `./setup-connectors.ps1` yourself after DMS is up and the schema is
  deployed.
* `start-local-dms.ps1` launches the DMS local build along with all necessary
  services.
* `start-published-dms.ps1` launches the DMS published build along with all
  necessary services.
* `start-postgresql.ps1` only starts PostgreSQL.
* `start-keycloak.ps1` only starts KeyCloak.

You can pass `-d` to each of these scripts to shut them down. To delete volumes,
also append `-v`. Examples:

```pwsh
# Start everything
./start-local-dms.ps1

# Start everything for E2E testing (DLL-backed schema packages)
../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1

# Stop the services, keeping volumes
./start-local-dms.ps1 -d

# Stop the services and delete volumes
./start-local-dms.ps1 -d -v
```

By default, authentication uses the Self-Contained (OpenIddict) identity provider. The environment and startup scripts are pre-configured for Self-Contained mode, and Keycloak is not required unless explicitly selected.

When `USE_RELATIONAL_BACKEND=true`, the relational E2E database must be
provisioned and DMS must observe the provisioned `dms.EffectiveSchema` before
it can serve requests. Because `provision-relational-e2e-database.ps1`
provisions inside the running `dms-postgresql` container, the sequence is:

1. Start the Docker environment so PostgreSQL is up. The E2E setup wrapper uses
   the DLL-backed `SCHEMA_PACKAGES` schema path until Story 04 adds runtime
   loose-file content loading:

   ```pwsh
   ../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 -EnvironmentFile ./.env.e2e.relational
   ```

2. Run the provisioning script:

   ```pwsh
   ./provision-relational-e2e-database.ps1 -EnvironmentFile ./.env.e2e.relational
   ```

3. Restart DMS so cached startup-validation state is discarded:

   ```pwsh
   docker restart dms-local-dms-1
   ```

This is the same sequence used by the `E2ETests` build target
(`build-dms.ps1` → `Initialize-RelationalE2EDatabase`).

If DMS starts before provisioning has run (or against a database missing
`dms.EffectiveSchema`), DMS will start successfully but requests to the
affected data stores return HTTP 503 (Service Unavailable). To recover, run the
provisioning script and restart DMS as in steps 2 and 3 above.

If you want to use Keycloak as the identity provider, pass the `-IdentityProvider keycloak` parameter to the startup script. This will configure the environment to use Keycloak authentication, and you must ensure Keycloak is running and properly configured.

```pwsh
# Start everything (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -IdentityProvider self-contained

# Start everything for E2E testing (Self-Contained/OpenIddict mode, DLL-backed schema by default)
../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1

# Stop the services, keeping volumes (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -d

# Stop the services and delete volumes (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -d -v
```

You can set up the Kafka UI containers for testing by passing the -EnableKafkaUI option.

```pwsh
# Start everything with Kafka UI
./start-local-dms.ps1 -EnableKafkaUI
```

You can launch Swagger UI as part of your local environment to explore DMS
endpoints from your browser.

```pwsh
# To enable Swagger UI
./start-local-dms.ps1 -EnableSwaggerUI
```

```pwsh
# To set up the Keycloak container for enabling authorization 
./start-local-dms.ps1
```

You can also pass `-r` to `start-local-dms.ps1` to force rebuilding the DMS API
image from source code.

```pwsh
./start-local-dms.ps1 -r
```

By default, Data Standard 5.2 core model and TPDM model are included. You can
include custom extensions in your deployment by configuring the SCHEMA_PACKAGES,
USE_API_SCHEMA_PATH and API_SCHEMA_PATH environment variables.

> [!NOTE]
> To add custom extensions: In your `.env` file, set
> `USE_API_SCHEMA_PATH` to `true` and specify `API_SCHEMA_PATH` as the path where
> schema files will be downloaded. Then, add or update the `SCHEMA_PACKAGES`
> variable to include the core package and any required extension packages.

```env
 USE_API_SCHEMA_PATH=true
 API_SCHEMA_PATH=/app/ApiSchema
 SCHEMA_PACKAGES='[
  {
    "version": "1.0.328",
    "feedUrl": "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
    "name": "EdFi.DataStandard52.ApiSchema"
  },
  {
    "version": "1.0.328",
    "feedUrl": "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
    "name": "EdFi.Sample.ApiSchema",
    "extensionName": "Sample"
  }
]'
```

For bootstrap-managed schema and extension security metadata, stage the inputs
before starting Docker:

> **Requirement — `dms-schema` tool:** `prepare-dms-schema.ps1` needs the
> in-repo `dms-schema` CLI published as a native executable. Build it once
> before running the prepare command (the publish step is safe to re-run after
> branch switches).

```pwsh
# 1. Publish the dms-schema tool (required on a clean checkout)
$schemaToolProject = "../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
$schemaToolOutput  = ".bootstrap/tools/dms-schema"
dotnet publish $schemaToolProject -c Release -p:UseAppHost=true -o $schemaToolOutput

# 2. Point to the platform-appropriate executable
$schemaToolExe = if ($IsWindows) { "$schemaToolOutput/dms-schema.exe" } else { "$schemaToolOutput/dms-schema" }

# 3. Stage schema and claims, then run the bootstrap wrapper
#    (sequences infrastructure -> configure -> provision -> DMS over the staged workspace)
./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath $schemaToolExe
./prepare-dms-claims.ps1 -ClaimsDirectoryPath <directory-with-tpdm-claimset-fragment>
./bootstrap-local-dms.ps1
```

As of DMS-1153, `start-local-dms.ps1` is infrastructure-lifecycle-only: it no
longer creates data stores or provisions the DMS schema, and with a bootstrap
manifest present it disables startup database provisioning. Running it bare
after staging leaves the stack without a configured instance or provisioned
schema. Use the `bootstrap-local-dms.ps1` wrapper as above, or run the phases
manually:

```pwsh
./start-local-dms.ps1 -InfraOnly
./configure-local-data-store.ps1
./provision-dms-schema.ps1
./start-local-dms.ps1 -DmsOnly
```

`prepare-dms-claims.ps1` stages `*-claimset.json` fragments into
`.bootstrap/claims`. Story 00 validates the prepared manifest but does
not point the Config Service at `.bootstrap/claims` during startup because
DMS Docker startup still uses the non-staged schema path. When a bootstrap
manifest is present, startup disables `USE_API_SCHEMA_PATH`/`API_SCHEMA_PATH`
so the container falls back to its built-in DLL-backed schema assemblies.
Runtime reads of `.bootstrap/ApiSchema` and matching staged claims activation
land together in Story 04.

The full in-repo `EdFi.DataStandard52.ApiSchema` directory includes TPDM. Story
00 automatically maps only Sample and Homograph extension claim fragments, so
the full in-repo schema requires `-ClaimsDirectoryPath` with a TPDM
`*-claimset.json` fragment. For a schema set containing only core, Sample, and
Homograph, the claims command can be run without `-ClaimsDirectoryPath`.

The DMS E2E setup wrappers stay on the prior DLL-backed `SCHEMA_PACKAGES` flow
because the DMS runtime ContentProvider does not yet load loose JSON content
(Story 04). The wrappers should move to the staged `.bootstrap/ApiSchema` and
`.bootstrap/claims` workspaces when Story 04 lands.

If `prepare-dms-schema.ps1` or `prepare-dms-claims.ps1` fail with a
fingerprint-mismatch teardown-guidance error after a branch switch or input
change, recover by running `./start-local-dms.ps1 -d -v -RemoveBootstrap`
(which removes the local `.bootstrap/` workspace) or the matching E2E
`teardown-local-dms.ps1` script, then rerun the prepare commands.

> **Note on `-RemoveBootstrap`:** By default, `./start-local-dms.ps1 -d -v`
> and `./start-published-dms.ps1 -d -v` do **not** delete the `.bootstrap/`
> workspace on teardown. Pass `-RemoveBootstrap` explicitly when you want the
> workspace wiped (e.g. after a branch switch). The E2E teardown wrappers
> always remove it unconditionally.

## IDE Debugging Workflow

Use the IDE debugging workflow when you want to run DMS from your IDE (e.g. Visual Studio or Rider)
against Docker-managed infrastructure (PostgreSQL, Config Service, Keycloak/OpenIddict).

### Starter configuration artifact

Copy the starter file from the DMS frontend project into the same directory and rename it:

```pwsh
cp src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json.example `
   src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json
```

The artifact includes:

| Key | Value |
|-----|-------|
| `ConnectionStrings:DatabaseConnection` | `host=localhost;port=5435;...` (matches `.env.example` PostgreSQL port) |
| `ConfigurationServiceSettings:BaseUrl` | `http://localhost:8081` (Config Service host port) |
| `ConfigurationServiceSettings:ClientId` | `CMSReadOnlyAccess` (created by identity setup) |
| `ConfigurationServiceSettings:ClientSecret` | `<local-cms-readonly-secret>` (replace with secret from identity setup output) |
| `ConfigurationServiceSettings:Scope` | `edfi_admin_api/readonly_access` |
| `ConfigurationServiceSettings:EncryptionKey` | `<dms-config-database-encryption-key>` (replace with value of `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` from `.env`; `.env.example` default `DefaultEncryptionKey32CharactersX1`) |
| `AppSettings:UseApiSchemaPath` | `true` (use staged bootstrap workspace schema) |
| `AppSettings:ApiSchemaPath` | `<repo-root>/eng/docker-compose/.bootstrap/ApiSchema` (replace `<repo-root>` with your absolute path) |
| `AppSettings:AuthenticationService` | `http://localhost:8081/connect/token` |
| `JwtAuthentication:Authority` | `http://localhost:8081` |
| `JwtAuthentication:ClientRole` | `dms-client` |
| `JwtAuthentication:RoleClaimType` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` |

Replace `<local-cms-readonly-secret>` with the secret printed by `start-local-dms.ps1` or
`bootstrap-local-dms.ps1` during identity setup. Replace `<dms-config-database-encryption-key>`
with the value of `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` from your `.env` file (`.env.example`
default `DefaultEncryptionKey32CharactersX1`). Replace `<repo-root>` with the absolute path
to the repository root on your machine.

> **Activation note:** `AppSettings:UseApiSchemaPath` and `AppSettings:ApiSchemaPath` point at
> the staged bootstrap workspace. Runtime loading of staged schema content lands in Story 04
> (DMS-1154). Until then, the DMS runtime falls back to its built-in DLL-backed schema assemblies
> even when these keys are set.

### Pre-DMS infrastructure setup

Run the bootstrap wrapper with `-InfraOnly` to start infrastructure, provision the schema, and
stop before launching DMS:

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -EnableConfig -IdentityProvider self-contained
```

The wrapper prints IDE next-step guidance (staged schema path and `CMSReadOnlyAccess` details)
after provisioning completes.

### Two IDE workflow shapes

**Shape 1 — Pre-DMS stop (terminal):** `-InfraOnly` alone starts infrastructure, creates or confirms the
DMS instance in Config Service, provisions the schema, prints IDE configuration guidance, then stops.
Start DMS in your IDE using the printed settings; this invocation does not wait for it.

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -IdentityProvider self-contained
# → prints appsettings values and CMSReadOnlyAccess secret; stops before DMS startup
# Start DMS in your IDE now using the printed settings.
```

**Shape 2 — Health-wait continuation:** `-InfraOnly -DmsBaseUrl <url>` runs the same pre-DMS phases, then
waits (up to 300 seconds) for the IDE-hosted DMS process to return HTTP 200 from `<url>/health`.
`-DmsBaseUrl` is withheld from the initial infrastructure invocation and used only for the post-provision
health wait. When `-LoadSeedData` is also requested, seed loading runs against `<url>` after the health
wait passes.

> [!IMPORTANT]
> The two shapes are alternatives, not a sequence. If a previous wrapper run already created the
> data store (for example a Shape 1 run on the same stack), add `-NoDataStore` to the follow-up run
> so the configure phase reuses the existing data store instead of creating a duplicate:
> `./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> -NoDataStore [-LoadSeedData ...]`

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -IdentityProvider self-contained
# → starts infra, provisions schema, waits for DMS at http://localhost:5198/health
# Start DMS in your IDE before the 300-second timeout elapses.

# With seed loading:
./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -IdentityProvider self-contained `
    -LoadSeedData -SeedTemplate Minimal
```

**Manual phase flow:** When running phases individually, complete configure and provision before invoking
the health wait, then start DMS in the IDE between phases:

```pwsh
cd eng/docker-compose
./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath ...
./prepare-dms-claims.ps1
./start-local-dms.ps1 -InfraOnly -IdentityProvider self-contained
./configure-local-data-store.ps1 -AddSmokeTestCredentials
./provision-dms-schema.ps1
# Start DMS in your IDE now.
./start-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"  # post-provision health wait only
```

**Fail-fast rules:**

- `-DmsBaseUrl` without `-InfraOnly` is rejected: `start-local-dms.ps1` and `bootstrap-local-dms.ps1`
  both require `-InfraOnly` when `-DmsBaseUrl` is set.
- `-LoadSeedData -InfraOnly` without `-DmsBaseUrl` is rejected on the wrapper: seed loading requires a
  live DMS endpoint.
- `bootstrap-published-dms.ps1` does not accept `-InfraOnly` or `-DmsBaseUrl`; these shapes are
  local-only.

### School-year token endpoint (self-contained `-SchoolYearRange` variant)

When `configure-local-data-store.ps1` was run with `-SchoolYearRange`, each data store maps to
a school-year-qualified route. In that configuration, clients obtain tokens using the
school-year-scoped endpoint:

```
POST http://localhost:8081/connect/token/{schoolYear}
```

where `{schoolYear}` is the four-digit school year (e.g. `2025`). The standard
`/connect/token` endpoint remains available for non-school-year-qualified data stores.

## Default URLs

* The DMS API: [http://localhost:8080](http://localhost:8080)
* Kafka UI: [http://localhost:8088/](http://localhost:8088/)
* Swagger UI: [http://localhost:8082](http://localhost:8082)

## Multi-Data-Store Testing with Route Qualifiers

The DMS supports multi-data-store routing with route qualifiers, allowing you to route requests to different databases based on URL path segments.

### Step 1: Configure Environment for Multi-Data-Store

```powershell
# Navigate to docker-compose directory
cd eng/docker-compose

# Copy the environment template if you haven't already
cp .env.example .env

# Edit .env and set the ROUTE_QUALIFIER_SEGMENTS line:
# Find this section in .env:
#   # Multi-Data-Store Route Qualifiers (Optional)
#   #ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
#
# Change to (uncomment the line):
#   ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
```

### Step 2: Deploy DMS with Multi-Data-Store Configuration

```powershell
# Deploy with multi-data-store configuration enabled
./start-local-dms.ps1 -EnableConfig -r
```

Wait for all services to start (check with `docker ps`).

### Step 3: Create Additional Databases

The main `.env` file creates the main database, but you need to create the additional databases for each data store:

```powershell
# Create databases
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;"
```

### Step 4: Deploy Schema to Each Database

You need to copy the DMS schema from the main database to each test database:

```powershell
# Export the schema from the main database (schema only, no data)
docker exec -i dms-postgresql pg_dump -U postgres -d edfi_datamanagementservice --schema-only > $env:TEMP\dms_schema.sql

# Apply schema to each test database
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024

# Verify schema was applied (should show 61 tables in dms schema)
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT COUNT(*) as table_count FROM information_schema.tables WHERE table_schema = 'dms';"
```

### Step 5: Run the REST Client Tests

1. Open `src/dms/tests/RestClient/multi-data-store-route-qualifiers.http` in VS Code
2. Execute the requests in order (they build on each other)
3. Follow the instructions in the comments

Key testing steps:

1. **Get Config Service Token** - Authenticates with the configuration service
2. **Create Data Stores** - Sets up 3 data stores with different route contexts:
   * Data store 1: District 255901, School Year 2024
   * Data store 2: District 255901, School Year 2025
   * Data store 3: District 255902, School Year 2024
3. **Create Application** - Creates an app associated with all data stores
4. **Get DMS Token** - Authenticates with DMS using app credentials
5. **Test Routing** - Creates descriptors via different routes and verifies they go to the correct database

### Step 6: Verify Data Routing

You can verify that data is going to the correct database by checking the descriptors created:

```powershell
# Check District 255901, School Year 2024 (should have "District255901-2024")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"

# Check District 255901, School Year 2025 (should have "District255901-2025")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"

# Check District 255902, School Year 2024 (should have "District255902-2024")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"
```

### Expected Behavior

When you make requests to:

* `/255901/2024/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255901_sy2024`
* `/255901/2025/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255901_sy2025`
* `/255902/2024/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255902_sy2024`

### Troubleshooting

**Route qualifiers not being parsed:**

* Check that `ROUTE_QUALIFIER_SEGMENTS` is set correctly in the `.env` file (comma-separated format)
* Verify the environment variable in the container: `docker exec docker-compose-dms-1 printenv | grep ROUTE`
* Verify the DMS logs: `docker logs docker-compose-dms-1`

**404 - No database data store found or "No candidates found for the request path":**

* **Most common cause**: DMS container needs to be restarted after creating data stores in the Configuration Service
* Verify DMS loaded all data stores by checking the logs:

  ```powershell
  docker logs docker-compose-dms-1 | grep "Successfully fetched"
  # Should show: "Successfully fetched 4 data stores" (or your expected count)
  ```

* Verify route contexts are created correctly in the Configuration Service

* Check that the application is associated with the data stores
* Verify the JWT token includes the correct `dataStoreIds` claim

**Connection errors:**

* Ensure all databases are created
* Verify schema is deployed to each database
* Check PostgreSQL is running: `docker ps | grep postgresql`

**Check DMS logs:**

```powershell
docker logs docker-compose-dms-1 --follow
```

**Check Config Service logs:**

```powershell
docker logs dms-config-service --follow
```

### Cleanup

To tear down the environment:

```powershell
cd eng/docker-compose
pwsh teardown-local-dms.ps1
```

## Kafka Topic-Per-Data-Store Architecture

For E2E testing and production deployments that require strict data isolation,
DMS supports topic-per-data-store architecture where each data store publishes to
its own dedicated Kafka topic.

### Overview

**Standard Setup:**

* All data stores → Single topic: `edfi.dms.document`

**Topic-Per-Data-Store Setup:**

* Data store 1 → Topic: `edfi.dms.1.document`
* Data store 2 → Topic: `edfi.dms.2.document`
* Data store 3 → Topic: `edfi.dms.3.document`

This architecture is critical for:

* **FERPA Compliance**: Prevents cross-data-store data leakage
* **Multi-tenant Isolation**: Each tenant/district has isolated message streams
* **Selective Consumption**: Consumers can subscribe to specific data stores

### Automated Setup for E2E Tests

The Instance Management E2E tests automatically configure topic-per-data-store via the build script:

```powershell
.\build-dms.ps1 InstanceE2ETest -Configuration Release
```

The build script:

1. Creates test databases for each data store
2. Creates PostgreSQL publications for CDC
3. Configures data-store-specific Debezium connectors
4. Runs E2E tests with Kafka validation

### Manual Setup for Development

#### 1. Create PostgreSQL Publications

Each data store database needs its own publication for Debezium:

```powershell
# Data store 1
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "CREATE PUBLICATION to_debezium_datastore_1 FOR TABLE dms.document, dms.educationorganizationhierarchytermslookup;"

# Data store 2
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025 -c "CREATE PUBLICATION to_debezium_datastore_2 FOR TABLE dms.document, dms.educationorganizationhierarchytermslookup;"

# Data store 3
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024 -c "CREATE PUBLICATION to_debezium_datastore_3 FOR TABLE dms.document, dms.educationorganizationhierarchytermslookup;"
```

#### 2. Configure Debezium Connectors

Use the automated setup script (from `eng/docker-compose` directory):

```powershell
$dataStores = @(
    @{ DataStoreId = 1; DatabaseName = "edfi_datamanagementservice_d255901_sy2024" },
    @{ DataStoreId = 2; DatabaseName = "edfi_datamanagementservice_d255901_sy2025" },
    @{ DataStoreId = 3; DatabaseName = "edfi_datamanagementservice_d255902_sy2024" }
)

.\setup-data-store-kafka-connectors.ps1 -DataStores $dataStores
```

This creates separate Debezium connectors with data-store-specific topics.

#### 3. Verify Topics Were Created

```powershell
docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --list --bootstrap-server localhost:9092
# Should show: edfi.dms.1.document, edfi.dms.2.document, edfi.dms.3.document
```

### Files

* **`data_store_connector_template.json`**: Template for data-store-specific Debezium connectors
* **`setup-data-store-kafka-connectors.ps1`**: Automated script to deploy connectors
* **`postgresql_connector.json`**: Standard single-topic connector (for reference)

### Monitoring

```powershell
# Check connector status
curl http://localhost:8083/connectors/postgresql-source-datastore-1/status

# View messages in Kafka UI
# Open http://localhost:8088
```

## Accessing Swagger UI

Open your browser and go to <http://localhost:8082> Use the dropdown menu to
select either the Resources or Descriptors spec. Swagger UI is configured to
consume the DMS endpoints published on the host (localhost and the port defined
in DMS_HTTP_PORTS).
>[!NOTE]
> The user that is configured to use swagger must have the Web Origins
> configuration in Keycloak to allow CORS. To do this you must search for your
> Client in keycloak and add Web Origins (Example: Web origins:
> <http://localhost:8082>).

## Tips

### Setup Keycloak

`setup-keycloak.ps1` will setup all the required realm, client, client
credentials, required role, and custom claims.

Keep the Keycloak client secret aligned with the CMS `IdentitySettings:ClientSecretValidation`
settings in `appsettings.json` or environment variables. CMS startup will
reject configured secrets whose length falls outside the configured
minimum/maximum range.

```pwsh

$parameters = @{
    KeycloakServer = "http://localhost:8065"  # Keycloak URL
    Realm = "your_realm"                      # Realm name (default: edfi)
    AdminUsername = "admin"                   # Admin username (default: admin, If you used a different admin
    # username during the Keycloak setup, please ensure you use that specific value instead of the default 
    # 'admin' username when running this script.)
    AdminPassword = "admin"                   # Admin password (default: admin, If you used a different admin
    # password during the Keycloak setup, please ensure you use that specific value instead of the default 
    # 'admin' password when running this script.)
    NewClientRole = "dms-client"               # Client role (default: dms-client), If you want to setup 
    # Configuration service client, then use "cms-client"
    NewClientId = "test-client"                # Client id (default: test-client)
    NewClientName = "test-client"              # Client name (default: Test client)
    NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Client secret example
    ClientSecretMinimumLength = 32                # Must match IdentitySettings:ClientSecretValidation:MinimumLength
    ClientSecretMaximumLength = 128               # Must match IdentitySettings:ClientSecretValidation:MaximumLength
    ClientScopeName = "sis-vendor"             # Scope name (default: sis-vendor) We are including the 
    # claim set name as a scope in the token. This can be customized to any claim set name (e.g., 'Ed-Fi-Sandbox').
    # Please note that the claim name cannot contain spaces; use a hyphen (-) instead.
    TokenLifespan  = 1800                      # Token life span (default: 1800)
}

# To set up the Keycloak with Realm and client 
./setup-keycloak.ps1  @parameters

# Optionally use the $SetClientAsRealmAdmin switch to assign realm admin role to the new client. 
# This is required for DMS Configuration Service. 
$parameters = @{
    NewClientRole = "config-service-ap"         # Defined in Configuration Service appsettings
    NewClientId = "DmsConfigurationService"     # Defined in Configuration Service appsettings
    NewClientName = "DmsConfigurationService"   
    NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Must match Configuration Service appsettings. Use appsettings.developer.json
    ClientSecretMinimumLength = 32                        # Must match IdentitySettings:ClientSecretValidation:MinimumLength
    ClientSecretMaximumLength = 128                       # Must match IdentitySettings:ClientSecretValidation:MaximumLength
}
./setup-keycloak.ps1  @parameters -SetClientAsRealmAdmin
```

### Setup OpenIddict

`setup-openiddict.ps1` configures the required application, client credentials, roles, scopes, and custom claims for OpenIddict. It uses an environment file for configuration and supports database initialization.

`NewClientSecret` must stay within the configured `IdentitySettings:ClientSecretValidation`
minimum/maximum range and include at least one lowercase letter, one uppercase
letter, one number, and one supported special character. The script exposes
`ClientSecretMinimumLength` and `ClientSecretMaximumLength` so local setup can
match the runtime settings.

```pwsh
$parameters = @{
  OpenIddictServer = "http://localhost:8081"    # OpenIddict URL
  NewClientRole = "dms-client"                  # Client role (default: dms-client); use "cms-client" for Configuration Service
  NewClientId = "test-client"                   # Client id (default: test-client)
  NewClientName = "test-client"                 # Client name (default: Test client)
  NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Client secret example
  ClientSecretMinimumLength = 32                # Must match IdentitySettings:ClientSecretValidation:MinimumLength
  ClientSecretMaximumLength = 128               # Must match IdentitySettings:ClientSecretValidation:MaximumLength
  ClientScopeName = "sis-vendor"                # Scope name (default: sis-vendor); can be customized (e.g., 'Ed-Fi-Sandbox')
  EnvironmentFile = "./.env"                    # Path to environment file for DB and other settings
}

# To set up OpenIddict with client and roles
./setup-openiddict.ps1 @parameters

# Optionally, use the $InitDb switch to initialize the database schema and keys before inserting data.
# Use $InsertData to control whether client, roles, scopes, and claims are inserted.

# Example: Initialize DB and insert data
./setup-openiddict.ps1 @parameters -InitDb -InsertData

# Example: Only insert data (skip DB initialization)
./setup-openiddict.ps1 @parameters -InsertData

# Example: Only initialize DB (skip data insertion)
./setup-openiddict.ps1 @parameters -InitDb

# To set up a Configuration Service client, use these parameters:
$parameters = @{
  NewClientRole = "config-service-ap"           # Defined in Configuration Service appsettings
  NewClientId = "DmsConfigurationService"       # Defined in Configuration Service appsettings
  NewClientName = "DmsConfigurationService"
  NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Must match Configuration Service appsettings. Use appsettings.developer.json
  EnvironmentFile = "./.env"
}
./setup-openiddict.ps1 @parameters -InsertData
```

**Notes:**

* `EnvironmentFile` is required for DB connection and other settings.
* `$InitDb` initializes the database schema and keys.
* `$InsertData` inserts the OpenIddict client, roles, scopes, and claims.
* `ClientSecretMinimumLength` and `ClientSecretMaximumLength` should match the configured `IdentitySettings:ClientSecretValidation` bounds used by CMS.
* `NewClientSecret` must also satisfy the CMS complexity rule: lowercase, uppercase, number, and one special character from `!@#$%^&*()-_=+[]{}:;,.?`.
* Both switches can be used together or separately as needed.
