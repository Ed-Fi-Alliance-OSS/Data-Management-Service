# PostgreSQL -> Kafka -> OpenSearch

> [!WARNING]
> **NOT FOR PRODUCTION USE!** Includes passwords in the default configuration that are
> visible in this repo and should never be used in real life. Be very careful!

This document describes how to set up a development environment that uses Kafka and OpenSearch
(along with PostgreSQL) to handle GET by queries. For requirements and design background, see
[Change Data Capture to Stream](https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/docs/DMS/CDC-STREAMING.md).

1. Start these containers.

    ```shell
    docker compose up -d
    ```

1. Open a shell on the PostgreSQL container and run the following to enable
   replication:

    ```bash
    echo "host    replication    postgres         kafka-postgresql-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
    echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf
    ```

1. Restart the PostgreSQL container.
1. Create the `edfi_datamanagementservice` database and run the DB installer. 
1. Run the following SQL commands against the `edfi_datamanagementservice` database:
    ```sql
    CREATE PUBLICATION to_debezium WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = true);
    SELECT pg_create_logical_replication_slot('debezium', 'pgoutput');
    ALTER PUBLICATION to_debezium ADD TABLE dms.document;
    ```
1. Run `setup.ps1` to inject the connector configurations into the respective
   sink and source connector containers.
1. In the DMS `appsettings.json`, change `AppSettings.QueryHandler` to `opensearch`, and add
   `ConnectionStrings.OpenSearchUrl` set to `http://localhost:9200`.
1. Start the DMS and begin inserting and updating documents.
1. View Kafka changes in Kafka UI and OpenSearch changes in its Dashboard

Tools

* [Kafka UI](http://localhost:8080/)
* [OpenSearch Dashboard](http://localhost:5601/)
  * After a record has been inserted, create an [index
    pattern](http://localhost:5601/app/management/opensearch-dashboards/indexPatterns): `ed-fi$*`

Known problems:

* DELETE is not having any impact on OpenSearch.

## Clearing Out All Data in OpenSearch

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

## sf notes

`docker-compose --pull always`

docker compose -f docker-compose.yml -f dms-local.yml up -d
