# Docker Compose Test and Demonstration Configurations

> [!WARNING]
> **NOT FOR PRODUCTION USE!** Includes passwords in the default configuration that are
> visible in this repo and should never be used in real life. Be very careful!

> [!NOTE]
> This document describes a reference architecture that should assist in
> building production deployments. This reference architecture will not be tuned
> for real-world production usage. For example, it will not include service clustering,
> may not be well secured, and it will not utilize cloud providers' manage services.
> Also see [Debezium Connector for PostgreSQL](https://debezium.io/documentation/reference/2.7/connectors/postgresql.html)
> for additional notes on securely configuring replication.

## Starting Services with Docker Compose

This directory contains several Docker Compose files, which can be combined to
start up different configurations:

1. `kafka-opensearch.yml` covers Kafka, Zookeeper, OpenSearch
2. `kafka-opensearch-ui.yml` covers KafkaUI, OpenSearch Dashboard
3. `postgresql.yml` starts only PostgreSQL
4. `local-dms.yml` runs the DMS from local source code.
5. `published-dms.yml` runs the latest DMS `pre` tag as published to Docker Hub.
6. `keycloak.yml` runs KeyCloak (identity provider).
7. `kafka-elasticsearch.yml` covers Kafka, Zookeeper, ElasticSearch
8. `kafka-elasticsearch-ui` covers KafkaUI, ElasticSearch(Kibana) Dashboard

Before running these, create a `.env` file. The `.env.example` is a good
starting point.

After starting up the environment, you must install Kafka connector
configurations into the two Kafka Connector images. The file
`setup-connectors.ps1` will do this for you. Warning: you need to wait a few
seconds after starting the services before you can install connector
configurations.

You can specify the search engine type to set the appropriate Kafka connector
configurations. Valid values are `OpenSearch` and `ElasticSearch`. The default value
is `OpenSearch`.

```pwsh
# To install OpenSearch Kafka connector configurations
./setup-connectors.ps1 -SearchEngine "OpenSearch"
```

```pwsh
# To install ElasticSearch Kafka connector configurations
./setup-connectors.ps1 -SearchEngine "ElasticSearch"
```

> [!WARNING]
> The script `setup-connectors.ps1` first attempts to delete connectors that are
> already installed. On first execution, this results in a 404 error. _This is
> normal_. Ignore that initial 404 error message.

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

You can set up the Kafka UI and OpenSearch or ElasticSearch Dashboard containers
for testing by passing the -EnableSearchEngineUI option.

```pwsh
# Start everything with Kafka UI and OpenSearch or ElasticSearch Dashboard
./start-local-dms.ps1 -EnableSearchEngineUI
```

Search engine type. Valid values are `OpenSearch`, `ElasticSearch`. Default: `OpenSearch`

```pwsh
# To setup OpenSearch search engine
./start-local-dms.ps1 -SearchEngine "OpenSearch"
```

```pwsh
# To setup ElasticSearch search engine
./start-local-dms.ps1 -SearchEngine "ElasticSearch"
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

## Default URLs

* The DMS API: [http://localhost:8080](http://localhost:8080)
* Kafka UI: [http://localhost:8088/](http://localhost:8088/)
* OpenSearch Dashboard: [http://localhost:5601/](http://localhost:5601/)

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
> To diagnose the problem described above, open a terminal in the PostgreSQL container
> and run `/docker-entrypoint-initdb.d/postgresql-init.sh`. Does the result show you
> that this is a _file_ or a _directory_? Sometimes Docker Desktop incorrectly loads
> this as a directory, which means that the file will not execute on startup.

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
    # Configuration service client, then use "config-service-app"
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
