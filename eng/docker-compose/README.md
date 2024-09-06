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

This directory contains three Docker Compose files:

1. `docker-compose.yml` starts up services used by the Data Management Service
   (DMS) _without_ starting the DMS itself.
2. `dms-local.yml` start the DMS, built locally from current source code.
3. `dms-published.yml` starts the DMS using the
   `edfialliance/data-management-service` image on Docker Hub.

When you only need to run the associated services - for instance, while running
DMS in debug mode in Visual Studio or VS Code - then just startup the
`docker-compose.yml` file. The `docker compose` command accepts multiple `-f
[filename]` flags, which allows you to combine multiple compose files into the
same network segment. Thus you can run `docker-compose.yml` _and_ one of the
`dms-*.yml` files at the same time to get a full-fledged testing and
demonstration environment.

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

Convenience PowerShell scripts have been included in the directory:

```pwsh
# Three options for starting services
./start-services-only.ps1
./start-local-dms.ps1
./start-published-dms.ps1

# With the local image, you can optionally force rebuilding the image
./start-local-dms.ps1 -b

# Stop the services, keeping volumes
./start-services-only.ps1 -d
./start-local-dms.ps1 -d
./start-published-dms.ps1 -d

# Stop the services and delete volumes
./start-services-only.ps1 -d -v
./start-local-dms.ps1 -d -v
./start-published-dms.ps1 -d -v
```

## Default URLs

* The DMS API: [http://localhost:8080](http://localhost:8080)
* Kafka UI: [http://localhost:8088/](http://localhost:8088/)
* OpenSearch Dashboard: [http://localhost:5601/](http://localhost:5601/)

## Known Problems

* DELETE is not having any impact on OpenSearch.

## Tips

### Load Sample Data

See the [../bulkLoad](../bulkLoad/README.MD) directory for scripts that will
bulk load XML files into the API.

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
