# Snapshots and read replicas

A data store can have up to two derivative databases: a snapshot and a read replica.
A derivative is a second, read-only copy of the data store's database that DMS reads from instead of the primary.
This document is for operators.
It explains what each derivative does, how to create the database behind one, how to register it, and what to avoid while a client is extracting data from it.

Creating, refreshing, and dropping the database is your job.
DMS and the Configuration Service (CMS) store a connection string and route requests to it, and they do nothing else.
Neither one creates a snapshot, clones a database, or tears one down.

## What each derivative does

| Derivative | When DMS uses it |
| --- | --- |
| `Snapshot` | A snapshot-eligible read that carries `Use-Snapshot: true`. |
| `ReadReplica` | Every replica-eligible read-only request, automatically, when the request did not ask for a snapshot. |

The rules that follow from this:

- A snapshot wins over a read replica.
  A read with `Use-Snapshot: true` is served by the snapshot even when a read replica is configured.
- Writes always use the primary.
  `POST`, `PUT`, and `DELETE` never reach a derivative.
- There is no fallback from a failing derivative.
  A request that selected a derivative that cannot serve it fails, and DMS never retries it against the primary or against the other derivative.
- A data store with no derivative serves every request from the primary.

The reads that can use a derivative are resource and descriptor GET-many, resource and descriptor GET-by-id, resource and descriptor `/partitions`, resource and descriptor `/deletes`, resource and descriptor `/keyChanges`, and `/changeQueries/v1/availableChangeVersions`.
Discovery, OpenAPI, health, token issuance, and token introspection never use a derivative.
The `Use-Snapshot` header has no effect on them.

## Requirements for the source database

Whatever you use to produce the database, it must meet three requirements.

- It is the same database engine as the primary.
- It has the same generated DDL and the same `dms.EffectiveSchema` fingerprint as the primary.
- DMS reaches it with read-only credentials.

DMS checks the fingerprint the first time a request is routed to the derivative.
A database that has no `dms.EffectiveSchema` row, or one provisioned for a different effective schema, is answered with `503`.
A PostgreSQL clone or a SQL Server database snapshot of the primary satisfies the fingerprint requirement by construction.
A copy taken before the primary was reprovisioned for a different effective schema does not, and has to be recreated.

The credentials need enough permission for DMS reads, and the exact set is engine specific.
The recipes below grant the minimum, and each lists the error that a missing grant produces.

## Creating the source

Use a SQL Server database snapshot, a PostgreSQL clone or restored backup, or any equivalent read-only source your platform provides.
The two recipes below are the ones that have been run end to end against a DMS stack.
In both, replace the placeholders:

- `<source_db>` is the primary's database name.
- `<snapshot_db>` is the name of the new database.
- `<reader>` is the name of the read-only login.
- `<RO_PASSWORD>` is a generated password for that login, with 16 to 128 letters and digits.

### PostgreSQL

PostgreSQL has no database snapshot, so the recipe copies the database with `pg_dump` and `pg_restore`.
Run each command as the PostgreSQL administrator, inside the `dms-postgresql` container for a Docker deployment.

```text
createdb -U postgres -E UTF8 <snapshot_db>
pg_dump -U postgres -Fc -f /tmp/<snapshot_db>.dump <source_db>
pg_restore -U postgres --no-owner --no-privileges -d <snapshot_db> /tmp/<snapshot_db>.dump
rm -f /tmp/<snapshot_db>.dump
```

Then create the read-only role and its grants, as the administrator, connected to the new database.
The role is cluster wide, so on PostgreSQL it can be created after the restore.

```sql
CREATE ROLE <reader> LOGIN PASSWORD '<RO_PASSWORD>' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
REVOKE ALL ON DATABASE <snapshot_db> FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE <snapshot_db> TO <reader>;
GRANT USAGE ON SCHEMA dms, edfi, tpdm, auth, tracked_changes_edfi, tracked_changes_tpdm TO <reader>;
GRANT SELECT ON ALL TABLES IN SCHEMA dms, edfi, tpdm, auth, tracked_changes_edfi, tracked_changes_tpdm TO <reader>;
GRANT SELECT ON SEQUENCE dms."ChangeVersionSequence" TO <reader>;
```

The schema list above is the one in a data store provisioned with the Ed-Fi and TPDM extension.
List the schemas that your database actually has with this query, and grant on every one of them.

```sql
SELECT nspname FROM pg_namespace WHERE nspname IN ('dms','edfi','tpdm','auth') OR nspname LIKE 'tracked\_changes\_%'
```

The minimum grant set for DMS reads on PostgreSQL is:

- `CONNECT` on the database.
- `TEMPORARY` on the database.
  `PUBLIC` holds it by default, so it only becomes a required grant after `REVOKE ALL ... FROM PUBLIC`, as in the recipe.
- `USAGE` on each schema and `SELECT` on its tables.
- `SELECT` on the sequence `dms."ChangeVersionSequence"`.
  `GRANT SELECT ON ALL TABLES` does not cover a sequence.
- `EXECUTE` on `dms."GetMaxChangeVersion"()`, which `PUBLIC` holds by default.

If a grant is missing, the affected read answers `500` and the DMS log carries one of these errors.

| Missing grant | Surface that fails | Error in the DMS log |
| --- | --- | --- |
| `TEMPORARY` on the database | GET-many | `42501: permission denied to create temporary tables in database "<snapshot_db>"` |
| `SELECT` on `dms."ChangeVersionSequence"` | `/changeQueries/v1/availableChangeVersions` | `42501: permission denied for sequence ChangeVersionSequence` |

`availableChangeVersions` is the surface that exposes a missing sequence grant, and the other reads pass without it.
Check it after you create a snapshot, not only a resource read.

### SQL Server

SQL Server has native database snapshots.
Run each statement as an administrator with `sqlcmd`, and set `QUOTED_IDENTIFIER ON` for every statement, because `sqlcmd` defaults it to `OFF`, which breaks statements that touch the `dms` schema.
For a Docker deployment, this is the command that runs a statement in the `dms-mssql` container, with the administrator password in the `SQLCMDPASSWORD` environment variable.

```text
docker exec -e SQLCMDPASSWORD dms-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; <sql>"
```

First, read the logical name of each data file of the primary.
A snapshot lists only the `ROWS` files, and a DMS database normally has one.

```sql
SELECT name FROM <source_db>.sys.database_files WHERE type_desc = N'ROWS' ORDER BY file_id;
```

Second, create the login, the user, and every grant in the primary.
This must happen before the snapshot is created.

```sql
CREATE LOGIN <reader> WITH PASSWORD = N'<RO_PASSWORD>', CHECK_POLICY = OFF, DEFAULT_DATABASE = master;
USE <source_db>;
CREATE USER <reader> FOR LOGIN <reader>;
ALTER ROLE db_datareader ADD MEMBER <reader>;
GRANT EXECUTE ON OBJECT::dms.GetMaxChangeVersion TO <reader>;
GRANT VIEW DEFINITION ON OBJECT::dms.ChangeVersionSequence TO <reader>;
```

Third, create the snapshot, with one `(NAME, FILENAME)` pair for each logical file name from the first step.

```sql
CREATE DATABASE <snapshot_db> ON (NAME = N'<logical_file_name>', FILENAME = N'/var/opt/mssql/data/<snapshot_db>_1.ss') AS SNAPSHOT OF <source_db>;
```

The order matters.
A SQL Server snapshot is read-only and inherits its users and permissions from the moment it was created, so a grant run inside it fails and cannot be repaired afterward.

```text
Msg 3906, Level 16, State 1, Server dms-mssql, Line 1
Failed to update database "<snapshot_db>" because the database is read-only.
```

The fix is to add the grant in the primary, drop the snapshot, and create a new one.

The minimum grant set for DMS reads on SQL Server, all applied in the primary before the snapshot, is:

- Membership of the user in `db_datareader`.
- `EXECUTE` on `dms.GetMaxChangeVersion`.
- `VIEW DEFINITION` on `dms.ChangeVersionSequence`.

If a grant is missing, `/changeQueries/v1/availableChangeVersions` answers `500` and the other reads still answer `200`.

| Missing grant | Error in the DMS log |
| --- | --- |
| `EXECUTE` on `dms.GetMaxChangeVersion` | `The EXECUTE permission was denied on the object 'GetMaxChangeVersion', database '<snapshot_db>', schema 'dms'.` |
| `VIEW DEFINITION` on `dms.ChangeVersionSequence` | `System.Data.SqlTypes.SqlNullValueException: Data is Null. This method or property cannot be called on Null values.` |

Without `VIEW DEFINITION`, the function reads `sys.sequences`, the catalog view hides the sequence from the login, and the function returns `NULL`.

### Read replicas

A read replica is whatever your platform provides for streaming or managed replication.
Its credentials must be read-only, and it serves the same reads as a snapshot, so the grants above apply to it as well.
Register it with the type `ReadReplica`, as described next.

## Registering, replacing, and removing a derivative

You manage derivatives through the CMS `/v3/dataStoreDerivatives` endpoints, with an admin bearer token.
A derivative belongs to one data store, and it has a type and a connection string.

Each data store has at most one derivative of each type.
Registering a second `Snapshot` for a data store that already has one, or a second `ReadReplica`, is refused with `400`, and the error names the duplicate `DerivativeType` and the data store.
Registering a derivative for a data store that does not exist is refused with `409`.

Register a snapshot with a `POST`, using the connection string DMS uses to reach the database.
The host name is the one DMS resolves, which is not necessarily the one you use from your own machine.

```text
curl -X POST http://localhost:8081/v3/dataStoreDerivatives -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"dataStoreId":1,"derivativeType":"Snapshot","connectionString":"host=dms-postgresql;port=5432;username=<reader>;password=<RO_PASSWORD>;database=<snapshot_db>"}'
```

The SQL Server form differs only in the connection string.

```json
{"dataStoreId":1,"derivativeType":"Snapshot","connectionString":"Server=dms-mssql,1433;Database=<snapshot_db>;User Id=<reader>;Password=<RO_PASSWORD>;TrustServerCertificate=true"}
```

CMS answers `201` with the new derivative's `id`.
Keep the id, because removing the derivative needs it.

Use `"derivativeType":"ReadReplica"` to register a read replica.
To replace a derivative, send `PUT /v3/dataStoreDerivatives/{id}` with the same fields plus the `id`, or remove the old one and register the new one.
Either way, read the next two sections first, because replacing a derivative while someone is extracting from it breaks that extraction.

Remove a derivative with its id.
CMS answers `204`.

```text
DELETE http://localhost:8081/v3/dataStoreDerivatives/<id>
```

## When a change takes effect

DMS caches the data store configuration, including the derivatives, and reloads it every `CacheSettings:DataStoreCacheExpirationSeconds`.
A registration, a replacement, or a removal reaches DMS at the next reload, not at the moment CMS answers.
Plan for a wait of up to that many seconds before you start an extraction, which is ten minutes at the default of `600`.

Until the registration takes effect, a read with `Use-Snapshot: true` is answered with Snapshot Not Found `404`.
A removal behaves the same way in the other direction: the snapshot keeps being served until the cache reloads.
As an example of the size of the wait, a registration at the default of `600` took 559 seconds to take effect on both engines.
With `CacheSettings:DataStoreCacheExpirationSeconds` set to `10`, it took between 4 and 11 seconds.

If a read that follows the `404` phase answers `500` instead of `200`, the derivative is registered and reachable but a read against it fails.
That is a missing grant or a database problem, not a cache wait.
Look for the errors in the [Creating the source](#creating-the-source) tables in the DMS log.

DMS also caches the result of validating a derivative database for `CacheSettings:DerivativeValidationCacheExpirationSeconds`.
That setting matters when you recreate a snapshot at the same connection string, which changes nothing in CMS: DMS notices the replacement database only when that cached result expires.
Both settings, their defaults, and their bounds are described in [Configuration](./CONFIGURATION.md#cachesettings) and [DerivativeValidationCacheExpirationSeconds](./CONFIGURATION.md#derivativevalidationcacheexpirationseconds).
They are not repeated here.

## Keep the snapshot fixed while a client extracts from it

DMS selects a derivative once for each request.
It has no notion of an extraction that spans requests, so nothing in the API ties the many reads of one extraction together.
The isolation that a snapshot gives therefore holds only while you keep the snapshot fixed for the whole extraction.

While an extraction is running:

- Do not replace or re-point the data store's `Snapshot` derivative in CMS.
- Do not recreate the snapshot database at the connection string that is already registered.
- Do not remove the derivative, and do not drop the snapshot database or make it unreachable.

The three cases do not fail the same way.

| What you did | What the client sees |
| --- | --- |
| Re-pointed the derivative to a different connection string. | Nothing is reported, and requests issued after the data store cache reloads are served from the new database. |
| Recreated the snapshot database at the same connection string. | Nothing is reported, and DMS cannot detect it, so requests are served from the new database once a later connection reaches it. |
| Removed the derivative, or dropped the snapshot database, or made it unreachable. | The extraction is interrupted with Snapshot Not Found `404`, and there is no fallback to the primary. |

The first two outcomes are silent.
The extraction finishes normally, but its pages come from two different points in time, and nothing in a response distinguishes that from a correctly isolated extraction.
Pages that were already returned are not read again.
The same connection string is the worse case, because the configuration never changed and DMS has nothing to notice.

The third outcome is not silent, and it does not break isolation.
It is the Snapshot Not Found response described below, reached in the middle of an extraction, and it still costs you the extraction.

If the replacement database was provisioned for a different effective schema than the one DMS expects, fingerprint validation answers `503` for the requests that reach it, once DMS revalidates the database.
Change a snapshot, or reuse its connection string, between extractions and never during one.

## Retiring obsolete databases

When you retire a snapshot, do both of these, in this order:

1. Remove its derivative row, so DMS stops routing to it.
2. Drop the database, and drop its read-only login or role.

Removing the row matters even when you give every snapshot a new name.
A derivative row that remains after its database is gone keeps pooled connections to the old database open, and it keeps answering `Use-Snapshot: true` reads with `404`.
Removal reaches DMS at the next data store cache reload, so wait for it before you drop a database that a running extraction might still read.

On PostgreSQL, retire a snapshot with these statements, run one at a time, because `DROP DATABASE` does not run inside a multi-statement transaction.

```sql
DROP DATABASE IF EXISTS <snapshot_db> WITH (FORCE);
DROP ROLE IF EXISTS <reader>;
```

On SQL Server, retire it with these.

```sql
IF DB_ID(N'<snapshot_db>') IS NOT NULL DROP DATABASE <snapshot_db>;
USE <source_db>;
IF USER_ID(N'<reader>') IS NOT NULL DROP USER <reader>;
IF SUSER_ID(N'<reader>') IS NOT NULL DROP LOGIN <reader>;
```

## Read replicas and consistency

A read replica can be eventually consistent.
DMS does not measure replica lag, and it does not guarantee that `/changeQueries/v1/availableChangeVersions` on a replica has caught up with the primary.
A client that needs a fixed extraction boundary should use a prepared `Snapshot`, not a `ReadReplica`.

A read replica that is registered but cannot be reached does not fall back to the primary.
When DMS validates the replica, which it does on the first request that selects it and again after the validation verdict expires, the request is answered `503` with a service configuration error.
When the replica becomes unreachable while an earlier successful validation is still cached, the request is answered with the generic `500`.
Neither is ever translated into Snapshot Not Found.

## Using the Ed-Fi API Publisher

The Publisher adds `Use-Snapshot: true` to every request it sends to a source API whose major version is 7 or later.
A Publisher run against a DMS source therefore needs one of two things.

- Configure a `Snapshot` derivative for the source data store, and keep it fixed for the run as described above.
  The Publisher then extracts from a point-in-time copy.
- Pass `--ignoreIsolation=true`.
  The Publisher then does not send the header, and it reads the live primary with no isolation.

A snapshot is what you want when the source keeps changing during the run.
The reads of one extraction are the change window from `/availableChangeVersions`, the `/partitions` probe, total counts, GET-many pages, `/deletes`, and `/keyChanges`.
All of them carry the header and are served by the same snapshot.
A write committed to the primary after the snapshot was created does not appear in the extraction.

DMS serves GET-by-id from a snapshot too.
The Publisher does not issue GET-by-id against a DMS source, so that route does not appear in a run.

### Troubleshooting: no snapshot is configured

If the source data store has no `Snapshot` derivative and the run does not pass `--ignoreIsolation=true`, every source read is answered with Snapshot Not Found `404`.
The Publisher exits with code `2`, and nothing is written to the target.
In the validated run, DMS rejected 386 requests: one `/changeQueries/v1/availableChangeVersions`, one `/partitions` probe, and 384 total-count reads, one for each resource the Publisher scheduled.
The number of count reads depends on the resources in your data standard.

These are the lines the Publisher writes, in order, from API Publisher 1.4.0.
The Publisher pads its lines with trailing spaces, and it puts stray quotes around the change-queries path in the first line.

```text
[2026-11-03 22:08:07,883] [WARN] [07] EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Versioning.EdFiApiSourceCurrentChangeVersionProvider - Unable to get current change version from source API at 'http://ed-fi-api:8080/"changeQueries/v1/availableChangeVersions"' (response status: NotFound). Full synchronization will always be performed against this source, and any concurrent changes made against the source may cause change processing to produce unreliable results.
[2026-11-03 22:08:08,117] [INFO] [17] EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Capabilities.EdFiApiSourceCapabilities - Source API does not expose /partitions (status 404); offset/limit paging will be used.
[2026-11-03 22:08:08,241] [EROR] [15] EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting.EdFiApiSourceTotalCountProvider - /ed-fi/accountTypeDescriptors: NotFound - Unable to obtain total count due to request failure. This resource will not be processed. Downstream failures are possible.
Response content: {"detail":"Snapshot not found.","type":"urn:ed-fi:api:not-found","title":"Not Found","status":404,"correlationId":"0HNPPUPON67RP:00000001","validationErrors":{},"errors":[]}
[2026-11-03 22:08:08,692] [FATL] [08] EdFi.Tools.ApiPublisher.Core.Processing.ChangeProcessor - An unhandled exception occurred during processing: EdFi.Tools.ApiPublisher.Core.Processing.PublishingFailedException: Processing completed, but 384 source read error(s) mean an unknown number of documents was never attempted (of 384 error(s) in total).
[2026-11-03 22:08:08,714] [EROR] [16] EdFi.Tools.ApiPublisher.Cli.Program - Processing failed:
  ! 384 source read error(s) occurred, so an unknown number of documents was never attempted (the affected pages and counts are in the reported errors).
```

Timestamps, thread numbers, the resource named in the `EROR` line, and the correlation id differ from run to run.
The Publisher reports only `Snapshot not found` inside the response body, and nothing in its own text tells you what to do about it.
The fix is to configure and register a snapshot, wait for the data store cache to reload, and run again, or to pass `--ignoreIsolation=true`.

A snapshot that was removed, and a snapshot that cannot be reached, produce the same response and the same lines.

## Response contract

### Snapshot Not Found

A snapshot-eligible read with `Use-Snapshot: true` is answered with Snapshot Not Found when no snapshot is configured, and when the configured snapshot cannot be reached.
The two causes produce the same response, so a client cannot tell them apart.
The status is `404`, the content type is `application/problem+json`, and the body is:

```json
{
    "detail": "Snapshot not found.",
    "type": "urn:ed-fi:api:not-found",
    "title": "Not Found",
    "status": 404,
    "correlationId": "<correlation id>",
    "validationErrors": {},
    "errors": []
}
```

An unreachable snapshot covers a database that does not exist, a login that is refused, a host that cannot be resolved, a timeout, and a connection string that the database provider rejects.
There is no fallback to the primary.
DMS logs the failure and the target kind, and it never logs the connection string.

A reachable snapshot that is not provisioned, or that was provisioned for a different effective schema, is a different failure.
It is answered with the database `503` from fingerprint validation, not with `404`.

### Writes with `Use-Snapshot: true`

A resource or descriptor `POST`, `PUT`, or `DELETE` that carries `Use-Snapshot: true` is refused with `405` and an `Allow: GET` header.
The content type is `application/problem+json`, and the body is:

```json
{
    "detail": "An attempt was made to modify data in a Snapshot, but this data is read-only.",
    "type": "urn:ed-fi:api:snapshots:method-not-allowed",
    "title": "Method Not Allowed with Snapshots",
    "status": 405,
    "correlationId": "<correlation id>",
    "validationErrors": {},
    "errors": []
}
```

DMS refuses the request before it validates the route, the content type, or the body, and without opening any database.
That includes the invalid shapes, which are a collection `DELETE`, a collection `PUT`, and an item `POST`.
With the header, those shapes get this `405`.
Without the header, or with `Use-Snapshot: false`, they keep the existing route-semantics `405`, with its own `Allow` value.
`Allow: GET` states what a snapshot permits, not what the route permits on the primary.

Verbs that DMS does not map, such as `HEAD` and `PATCH`, keep their existing method-not-allowed response whether or not the header is present.
`OPTIONS` is also unaffected.

A client that sends `Use-Snapshot: true` on every request, including its writes, therefore has its writes refused.
The Publisher is not affected, because it sends the header to its source only, and it writes to its target.

### Reading the `Use-Snapshot` header

Only a value that parses as boolean true selects the snapshot.
`true` in any letter case does, and so does a value with surrounding whitespace.
An absent header, `false`, and any other value, such as `yes` or `1`, mean current data.
A malformed value is not an error.
A header with a blank value is treated as absent.

### Order of checks

Authentication, tenant validation, authorization, and route resolution run before a derivative is selected.
An invalid caller, or a request for an unknown resource, gets its usual `401`, `403`, `400`, or `404`, whatever the header says and whatever is configured.
A request for an unknown resource with `Use-Snapshot: true` therefore still answers `404` for the resource.
Only after that does DMS apply the snapshot rules above.
