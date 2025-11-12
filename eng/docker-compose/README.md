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

After starting the environment, you’ll need to install Kafka connector
configurations in both Kafka Connector images. The file `setup-connectors.ps1`
will do this for you. Warning: you need to wait a few seconds after starting the
services before you can install connector configurations.

```pwsh
./setup-connectors.ps1
```

> [!WARNING]
> The script `setup-connectors.ps1` first attempts to delete
> connectors that are already installed. On first execution, this results in a
> 404 error. _This is normal_. Ignore that initial 404 error message.

Convenience PowerShell scripts have been included in the directory, which
startup the appropriate services and inject the Kafka connectors (where
relevant).

* `start-all-services.ps1` launches both `postgresql.yml` and
  `kafka.yml`, without starting the DMS. Useful for running DMS in a
  local debugger.
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

# Start everything for E2E testing
./start-local-dms.ps1 -EnvironmentFile ./.env.e2e

# Stop the services, keeping volumes
./start-local-dms.ps1 -d

# Stop the services and delete volumes
./start-local-dms.ps1 -d -v
```

By default, authentication uses the Self-Contained (OpenIddict) identity provider. The environment and startup scripts are pre-configured for Self-Contained mode, and Keycloak is not required unless explicitly selected.

If you want to use Keycloak as the identity provider, pass the `-IdentityProvider keycloak` parameter to the startup script. This will configure the environment to use Keycloak authentication, and you must ensure Keycloak is running and properly configured.

```pwsh
# Start everything (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -IdentityProvider self-contained

# Start everything for E2E testing (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -IdentityProvider self-contained -EnvironmentFile ./.env.e2e

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
    "version": "1.0.300",
    "feedUrl": "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
    "name": "EdFi.DataStandard52.ApiSchema"
  },
  {
    "version": "1.0.300",
    "feedUrl": "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
    "name": "EdFi.Sample.ApiSchema",
    "extensionName": "Sample"
  }
]'
```

You can also automatically include extension-specific metadata in the
authorization hierarchy to enable authorization for your extension resources. To
do this:

1. Author your security claims hierarchy JSON file. See `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets`
for examples. Files must follow the pattern: `{number}-{description}-claimset.json` and will be processed in order.

2. Place it in a directory mounted as a Docker volume named `app/additional-claims` for CMS to see at runtime. The default behavior of the docker compose
scripts is to mount `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets`.

3. Use the `-AddExtensionSecurityMetadata` parameter to configure CMS to read claimsets from `app/additional-claims`:

```pwsh
./start-local-dms.ps1 -AddExtensionSecurityMetadata
```

## Default URLs

* The DMS API: [http://localhost:8080](http://localhost:8080)
* Kafka UI: [http://localhost:8088/](http://localhost:8088/)
* Swagger UI: [http://localhost:8082](http://localhost:8082)

## Multi-Instance Testing with Route Qualifiers

The DMS supports multi-instance routing with route qualifiers, allowing you to route requests to different databases based on URL path segments.

### Step 1: Configure Environment for Multi-Instance

```powershell
# Navigate to docker-compose directory
cd eng/docker-compose

# Copy the environment template if you haven't already
cp .env.example .env

# Edit .env and set the ROUTE_QUALIFIER_SEGMENTS line:
# Find this section in .env:
#   # Multi-Instance Route Qualifiers (Optional)
#   #ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
#
# Change to (uncomment the line):
#   ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
```

### Step 2: Deploy DMS with Multi-Instance Configuration

```powershell
# Deploy with multi-instance configuration enabled
./start-local-dms.ps1 -EnableConfig -r
```

Wait for all services to start (check with `docker ps`).

### Step 3: Create Additional Databases

The main `.env` file creates the main database, but you need to create the additional databases for each instance:

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

1. Open `src/dms/tests/RestClient/multi-instance-route-qualifiers.http` in VS Code
2. Execute the requests in order (they build on each other)
3. Follow the instructions in the comments

Key testing steps:

1. **Get Config Service Token** - Authenticates with the configuration service
2. **Create DMS Instances** - Sets up 3 instances with different route contexts:
   - Instance 1: District 255901, School Year 2024
   - Instance 2: District 255901, School Year 2025
   - Instance 3: District 255902, School Year 2024
3. **Create Application** - Creates an app associated with all instances
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

- `/255901/2024/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255901_sy2024`
- `/255901/2025/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255901_sy2025`
- `/255902/2024/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255902_sy2024`

### Troubleshooting

**Route qualifiers not being parsed:**

- Check that `ROUTE_QUALIFIER_SEGMENTS` is set correctly in the `.env` file (comma-separated format)
- Verify the environment variable in the container: `docker exec docker-compose-dms-1 printenv | grep ROUTE`
- Verify the DMS logs: `docker logs docker-compose-dms-1`

**404 - No database instance found or "No candidates found for the request path":**

- **Most common cause**: DMS container needs to be restarted after creating instances in the Configuration Service
- Verify DMS loaded all instances by checking the logs:
  ```powershell
  docker logs docker-compose-dms-1 | grep "Successfully fetched"
  # Should show: "Successfully fetched 4 DMS instances" (or your expected count)
  ```
- Verify route contexts are created correctly in the Configuration Service
- Check that the application is associated with the instances
- Verify the JWT token includes the correct `dms_instance_ids` claim

**Connection errors:**

- Ensure all databases are created
- Verify schema is deployed to each database
- Check PostgreSQL is running: `docker ps | grep postgresql`

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

## Kafka Topic-Per-Instance Architecture

For E2E testing and production deployments that require strict data isolation, DMS supports topic-per-instance architecture where each instance publishes to its own dedicated Kafka topic.

### Overview

**Standard Setup:**
- All instances → Single topic: `edfi.dms.document`

**Topic-Per-Instance Setup:**
- Instance 1 → Topic: `edfi.dms.1.document`
- Instance 2 → Topic: `edfi.dms.2.document`
- Instance 3 → Topic: `edfi.dms.3.document`

This architecture is critical for:
- **FERPA Compliance**: Prevents cross-instance data leakage
- **Multi-tenant Isolation**: Each tenant/district has isolated message streams
- **Selective Consumption**: Consumers can subscribe to specific instances

### Automated Setup for E2E Tests

The Instance Management E2E tests automatically configure topic-per-instance via the build script:

```powershell
.\build-dms.ps1 InstanceE2ETest -Configuration Release
```

The build script:
1. Creates test databases for each instance
2. Creates PostgreSQL publications for CDC
3. Configures instance-specific Debezium connectors
4. Runs E2E tests with Kafka validation

### Manual Setup for Development

#### 1. Create PostgreSQL Publications

Each instance database needs its own publication for Debezium:

```powershell
# Instance 1
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "CREATE PUBLICATION to_debezium_instance_1 FOR TABLE dms.document, dms.educationorganizationhierarchytermslookup;"

# Instance 2
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025 -c "CREATE PUBLICATION to_debezium_instance_2 FOR TABLE dms.document, dms.educationorganizationhierarchytermslookup;"

# Instance 3
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024 -c "CREATE PUBLICATION to_debezium_instance_3 FOR TABLE dms.document, dms.educationorganizationhierarchytermslookup;"
```

#### 2. Configure Debezium Connectors

Use the automated setup script (from `eng/docker-compose` directory):

```powershell
$instances = @(
    @{ InstanceId = 1; DatabaseName = "edfi_datamanagementservice_d255901_sy2024" },
    @{ InstanceId = 2; DatabaseName = "edfi_datamanagementservice_d255901_sy2025" },
    @{ InstanceId = 3; DatabaseName = "edfi_datamanagementservice_d255902_sy2024" }
)

.\setup-instance-kafka-connectors.ps1 -Instances $instances
```

This creates separate Debezium connectors with instance-specific topics.

#### 3. Verify Topics Were Created

```powershell
docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --list --bootstrap-server localhost:9092
# Should show: edfi.dms.1.document, edfi.dms.2.document, edfi.dms.3.document
```

### Files

- **`instance_connector_template.json`**: Template for instance-specific Debezium connectors
- **`setup-instance-kafka-connectors.ps1`**: Automated script to deploy connectors
- **`postgresql_connector.json`**: Standard single-topic connector (for reference)

### Monitoring

```powershell
# Check connector status
curl http://localhost:8083/connectors/postgresql-source-instance-1/status

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
    NewClientSecret = "s3creT@09"              # Client secret (default: s3creT@09)
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
    NewClientSecret = "s3creT@09"               # Must match Configuration Service appsettings. Use appsettings.developer.json
}
./setup-keycloak.ps1  @parameters -SetClientAsRealmAdmin
```

### Setup OpenIddict

`setup-openiddict.ps1` configures the required application, client credentials, roles, scopes, and custom claims for OpenIddict. It uses an environment file for configuration and supports database initialization.

```pwsh
$parameters = @{
  OpenIddictServer = "http://localhost:8081"    # OpenIddict URL
  NewClientRole = "dms-client"                  # Client role (default: dms-client); use "cms-client" for Configuration Service
  NewClientId = "test-client"                   # Client id (default: test-client)
  NewClientName = "test-client"                 # Client name (default: Test client)
  NewClientSecret = "s3creT@09"                 # Client secret (default: s3creT@09)
  ClientScopeName = "sis-vendor"                # Scope name (default: sis-vendor); can be customized (e.g., 'Ed-Fi-Sandbox')
  EnvironmentFile = "./.env"                    # Path to environment file for DB and other settings
}

# To set up OpenIddict with client and roles
./setup-openiddict.ps1 @parameters

# Optionally, use the $InitDb switch to initialize the database schema and keys before inserting data.
# Use $InsertData to control whether client, roles, scopes, and claims are inserted.
# Both switches are enabled by default.

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
  NewClientSecret = "s3creT@09"                 # Must match Configuration Service appsettings. Use appsettings.developer.json
  EnvironmentFile = "./.env"
}
./setup-openiddict.ps1 @parameters -InsertData
```

**Notes:**

* `EnvironmentFile` is required for DB connection and other settings.
* `$InitDb` initializes the database schema and keys.
* `$InsertData` inserts the OpenIddict client, roles, scopes, and claims.
* Both switches can be used together or separately as needed.
