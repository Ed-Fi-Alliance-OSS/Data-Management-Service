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

1. `kafka-opensearch.yml` covers Kafka, OpenSearch
2. `kafka-opensearch-ui.yml` covers KafkaUI, OpenSearch Dashboard
3. `postgresql.yml` starts only PostgreSQL
4. `local-dms.yml` runs the DMS from local source code.
5. `published-dms.yml` runs the latest DMS `pre` tag as published to Docker Hub.
6. `keycloak.yml` runs KeyCloak (identity provider).
7. `kafka-elasticsearch.yml` covers Kafka, ElasticSearch
8. `kafka-elasticsearch-ui.yml` covers KafkaUI, ElasticSearch(Kibana) Dashboard
9. `swagger-ui.yml` covers SwaggerUI

Before running these, create a `.env` file. The `.env.example` is a good
starting point.

After starting the environment, you’ll need to install Kafka connector
configurations in both Kafka Connector images. The file `setup-connectors.ps1`
will do this for you. Warning: you need to wait a few seconds after starting the
services before you can install connector configurations.

You can specify the search engine type to set the appropriate Kafka connector
configurations. Valid values are `OpenSearch` and `ElasticSearch`. The default
value is `OpenSearch`.

```pwsh
# To install OpenSearch Kafka connector configurations
./setup-connectors.ps1 -SearchEngine "OpenSearch"
```

```pwsh
# To install ElasticSearch Kafka connector configurations
./setup-connectors.ps1 -SearchEngine "ElasticSearch"
```

> [!WARNING]
> The script `setup-connectors.ps1` first attempts to delete
> connectors that are already installed. On first execution, this results in a
> 404 error. _This is normal_. Ignore that initial 404 error message.

Convenience PowerShell scripts have been included in the directory, which
startup the appropriate services and inject the Kafka connectors (where
relevant).

* `start-all-services.ps1` launches both `postgresql.yml` and
  `kafka-opensearch.yml`, without starting the DMS. Useful for running DMS in a
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

If you want to use Self-Contained authentication using OpenIddict instead of Keycloak, you can pass the `-IdentityProvider self-contained` parameter to the startup script. This will configure the environment to use Self-Contained authentication as the identity provider, and Keycloak will not be required.

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

You can set up the Kafka UI and OpenSearch or ElasticSearch Dashboard containers
for testing by passing the -EnableSearchEngineUI option.

```pwsh
# Start everything with Kafka UI and OpenSearch or ElasticSearch Dashboard
./start-local-dms.ps1 -EnableSearchEngineUI
```

Search engine type. Valid values are `OpenSearch`, `ElasticSearch`. Default:
`OpenSearch`

```pwsh
# To setup OpenSearch search engine
./start-local-dms.ps1 -SearchEngine "OpenSearch"
```

```pwsh
# To setup ElasticSearch search engine
./start-local-dms.ps1 -SearchEngine "ElasticSearch"
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
* OpenSearch Dashboard: [http://localhost:5601/](http://localhost:5601/)
* Swagger UI: [http://localhost:8082](http://localhost:8082)

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

### Clearing Out All Data in OpenSearch

Run the following commands from the Dev Tools console in OpenSearch Dashboard:

Delete all documents:

```none
POST */_delete_by_query
{
  "query": {
    "match_all": {}
  }
}
```

Delete all indices:

```none
DELETE *
```

### OpenSearch Integration Stops Working

If the OpenSearch integration fails, you'll need to dig into log messages in the
Docker containers. In PostgreSQL, if you see a message indicating that the
replication setup failed, this may be a Docker problem where it has failed to
load a startup script properly. Restarting Docker Desktop (and possibly applying
the latest updates) may fix the issue.

> [!TIP]
> To diagnose the problem described above, open a terminal in the
> PostgreSQL container and run `/docker-entrypoint-initdb.d/postgresql-init.sh`.
> Does the result show you that this is a _file_ or a _directory_? Sometimes
> Docker Desktop incorrectly loads this as a directory, which means that the
> file will not execute on startup.

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
