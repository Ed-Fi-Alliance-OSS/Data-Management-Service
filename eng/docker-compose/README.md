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

Before running these, create a `.env` file. The `.env.example` is a good
starting point.

After starting up the environment, you must install Kafka connector
configurations into the two Kafka Connector images. The file
`setup-connectors.ps1` will do this for you. Warning: you need to wait a few
seconds after starting the services before you can install connector
configurations.

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

You can set up the Kafka UI and OpenSearch Dashboard containers for testing by
passing the -EnableOpenSearchUI option.

```pwsh
# Start everything with Kafka UI and OpenSearch Dashboard
./start-local-dms.ps1 -EnableOpenSearchUI
```

## Default URLs

* The DMS API: [http://localhost:8080](http://localhost:8080)
* Kafka UI: [http://localhost:8088/](http://localhost:8088/)
* OpenSearch Dashboard: [http://localhost:5601/](http://localhost:5601/)

## Known Problems

* DELETE is not having any impact on OpenSearch.

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
