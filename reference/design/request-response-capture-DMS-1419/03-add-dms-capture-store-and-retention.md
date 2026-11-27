---
jira:
epic: DMS-1418
source_spike: DMS-1419
---

# Story: Add the Capture Store, Writer, and Retention Purge

## TL;DR

Adds the separate database where captured API traffic is kept, the background writer that fills it without slowing requests, and the job that deletes old captures after a set number of days.

## Description

Captured rows go to a dedicated capture database, never to the logs.
This story builds that store: the command that creates its table, the DMS settings that point at it, the background writer, the purge, and the Docker opt-in the capture-pipeline story's tests run on.
The row itself is defined by the credential-exclusion story, and nothing queues a row until the capture-pipeline story lands.

Per `reference/design/request-response-capture-DMS-1419/design.md` ("## D6: Capture Store and Retention": "### Configuration Surface", "### Provisioning", "### Semantics", "### Retention").

**Citation convention.**
Unprefixed paths are relative to the repository root.

## Acceptance Criteria

**Provisioning**

- A SchemaTools subcommand, `capture provision`, creates the capture table and its indexes in a named database, for PostgreSQL and SQL Server, on the pattern of `ddl provision` (`src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md`).
- The table's columns are the capture-row contract's columns, generated from it.
- The indexes cover the receive time, the client key with the receive time, and the grant id.
- The command is idempotent: run twice, it changes nothing the second time. It never drops or alters an existing table.
- The SchemaTools README documents the command.
- The Docker environment (`eng/docker-compose/`) can opt in to capture: it provisions a capture database with `capture provision` and passes `RequestResponseCapture__ConnectionString` to DMS, for PostgreSQL and SQL Server. The E2E environment opts in.
- The opt-in also passes `RequestResponseCapture__PollIntervalSeconds` and `RequestResponseCapture__PollTimeoutSeconds`, overridable environment variables defaulting to 2 and 1, as the compose files already do for other pollers (`eng/docker-compose/local-dms.compose.yml:76`). A new grant then reaches DMS within `A` = 5 seconds instead of 70.

**Settings**

- DMS binds `RequestResponseCapture:ConnectionString`, `RetentionDays` (default 7), `MaxStoredBodyBytes` (default 1,048,576), `WriterQueueCapacity` (default 1,000), and `WriterQueueMaxBytes` (default 67,108,864).
- An empty connection string registers nothing from this story.
- A non-positive number fails startup.
- The store uses the dialect of `AppSettings:Datastore`.

**The writer**

- Rows are queued to a bounded in-memory queue, holding at most `WriterQueueCapacity` rows and `WriterQueueMaxBytes` of body bytes, and inserted in batches by a background service.
- When the writer starts, it checks that the capture table exists with the capture-row contract's columns. If it does not, the writer logs one error naming `capture provision` and stays disabled: rows are dropped, DMS still starts, and no further error is logged per row or batch.
- Queuing never blocks and never throws. When either bound would be exceeded, the row is dropped. The drop warning is rate-limited per grant and carries only the grant id and the number of rows dropped since the last warning.
- When the store fails, the batch is dropped and a warning carries only metadata, rate-limited like the drop warning. The writer keeps running.
- On shutdown, the writer drains what it can within the host's shutdown timeout.

**Retention**

- A hosted purge deletes rows whose receive time is older than `RetentionDays`, in batches, on a fixed schedule.
- It runs under a database mutex, `pg_advisory_lock` or `sp_getapplock`, on the precedent of `PostgresqlDocumentCacheAdministrativeMutex` and `MssqlDocumentCacheAdministrativeMutex`, so one replica purges at a time.

**Tests**

- Integration tests on both engines cover:
    - provisioning creates the contract's columns and indexes, and a second run is a no-op
    - every column round-trips
    - the purge deletes only rows past retention
    - two purgers serialize
- Units cover a full queue dropping instead of blocking with one warning per interval, rows with large bodies dropped at the byte bound before the row bound is reached, a store failure warning with metadata only and one warning per interval when it keeps failing, and an empty connection string registering nothing.
- Integration tests on both engines also cover a missing table and a table missing a contract column: the writer logs one error naming `capture provision`, stays disabled, and DMS still starts.
- Rows are located by a per-test grant id, never by insertion order.

## Dependencies

- Credential exclusion (02): the capture-row contract defines the table.

## Estimate

2 SP.
