# DMS-1521 Keyset Temp-Table Findings

Ticket: [DMS-1521](https://edfi.atlassian.net/browse/DMS-1521), "Investigate if we need/can remove
temp tables on get-many requests". Related: DMS-848, DMS-1392, DMS-1399, DMS-1269, DMS-1401,
EDFI-2956.

This report grows with the evidence. Each section names the spike task that writes it. A section
marked *Pending* has not been measured yet.

## Executive summary

Written by task 9. **Yes, the temp tables can go on PostgreSQL, and should.** On SQL Server, two of
the four paths can drop theirs now. The other two keep `#page` until a replacement is validated.

| Path | PostgreSQL | SQL Server |
|---|---|---|
| P1 resource GET-many | `inline`: no temp table | `#page` stays |
| P2 read-acceleration candidate metadata | `inline`: no temp table | `inline`: no temp table |
| P3 selected-page fallback | `inline`: no temp table | `#page` stays |
| P4 descriptor GET-many | `inline`: no temp table | `inline`: no temp table |

- **PostgreSQL: the DMS-1392 hypothesis is confirmed** (AC2). The temp table has no statistics, so
  the planner estimates every page at 2,040 rows and scans each 500,000-row child table in full.
  `inline` carries the selected ids in a transaction-local setting, or a `bigint[]` parameter for a
  selected page, and creates no catalog object. Against `main` it cuts resource GET-many API p50
  and p95 to 0.29–0.48× and child-collection time to 0.002–0.021×. It writes no WAL and no catalog
  rows (`main`: 44 catalog tuples and about 10 KB of WAL per request). No path regresses, and
  correctness validation found no behavior change. Because it issues no DDL, GET-many also works in
  a read-only transaction, which fixes the read-replica failure in EDFI-2956.
- **SQL Server: `#page` is not a plan or recompile problem.** It has statistics; its plan, compiled
  at the first page's estimate (25), is reused at 500 rows and still seeks, and neither variant
  recompiled. Its cost is tempdb metadata latch contention under concurrency: 13–17
  `PAGELATCH` waits per request at 32 sessions. `inline` removes that cost on every path. On the
  500-row hydration batches, though, it re-parses the JSON carrier in every statement and costs
  14–34% more, which breaks AC5's third priority. So `inline` is recommended where it passes every
  criterion, descriptor and candidate metadata (×1.9–×2.7 throughput at 32 sessions), and `#page`
  stays on P1 and P3 (AC6). A memory-optimized table variable passed every screening criterion
  for P1 and P3, but it needs DDL provisioning. It remains an unvalidated lead for the task 11
  review.
- **`ANALYZE` works as an interim PostgreSQL mitigation** (resource API 0.31–0.48×). It keeps the
  temp table, adds catalog writes, and does not fix EDFI-2956. It qualifies for a story only if
  story 1 cannot ship in 8.2.
- **Stories:** (1) resource multi-document hydration without a keyset temp table: PostgreSQL P1–P3
  and SQL Server P2, owning the shared keyset-contract work. (2) descriptor GET-many without
  `descriptor_page`, on both engines. No provisioning story is needed: nothing recommended adds
  DDL. Thresholds are under [Thresholds](#thresholds).

## Scope and paths

Inventory taken on `main` at `3774fc844` (2026-11-05). The plan was drafted against `59937bba5`;
nothing under `src/dms/backend` changed between the two commits.

### Multi-document paths that create a keyset temp table

| # | Path | Entry point | Temp table | Where the ids come from |
|---|---|---|---|---|
| P1 | Resource GET-many (query keyset, the default path) | `RelationalDocumentStoreRepository` -> `IDocumentHydrator` -> `ConsistentHydrationExecutor` -> `HydrationBatchBuilder` with `PageKeysetSpec.Query` (built by `RelationalQueryPageKeysetPlanner`) | `page` / `#page` | Page selection inside the batch (`WITH page_ids AS (...) INSERT ... RETURNING` / `OUTPUT INSERTED`) |
| P2 | Read-acceleration candidate metadata (only when `DocumentCache:ReadAcceleration:Enabled`, default `false`) | `RelationalDocumentStoreRepository.cs:2104` -> `HydrationBatchBuilder.BuildCandidateMetadataBatch` (`HydrationBatchBuilder.cs:149`, DDL at line 162) | `page` / `#page` | Page selection inside the batch |
| P3 | Read-acceleration fallback on cache misses (selected page) | `RelationalDocumentStoreRepository.cs:1998`, `PageKeysetSpec.SelectedPage` | `page` / `#page` with `Ordinal` | The client: PostgreSQL binds 2 parameters per id into a `VALUES` insert; SQL Server binds one JSON parameter and inserts from `OPENJSON` |
| P4 | Descriptor GET-many, and descriptor read-acceleration candidate selection (only when read acceleration is enabled) | `DescriptorReadHandler.BuildQueryCommand` (`DescriptorReadHandler.cs:2025`), added by DMS-1401 (`8a4eda2f6`, 2026-10-14). Two callers: `ReadQueryRowsAsync` (`DescriptorRowProjection.FullRow`) and `ReadQueryCandidateRowsAsync` (`DescriptorRowProjection.CandidateMetadata`, line 1729). The spike measured the GET-many caller; the candidate caller issues the same DDL and passed task 7 through `Given_*_DocumentCacheReadAcceleration_With_DescriptorRuntime` | `descriptor_page` / `#descriptor_page` | Page selection inside the batch (`INSERT INTO ... <PageDocumentIdSql>`); both version fences, the owner rows, and the `MAX(...)` boundary read it |

The shared DDL for P1-P3 is `IPlanSqlDialect.AppendCreateKeysetTempTable`:

- PostgreSQL (`PgsqlPlanDialect.cs:107`): `DROP TABLE IF EXISTS page; CREATE TEMP TABLE page
  ("DocumentId" bigint PRIMARY KEY, "Ordinal" int NULL[, anchor bigint NULL]) ON COMMIT DROP;`
- SQL Server (`MssqlPlanDialect.cs:129`): `IF OBJECT_ID('tempdb..#page') IS NOT NULL DROP TABLE
  #page; CREATE TABLE #page (...);`
- Neither analyzes the table.

P4 builds its own DDL as string literals (`DescriptorReadHandler.cs:2034-2045`):
`DROP TABLE IF EXISTS <table>; CREATE [TEMP] TABLE <table> ("DocumentId" bigint PRIMARY KEY[,
"ContentVersion" bigint NOT NULL])[ ON COMMIT DROP];`. A zero limit skips the insert (SQL Server
rejects `FETCH NEXT 0`).

### The keyset join seam

P1-P3 render every keyset reference through `PlanSqlWriterExtensions.AppendRelation(keyset.Table)`:

- `ReadPlanCompiler.cs:446`, the compiled `SelectByKeysetSql` join (alias `k`), which also appears
  in the mapping-set manifest goldens
- `ProjectionSourceFilterSql.cs:38`, descriptor projections and the document-reference lookup
- `IPlanSqlDialect.cs:248`, `DocumentMetadataColumns.AppendDocumentMetadataSelectBody`
- `HydrationBatchBuilder.AppendDocumentVersions` (`HydrationBatchBuilder.cs:454`, join at 477), plus
  the materialization and candidate-metadata statements in the same file (lines 581-759)

**P4 does not use the seam.** The descriptor batch quotes `descriptor_page` itself and selects from
it with `SELECT * FROM <table>`. A replacement for P4 therefore cannot be delivered by changing
`AppendRelation` alone; it needs its own change in `DescriptorReadHandler`.

### Single-document fast path (C5): confirmed

Both dialects report `SupportsSingleDocumentHydration => true` (`PgsqlPlanDialect.cs:26`,
`MssqlPlanDialect.cs:26`). `HydrationBatchBuilder.ShouldUseSingleDocumentBatch` (line 233) takes
the fast path only for `PageKeysetSpec.Single` with `UseSingleDocumentFastPath`. Every production
`PageKeysetSpec.Single` caller passes `UseSingleDocumentFastPath: true`:

| Caller | Site | Options |
|---|---|---|
| GET by id | `RelationalDocumentStoreRepository.cs:4364` | `CreateGetHydrationExecutionOptions` (line 4442), `UseSingleDocumentFastPath: true` |
| GET by id cache candidate | `RelationalDocumentStoreRepository.cs:4678` | same helper |
| Write current state | `RelationalWriteCurrentState.cs:102` | `UseSingleDocumentFastPath: true` (line 106) |
| Write first phase, captured target | `RelationalWriteFirstPhase.cs:719` | `BuildHydrationOptions` (line 1105), `UseSingleDocumentFastPath: true` |
| Write first phase, co-batched composite | `RelationalWriteFirstPhase.cs:1068` -> `HydrationBatchBuilder.BuildGuardedSingleDocumentBatch` (line 201) | same `BuildHydrationOptions` |
| Cache materialization | `DocumentCacheMaterializer.cs:113` | `UseSingleDocumentFastPath: true` (line 114) |

No other production code constructs `PageKeysetSpec.Single`. The fast path is out of scope for
replacement and must stay unchanged.

### Design drift: `descriptor_page`

`partitioned-cursor-paging.md` (line 711) says: "Descriptor page selection retrieves its rows in the
same statement that selects them, so it needs no keyset temp table". Since DMS-1401 (`8a4eda2f6`)
that is no longer true: P4 materializes `descriptor_page` so that both version fences and the owner
rows read one selection per attempt. The descriptor story reconciles this text, together with
`transactions-and-concurrency.md`. This spike does not edit either document.

### Read replicas (EDFI-2956)

[EDFI-2956](https://edfi.atlassian.net/browse/EDFI-2956) (2026-11-10) reports that GET-many fails
on an Aurora PostgreSQL reader instance. DMS 8.1 routes the GET to the reader, which enforces
`default_transaction_read_only`. The keyset prelude's `DROP TABLE IF EXISTS` then fails with
`25006: cannot execute DROP TABLE in a read-only transaction`, and the API returns 500.
EDFI-2956 was cloned on 2026-11-11 to [DMS-1687](https://edfi.atlassian.net/browse/DMS-1687), which tracks the fix in DMS.

Every PostgreSQL keyset path (P1-P4) issues this DDL, so a read-only-safe keyset is a requirement
for the recommendation, not just a side effect:

- The ANALYZE-only mitigation keeps the temp table and adds another write, so it does not fix
  EDFI-2956.
- Replacements that create no catalog object (`VALUES`, `unnest`, CTE or derived table) do. P4
  needs its own change (see "The keyset join seam").
- SQL Server readable secondaries permit `#` temp tables in tempdb, so the replica failure is a
  PostgreSQL issue. The recommendation should still state the read-only behavior per dialect.

### Test doubles that split or parse keyset batches

Found while mapping C1-C6. These are task 4 inputs (failure type (d)) and story scope:

- `AuthoritativeTimestampReadConsistencyScenario` splits keyset batches at **two** sites, not one:
  - the resource hydration batch (lines 446-490), split before the leading version or metadata
    `SELECT`, with a SQL Server special case that runs the `CREATE TABLE` prelude in session scope
    so `#page` survives both commands;
  - the descriptor batch (lines 644-690), split at the `-- Descriptor acquisition:` markers, with
    the same `CREATE TABLE` special case.

  A batch-scoped SQL Server variable survives neither split, so the task 4 adaptation covers both.
- `DescriptorReadCommandExecutor` (`Backend.Tests.Unit`) recognizes the descriptor batch by its
  `-- Descriptor acquisition:` and `-- Descriptor page selection boundary` markers and computes the
  boundary from the command text. A changed descriptor batch must keep these markers or the
  executor must change with it.

## Prior evidence

Downloaded from Jira through REST v3 on 2026-11-06: DMS-1392's `dms-1392-final-report.md`,
`dms-1392-pg-statement-cost.csv`, and `dms-1392-pg-plan-nodes.csv`, and DMS-1269's
`performance test results.zip`. These are **cross-machine context only**. No ratio in this spike
compares against them.

### DMS-1392 (PostgreSQL 18, `primary-500k`, 2026-09-02)

Plans were captured by replaying the batch under `EXPLAIN (ANALYZE, BUFFERS)`, not from the driver
path. The resource batch has 11 statements; statements 6-9 are the four Student child collections.

Offset-zero and first-cursor cells (one plan per cell):

| Cell | Rows | `page` estimate / actual | Child statements 6-9: execution | Child buffers (hit + read) | Batch execution | Batch planning |
|---|---|---|---|---|---|---|
| `traditional-offset-zero` | 25 | 2,040 / 25 | 217.6 ms | 54,537 | 220.8 ms | 9.40 ms |
| `traditional-offset-zero` | 500 | 2,040 / 500 | 219.3 ms | 54,534 | 230.5 ms | 2.71 ms |
| `cursor-unfiltered-first` | 25 | 2,040 / 25 | 210.7 ms | 54,534 | 212.8 ms | 2.43 ms |
| `cursor-unfiltered-first` | 500 | 2,040 / 500 | 217.3 ms | 54,534 | 226.6 ms | 2.90 ms |

- **Keyset estimate.** Every `page` scan feeding statements 4-9 is estimated at 2,040 rows (168
  nodes at 25 rows, 168 at 500), whatever the actual 25 or 500.
- **Node types.** All 96 child-table scan nodes across all cells are `Seq Scan` of the whole child
  table (500,000 rows), hash-joined to a `Seq Scan` of `page`. Example, statement 6 at 25 rows:
  `Sort (est 2,040, act 25, 51.2 ms) <- Hash Join <- Seq Scan StudentIdentificationDocument (est
  500,000, act 500,000, 25.6 ms) + Hash <- Seq Scan page (est 2,040, act 25)`.
- **Cost shape.** Child-collection cost is flat across page sizes (about 210-220 ms and a constant
  54,534 buffers per batch at both 25 and 500), which is what full scans predict. Each child
  statement costs about 50-60 ms and 13,000-14,000 buffers.
- **Statements that do probe.** Statements 4 (`dms.Document`) and 5 (`edfi.Student`) join `page` by
  nested loop with index scans despite the same 2,040 estimate: their parent tables are joined on
  their primary keys, where 2,040 probes still beat a scan.
- **Planning against execution.** Batch planning is 2-9 ms against 213-231 ms of execution: the
  hydration cost is execution, not planning, in this replay. A replay plans each statement once, so
  it cannot show re-planning per request; that is measured in tasks 3 and 5.
- **API latency** (p50, same run): PostgreSQL 118.6 ms at 25 rows and 149.2 ms at 500
  (`cursor-unfiltered-first`); SQL Server 7.1 ms and 34.7 ms.
- **Descriptors.** DMS-1392's descriptor run predates DMS-1401 (2026-10-14), so it was a single
  statement with no `descriptor_page`. It carries no evidence about P4.

### DMS-1269 (PostgreSQL 18, perf rig, Deep Paging suite, 2026-07-30)

`pg_stat_statements` from the before and after runs, with `track_planning=on`:

- **Re-planning.** Every frequently called statement that joins the keyset (56 statements with more
  than 50 calls) and every page-selection statement shows `plans` equal to `calls` (within one).
  Each execution was planned. This is consistent with temp-table churn defeating plan reuse, but
  the run did not record whether statements were server-prepared, so it is not proof.
- **Where the buffers went.** Page selection (`WITH page_ids AS (...) INSERT INTO page`, which
  carries the authorization subqueries) was 96.1% (before) and 96.9% (after) of all statement
  buffers. Statements joining the keyset were 3.8% and 3.1%; those joining `dms.Document` were 2.2%
  and 1.6%.
- **Temp-table DDL is absent** from `pg_stat_statements` in both runs, although `track=all`. Task 2
  must confirm `pg_stat_statements.track_utility` before using statement stats for DDL; the WAL and
  catalog counters do not depend on it.
- **Cluster WAL** for each window was about 41.5 MB (`pg_stat_wal`); the run did not isolate the
  keyset's share.

**Discrepancy.** DMS-1392 comments 98803 and 98824 say that "DMS-1269 measured the document join as
99.8% of PostgreSQL buffer traffic on a similar path". Neither the DMS-1269 attachment nor its
comments contain that figure; in the attached statistics the document join is about 2% and page
selection about 96%. The DMS-1269 measurements that do exist (comment 98040) concern the
referenced-document UUID lookup: 43.2 -> 21.5 ms per 500-row StudentSectionAssociation page. This
spike therefore does not rely on the 99.8% figure. The DMS-1392 plan evidence above is sufficient
to state the hypothesis.

### What the prior evidence does and does not establish

- **Established:** the `page` keyset is estimated at 2,040 rows regardless of its size, and the four
  child collections hash-join a full scan of each child table to it, costing about 210 ms of a
  213 ms batch at 25 rows.
- **Not established:** that the missing statistics cause the scan choice (task 3, AC2); the WAL and
  catalog cost of the temp-table lifecycle (AC3); per-request re-planning on the driver path; SQL
  Server compiles and recompiles per request; anything about P2, P3, or P4.

## Method and environment

Written by task 2. Every experiment runs through the spike runner in
[`experiments/KeysetSpike`](experiments/KeysetSpike) and the run script
[`experiments/Invoke-KeysetSpike.ps1`](experiments/Invoke-KeysetSpike.ps1). Raw evidence goes to
`C:\perf\dms-1521\<run id>\`, outside the repository, and is attached to DMS-1521 by task 12.

### Runner

`KeysetSpike.csproj` is a test project outside the solution (`ManagePackageVersionsCentrally=false`;
NUnit 4.2.2, NUnit3TestAdapter 4.6.0, Microsoft.NET.Test.Sdk 17.11.1, FluentAssertions 6.12.1) with a
`ProjectReference` to the performance harness. It reuses the harness fixtures, loaders,
`PerfScenarioExecutor`, `PerfLatencyMeasurement`, `PerfEnvironmentCapture`, and its CI and dirty-path
guards. It does not use `PerfBaselineRunPipeline`, `PgsqlPlanCapture`, `MssqlPlanCapture`, or
`DriverCommandObserver`: the first two reject string literals, comments, and `ANALYZE`, and the last
two drop the parameter types and sizes that production binds.

| Fixture class (per engine) | Lease | What it measures |
|---|---|---|
| `Given_<Engine>_KeysetSpikeResourceRun` | `primary-500k` | Observer calibration on the empty lease; then the six-cell matrix through `PerfScenarioExecutor.RunAsync` (one command per request, asserted by the harness) |
| `Given_<Engine>_KeysetSpikeDescriptorRun` | `descriptors-25k` | Descriptor GET-many at limits 25 and 500, offset 0; per request exactly one descriptor acquisition command and no hydration |
| `Given_<Engine>_KeysetSpikeReadAccelerationRun` | `primary-500k`, read acceleration on | A cache-hit cell (no hydration; one candidate batch) and a forced-miss cell (cache rows and projection work cleared outside the timed window; exactly one selected-page hydration of the whole page), at 25 and 500 |

The SQL Server classes set `MatchProductionWriteIsolation => true` (RCSI and snapshot isolation).
Each cell records its commands per request and the command mix of its last request, and fails if
any request leaves its intended state.

Each run writes `run-manifest.json` (variant label, keyset variant, subject commit, dirty paths,
fixture load time), `cells.json` (API latency samples, commands per request, batch SHA-256),
`batches/<kind>.<size>.sql` with `.params.json` (full binding specifications), `plans/`,
`replays.json` (replay results with their counter windows), `parity.json`, `environment.json`, and,
for the first resource run of a session, `calibration.json`.

### Binding specifications (`SpikeCommandCapture`)

Every parameter is captured with name, value, `DbType`, provider type (`SqlDbType`, or `NpgsqlDbType`
and `DataTypeName`), size, precision, scale, direction, and TVP type name. Plan capture and replay bind
every field from it and never infer a type.

- **SQL Server:** read from the `SqlCommand` in SqlClient's `WriteCommandBefore` diagnostic event,
  before execution.
- **PostgreSQL:** Npgsql 8.0.4's activities carry only the statement text, so the text and interval
  come from the `Npgsql` ActivitySource, and the parameters come from the production binding code
  itself. The harness recorders keep each request's `PageKeysetSpec` and `RelationalCommand`; the
  capture binds them onto a fresh `NpgsqlCommand` through the public
  `HydrationBatchBuilder.AddParameters` (hydration batches) and through the same three steps
  `PostgresqlRelationalCommandExecutor.AddParameters` takes (descriptor and candidate batches). The
  parity check below proves the result.

**Parity check** (once per engine and batch kind, in every run; a mismatch fails the run):

- SQL Server: after one replay, the plan cache must hold a single `Prepared` entry for the batch text
  whose `sp_executesql` declaration is the production one, with its use count advanced. A different
  declaration would create a second entry.
- PostgreSQL: the batch runs on one physical connection until Npgsql auto-prepares it, and
  `pg_prepared_statements.parameter_types` on that connection is compared, placeholder by placeholder,
  with the type each production-bound parameter declares.

| Engine | Batch kind | Result |
|---|---|---|
| PostgreSQL | resource | identical: `limit bigint`, `offset bigint` |
| PostgreSQL | descriptor | identical: `resourceKeyId smallint`, `limit bigint`, `offset bigint` |
| PostgreSQL | candidate | identical: `limit bigint`, `offset bigint` |
| PostgreSQL | fallback (selected page) | identical: 25 × (`selectedDocumentId_n bigint`, `selectedOrdinal_n integer`) |
| SQL Server | resource | identical: `(@offset bigint,@limit bigint)`, use count 31 → 32 |
| SQL Server | descriptor | identical: `(@resourceKeyId smallint,@offset bigint,@limit bigint)`, 11 → 12 |
| SQL Server | candidate | identical: `(@offset bigint,@limit bigint)` |
| SQL Server | fallback (selected page) | identical: `(@selectedDocumentIdsJson nvarchar(max) )` |

The fallback row is the case review round 4 raised: the replayed JSON parameter is `nvarchar(max)`,
as production binds it.

### Plans

- **PostgreSQL** (`SpikePgsqlPlanCapture`): the batch is split with awareness of single-quoted
  literals (including `''`), double-quoted identifiers, and `--` and `/* */` comments; dollar quoting
  and unclassifiable statements are refused. The statements replay in one transaction, which is rolled
  back. `SELECT`, `WITH`, and `INSERT` run under `EXPLAIN (ANALYZE, BUFFERS, WAL, SETTINGS, FORMAT
  JSON)`; `CREATE`, `DROP`, and `ANALYZE` execute timed and are kept as statement rows. Each statement
  binds only the parameters it references.
- **SQL Server** (`SpikeMssqlPlanCapture`): `SET STATISTICS XML, IO, TIME ON` over the whole batch on
  one connection, as the harness capture does, but binding from the specifications.
- **Driver-path plans** come from `auto_explain` in separate instrumented windows (tasks 3 and 5),
  never from latency samples.

### Replay (`KeysetReplay`)

Each captured batch replays at concurrency 1, 8, and 32 through production-equivalent drivers: an
`NpgsqlDataSource` with `NoResetOnClose`, `AutoPrepareMinUsages=3`, and `MaxAutoPrepare=256`, as
`NpgsqlDataSourceLifetime` builds it; SqlClient with the leased connection string unchanged, as
`MssqlConnectionAcquisition` uses it for a primary target. Each execution opens and closes a pooled
connection and reads every result set. Workers run their warmup first; throughput is the measured
executions over the measured phase's wall time. p50, p95, p99, throughput, and errors are recorded.

### Counters (`ServerCounters`)

Protocol: the workload finishes; after at least 10 s of idle, readings are taken on a fresh,
unpooled connection outside a transaction (PostgreSQL after `pg_stat_clear_snapshot()`) until two
readings at least 1 s apart agree on the gate counters; an idle window of the same length is measured
the same way and subtracted; the net is divided by the executions in the window. A window wraps only
a replay's measured phase: each worker's warmup runs before the window opens, so compiles and
recompiles recorded in it happened after warmup.
The readers run outside the measured database (`postgres` for `pg_stat_statements`, `master` on SQL
Server) or are marked and excluded, so they never count themselves.

- **PostgreSQL:** `pg_stat_wal` and the WAL insert LSN; `n_tup_ins` and `n_tup_del` of `pg_class`,
  `pg_attribute`, `pg_type`, `pg_depend`, `pg_index`, `pg_constraint`, and `pg_statistic` in the
  measured database (`n_dead_tup` informational only); `pg_stat_statements` calls, plans, and plan
  time for the measured database.
- **SQL Server:** `Temp Tables Creation Rate`, `SQL Compilations/sec`, `SQL Re-Compilations/sec`, and
  `Batch Requests/sec` (cumulative); `PAGELATCH_*` waits; `sys.dm_exec_query_stats` for the measured
  database; tempdb reserved pages (current, informational); and an Extended Events histogram of
  `sql_statement_recompile` by `recompile_cause`, filtered to the measured database.
  - **Gate counter (from task 6).** `Temp Tables Creation Rate` is server-wide, and task 4 saw it
    pick up background temp tables. The SQL Server gate is therefore an Extended Events
    `event_counter` on `sqlserver.object_created`, filtered to user tables (`object_type = 8277`)
    in tempdb at DDL commit (`ddl_phase = 1`) and to sessions whose current database is the
    measured one (`sqlserver.database_id`). A probe confirmed it counts `#temp` tables, and not
    table variables or other databases' sessions. The server counter is still recorded as
    information.

By default, counters wrap the concurrency-1 replay on PostgreSQL (WAL and catalog work per request)
and the 1 and 32 replays on SQL Server (where `PAGELATCH` waits under concurrency matter); the run
script's `-CounterConcurrency` changes this.

### Observer calibration

Run on each engine before any zero is used. N keyset-shaped temp-table cycles (`DROP IF EXISTS`,
`CREATE` with a primary key, `INSERT`, `SELECT`, one parameterized command each) must register in
proportion to N, and plain parameterized `SELECT`s must register exactly zero.

| Engine | Gate counters | 10 cycles | 20 cycles | 20 plain SELECTs | Result |
|---|---|---|---|---|---|
| PostgreSQL | sum of catalog `n_tup_ins` + `n_tup_del` | 440 | 880 | 0 | calibrated (ratio 2.00) |
| SQL Server | `Temp Tables Creation Rate` (tasks 2–5) | 10 | 20 | 0 | calibrated (ratio 2.00) |
| SQL Server | XE `object_created` count (task 6 on) | 10 | 20 | 0 | calibrated (ratio 2.00), in `t6smoke` and all three task 6 sessions |

Per PostgreSQL temp-table cycle the catalog churn is exactly: `pg_class` 2 inserts and 2 deletes
(table and primary-key index), `pg_attribute` 9 and 9, `pg_type` 2 and 2, `pg_depend` 6 and 6,
`pg_index` 1 and 1, `pg_constraint` 2 and 2, `pg_statistic` 0. Runs: `smoke1-postgresql-resource-B01-20261106172231`
and `smoke1-mssql-resource-B01-20261106174306`.

### Environment

| Item | Value |
|---|---|
| Host | Windows 11 (10.0.26300), Intel Family 6 Model 141, 16 logical cores, 31.7 GB; Docker Desktop with 16 GB |
| .NET | 10.0.14, workstation GC |
| PostgreSQL | 18.6 (`postgres:18.6-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873`), container `dms-1521-postgresql`, volume `dms-1521-pgdata`, `127.0.0.1:15521` |
| PostgreSQL settings | `max_locks_per_transaction=256`; `shared_preload_libraries=pg_stat_statements,auto_explain`; `pg_stat_statements.track_planning=on`, `track_utility=on`; `track_io_timing=on`; `auto_explain.log_format=json`, `log_analyze`, `log_buffers`, `log_timing` on; `log_min_duration` left at its default `-1` (off) and set to 0 with `ALTER SYSTEM` only inside a capture window; defaults otherwise (`plan_cache_mode=auto`, `jit=on`, `shared_buffers=128MB`, `work_mem=4MB`) |
| SQL Server | 2025 RTM-CU7 (17.0.4065.4) Developer, (`mcr.microsoft.com/mssql/server:2025-latest@sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a`), container `dms-1521-mssql`, volume `dms-1521-mssqldata`, `127.0.0.1:14351`; MAXDOP 0, cost threshold 5 |
| Drivers | Npgsql 8.0.4; Microsoft.Data.SqlClient 6.1.4 |

Storage is a Docker volume, never tmpfs. Ports 15521 and 14351 avoid Windows' reserved ranges
(55140–55339 refused the first choice) and the native PostgreSQL service on 5432.

### Deviations from the plan, and observations from the smoke runs

1. **Descriptor principal.** The final-gate descriptor run uses a namespace-based claim set built by
   `PeopleRelationshipGetManyScenarioHelpers`, which is internal to the integration assembly. The
   spike's descriptor run uses the harness default (authorization bypassed), like the resource run.
   The temp-table lifecycle does not depend on the authorization predicate; the selection SQL is the
   unfiltered form.
2. **Read-acceleration warm-up.** A query miss direct-fills documents one at a time within
   `DirectFillTimeout`, so a page warms only partly per request. The runner warms with one GET by id
   per missing document, re-planting projection work first, and fails unless the whole page is then
   cached.
3. **`DirectFillTimeout` stays 5 s**, as in `Given_*_DocumentCacheReadAcceleration`. At the production
   default (250 ms), SQL Server direct-filled **no** document on this machine (0 of 25, even one GET by
   id at a time); a single-document direct fill takes about 0.5 s there. This is outside DMS-1521's
   scope and is reported here only as an observation.
4. **The forced-miss cell includes a direct-fill attempt.** Every query miss in `Tracking` state
   (missing, stale, or drifted row) makes the coordinator try to direct-fill the missed documents
   after the fallback hydration; without projection work the writer skips each write after
   materializing the document. This is production behavior, and it dominates the cell's API latency:
   - PostgreSQL: about 7 single-document commands per missed document (178 commands per request at 25
     rows, 3,503 at 500; smoke p50 225 ms and 2.8 s);
   - SQL Server: stops at the 5 s timeout after about 10 documents (77 commands; smoke p50 5.3 s at
     both sizes).

   The fallback batch's own plans and replays isolate the keyset effect; the cell's API latency does
   not. See the gate decision below.
5. **Counter levels.** Counters wrap a subset of the replay concurrency levels (above) to keep runs
   inside the budget; every level is still replayed for latency, throughput, and errors.
6. **`pg_stat_statements` now records the temp-table DDL** (`track_utility=on`): the smoke shows
   `DROP TABLE IF EXISTS "page"` and `CREATE TEMP TABLE "page"` as separate statements with 0 plans.
   This resolves the DMS-1269 gap noted under [Prior evidence](#prior-evidence).

**Gate decision (task 2 review, accepted).** Because direct fill dominates the forced-miss cell
(item 4), its API latency cannot show a keyset improvement of tens of milliseconds. Gate 2 therefore
judges the forced-miss path on the fallback batch's replay latency and child-collection execution
time and buffers (improvement), and keeps the cell's complete API p50 and p95 as non-regression
only. The plan's tasks 8 and 9 and resolved decision 10 were updated to match.

**Review fixes to the runner** (task 2 review, before any evidence run):

- **Resource batch bindings.** All six resource cells compile the same batch text, and the first
  smoke runs matched captures by text alone, so every resource plan and replay bound the first cell's
  `offset=0, limit=25`, including those labeled 500 rows or deep offset. The runner now selects each
  cell's capture by its bound `offset` and `limit`, records both in `cells.json`, and refuses any
  batch whose binding (`limit`, selected-id count, or JSON id count) does not select its labeled page
  size. The resource plans and replays at 500 rows and at shallow and deep offsets in
  `smoke1-*-resource-*` are therefore invalid; the API samples in their `cells.json` were unaffected.
- **Warmup outside counter windows** (see [Counters](#counters-servercounters)). The first smoke
  windows included each replay's warmup.

Smoke observations under `B0` (sanity only, `smoke-10k`; not evidence): every keyset batch on both
engines creates one temp table (PostgreSQL: `pg_class` +2/−2; SQL Server: `Temp Tables Creation
Rate` 1). A request runs one keyset batch on the resource, descriptor, and cache-hit paths, and two
on the forced-miss path (candidate selection, then the selected-page fallback). PostgreSQL plans
about 10 of the resource batch's 11–12 statement calls per request even through the auto-preparing
replay pool, and the 2,040-row `page` estimate reproduces locally.

### Load times and recalibrated budgets

| Engine | `smoke-10k` load | `descriptors-smoke-2k` load | Smoke wall time: resource (with calibration) / descriptor / read acceleration |
|---|---|---|---|
| PostgreSQL | 4.8 s | 0.6 s | 5.5 / 3.1 / 4.1 min |
| SQL Server | 11.6 s | 0.5 s | 5.7 / 3.2 / 13.8 min |

Calibration takes about 2.3 min per engine; a counter window takes about 24 s of workload and idle
plus four settle reads of at least 11 s each. Linear extrapolation of the load to `primary-500k`
gives about 4 min (PostgreSQL) and 10 min (SQL Server) per lease; task 3's first full run measures it.

Estimated full-size run (three fixtures, 10 + 60 iterations, replays at 1, 8, and 32 with 3 + 20
executions per worker):

| Engine | Per run | Six runs (tasks 5 and 6) | Plan budget | Main costs |
|---|---|---|---|---|
| PostgreSQL | about 40 min | about 4 h | 2.5–3 h | two 500k loads; forced-miss direct fill at 500 rows (about 2.8 s × 70 requests) |
| SQL Server | about 65 min | about 6.5 h | 3–3.5 h | two 500k loads; forced-miss direct fill (about 5.3 s × 140 requests ≈ 12 min); counter windows at 1 and 32 |

Both exceed the plan. Re-estimated after the review fixes (the corrected resource smoke,
`smoke6-*-resource-*`, binds each cell's own offset and limit; its 500-row replays now cost more
than its 25-row ones, as they should), the per-run estimates stand. Tasks 5 and 6 therefore run
as three `-Sequence B,I` sessions per engine from the user's terminal, keeping the planned
repetitions and iteration counts: about 80 min per PostgreSQL session and about 130 min per
SQL Server session. Task 3's first full run measures the real `primary-500k` load time, and these
estimates are revised from it before task 5 starts.

**Measured in task 3 (PostgreSQL, full size).** `primary-500k` loads in 240–246 s, and
`descriptors-25k` in 7 s. A full run, including the `auto_explain` windows, took 23.3 min (`A`:
resource 8.6, descriptor 2.1, read acceleration 12.6 min) and 25.7 min (`B0`: 10.1, 2.2, and
13.5 min), not the 40 min estimated. A PostgreSQL `B, I` session is therefore about 50 min, and
task 5's three sessions about 2.5 h, inside the plan's 2.5–3 h budget.

**Measured in task 6 (SQL Server, full size).** A `B` run takes 55–64 min (resource 15–17,
descriptor 5, read acceleration 34–43 min) and an `I` run 52–55 min, so a `B, I` session is about
1 h 50 min and task 6's three sessions about 5.5 h, over the plan's 3–3.5 h budget but under the
6.5 h estimated above.

## Baseline

PostgreSQL: the repeated baseline (`B`, three sessions, with run-to-run CV) is reported against
`inline` under [PostgreSQL results](#postgresql-results). Task 3's single `B0` run (pristine
`main`) is reported with the `A` comparison under [Cardinality hypothesis](#cardinality-hypothesis).
SQL Server: the repeated baseline (`B`, three sessions) is reported against `inline` under
[SQL Server results](#sql-server-results).

## Cardinality hypothesis

Written by task 3, the diagnostic the ticket asks for first. **Both verdicts are favorable:** the
hypothesis is **confirmed** at 25 and 500 rows, and `ANALYZE` is a **worthwhile interim
mitigation** on PostgreSQL. Neither verdict changes the temp-table objectives of AC3 and AC5:
`ANALYZE` keeps the per-request temp-table lifecycle and adds to its catalog work.

### Runs

| Variant | What runs | Run ids (`C:\perf\dms-1521\`) |
|---|---|---|
| `B0` | Pristine `main` at `3621b1eb6`, no patch (`dirtyPaths` holds only the spike folder and plan notes) | `t3-postgresql-resource-B01-20261106201757`, `t3-postgresql-descriptor-B01-20261106202804`, `t3-postgresql-read-acceleration-B01-20261106203012` |
| `A` | `prototype.patch` applied, `DMS1521_KEYSET_VARIANT=pg-analyze` | `t3-postgresql-resource-A1-20261106194105`, `t3-postgresql-descriptor-A1-20261106194943`, `t3-postgresql-read-acceleration-A1-20261106195147` |

Full size (`primary-500k`, `descriptors-25k`), 10 warmups and 60 measured API iterations per cell,
replays at concurrency 1, 8, and 32 (3 + 20 executions per worker), counters on the concurrency-1
replays, and the observer calibrated in both runs (440 and 880 catalog tuples for 10 and 20 cycles,
plain `SELECT`s 0). One run per variant: these ratios carry no run-to-run CV yet, so they are read
against the 5% floor of `m`, and tasks 5 and 6 supply the repeated measurements.

**The `pg-analyze` variant** (`experiments/prototype.patch`) adds one statement after keyset
materialization, on PostgreSQL only: `ANALYZE "page";` in the query, selected-page, and
candidate-metadata batches, and `ANALYZE "descriptor_page";` in the descriptor batch. Every other
byte of the batches is unchanged.

**Driver-path plans** come from `auto_explain` windows of 20 requests per page size, separate from
the latency samples (`autoexplain/<label>.plans.json` in each run). The resource, descriptor, and
cache-hit (candidate) windows issue API GETs. The forced-miss window instead replays the captured
fallback batch 20 times through a fresh production-equivalent Npgsql pool, because each forced-miss
request also direct-fills every missed document (about 7 commands each) and would log thousands of
plans per request. `auto_explain` instruments every node with timing, so the absolute times in the
windows are inflated; only the B0-against-A comparison within the windows is used.

### Hypothesis verdict: confirmed

**Estimates.** Under `B0` the planner estimates the keyset at a fixed **2,040 rows** (`page`) and
**2,260 rows** (`descriptor_page`) whatever the page holds. Under `A` the estimate equals the actual
count in every one of the 20 driver-path requests at both sizes, on every path (25 and 500). The
statements are therefore planned after the statistics exist: Npgsql pipelines the batch's
statements in order, and each later statement is bound, and planned, after `ANALYZE` has executed.

**Child-row loading** (the four child-collection statements, summed per request; driver path, 20
requests per cell; median with min–max):

| Path | Rows | B0 child time (ms) | A child time (ms) | B0 child buffers | A child buffers | Child node types, B0 → A (20 of 20 requests) |
|---|---|---|---|---|---|---|
| Resource GET-many | 25 | 174 (170–181) | 0.24 (0.20–0.32) | 17,166 | 16 | `Seq Scan` → `Index Scan` on all four |
| Resource GET-many | 500 | 183 (179–189) | 1.99 (1.89–2.48) | 17,166 | 36 | `Seq Scan` → `Index Scan` on all four |
| Forced-miss fallback | 25 | 167 (161–177) | 0.20 (0.18–0.25) | 17,166 | 16 | `Seq Scan` → `Index Scan` on all four |
| Forced-miss fallback | 500 | 181 (175–198) | 1.85 (1.77–2.67) | 17,166 | 36 | `Seq Scan` → `Index Scan` on all four |

The drop is far beyond `B0`'s spread in every cell: `B0`'s range is 10–23 ms, and the drop is
165–181 ms. `B0`'s buffer counts do not vary at all. The spike plans (one `EXPLAIN (ANALYZE,
BUFFERS)` replay of each captured batch, `ANALYZE`'s own time excluded) agree: child execution
180 → 0.26 ms and 187 → 0.90 ms (resource, 25 and 500), 202 → 0.24 ms and 195 → 1.02 ms (fallback),
with child buffers 17,169 → 16 and 36. No `enable_seqscan = off` replay was needed.

Plan excerpt, the `StudentVisa` child statement at 500 rows (driver path, request 6 of 20):

```text
B0 (41.3 ms)                                          A (0.42 ms)
Sort                    est 2040, actual 500           Merge Join               est 500, actual 500
  Hash Join             est 2040, actual 500             Index Scan StudentVisa  actual 501, buffers 8
    Seq Scan StudentVisa  actual 500000, buffers 3677    Index Only Scan page    est 500, actual 500
    Hash
      Seq Scan page     est 2040, actual 500
```

At 2,040 estimated keys the planner prices a hash join over a full scan of each 500,000-row child
table as cheaper than 2,040 index probes. With the real count it merges the page against the
child's `Student_DocumentId` index.

The misestimate is not confined to child collections. The descriptor-projection statement (a
`UNION` over every descriptor column joined to the page) drops from 11,028 to 536 buffers at 500
rows, and the whole hydration batch on the driver path drops from 175 to 1.25 ms (25) and from 193
to 8.8 ms (500). The descriptor batch, which loads no child rows, drops from 10.3 to 0.35 ms (25)
and from 13.0 to 2.3 ms (500); the candidate-metadata batch is unchanged (0.97 → 0.92 ms and
7.7 → 6.9 ms).

### Mitigation verdict: worthwhile as an interim mitigation

**End-to-end API latency, `ANALYZE` included** (ratio `A / B0`; the cost-metric non-regression
bound is `<= 1 + m`, with `m` at its 5% floor until tasks 5 and 6 give a CV):

| Cell | B0 p50 / p95 (ms) | A p50 / p95 (ms) | Ratio p50 / p95 |
|---|---|---|---|
| Resource offset 0, 25 | 127.6 / 138.7 | 43.4 / 48.7 | 0.34 / 0.35 |
| Resource offset 0, 500 | 152.3 / 158.8 | 57.8 / 64.9 | 0.38 / 0.41 |
| Resource shallow, 25 | 128.4 / 136.5 | 41.0 / 43.0 | 0.32 / 0.31 |
| Resource shallow, 500 | 154.3 / 163.7 | 56.5 / 67.7 | 0.37 / 0.41 |
| Resource deep, 25 | 150.0 / 158.7 | 60.8 / 64.4 | 0.41 / 0.41 |
| Resource deep, 500 | 176.7 / 190.2 | 82.6 / 91.9 | 0.47 / 0.48 |
| Descriptor, 25 | 41.2 / 45.4 | 36.4 / 39.7 | 0.88 / 0.87 |
| Descriptor, 500 | 42.4 / 46.2 | 35.6 / 38.6 | 0.84 / 0.84 |
| Cache hit, 25 | 35.7 / 39.2 | 36.1 / 39.5 | 1.01 / 1.01 |
| Cache hit, 500 | 47.6 / 83.4 | 46.7 / 60.1 | 0.98 / 0.72 |
| Forced miss, 25 | 288.4 / 301.0 | 241.6 / 255.4 | 0.84 / 0.85 |
| Forced miss, 500 | 3,691 / 3,837 | 3,512 / 3,587 | 0.95 / 0.94 |

**Replays under concurrency** (throughput per second, `A / B0`; no errors in any replay of either
variant):

| Batch | Rows | c = 8 | c = 32 | p95 at c = 32 (ms), B0 → A |
|---|---|---|---|---|
| Resource | 25 | 54.9 → 595.1 (×10.8) | 78.8 → 832.3 (×10.6) | 481 → 54 |
| Resource | 500 | 44.4 → 297.0 (×6.7) | 58.2 → 394.1 (×6.8) | 746 → 102 |
| Fallback | 25 | 55.1 → 572.0 (×10.4) | 79.7 → 880.0 (×11.0) | 483 → 55 |
| Fallback | 500 | 44.4 → 273.8 (×6.2) | 62.8 → 389.5 (×6.2) | 612 → 111 |
| Descriptor | 25 | 670 → 1,354 (×2.0) | 965 → 1,461 (×1.51) | 45 → 34 |
| Descriptor | 500 | 497 → 755 (×1.52) | 642 → 894 (×1.39) | 73 → 54 |
| Candidate | 25 | 1,187 → 992 (**×0.84**) | 1,980 → 1,619 (**×0.82**) | 22.8 → 28.0 (**×1.23**) |
| Candidate | 500 | 798 → 844 (×1.06) | 1,090 → 1,142 (×1.05) | 43.8 → 41.8 |

**What `ANALYZE` costs:**

- **Time.** 1.0–1.5 ms per keyset batch in the spike plans, which is 6–16% of the replayed batch
  (resource 8% and 6% at 25 and 500; descriptor 12% and 11%; candidate 15% and 16%; fallback 8%
  and 8%).
- **Catalog work.** `pg_statistic` +2 inserts and +2 deletes per request on the resource,
  candidate, and fallback batches (one row each for `DocumentId` and `Ordinal`) and +1/+1 on the
  descriptor batch (`DocumentId` only). The calibrated catalog total goes from
  44 to 48 tuples per request (42 to 44 for the descriptor batch), on top of the temp-table
  lifecycle, which is unchanged.
- **WAL.** In counter windows whose idle window was clean (idle WAL at most 190 bytes), `B0` writes
  10.1–10.6 KB per request and `A` 11.2–12.6 KB, so `ANALYZE` adds roughly 1–2 KB (10–20%). Six of
  the sixteen windows are excluded: background WAL of 0.4–4.3 MB landed in their idle or workload
  window (for example `A` fallback 25 at 55.8 KB per request), which swamps a per-request figure of
  about 10 KB. Task 5's repetitions report WAL with that check applied.
- **Planning.** `pg_stat_statements` plans per call stay the same (0.91–1.02 in both variants:
  almost every statement call is planned, because the recreated temp table invalidates each
  auto-prepared statement), and plan time per request rises from 1.99 to 3.14 ms (25) and from 2.14
  to 3.43 ms (500) on the resource batch.

**Verdict.** On the resource GET-many cells, `ANALYZE` cuts API p50 and p95 by 52–69%, and on
both batches that load child rows (resource and fallback) it raises replay throughput 6–11 times,
`ANALYZE` included. The forced-miss cells improve by 5–16%, because direct fill dominates them. The descriptor path also improves (12–16% API
latency, 1.4–2.0 times the throughput). The cache-hit cells meet non-regression end to end
(1.01 / 1.01 at 25 rows). The one cost beyond the 5% bound is the 25-row candidate-metadata batch
under concurrency (throughput ×0.82–0.84, p95 ×1.23 at 32 sessions): that batch loads no child
rows, so `ANALYZE`'s ~1.3 ms is pure overhead on a ~5 ms batch. It is single-run evidence, and it
would argue for limiting a production `ANALYZE` to the hydration and descriptor batches. The
mitigation verdict therefore supports `ANALYZE` as an interim mitigation for PostgreSQL. Whether
it becomes the conditional story 3 depends on story 1's delivery (task 10), and it does not touch
the temp-table lifecycle that AC3 and AC5 remove.

### Notes on method

- **`auto_explain` windows** were added to the runner in task 3 (`AutoExplainWindow.cs`): `ALTER
  SYSTEM SET auto_explain.log_min_duration = 0` and `pg_reload_conf()`, a `RAISE LOG` marker before
  and after, then `ALTER SYSTEM RESET` and reload on every exit path. The plans are read from
  `docker logs` between the markers.
- **Run-script fix.** Task 2's container passed `-c auto_explain.log_min_duration=-1`. A
  command-line setting outranks `ALTER SYSTEM`, so the first task 3 smoke failed when it tried to
  open a window (`pg_settings.source = command line`). The flag was removed (`-1` is the default),
  the script recreates a container that still has it (the volume is kept), and it resets the setting
  at start in case an interrupted run left a window open.

## Candidates and representative SQL

Written by task 4. The `inline` candidate is in
[`experiments/prototype.patch`](experiments/prototype.patch) behind `DMS1521_KEYSET_VARIANT=inline`,
with `baseline` (the default) and `pg-analyze` beside it. It removes the keyset temp table from all
four paths on both engines, with one round trip and no catalog object, and it passes every task 4
check: baseline byte-identity, the PostgreSQL carrier probes (113 of 113), and the smokes of all
three fixtures on both engines.

### Design

| Piece | PostgreSQL | SQL Server |
|---|---|---|
| Carrier | Transaction-local setting `dms.page_keyset` (comma-separated ids), set with `set_config(…, true)` | Batch variable `@page_keyset nvarchar(max)`, JSON objects `{"DocumentId":…[,"Ordinal"…][,"ContentVersion"…]}` |
| Query keyset | One statement selects the page once, sets the carrier through a one-row aggregate, and returns the selected keys (and the anchor) by unnesting it: the first result set, same position and shape | `DECLARE @page_keyset`; `WITH page_ids AS (…) SELECT @page_keyset = (… FOR JSON PATH)`; the selected keys come from `OPENJSON(@page_keyset)` |
| Selected page | One `bigint[]` parameter `@page_document_ids` (was two parameters per id); no statement | `DECLARE @page_keyset nvarchar(max) = @selectedDocumentIdsJson` (the existing JSON parameter, whose format already matches) |
| Candidate metadata | Same as a query keyset, then the count and the metadata select | Same |
| Descriptor | Selection moves into the boundary statement, which sets the carrier and returns `MAX(anchor)` | Selection fills the variable where `descriptor_page` was filled |
| Keyset relation (`AppendRelation`) | `(SELECT u."DocumentId", CASE WHEN CAST(@page_document_ids AS bigint[]) IS NULL THEN NULL ELSE CAST(u."Ordinal" - 1 AS int) END AS "Ordinal" FROM unnest(COALESCE(CAST(@page_document_ids AS bigint[]), CAST(string_to_array(current_setting('dms.page_keyset', true), ',') AS bigint[]))) WITH ORDINALITY AS u("DocumentId", "Ordinal"))` | `(SELECT kj.[DocumentId], kj.[Ordinal] FROM OPENJSON(@page_keyset) WITH ([DocumentId] bigint '$.DocumentId', [Ordinal] int '$.Ordinal') kj)` |

Every keyset join still renders through `PlanSqlWriterExtensions.AppendRelation` (the
`TempTable` case), so the compiled `SelectByKeysetSql` of every table, descriptor projection, and
document-reference lookup changes with no compiler change. `descriptor_page` does not go through
that seam, so `DescriptorReadHandler` has its own `inline` branch. Query keysets keep a NULL
ordinal (metadata still orders by `DocumentId`); selected pages keep their caller's order. The
version fences, retries, and readers are unchanged: no reader was touched, because every batch keeps
its result-set sequence.

**Deviations from the plan, and why:**

1. **PostgreSQL selected pages read a parameter, so the relation reads `COALESCE(parameter, carrier)`.**
   PostgreSQL has no statement that takes bind parameters, calls a function, and returns no result
   set: `SET LOCAL` takes no parameters, and `SELECT set_config(…)` adds a result set. A query keyset
   can carry `set_config` in its selected-keys statement, but a selected page has no result set to
   carry it, and adding one would change the reader contract. The compiled relation text is shared
   by every keyset kind, so it reads the bound `bigint[]` when one is bound and the carrier when it
   is NULL. Consequence: on the resource paths every keyset reader is a **parameterized** statement;
   only the descriptor batch's readers are **carrier-only**. The NULL is bound untyped; the SQL's
   `CAST(… AS bigint[])` types it, so `Backend.Plans` needs no Npgsql reference.
2. **The candidate-metadata batch keeps its three result sets** (selected keys, count, metadata)
   instead of the plan's single left-joined CTE. That keeps
   `RelationalDocumentStoreRepository.ReadDocumentCandidatePageAsync` unchanged and gives the batch
   the same contract as a query keyset; its one change is binding the NULL `@page_document_ids`.
3. **On PostgreSQL the descriptor page is selected after the count statement**, inside the boundary
   statement, for the same no-result-set reason. Each statement takes its own snapshot under `READ
   COMMITTED` either way. The `-- Descriptor …` markers are unchanged.
4. **A single-document keyset without the fast path** is a non-production path (C5). On PostgreSQL
   its `bigint[]` is bound only when the batch text references it, so the fast-path command gains no
   parameter; SQL Server declares the carrier from `@DocumentId`. A *guarded* single-document keyset
   without the fast path throws `NotSupportedException` in the prototype.

### Representative SQL (25-row Student GET-many, abridged)

PostgreSQL, `baseline` (= `main`):

```sql
DROP TABLE IF EXISTS "page";
CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY, "Ordinal" int NULL) ON COMMIT DROP;
WITH page_ids AS (SELECT r."DocumentId" FROM "edfi"."Student" r ORDER BY r."DocumentId" ASC LIMIT @limit OFFSET @offset)
INSERT INTO "page" ("DocumentId") SELECT "DocumentId" FROM page_ids RETURNING "DocumentId";
SELECT d."DocumentId", d."ContentVersion" FROM "dms"."Document" d INNER JOIN "page" k ON d."DocumentId" = k."DocumentId";
…
SELECT t."CollectionItemId", … FROM "edfi"."StudentVisa" t INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId" ORDER BY …;
```

PostgreSQL, `pg-analyze`: the same, with `ANALYZE "page";` after the `INSERT` (see
[Cardinality hypothesis](#cardinality-hypothesis)).

PostgreSQL, `inline`:

```sql
WITH page_ids AS (SELECT r."DocumentId" FROM "edfi"."Student" r ORDER BY r."DocumentId" ASC LIMIT @limit OFFSET @offset)
SELECT u."DocumentId"
FROM (SELECT set_config('dms.page_keyset', COALESCE(string_agg(CAST(p."DocumentId" AS text), ',' ORDER BY p."DocumentId"), ''), true) AS keyset FROM page_ids p) c
CROSS JOIN LATERAL unnest(CAST(string_to_array(c.keyset, ',') AS bigint[])) AS u("DocumentId");
SELECT d."DocumentId", d."ContentVersion" FROM "dms"."Document" d INNER JOIN (<relation>) k ON d."DocumentId" = k."DocumentId";
…
SELECT t."CollectionItemId", … FROM "edfi"."StudentVisa" t INNER JOIN (<relation>) k ON t."Student_DocumentId" = k."DocumentId" ORDER BY …;
```

The aggregate subquery always yields one row and feeds the `LATERAL` unnest, so `set_config` runs
(and clears the carrier to `''`) even for an empty or zero-size page, before any later statement
reads it.

SQL Server, `baseline` (= `main`):

```sql
IF OBJECT_ID('tempdb..[#page]') IS NOT NULL DROP TABLE [#page];
CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);
WITH page_ids AS (SELECT r.[DocumentId] FROM [edfi].[Student] r ORDER BY r.[DocumentId] ASC OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY)
INSERT INTO [#page] ([DocumentId]) OUTPUT INSERTED.[DocumentId] SELECT [DocumentId] FROM page_ids;
SELECT d.[DocumentId], d.[ContentVersion] FROM [dms].[Document] d INNER JOIN [#page] k ON d.[DocumentId] = k.[DocumentId];
…
```

SQL Server, `inline`:

```sql
DECLARE @page_keyset nvarchar(max);
WITH page_ids AS (SELECT r.[DocumentId] FROM [edfi].[Student] r ORDER BY r.[DocumentId] ASC OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY)
SELECT @page_keyset = (SELECT [DocumentId] FROM page_ids FOR JSON PATH);
SELECT k.[DocumentId] FROM OPENJSON(@page_keyset) WITH ([DocumentId] bigint '$.DocumentId') k;
SELECT d.[DocumentId], d.[ContentVersion] FROM [dms].[Document] d INNER JOIN (<relation>) k ON d.[DocumentId] = k.[DocumentId];
…
```

A SQL Server selected page opens with `DECLARE @page_keyset nvarchar(max) = @selectedDocumentIdsJson;`,
and its descriptor batch with the same `DECLARE` and `SELECT @page_keyset = (… FOR JSON PATH)`, its
boundary reading `MAX(kj.[DocumentId])` from `OPENJSON(@page_keyset)`. Every construct is
SQL Server 2016-level (`OPENJSON`, `FOR JSON PATH`, compatibility level 130); that is a conservative
choice, not evidence that SQL Server 2016 is supported.

**Batch size** (characters; bound parameters):

| Batch | PostgreSQL `baseline` | PostgreSQL `inline` | SQL Server `baseline` | SQL Server `inline` |
|---|---|---|---|---|
| Resource (Student) | 7,774; 2 | 14,922; 3 | 6,028; 2 | 7,822; 2 |
| Selected page, 25 rows | 8,866; 50 | 14,518; 1 | 6,057; 1 | 7,538; 1 |
| Selected page, 500 rows | 33,891; 1,000 | 14,518; 1 | 6,057; 1 | 7,538; 1 |
| Candidate metadata | 675; 2 | 1,061; 3 | 723; 2 | 814; 2 |
| Descriptor | 2,053; 3 | 2,420; 3 | 2,068; 3 | 2,297; 3 |

The PostgreSQL relation repeats about 330 characters per keyset join, which roughly doubles the
resource batch. The selected page loses its per-id parameters, so its text no longer depends on the
page size: one statement text per batch for every page size, and 1,000 fewer parameters at 500 rows.

### Checks

**(a) Baseline byte-identity.** With the patch applied and no variant set, both unit suites pass in
full (`Backend.Plans.Tests.Unit` 2,671 of 2,671; `Backend.Tests.Unit` 4,771 of 4,771), so every
SQL-text pin and manifest golden still matches `main`. The captured batch texts agree too: with
the patch applied in `baseline` mode (runs `t4baseline-postgresql-*` and `t4baseline-mssql-*`, all
three fixtures), the SHA-256 of every batch (six resource cells, descriptor, candidate, and fallback
at 25 and 500 rows) equals `B0`'s, 12 of 12 on each engine (`B0`: `t3-postgresql-*-B01-*` on
PostgreSQL, task 2's `smoke1`/`smoke4`/`smoke6` `B0` runs on SQL Server). Tasks 5 and 6 can run `B`
with the patch applied.

**(b) PostgreSQL carrier probes** (`Given_Postgresql_KeysetCarrierProbes` and
`Given_Postgresql_KeysetCarrierDescriptorProbes`, run `t4probes-postgresql-carrier-probes-I1-20261106214212`
and `t4probes-postgresql-carrier-descriptor-probes-I1-20261106214241`; `smoke-10k` and
`descriptors-smoke-2k`). Each probe runs the production `ConsistentHydrationExecutor`, batch
builder, and reader on a one-connection pool built with `NpgsqlDataSourceLifetime`'s settings
(`NoResetOnClose`, `AutoPrepareMinUsages=3`, `MaxAutoPrepare=256`), and asserts the exact ids in
order, the carrier read back afterwards on the same connection, and the backend pid.

| Probe | Result |
|---|---|
| On one physical connection: query keysets of 25, then an empty selection, a zero-size page, 1, 25 at offset 100, 500, and 500 at offset 9000; selected pages of 25 (descending order), empty, 1, and 500 (descending order); an empty selection after a selected page; 25 again | 13 of 13 exact; the carrier reads `''` after every batch (the transaction-local value is gone) |
| The captured production batch plus `SELECT 1 / 0;` fails after the carrier is set (SQLSTATE 22012); then the carrier, an empty query keyset, and an empty selected page are read on that connection | 3 of 3: carrier `''`, no rows |
| A forced `ConsistentHydrationExecutor` retry (the first attempt bumps a page document's `ContentVersion` between the version fences) | 2 attempts, exact page |
| 8 executions each of a 25-row query keyset and a 25-row selected page under `plan_cache_mode` `auto`, `force_custom_plan`, and `force_generic_plan` (fresh connection per mode) | 48 of 48 exact |
| Descriptor batch, 8 non-empty/empty pairs per mode on one connection | 48 of 48 exact: boundary, leading and trailing version ids, owner-row count; no stale ids on the empty page |

Each probe batch runs in its own transaction, and the carrier is transaction-local. These probes
therefore show that nothing survives a commit or rollback, and the `''` reading rules out a
session-level carrier. They do not run two batches in one transaction, which is the only place a
batch could read another's ids; both stories add that test.

**Plans actually used** (from `pg_prepared_statements` on the probe's own connection, and
`auto_explain` on its last executions; keyset estimate is the `unnest` function scan's row estimate
against the actual count):

| Statements | Class | `auto` | `force_custom_plan` | `force_generic_plan` |
|---|---|---|---|---|
| Resource keyset readers (10 statements) | Parameterized (`@page_document_ids`) | 50 custom then 20 generic plans: 5 custom per statement, then the generic plan; by execution 8 the estimate is a **fixed 10 rows** for a 25-row page | 70 custom; estimate exact (25 for 25) | 70 generic; estimate fixed at 10 |
| Query-keyset carrier statement | Parameterized (`limit`, `offset`) | custom | custom | generic |
| Descriptor keyset readers (3 statements) | Carrier-only (no parameters) | 42 generic, 0 custom | 42 generic, 0 custom | 42 generic, 0 custom |

- A **custom plan estimates the carrier exactly.** `current_setting` is stable, so the planner
  evaluates it while planning, and each statement in the pipelined batch is planned after the
  carrier statement has run. The smoke's driver-path plans show it: 25 and 500 estimated, 25 and 500
  actual, and the child statements on `Index Scan` at 25 rows.
- Under the default `auto` mode **the resource readers converge on the generic plan** after five
  executions, because with an untyped-NULL or array parameter the generic plan's cost is no worse.
  Its keyset estimate is PostgreSQL's fixed 10 rows for an array of unknown length.
- A **carrier-only statement always gets a generic plan**, whatever the mode, and its estimate is
  frozen at the carrier's size when the statement was first planned on that connection: 25 here,
  still 25 for an empty page. A connection whose first descriptor page held 1 row keeps estimating 1.

Both fixed estimates (10, or the first page's size) are far closer to small pages than `baseline`'s
2,040, but neither tracks the page. Task 5 measures what they cost at 25 and 500 rows on
`primary-500k`.

**(c) Sizing** (`inline`, no `UPDATE_GOLDENS`): `Backend.Plans.Tests.Unit` fails 91 of 2,671 tests in
27 classes, `Backend.Tests.Unit` 21 of 4,771 in 4 classes. Every failure is a pin of the old text or
parameter list, except 3 that exercise the guarded single-document keyset without the fast path
(deviation 4):

| Kind | Tests | Classes |
|---|---|---|
| Batch and dialect SQL-text pins | 69 | `Given_HydrationBatchBuilder_*` (zero-size, zero-limit, anchored, single, selected-page, compiled, materialization, fast-path-disabled cases), `Given_ReadPlanCompiler` (15), `Given_PgsqlPlanDialect`, `Given_MssqlPlanDialect`, `Given_PlanSqlWriterExtensions`, `Given_A_Keyset_That_Carries_No_Anchor` |
| Descriptor batch SQL-text pins | 18 | `Given_DescriptorReadHandler` |
| Parameter-count pins (the PostgreSQL `@page_document_ids`) | 14 | `Given_HydrationBatchBuilder_Query_Parameter_Binding`, `…AddParameters_With_Query_Keyset`, `…With_Pgsql_Selected_Page_Keyset`, `Given_RelationalQueryRequestPreprocessor+Given_CompactDescriptorFilters` (3, PostgreSQL) |
| Golden fixtures | 8 | runtime-plan-compilation and mapping-set manifest goldens: 2,494 changed lines (ds-5.2 1,158; ds-5.2 sample extension 1,278; the six smaller fixtures 4–18 each) |
| Guarded single document without the fast path | 3 | `Given_HydrationBatchBuilder_With_A_Single_Document_Keyset`, `…With_Mssql_Single_Document_Fast_Path` (the flag-disabled case) |

The single-document fast-path batches and their pins are unchanged (C5). A first cut bound the
PostgreSQL array on every single-document command, which `Given_Relational_Write_Current_State_Loader`
caught as an extra parameter on the fast path; binding it only when the batch reads the keyset fixed
that.

**(d) Smokes** (`smoke-10k`, `descriptors-smoke-2k`; runs `t4smokeI-postgresql-*` and
`t4smokeI-mssql-*`). All three fixtures pass on both engines with binding parity and no replay
errors. Temp-table work per request in the counter windows:

- PostgreSQL: catalog `n_tup_ins` + `n_tup_del` **0** in every window (resource, descriptor,
  candidate, fallback; `B0` 42–44).
- SQL Server: `Temp Tables Creation Rate` **0** in 10 of 12 windows (`B0` 1 per batch). The other two
  read +6 (fallback 25 at concurrency 1, one window that also logged a cause-26 recompile) and −27
  (resource 500 at concurrency 8). The counter is server-wide, and a negative net means the idle
  window saw more creations than the workload, so these are background temp tables, not the batch.
  Task 6 must not gate on a single window of this counter: it needs repeated windows, or a
  session-filtered Extended Events count of temp-table creation, before a zero counts.

**Temporary test adaptation** (story scope; the marked `DMS-1521 inline (test adaptation)` section of
`AuthoritativeTimestampReadConsistencyScenario`, at both split sites). On SQL Server the double
appends `SELECT @page_keyset AS [dms1521_carrier]` to the first command, removes that result set from
the buffered tables, and opens the second command with `DECLARE @page_keyset nvarchar(max) =
@dms1521_carrier;`, bound as `nvarchar(max)`. The PostgreSQL split already runs in one explicit
transaction, so its carrier survives. It compiles; task 7 runs it.

## PostgreSQL results

Written by task 5. **`inline` removes the temp-table lifecycle on every path and improves the
problematic hydration workload far beyond the observed variability; no path regresses.** The task 8
Gate 2 decision for PostgreSQL also needs task 7's correctness result.

### Runs

Three interleaved `B, I` sessions (`-Sequence B,I`, session ids `t5s1`, `t5s2`, `t5s3`), with
`prototype.patch` applied for all six runs: `B` is its `baseline` mode, proven byte-identical to
`main` in task 4, and `I` is `inline`. Each run covers all three fixtures at full size
(`primary-500k`, `descriptors-25k`): 10 warmups and 60 measured API iterations per cell, replays at
concurrency 1, 8, and 32 (3 + 20 executions per worker), counters on the concurrency-1 replays, one
`auto_explain` window per fixture and page size, and an observer calibration at the start of each
session (440 and 880 catalog tuples for 10 and 20 cycles, plain `SELECT`s 0, in all three). Subject
commit `3621b1eb6`; the container stopped between sessions, so no session started warm.

| Session | `B` run ids | `I` run ids | Wall time `B` / `I` |
|---|---|---|---|
| `t5s1` | `t5s1-postgresql-{resource,descriptor,read-acceleration}-B1-*` | `…-I2-*` | 25.9 / 21.3 min |
| `t5s2` | same pattern | same pattern | 24.9 / 20.2 min |
| `t5s3` | same pattern | same pattern | 25.6 / 20.2 min |

Every batch kept the same SHA-256 in all three sessions of its variant, every replay finished with
0 errors (warmup and measured, every concurrency level), and commands per request are identical in
`B` and `I` (resource and descriptor 1; cache hit 2; forced miss 178 at 25 rows and 3,503 at 500).

**How to read the tables.** Each figure is the mean ± SD across the three sessions of that run's
statistic (a within-run p50, p95, throughput, or per-request counter), with the run-to-run CV in
brackets. Ratios are `I / B`. `m` is `max(2 × CV, 5%)` computed from `B`'s CV, so the improvement
bound is `ratio ≤ 1 − m` and the non-regression bound is `ratio ≤ 1 + m` (cost) or `≥ 1 − m`
(throughput). Analysis script: `t5_analyze.py` (task 12 attaches it with the evidence).

### Temp-table lifecycle and WAL (AC3)

Counters on the concurrency-1 replay windows, per request, net of the idle window:

| Batch | Rows | Catalog `n_tup_ins` + `n_tup_del`, B → I | `pg_statistic` | WAL bytes, B (clean windows) → I | Statement calls per request, B → I |
|---|---|---|---|---|---|
| Resource | 25 / 500 | 44 → **0** / 44 → **0** | 0 → 0 | 10,555 / 10,623 → **0** | 11 → 11 |
| Fallback (selected page) | 25 / 500 | 44 → **0** / 44 → **0** | 0 → 0 | 10,214 / 10,116 → **0** | 11 → 10 |
| Candidate metadata | 25 / 500 | 44 → **0** / 44 → **0** | 0 → 0 | – / 10,391 → **0** | 2 → 2 |
| Descriptor | 25 / 500 | 42 → **0** / 42 → **0** | 0 → 0 | 10,040 (gross) / 10,353 → **0** | 5 → 4 |

- **Catalog.** `B` repeats the calibrated temp-table cycle exactly (44 tuples; 42 for
  `descriptor_page`, which has no `Ordinal` column) in every one of its 24 windows, with SD 0. `I`
  reads exactly 0 in all 24 windows. The calibrated observer therefore shows that no
  multi-document request creates or drops a temp table under `inline`.
- **WAL.** `I` writes **no WAL**. Its workload windows hold 0 bytes in 23 of 24 windows, and one
  50-byte record in the other (descriptor 500, `t5s3`). Its batches are read-only, while `B`'s
  catalog churn writes about 93–96 WAL records and 10.0–10.6 KB per request. The `B` figure uses
  only clean windows: no full-page images in the workload window and at most 200 bytes of idle
  WAL. 14 of `B`'s 24 windows are not clean, because a checkpoint's full-page images (404–467 FPIs,
  up to 220 KB per request) or background WAL in the idle window (0.24–2.1 MB) swamps a 10 KB
  figure. The idle windows of both variants' descriptor runs at 25 rows caught background WAL
  (240–559 KB in `B`, 318 KB in `I`), so for that batch the table gives `B`'s gross workload-window
  figure (200.8 KB over 20 requests, no FPIs, identical in all three sessions) against `I`'s 0.
  The candidate batch at 25 rows has no usable `B` window, because every one of them contains
  FPIs.
- `pg_stat_statements` counts one statement call fewer per request on the fallback and descriptor
  batches, and the same number on the resource and candidate batches. The `DROP` and `CREATE`
  statements are gone in every batch, and the query keysets' selection statement takes over from
  the `INSERT … RETURNING`.

### Problematic hydration workload (improvement bound)

**Resource GET-many, API latency** (`primary-500k`, ms):

| Cell | B p50 | I p50 | p50 ratio | B p95 | I p95 | p95 ratio | `m` (p50 / p95) |
|---|---|---|---|---|---|---|---|
| Offset 0, 25 | 128.6 ± 2.9 (2.2%) | 39.0 ± 0.7 | **0.30** | 142.4 ± 6.2 (4.4%) | 43.1 ± 1.9 | **0.30** | 5.0 / 8.8% |
| Offset 0, 500 | 152.3 ± 3.4 (2.2%) | 63.8 ± 0.9 | **0.42** | 160.2 ± 2.8 (1.8%) | 71.9 ± 3.5 | **0.45** | 5.0 / 5.0% |
| Shallow, 25 | 127.8 ± 3.4 (2.7%) | 36.8 ± 0.5 | **0.29** | 135.3 ± 3.5 (2.6%) | 40.3 ± 1.3 | **0.30** | 5.4 / 5.1% |
| Shallow, 500 | 152.8 ± 2.4 (1.6%) | 58.6 ± 1.3 | **0.38** | 167.4 ± 4.2 (2.5%) | 70.4 ± 2.1 | **0.42** | 5.0 / 5.1% |
| Deep, 25 | 148.5 ± 0.9 (0.6%) | 57.7 ± 0.6 | **0.39** | 156.5 ± 1.4 (0.9%) | 61.9 ± 0.3 | **0.40** | 5.0 / 5.0% |
| Deep, 500 | 174.5 ± 0.6 (0.4%) | 81.4 ± 1.0 | **0.47** | 185.4 ± 2.1 (1.1%) | 89.4 ± 5.3 | **0.48** | 5.0 / 5.0% |

**Child-collection cost** (the four Student child statements, summed per request):

| Source | Path | Rows | B time (ms) | I time (ms) | Time ratio | B buffers | I buffers | Buffer ratio |
|---|---|---|---|---|---|---|---|---|
| Driver path (`auto_explain`, median of 20 requests) | Resource | 25 | 173.3 ± 4.3 | 0.34 ± 0.01 | **0.002** | 17,166 | 400 | **0.023** |
| | Resource | 500 | 183.5 ± 2.9 | 3.93 ± 0.02 | **0.021** | 17,166 | 8,000 | **0.466** |
| | Fallback | 25 | 167.8 ± 2.9 | 0.28 ± 0.01 | **0.002** | 17,166 | 400 | **0.023** |
| | Fallback | 500 | 180.7 ± 4.1 | 3.56 ± 0.04 | **0.020** | 17,166 | 8,000 | **0.466** |
| Spike plans (`EXPLAIN ANALYZE` replay) | Resource | 25 | 182.3 ± 3.6 | 0.38 ± 0.04 | 0.002 | 17,169 | 403 | 0.023 |
| | Resource | 500 | 192.5 ± 1.1 | 3.34 ± 0.06 | 0.017 | 17,169 | 8,003 | 0.466 |
| | Fallback | 25 | 190.0 ± 6.5 | 0.39 ± 0.01 | 0.002 | 17,169 | 403 | 0.023 |
| | Fallback | 500 | 194.6 ± 5.3 | 3.14 ± 0.08 | 0.016 | 17,167 | 8,002 | 0.466 |

`B`'s child buffers do not vary at all (SD 0), so `m` is the 5% floor. At 500 rows `I` still reads
8,000 buffers, because it probes each child index 500 times; that is under half of `B`'s full
scans, and the time is 2% of `B`'s.

**Fallback batch replay** (the forced-miss criterion; also the resource batch for reference):

| Batch | Rows | c | B p50 / p95 (ms) | I p50 / p95 (ms) | Ratio p50 / p95 | B → I throughput (/s) | Ratio |
|---|---|---|---|---|---|---|---|
| Fallback | 25 | 1 | 99.9 / 105.5 | 11.3 / 12.5 | **0.11 / 0.12** | 9.9 → 87.2 | ×8.8 |
| Fallback | 25 | 8 | 144.7 / 173.7 | 6.8 / 9.9 | 0.05 / 0.06 | 54.1 → 1,106 | ×20.4 |
| Fallback | 25 | 32 | 396.2 / 478.4 | 9.1 / 15.1 | 0.02 / 0.03 | 79.8 → 3,159 | ×39.6 |
| Fallback | 500 | 1 | 106.3 / 114.7 | 13.5 / 14.7 | **0.13 / 0.13** | 9.3 → 72.8 | ×7.8 |
| Fallback | 500 | 8 | 173.9 / 194.1 | 25.2 / 28.9 | 0.15 / 0.15 | 45.1 → 311.9 | ×6.9 |
| Fallback | 500 | 32 | 502.0 / 610.6 | 86.7 / 102.8 | 0.17 / 0.17 | 63.3 → 369.8 | ×5.8 |
| Resource | 25 | 1 | 88.3 / 92.7 | 2.3 / 5.5 | 0.03 / 0.06 | 11.3 → 326.7 | ×29.0 |
| Resource | 25 | 32 | 398.9 / 474.5 | 9.7 / 25.3 | 0.02 / 0.05 | 79.5 → 2,525 | ×31.8 |
| Resource | 500 | 1 | 108.3 / 113.4 | 14.2 / 15.6 | 0.13 / 0.14 | 9.2 → 68.9 | ×7.5 |
| Resource | 500 | 32 | 528.8 / 642.6 | 86.5 / 104.1 | 0.16 / 0.16 | 60.1 → 371.9 | ×6.2 |

The largest `B` CV among these cells is 15.7% (resource 500 at c = 8, p95), so `m` is at most
31%. Every ratio is far below `1 − m`. Under `B`, 32 sessions barely raise throughput above 8,
because every request scans about 2 million child rows. Under `I`, throughput keeps scaling at
25 rows.

### Every other path (non-regression bound)

| Cell (API) | B p50 / p95 (ms) | I p50 / p95 (ms) | Ratio p50 / p95 | Bound `1 + m` (p50 / p95) |
|---|---|---|---|---|
| Descriptor, 25 | 41.0 / 46.2 | 29.8 / 34.2 | 0.73 / 0.74 | 1.05 / 1.05 |
| Descriptor, 500 | 42.6 / 46.8 | 42.0 / 46.0 | 0.99 / 0.98 | 1.05 / 1.07 |
| Cache hit, 25 | 36.7 / 40.4 | 31.9 / 35.6 | 0.87 / 0.88 | 1.06 / 1.09 |
| Cache hit, 500 | 48.1 / 67.8 | 42.4 / 56.7 | 0.88 / 0.84 | 1.05 / 1.15 |
| Forced miss, 25 (complete) | 301.4 / 316.2 | 208.3 / 220.1 | 0.69 / 0.70 | 1.15 / 1.17 |
| Forced miss, 500 (complete) | 3,446 / 3,531 | 3,087 / 3,139 | 0.90 / 0.89 | 1.25 / 1.26 |

| Replay | Rows | p50 ratio c = 1 / 8 / 32 | p95 ratio c = 32 | Throughput ratio c = 8 / 32 |
|---|---|---|---|---|
| Descriptor | 25 | 0.12 / 0.11 / 0.09 | 0.14 | ×8.8 / ×8.0 |
| Descriptor | 500 | 0.34 / 0.51 / 0.59 | 0.46 | ×1.9 / ×1.8 |
| Candidate | 25 | 0.19 / 0.16 / 0.17 | 0.23 | ×5.9 / ×4.0 |
| Candidate | 500 | 0.40 / 0.45 / 0.50 | 0.43 | ×2.2 / ×2.1 |

- Every path meets non-regression, and most improve. The forced-miss cells improve by 10–31%
  even though direct fill dominates them (see [Method and environment](#method-and-environment)).
- **Descriptor at 500 rows is flat end to end** (0.99 / 0.98), while its batch replay improves
  (×1.8–1.9 throughput). At 500 rows the API cost is dominated by response serialization, not
  the batch. At 25 rows the batch's `descriptor_page` lifecycle was a large share of the request,
  and removing it saves about 11 ms.
- **The candidate-metadata batch**, the one path where task 3's `ANALYZE` regressed (×0.82–0.84
  throughput at 25 rows), improves under `inline`: ×4.0–5.9 at 25 rows and ×2.1–2.2 at 500. Its
  buffers are unchanged (207 and 4,010); the gain is the removed DDL and its re-planning.
- In the `auto_explain` windows, the descriptor batch's keyset statements total 292.8 ms per
  20-request window under `I` against 265.7 ms under `B` at 500 rows (×1.10, `auto_explain`
  timing instrumentation included). The uninstrumented replays and the API cell show no regression
  (above), so this is reported as a supporting detail only.

### Plans and planning

**Child node types** (driver path, every child-table node in every window): `B` `Seq Scan` 960 of
960; `I` `Index Scan` 960 of 960 (resource and fallback, 25 and 500).

**Keyset estimates on the driver path:**

| Path | B estimate / actual | I estimate / actual |
|---|---|---|
| Resource (API window) | 2,040 / 25 and 2,040 / 500 | exact (25 / 25, 500 / 500) in every keyset reader |
| Fallback (one pooled connection, 20 replays) | 2,040 / 25 and 2,040 / 500 | exact for roughly the first 5 executions of each statement (custom plans), then a fixed **10** (generic plan) |
| Descriptor (API window) | 2,260 / 25 and 2,260 / 500 | exact (25 / 25, 500 / 500) |
| Candidate (API window) | 2,040, plus the metadata statement | exact in the metadata statement |

- **Generic plans are cheap too.** In the fallback window, the per-request child cost of the first
  5 replays (custom plans) and the last 10 (generic, estimate 10) is the same: 0.30 against
  0.27 ms at 25 rows, and 3.54 against 3.57 ms at 500, with `Index Scan` throughout. The task 4
  concern, that a fixed estimate of 10 cannot follow a 500-row page, does not cost anything
  measurable on `primary-500k`: at 10 estimated keys and at 500 actual ones, an index probe per
  key still beats a full scan of a 500,000-row child table.
- **Plans per call** (`pg_stat_statements`, concurrency-1 replay windows on one auto-preparing
  pool): `B` 0.91–1.02 on every batch, because the recreated temp table invalidates each prepared
  statement, so almost every call is planned. `I`: resource 0.29 (25) and 0.09 (500); fallback
  1.00 (25) and 0.10 (500); candidate 0.65 and 0.50; descriptor 0.26 and 0.25. Plan time per
  request drops by 5–94%: resource 1.94 → 0.56 ms and 2.22 → 0.13 ms; fallback 4.94 → 4.70 ms
  (25) and 2.20 → 0.33 ms (500). The 25-row fallback window planned every call, and still ran
  11.3 ms against 99.9 ms. That is a property of the window, not of the plan cache: the replay
  pool is shared across batches, and this c = 1 replay ran right after the candidate batch's
  c = 32 replay, so its sequential executions most likely spread over the pool's ~32 idle
  connections and none reached `AutoPrepareMinUsages = 3` (inferred, not probed). On one
  connection (the fallback `auto_explain` window above) the same statements switched to the
  generic plan after about five executions. The 500-row fallback, the same text already prepared
  on every connection, reads 0.10.
- **Observation: the API path ran each request on a new backend.** In every API `auto_explain`
  window, under both variants, the 20 sequential requests ran on 20 different backend pids (the
  two commands of a cache-hit request share one pid). Prepared statements therefore never reached
  PostgreSQL's five-execution switch to a generic plan on the API path, and all its plans are
  custom plans, in `B` and `I` alike. The pooled replays and the fallback window (one physical
  connection) provide the steady-state, generic-plan evidence above. The cause was not
  established in this task: production's `NpgsqlDataSourceCache` keeps its data source, and the
  leased connection string has `pooling=True`. It affects both variants equally, and it is
  recorded for task 9's prepared-plan statement and the story evidence.
- **Spike-plan planning time** per batch is slightly higher under `I` (resource 6.74 → 7.06 ms
  and 7.89 → 8.50 ms; one plan per statement, all custom), because the inline relation is a longer
  expression than a temp-table name. Under re-use (above), `I` plans far less often, so planning
  time per request falls overall.

### What task 5 shows for Gate 2 (PostgreSQL)

| Gate 2 criterion | Result |
|---|---|
| Lifecycle removed on every remaining path (calibrated) | Met: 0 catalog tuples and 0 WAL per request on all four batches, in all 24 `I` windows |
| Hydration improves beyond variability at 25 and 500 rows: resource child time and buffers, API p50 and p95 | Met: child time 0.002–0.021, buffers 0.023–0.466, API 0.29–0.48 (bounds 0.91–0.95) |
| Forced miss: fallback replay latency and child time and buffers | Met: replay p50 and p95 0.02–0.17, child time 0.002–0.020, buffers 0.023–0.466 |
| Forced-miss complete API p50 and p95: non-regression | Met: 0.69–0.90 (bounds 1.15–1.26) |
| Descriptor, candidate metadata, cache hit: non-regression | Met: API 0.73–0.99, replay latency 0.09–0.59, throughput ×1.8–×8.8 |
| No type (b) failure in task 7 | Met: none on either engine (see [Correctness validation](#correctness-validation)) |

## SQL Server results

Written by task 6. **`inline` removes the temp-table lifecycle on every path, with its `PAGELATCH`
waits, and never recompiles; it improves most paths, but it regresses the 500-row hydration batches
beyond the non-regression bound.** At 500 rows the resource batch at concurrency 1 and the
selected-page fallback batch at concurrency 1 and 32 cost 14–34% more than `baseline`, because every
keyset statement re-parses the JSON carrier. SQL Server therefore does not meet the task 8 Gate 2
criterion with `inline` as prototyped, and task 8's derived-form contingency is triggered for
SQL Server.

### Runs

Three interleaved `B, I` sessions (`-Sequence B,I`, session ids `t6s1`, `t6s2`, `t6s3`) on
`dms-1521-mssql`, with `prototype.patch` applied for all six runs (`B` = `baseline` mode, proven
byte-identical to `main` in task 4; `I` = `inline`). Every database ran with production's write
isolation (`MatchProductionWriteIsolation`: `READ_COMMITTED_SNAPSHOT` and snapshot isolation on;
`is_read_committed_snapshot_on = 1` in every `environment.json`). The run shape is task 5's
(`primary-500k`, `descriptors-25k`, 10 warmups and 60 measured API iterations per cell, replays at
concurrency 1, 8, and 32 with 3 + 20 executions per worker), with counter windows on the 1 and 32
replays and no `auto_explain` (PostgreSQL only). Subject commit `3621b1eb6`; the container stopped
between sessions.

| Session | Run ids | Wall time `B` / `I` |
|---|---|---|
| `t6s1` | `t6s1-mssql-{resource,descriptor,read-acceleration}-{B1,I2}-*` | 64 / 55 min |
| `t6s2` | same pattern | 59 / 52 min |
| `t6s3` | same pattern | 55 / 52 min |

A SQL Server `B, I` session takes about 1 h 50 min, not the 2 h 10 min estimated in task 2; the
`primary-500k` resource run takes 13–17 min, and read acceleration 34–43 min, most of it the
forced-miss cell's 5 s direct-fill timeout.

Every batch kept one SHA-256 across the three sessions of its variant, every replay finished with
0 errors (warmup and measured, every level), and commands per request are the same in `B` and `I`
(resource and descriptor 1, cache hit 2; forced miss 50–82, bounded by the direct-fill timeout in
both). The tables read as in [PostgreSQL results](#postgresql-results): mean ± SD across the three
sessions of each run's statistic, run-to-run CV in brackets, ratios `I / B`, and
`m = max(2 × CV(B), 5%)`. Analysis script: `t6_analyze.py`, report `t6_report.md` (both under
`C:\perf\dms-1521\`, attached by task 12).

**Gate counter.** Task 4 found the server-wide `Temp Tables Creation Rate` too noisy to gate a zero,
so task 6 gates on a session-filtered Extended Events count (`object_created`, user table, in
tempdb, at DDL commit, from sessions whose current database is the measured one; see
[Counters](#counters-servercounters)). It calibrated exactly at the start of every session (10, 20,
and 0). In the full-size windows both counters agree exactly, so the background noise seen in the
smokes did not occur here.

### Temp-table lifecycle, latches, and recompiles (AC3)

Per request, net of the idle window, in the concurrency-1 and concurrency-32 replay windows (24
windows per variant):

| Batch | Rows | Temp tables (XE), B → I | `PAGELATCH` waits per request at c = 32, B → I | XE recompiles and plan generations, B and I | Statements per request, B → I |
|---|---|---|---|---|---|
| Resource | 25 / 500 | 1 → **0** / 1 → **0** | 14.5 waits, 18.6 ms → **0** / 14.9 waits, 108.2 ms → **0** | 0 | 11 → 12 |
| Fallback (selected page) | 25 / 500 | 1 → **0** / 1 → **0** | 15.9, 54.3 ms → **0** / 15.2, 90.8 ms → **0** | 0 | 11 → 10 |
| Candidate metadata | 25 / 500 | 1 → **0** / 1 → **0** | 13.2, 14.1 ms → **0** / 17.2, 42.4 ms → **0** | 0 | 2 → 3 |
| Descriptor | 25 / 500 | 1 → **0** / 1 → **0** | 12.7, 8.1 ms → **0** / 14.3, 62.1 ms → **0** | 0 | 5 → 5 |

- **Temp tables.** `B` creates exactly one per request in all 24 of its windows (20 of 20 at
  concurrency 1, 640 of 640 at 32, SD 0); `I` creates none in all 24.
- **`PAGELATCH`.** At concurrency 1, `B` takes exactly 2 uncontended latch waits per request (the
  create and the drop). At 32 they become 13–17 contended waits costing 8–108 ms per request, the
  DMS-1399 tempdb metadata contention, now measured on the read paths. `I` takes none at either
  level.
- **Recompiles.** Zero in both variants: the XE `sql_statement_recompile` histogram is empty in
  every window, `SQL Re-Compilations/sec` nets 0, and the summed `plan_generation_num` does not
  move. So on these paths `baseline`'s `#page` caused no recompile after warmup either; the
  DMS-1248 recompile concern does not show here. Compilations net 0 in both (at most ±0.02 per
  request, idle-window noise).
- **Memory-optimized tempdb metadata** was not re-measured. DMS-1399 found that it removes the
  metadata latch wait but recovers only about 11% of the lost throughput, and it is a server-level
  setting DMS cannot assume. `inline` needs neither.

### Hydration batches

**Replay latency and throughput** (the gate is p50 and p95 at concurrency 1, p95 and throughput at
32):

| Batch | Rows | c = 1 p50 / p95 ratio | Bound (p50 / p95) | c = 32 p95 ratio | Bound | c = 32 throughput ratio | Bound | Result |
|---|---|---|---|---|---|---|---|---|
| Resource | 25 | 0.76 / 0.76 | 1.14 / 1.36 | 0.12 | 2.20 | ×2.19 | ≥ 0.76 | pass |
| Resource | 500 | **1.17 / 1.15** | 1.09 / 1.10 | 0.39 | 1.17 | ×1.17 | ≥ 0.94 | **fail** (c = 1) |
| Fallback | 25 | 0.74 / 0.61 | 1.11 / 1.37 | 0.07 | 1.20 | ×3.33 | ≥ 0.90 | pass |
| Fallback | 500 | **1.34 / 1.33** | 1.05 / 1.05 | **1.14** | 1.05 | **×0.79** | ≥ 0.95 | **fail** (c = 1 and 32) |

Absolute values for the failing cells (ms, mean ± SD):

| Batch | Rows | c | B p50 / p95 | I p50 / p95 | B → I throughput (/s) |
|---|---|---|---|---|---|
| Resource | 500 | 1 | 21.3 ± 1.0 / 22.7 ± 1.1 | 24.9 ± 0.1 / 26.1 ± 0.3 | 46.6 → 39.8 |
| Resource | 500 | 32 | 142.9 ± 5.7 / 308.8 ± 26.6 | 57.6 ± 1.6 / 121.8 ± 3.4 | 182.4 → 214.0 |
| Fallback | 500 | 1 | 21.0 ± 0.4 / 22.1 ± 0.4 | 28.0 ± 0.1 / 29.4 ± 0.1 | 47.1 → 35.5 |
| Fallback | 500 | 32 | 137.5 ± 2.1 / 181.1 ± 4.9 | 108.6 ± 18.0 / 206.3 ± 24.8 | 226.8 → 180.0 |

**Why 500 rows regress** (spike plans, `SET STATISTICS XML, IO, TIME`; identical in all three
sessions):

| Batch | Rows | Batch CPU ms, B → I | Batch elapsed ms, B → I | Child logical reads, B → I | Batch logical reads, B → I |
|---|---|---|---|---|---|
| Resource | 25 | 9.0 → 11.0 | 4.3 → 5.3 | 1,068 → 1,320 (×1.24) | 1,688 → 1,924 |
| Resource | 500 | 27.0 → 34.7 (×1.28) | 25.7 → 31.7 (×1.23) | 21,112 → 22,232 (×1.05) | 32,719 → 33,058 |
| Resource, deep offset | 500 | 96.0 → 105.3 | 94.0 → 102.3 | 21,128 → 22,248 | 42,336 → 42,675 |
| Fallback | 500 | 27.3 → 35.0 (×1.28) | 25.7 → 32.7 (×1.27) | 21,112 → 22,232 (×1.05) | 32,683 → 33,022 |

- **The plans do not change.** Every child statement uses the same nonclustered index seek plus
  clustered key lookup in both variants, at both page sizes and both offsets. The ticket's
  plan-shape concern does not apply to SQL Server, where `#page` gets statistics: `B`'s `#page`
  scan estimates 25 rows (one plan, compiled for the first page and reused at 500 without a
  recompile), and `I`'s `OPENJSON` keeps its fixed estimate of 50 rows per execution. Neither drives
  a scan on `primary-500k`. (The 10k smoke's `StudentVisa` scan under `inline`, noted in task 4,
  does not appear at 500k.)
- **The cost is the carrier, paid per statement.** `B` parses the page once into `#page`, which its
  clustered key keeps sorted. `I` reads `OPENJSON(@page_keyset)` in every statement after the
  selection (11 in the resource batch), and each one parses the carrier again and adds a
  `Worktable`. Per statement this is
  1–2 ms more CPU at 500 rows (for example 2 → 3 ms per child statement, 8 → 10 ms for the
  descriptor projection) and 3–8% more logical reads, about +7.5 ms CPU per batch. At 25 rows the
  parse is cheap, and removing the create, drop, and insert outweighs it.
- **At concurrency 32 the two effects compete.** Removing the latch contention wins for the
  resource batch (p95 0.39, ×1.17 throughput) but not for the fallback batch (p95 1.14, ×0.79),
  although `B`'s latch cost per request is similar on both. The run data does not explain the
  difference; the `I` fallback cell at 32 is also the least stable (CV 17% throughput, against 2%
  for `B`).
- **API latency hides it.** Every resource GET-many cell, including 500 rows at every offset, is
  within ±5% of `B` (p50 0.95–1.02, p95 0.94–1.00, bounds 1.07–1.23), because a 500-row request
  spends about 130 ms, of which the batch is about 25 ms.

### Every other path (non-regression bound)

| Cell (API) | B p50 / p95 (ms) | I p50 / p95 (ms) | Ratio p50 / p95 | Bound `1 + m` |
|---|---|---|---|---|
| Resource, offset 0, 25 / 500 | 104.0 / 117.2; 128.2 / 142.3 | 99.5 / 116.8; 129.3 / 142.3 | 0.96 / 1.00; 1.01 / 1.00 | 1.14 / 1.23; 1.15 / 1.21 |
| Resource, shallow, 25 / 500 | 97.6 / 106.2; 123.2 / 134.9 | 95.3 / 104.3; 125.0 / 134.4 | 0.98 / 0.98; 1.02 / 1.00 | 1.09 / 1.20; 1.11 / 1.16 |
| Resource, deep, 25 / 500 | 160.0 / 172.1; 185.5 / 198.0 | 152.3 / 162.3; 181.9 / 195.3 | 0.95 / 0.94; 0.98 / 0.99 | 1.07 / 1.14; 1.07 / 1.08 |
| Descriptor, 25 | 94.4 / 100.9 | 93.0 / 102.1 | 0.99 / 1.01 | 1.05 / 1.06 |
| Descriptor, 500 | 99.0 / 107.1 | 98.1 / 104.5 | 0.99 / 0.98 | 1.11 / 1.19 |
| Cache hit, 25 | 187.0 / 201.7 | 182.4 / 193.5 | 0.98 / 0.96 | 1.05 / 1.07 |
| Cache hit, 500 | 287.0 / 734.4 | 257.2 / 271.5 | 0.90 / 0.37 | 1.20 / 3.09 |
| Forced miss, 25 (complete) | 5,292 / 5,742 | 5,282 / 5,338 | 1.00 / 0.93 | 1.05 / 1.24 |
| Forced miss, 500 (complete) | 5,335 / 5,399 | 5,326 / 5,379 | 1.00 / 1.00 | 1.05 / 1.05 |

| Replay | Rows | c = 1 p50 / p95 ratio | c = 32 p95 ratio | c = 32 throughput ratio | Result |
|---|---|---|---|---|---|
| Descriptor | 25 | 0.47 / 0.45 | 0.05 | ×2.15 | pass |
| Descriptor | 500 | 0.93 / 0.90 | 0.11 | ×2.25 | pass |
| Candidate | 25 | 0.52 / 0.49 | 0.06 | ×2.68 | pass |
| Candidate | 500 | 0.76 / 0.78 | 0.31 | ×1.89 | pass |

- Every API cell meets non-regression. The forced-miss cells sit at the 5 s direct-fill timeout in
  both variants (see [Method and environment](#method-and-environment)); the cache-hit 500-row
  `B` p95 (734 ms, CV 104%) comes from one session's outliers.
- The descriptor and candidate batches read the carrier in only four and two statements, against
  about eleven in the hydration batches (statement counts above), so their extra parses cost less
  than the create, drop, insert, and latch they lose: 0.45–0.93 at
  concurrency 1, and ×1.9–×2.7 throughput at 32. Their logical reads fall (descriptor 6,607 →
  5,755 at 500; candidate 4,066 → 3,276), because the `#page` pages are gone.
- **Tail latency at 32 sessions.** p99 is not a gate, but it is reported: `I`'s p99 at 32 is higher
  than `B`'s on the 500-row candidate (314 against 71 ms), resource (1,317 against 778 ms), and
  fallback (1,572 against 199 ms) batches, while its p95 is lower on all but the fallback. Every
  other replay's p99 falls. At concurrency 8, both variants show a p99 near 100–200 ms on several
  25-row batches, so part of the tail is the host and the client, not the variant.

### What task 6 shows for Gate 2 (SQL Server)

| Gate 2 criterion | Result |
|---|---|
| Lifecycle removed on every remaining path (calibrated XE count) | Met: 0 temp tables in all 24 `I` windows (`B`: exactly 1 per request) |
| No hydration recompiles after warmup | Met (and also true of `B`): 0 XE recompiles, 0 plan generations |
| Latency p50 and p95 at c = 1 ≤ 1 + m, every path | **Not met**: resource 500 (1.17 / 1.15 against 1.09 / 1.10) and fallback 500 (1.34 / 1.33 against 1.05); every other batch and every API cell is met |
| p95 at c = 32 ≤ 1 + m and throughput at c = 32 ≥ 1 − m, every path | **Not met** for fallback 500 (p95 1.14 against 1.05, throughput ×0.79 against 0.95); met everywhere else, with ×1.2–×3.3 throughput |
| No type (b) failure in task 7 | Met: none on either engine (see [Correctness validation](#correctness-validation)) |

`inline` therefore fails Gate 2 on SQL Server, at 500 rows on the hydration batches only, and task 8
screens derived forms for SQL Server. The measurements point at what a form must do: parse the
page once per batch into an ordered, indexed row source, without the `#page` create-and-drop
latch cost. The plan already lists a table variable (it still uses tempdb metadata, so its latch
behavior at 32 sessions must be measured), client-held ids through an `OPENJSON` parameter,
`VALUES`, and a TVP. The 25-row results, and every descriptor and candidate result, show what
removing the temp table is worth on SQL Server: the latch contention at concurrency, not a plan or
recompile problem.

## Alternatives (measured or reasoned)

Written by task 8. **PostgreSQL** passes Gate 2 with `inline` (tasks 5 and 7), so its alternatives
get reasoned dispositions and are not measured. **SQL Server** fails Gate 2 with `inline` at
500 rows (task 6), so task 8 screened derived forms there. One form, **a memory-optimized table
variable (`memtablevar`)**, passes every SQL Server screening criterion. It is an
**unvalidated lead** until it completes the three validation steps below.

### PostgreSQL: reasoned dispositions (not measured)

| Option (ticket) | Disposition |
|---|---|
| `VALUES` list | Not needed. It costs two parameters per id (1,000 at 500 rows, against the 2,100-parameter budget SQL Server shares), its text grows with the page size so each page size compiles its own plan, and it repeats the list in every keyset statement. `inline`'s selected page binds one `bigint[]` instead (task 4) |
| TVP | SQL Server only |
| Table variables (plain or memory-optimized) | SQL Server only |
| Session temp table (`ON COMMIT DELETE ROWS`) | Still a catalog object per session, with no statistics until something analyzes it (the task 3 hypothesis), and session state that a transaction-mode pooler (PgBouncer) can hand to another client. `inline` has neither problem |
| Two-phase (select ids, then hydrate) | An extra round trip, against `partitioned-cursor-paging.md` invariant 3 ("No extra roundtrip"); `inline` keeps one round trip |

### SQL Server: screening

**Method.** `KeysetForms` (in [`experiments/KeysetSpike`](experiments/KeysetSpike)), fixture
`Given_Mssql_KeysetFormsScreening`, run with `Invoke-KeysetSpike.ps1 -Engine mssql -Sequence B0
-Fixtures forms` on pristine `main` (no patch). It loads `primary-500k` on a fresh lease and takes
the task 6 captures (`t6s1`) of the two failing batches, the resource GET-many query keyset and
the selected-page fallback, at 25 and 500 rows. Each form is a text transform of the `baseline`
batch:

- `tablevar`: `#page` becomes `DECLARE @page TABLE (…)` with the same columns and primary key.
- `memtablevar`: `DECLARE @page dbo.dms1521_PageKeysetMem`, a memory-optimized table type with a
  nonclustered primary key; the run provisions a `MEMORY_OPTIMIZED_DATA` filegroup and the type.
- `values`: each keyset join reads a parameterized `(VALUES (@k0, @o0), …)` list.
- `tvp`: each keyset join reads a table-valued parameter of a type the run creates.

A selected page already holds its ids, so `values` and `tvp` run in one round trip there. A query
keyset selects its page inside the batch, so for those two forms the resource path runs in two
phases on one connection (select the ids, then hydrate), which breaks invariant 3. `baseline` and
`inline` replay their captures unchanged; `inline` is the control.

Each form runs once to check its result sets against `baseline` (row count and a SHA-256 of the
sorted rows, per result set, both phases). It then runs once after `ALTER DATABASE SCOPED
CONFIGURATION CLEAR PROCEDURE_CACHE` for first-execution latency. Finally it replays in 3 rounds at
concurrency 1, 8, and 32, with the form order rotated each round. Counter windows (calibrated XE
count, `Temp Tables Creation Rate`, `PAGELATCH`, recompiles) run in round 1 at concurrency 1 and
32. Ratios are form / `baseline` in the same run, with `m = max(2 × CV(baseline across the 3
rounds), 5%)`.

Runs: `t8-mssql-forms-B01-20261112143320` (`baseline`, `inline`, `tablevar`, `values`, `tvp`;
51 min) and `t8mem-mssql-forms-B01-20261112155036` (`baseline`, `inline`, `memtablevar`; 36 min),
plus the `t8smoke` and `t8memsmoke` smokes. Analysis: `t8_analyze.py`, reports `t8_report.md` and
`t8mem_report.md`. Every form returned exactly `baseline`'s result sets at both page sizes, with 0
replay errors.

**SQL Server counts a table variable as a temp table, and the XE gate cannot see it.** A probe on
`dms-1521-mssql` found that 10 and 20 batches that each declare a table variable add exactly 10
and 20 to `Temp Tables Creation Rate`, while plain batches add 0. No `object_created` event fires for
a table variable. Every `tablevar` replay window confirms it: 1.00 temp table per request, against
0 in the XE count. A table variable therefore does not remove the temp-table lifecycle, and the
XE count alone would have shown a false zero. The windowed table-variable calibration in the two
runs read 16 and 20–22 for 10 and 20 batches, so it is not proportional. The counter is server-wide,
and those windows settle on the XE count only, so the manual probe and the 640-request replay
windows are the evidence here.

| Form | Temp tables per request (SQL Server's counter) | `PAGELATCH` waits per request at 32 sessions | Latency and throughput (16 gate criteria) | SQL length at 500 rows | First execution at 500 rows (resource / fallback) |
|---|---|---|---|---|---|
| `baseline` | 1 | 11–18 | – | 6,028 / 6,057 | 62–65 / 61–65 ms |
| `inline` (control) | 0 | 0 | 11–12 of 16; fails 500 rows at concurrency 1 (1.18–1.36), as in task 6 | 7,822 / 7,538 | 60–64 / 63 ms |
| `tablevar` | **1** | **17–23** (worse than `baseline`) | 14 of 16; fails 25-row resource at 32 sessions (p95 ×2.67, throughput ×0.69) | 5,929 / 5,958 | 96 / 102 ms |
| `values` | 0 at 32 sessions (about 1 in the concurrency-1 windows at 500 rows, with recompiles) | 0 | 13 of 16; fails 500 rows (fallback p95 1.28 at c = 1; resource p95 1.13 and throughput ×0.87 at 32, two-phase) | 200,630 (two-phase) / 114,911; up to 1,000 parameters | 335 / 316 ms |
| `tvp` | 0 | 0 | 2 of 16; 1.5–3.7× slower, p95 about ×3 at 32 sessions | 5,890 (two-phase) / 5,711 | 95 / 94 ms |
| **`memtablevar`** | **0** | **0** | **16 of 16** | 5,895 / 5,924 | 84 / 88 ms |

**`memtablevar` in detail** (run `t8mem`, mean of 3 rounds; ratios to `baseline`):

| Batch | Rows | p50 / p95 ratio at c = 1 | Bound | p95 ratio at c = 32 | Throughput ratio at c = 32 | Logical reads per request, `baseline` → `memtablevar` |
|---|---|---|---|---|---|---|
| Resource | 25 | 0.57 / 0.55 | 1.17 / 1.35 | 0.24 | ×3.19 | 1,690 → 1,619 |
| Resource | 500 | 0.91 / 0.87 | 1.09 / 1.13 | 0.68 | ×1.36 | 32,847 → 32,141 |
| Fallback | 25 | 0.68 / 0.55 | 1.24 / 1.54 | 0.45 | ×2.40 | 1,684 → 1,970 |
| Fallback | 500 | 0.96 / 0.90 | 1.05 / 1.23 | 0.83 | ×1.18 | 32,998 → 34,319 |

Recompiles are 0 in every `memtablevar` window. A memory-optimized table variable lives in memory,
not tempdb, so it has no catalog work and no latch. Its primary key keeps the page parsed once and
ordered, which is the property task 6 found `inline` lacks.

**What `memtablevar` costs:**

- **Provisioning (DDL).** Every database needs a `MEMORY_OPTIMIZED_DATA` filegroup with a file, plus
  `CREATE TYPE … AS TABLE (…) WITH (MEMORY_OPTIMIZED = ON)`. The run's provisioning took 1.2 s on an
  empty filegroup. Whether the filegroup can be removed again depends on the version:
  - **SQL Server 2022 and older:** only by dropping the database.
  - **SQL Server 2025 and later:** only after every In-Memory OLTP object is dropped (here, the
    table type). Removing the last container is an undeploy that waits for the log to advance:
    `CHECKPOINT`, plus log backups under full recovery. Every availability replica must apply the
    undeploy record, and the database must have no snapshots. `REMOVE FILEGROUP` follows. See
    Microsoft Learn,
    [The memory-optimized filegroup](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/the-memory-optimized-filegroup?view=sql-server-ver17)
    and
    [Memory-optimized container and filegroup removal](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/memory-optimized-container-filegroup-removal?view=sql-server-ver17).
  - **Probe** on `dms-1521-mssql` (SQL Server 2025 RTM-CU7, 17.0.4065.4; 2026-11-12), using the
    runner's provisioning SQL:
    - With the type present, `REMOVE FILE` failed with Msg 41880 ("There are in-memory database
      objects present").
    - After `DROP TYPE`, it waited in undeploy state 3 until `CHECKPOINT`s issued from another
      session advanced the log.
    - It then removed the file, the engine reported "not deployed", and `REMOVE FILEGROUP`
      succeeded.

  This is a provisioning change, so the AGENTS.md `RelationalMappingVersion` cadence applies when it
  lands (task 9), and existing databases would need re-provisioning or a migration step.
- **Memory.** The XTP memory clerks held 52,848 KB after the run, with 32 concurrent sessions at
  500 rows.
- **First execution** at 500 rows is 84–88 ms, against 61–65 ms for `baseline` (compile).
- **Availability** must be confirmed for every supported SQL Server edition and hosting tier before
  a story adopts it (task 9). Not established here: In-Memory OLTP is not available in every
  managed tier.
- **Isolation.** The screening replays run in autocommit. Production hydration runs inside
  transactions under RCSI and snapshot isolation, which memory-optimized objects restrict, so
  step 3 below must exercise those paths.

### Status and the validation `memtablevar` still needs

Screening proves only that the transformed SQL is fast and returns the same rows. Under the plan,
`memtablevar` can be recommended only after:

1. it is added to `prototype.patch` as a `DMS1521_KEYSET_VARIANT` value (all four paths, including
   candidate metadata and `descriptor_page`), with the provisioning in the test and perf fixtures,
   and the `baseline` byte-identity check still passing;
2. it is measured end to end in an interleaved task 6 session on SQL Server (`B`, `M`, three
   sessions, all three fixtures, including the descriptor, candidate, cache-hit, and forced-miss
   cells); and
3. it passes task 7 validation on SQL Server, with the failure classification.

Until then it is an unvalidated lead, and no story adopts it.

**Decision (2026-11-12, the spike owner):** `memtablevar` is recorded as an unvalidated lead, and
steps 1–3 are not run in this spike. Its provisioning (a filegroup, which SQL Server 2022 and older
can never remove and SQL Server 2025 can remove only through the undeploy procedure, and a new type),
its memory, and its availability across supported editions and tiers are product decisions to take
at the task 11 review before more machine time is spent. SQL Server therefore ends this spike
with **no validated replacement**: `#page` stays, and task 9 gives the AC5 explanation and the AC6
documentation of the retained temp table, with `memtablevar` listed as the lead together with its
screening numbers and the three missing steps. Task 8 is complete.

Task 9 later narrowed this. `inline` passed every criterion on SQL Server's candidate-metadata (P2)
and descriptor (P4) paths, so it is recommended there. "No validated replacement" applies to P1
and P3, where `#page` stays (see [Recommendation](#recommendation)).

## Correctness validation

Written by task 7. **`inline` changes no behavior on either engine: no type (b) failure.** Every API
integration test passes on both engines. Every remaining failure is a pin of the temp-table text or
mechanism (type (a)), or a test double that rewrites keyset batches (type (d)), which the patch's
test section now adapts. There are no type (c) failures.

### Runs

`prototype.patch` applied, `DMS1521_KEYSET_VARIANT=inline` set in PowerShell for every test host
(lane script `t7-lanes.ps1`, attached by task 12), one engine at a time. The API suite runs under
the harness defaults (locking `READ COMMITTED`), complementing the RCSI performance runs.

**Deviation: CI-matched containers.** The lanes ran on two temporary containers that copy CI's
settings rather than on the spike containers. They use the same pinned image digests, without tmpfs
and with an 8 GB memory limit, and were removed after the run:
- PostgreSQL (`.github/actions/start-postgresql-test-container`): `max_locks_per_transaction=256`,
  `wal_level=logical`, `max_replication_slots=10`, trust authentication.
- SQL Server (`start-mssql-test-container`): SQL Server Agent enabled, `MSSQL_MEMORY_LIMIT_MB=4096`,
  and the `backup-restore` lease strategy.

The spike containers lack `wal_level=logical` and the Agent, which CDC-dependent tests need; using
them would have produced environmental failures that say nothing about the prototype.

| Lane | Engine | Passed / total | Failures by type |
|---|---|---|---|
| `Backend.Plans.Tests.Unit` (full) | none | 2,580 / 2,671 | 91 (a), identical to task 4's sizing |
| `Backend.Tests.Unit` (full) | none | 4,750 / 4,771 | 21 (a), identical to task 4's sizing |
| `Frontend.AspNetCore.Tests.Unit`, `RelationalWriteSmokeTests` (C5) | none | 9 / 9 | – |
| API integration (`Category=ApiIntegration&Category=PostgresqlIntegration`, full) | PostgreSQL | 486 / 486 | – |
| Backend read-path integration (23 `Given_` classes) | PostgreSQL | 176 / 179 | 2 (d), 1 (a) |
| API integration (`Category=ApiIntegration&Category=MssqlIntegration`, full) | SQL Server | 491 / 492 (1 skipped) | – |
| Backend read-path integration (29 `Given_` classes, `MssqlWriteSessionCommandStreamTests` included) | SQL Server | 192 / 195 | 2 (d), 1 (a) |
| Re-run of the adapted class under `inline`, then `baseline` | PostgreSQL | 6 / 6, then 6 / 6 | – |
| Re-run of the adapted class under `inline`, then `baseline` | SQL Server | 6 / 6, then 6 / 6 | – |

Run ids: `t7-unit-inline`, `t7-pg-inline`, `t7-mssql-inline`, `t7-rerun-pg`, `t7-rerun-mssql`. The
one SQL Server skip is a pre-existing `[Ignore]` (`Given_Mssql_CaseVariantIdentity`, citing
DMS-1624). The SQL Server lane's 9.5-hour gap in its log, between the two suites, was the console
paused in selection mode, not test time: the backend suite ran 01:49–02:01 against a container that
had not restarted.

### Failure classification

| Test | Engines | Type | Why |
|---|---|---|---|
| 109 of the 112 unit failures (69 batch and dialect text pins, 18 descriptor text pins, 14 parameter-count pins, 8 goldens) | – | (a) | Pins of the old SQL text or parameter list; the same classes and counts as task 4 (c) |
| 3 unit tests of a guarded single-document keyset without the fast path (`Given_HydrationBatchBuilder_With_A_Single_Document_Keyset` ×2, `…With_Mssql_Single_Document_Fast_Path` flag-disabled) | – | (a), story scope | The prototype throws `NotSupportedException` on this non-production path (every production `PageKeysetSpec.Single` caller passes the fast path, C5); the story implements or removes it |
| `Given_A_{Pgsql,Mssql}_Query_Keyset_That_Returns_Its_Selected_Ids.It_returns_the_maximum_when_every_selected_row_was_deleted_before_hydration`, and the `content_version` variant | both | (d) | The test splices `DELETE`s after the temp-table `INSERT … RETURNING` / `OUTPUT` text, which `inline` does not emit. Adapted (below); passes under both variants |
| `Given_Two_Postgresql_Hydration_Batches_On_The_Same_Transaction.It_keeps_the_pgsql_temp_table_alive_until_commit_between_hydration_calls` | PostgreSQL | (a) | Asserts that `page` exists in `pg_class` mid-transaction: a pin of the removed mechanism. Its behavioral sibling in the same fixture (two hydrations in one transaction each return their own document) passes. It hydrates `PageKeysetSpec.Single` with the fast path off, which binds the `bigint[]` and never sets or reads the carrier, so it says nothing about the carrier within one transaction; the stories add that test |
| `Given_HydrationExecutor_Single_Document_Fast_Path_With_DescriptorProjection_And_DocumentReferenceLookup_Mssql.It_generates_a_keyset_batch_that_does_use_the_page_temp_table` | SQL Server | (a) | Pins the `#page` text of the comparison (non-fast-path) batch. The class's behavior tests pass, including `It_matches_the_existing_keyset_hydration_result` |

**Type (d) adaptations** (the patch's test section, marked `DMS-1521 inline (test adaptation)`):

- `AuthoritativeTimestampReadConsistencyScenario`, both split sites (task 4): on SQL Server the
  double carries `@page_keyset` across the split. Exercised here: 31 PostgreSQL and 62 SQL Server
  tests of the scenario pass.
- `HydrationExecutorTests` on each engine (new in task 7): under `inline` the delete is spliced after
  the statement that selects the page and sets the carrier (PostgreSQL: the batch's first statement;
  SQL Server: the selected-keys `SELECT … FROM OPENJSON(@page_keyset) … k;`). That is still between
  selection and the hydration selects, so the tests keep checking what they were written for: when
  every selected row is deleted before hydration, the batch returns no body and the selected
  anchor still arrives. `baseline` keeps the original splice point.

The patch was re-exported with the two new diffs; its other sections are byte-identical to the
task 6 version, and it applies and reverses cleanly.

### C1–C6 matrix

| Requirement | Evidence under `inline` | Result |
|---|---|---|
| C1 page membership and order | API: `CursorPagingExecutionScenario` (7 per engine), `CursorPartitionAuthorizationMatrixScenario` (11), `Given_*_DocumentCacheReadAcceleration` (8); backend: `HydrationExecutorTests`, `*RelationalQueryExecutionTests`, `*RelationalQueryAuthorizationTests`, `*WindowedPageQueryPlanTests`, and `Given_A_Postgresql_Selected_Page_Keyset_With_Nonascending_DocumentIds`; one command per request in every task 5 and 6 run | Met, both engines |
| C2 reconstruction | `HydrationExecutorTests` (child and nested-child rows), `*DocumentReferenceLookupPageTests`, `*PageLinkInjectionIntegrationTests`, `DescriptorRuntimeScenario` (24 per engine), `CompactDescriptorResourceScenario` (10), `*DescriptorReadQueryFilterTests` | Met, both engines |
| C3 counts, empty and zero pages, anchors | `LimitZeroCountReadScenario` (18 per engine), `CursorEmptyHydrationScenario`, `WindowedCursorAnchoringScenario` (8), `CursorPublicContractScenario` (8 / 3), the `Given_HydrationReader_With_A_Selected_Keyset_Result_Set` and `…Executor…` unit tests (16), the adapted deleted-before-hydration anchor tests, and the task 4 carrier probes (113 / 113) | Met, both engines |
| C4 concurrency, authorization, isolation | `CursorPartitionConcurrencyScenario` (3 per engine), `AuthoritativeTimestampReadConsistencyScenario` (31 / 62, with the type (d) adaptation), `*RelationalOwnershipQueryAuthorizationTests`, `*DescriptorQueryOwnershipAuthorizationTests`; zero replay errors at 32 sessions in tasks 5 and 6 | Met, both engines |
| C5 fast path | Every fast-path-enabled single-document unit pin passes unchanged; `RelationalWriteSmokeTests` 9 / 9; the `DocumentCacheMaterializer*Tests` classes pass in full; the `*Single_Document_Fast_Path*` and `Given_A_Single_DocumentId_Keyset*` integration classes pass, apart from the one type (a) comparison-batch pin; `MssqlWriteSessionCommandStreamTests` passes | Met, both engines |
| C6 everything changes together | Story scope: the 101 unit text and parameter pins and 8 goldens (2,494 golden lines), the 3 guarded single-document cases, the 2 integration mechanism pins, and the type (d) adaptations at 4 sites (2 in `AuthoritativeTimestampReadConsistencyScenario`, 1 in each engine's `HydrationExecutorTests`). `DescriptorReadCommandExecutor` needs no change, because the descriptor markers are unchanged | Sized |

## Recommendation

Written by task 9. The recommendation is made per engine and per path. An engine's path is
recommended only if `inline` met every Gate 2 criterion on it and task 7 found no type (b) failure.
Unvalidated leads are listed separately and do not become story scope.

### PostgreSQL: `inline` on all four paths

**Mechanism** (as prototyped; see [Candidates and representative SQL](#candidates-and-representative-sql)):

- The statement that selects a query keyset (P1, P2, P4) stores the selected ids in the
  transaction-local setting `dms.page_keyset` with `set_config(…, true)`. It also returns the
  selected keys, and the anchor where there is one, as the batch's first result set, in the same
  position and shape as before. An empty or zero-size page sets the carrier to `''`.
- A selected page (P3) binds one `bigint[]` parameter, `@page_document_ids`, instead of two
  parameters per id.
- Every keyset reader joins a relation that unnests
  `COALESCE(@page_document_ids, carrier) WITH ORDINALITY`, rendered through
  `PlanSqlWriterExtensions.AppendRelation`. Query keysets keep a NULL ordinal; selected pages keep
  the caller's order.
- The descriptor batch selects its page in the boundary statement, which sets the carrier and
  returns `MAX(anchor)`. Its `-- Descriptor …` markers do not change.
- Version fences, retries, readers, and the single-document fast path do not change.

**Why, in AC5 order:**

1. **Removes the per-operation create and drop.** The calibrated observer reads 0 catalog tuples and
   0 WAL per request on all four batch kinds, in all 24 `I` windows. `main` reads exactly 44 tuples
   (42 for `descriptor_page`) and about 10 KB of WAL per request.
2. **Improves hydration.** Resource GET-many API p50 and p95 fall to 0.29–0.48×. Child-collection
   time falls to 0.002–0.021× and child buffers to 0.023–0.466×, at 25 and 500 rows. The 500-row
   fallback batch replay reaches 0.13× at concurrency 1 and ×5.8 throughput at 32. The plans move
   from `Seq Scan` to `Index Scan` on every child table (960 of 960 nodes).
3. **SQL Server under concurrency** is unaffected. The PostgreSQL change is dialect-specific.

`inline` also beats the `ANALYZE` mitigation on every point except simplicity. `ANALYZE` recovers
most of the hydration gain (API 0.31–0.48×), but it keeps the lifecycle, adds `pg_statistic` writes
and about 1.3 ms per batch, and regressed the 25-row candidate batch under concurrency
(×0.82–0.84 throughput). It also cannot run in a read-only transaction.

**Read-only transactions (EDFI-2956).** A probe on `dms-1521-postgresql` (PostgreSQL 18.6, 2026-11-12)
set a database's `default_transaction_read_only = on`. In it, `main`'s keyset prelude failed with
`cannot execute DROP TABLE in a read-only transaction` (SQLSTATE 25006). `inline`'s selecting
statement, a carrier-reading join, and a `bigint[]` selected-page join all succeeded, returning 25,
25, and 3 rows. The probe covers a read-only transaction on a primary; it was not run on an Aurora
reader or a hot standby. `set_config` and `current_setting` write no WAL and are allowed during
recovery, but story 1 must test the read-only case itself (see [Thresholds](#thresholds), E4).

**Provisioning: none.** `inline` adds no DDL, extension, or server setting; a custom setting with a
dotted name needs no registration. Plan SQL is not an input to `EffectiveSchemaHash`, so no
`RelationalMappingVersion` bump and no re-provisioning are needed (the DMS-1399 precedent).
`mpack-format-v1.md` describes the keyset contract and is updated with it (see
[Design-document impact](#design-document-impact)). No pack needs rejecting: pack emission is not
implemented yet (`buildMappingPack=true` throws `NotSupportedException`), so no pack was built with
the old contract.

**Parameter binding:**

| Batch | `main` | `inline` |
|---|---|---|
| Resource (query keyset) | query and authorization parameters, `limit`, `offset` | the same, plus `@page_document_ids` bound as an untyped NULL (the SQL's `CAST(… AS bigint[])` types it) |
| Candidate metadata | `limit`, `offset` | the same, plus the NULL `@page_document_ids` |
| Selected page | 2 per id: 50 at 25 rows, 1,000 at 500 | 1 `bigint[]` at every page size; one statement text for every page size |
| Descriptor | `resourceKeyId`, `limit`, `offset` | unchanged |
| Single-document fast path (C5) | unchanged | unchanged: the array is bound only when the batch text reads it |

The 2,100-parameter limit is SQL Server's. On PostgreSQL the selected page now uses one parameter
at `MaximumPageSize` (500) instead of 1,000. The batch text is longer: the relation repeats about
330 characters per keyset join, and the resource batch grows from 7,774 to 14,922 characters.

**Prepared-plan behavior** (from `pg_prepared_statements` on the probe's own connection, and
`auto_explain`; never inferred from execution counts):

| Statements | Class | Custom plan | Generic plan | What `plan_cache_mode = auto` chose |
|---|---|---|---|---|
| Resource and selected-page keyset readers | Parameterized (`@page_document_ids`) | Estimate exact: `current_setting` is stable and folded at planning, and each statement in the pipelined batch is planned after the carrier is set | Fixed estimate of 10 rows | 5 custom plans per statement, then generic (probe: 50 custom, then 20 generic) |
| Query-keyset selecting statement | Parameterized (`limit`, `offset`, filters) | as in `main` | as in `main` | custom |
| Descriptor keyset readers | Carrier-only (no parameters) | never used | Always generic; estimate frozen at the carrier's size when the statement was first planned on that connection | generic (42 of 42) |

- **Both fixed estimates are cheap on `primary-500k`.** In the fallback window on one physical
  connection, child cost per request was 0.30 ms under the first 5 replays' custom plans and
  0.27 ms under the generic plan at 25 rows. At 500 rows it was 3.54 and 3.57 ms. Both used
  `Index Scan` throughout. At an estimate of 10 against 500 actual keys, an index probe per key
  still beats a full scan of a 500,000-row child table.
- **Re-planning falls.** `main` plans 0.91–1.02 statements per call, because each recreated temp
  table invalidates the prepared statements. `inline` plans 0.09–0.29 per call on the resource
  batch, 0.25–0.26 on the descriptor batch, and 0.50–0.65 on the candidate batch. The 25-row
  fallback window reads 1.00 because no pooled connection had auto-prepared its statements yet
  (see [Plans and planning](#plans-and-planning)); on one connection they go generic after five
  executions, like the other parameterized readers. Plan time per request falls by 5–94%.
- **The API path ran on a new backend per request** in both variants, so its plans were all custom
  plans. The cause was not established; it affects `B` and `I` equally. The pooled replays and the
  one-connection fallback window supply the generic-plan evidence. Story 1's evidence must show
  child cost under both plan types (the Testing Strategy requirement).
- **Residual risk, descriptor path.** A connection whose first descriptor page is small keeps that
  estimate for later pages. The descriptor statements join small descriptor tables by key, and no
  measured cell regressed, but no run planned a 1-row page and then read a 500-row page on the same
  connection. Story 2 adds that sequence to its performance evidence (reported, not a CI test).

**Correctness.** No type (b) failure (task 7): 486 of 486 PostgreSQL API integration tests, and the
backend read-path suite except 2 type (d) and 1 type (a). Carrier probes: 113 of 113. These cover
no stale ids on a reused connection with `NoResetOnClose`, no leak after a failed batch, retries,
and every `plan_cache_mode`. C1–C5 are met; C6 is story scope (see
[C1–C6 matrix](#c1c6-matrix)).

**Retained fallback: none.** No PostgreSQL path keeps a temp table.

**Carrier acceptance.** The plan reserves the production acceptance of the transaction-local carrier
for the task 11 review (resolved decision 2). Its evidence is in the task 4 probes, task 5, and
task 7. A transaction-mode pooler is not a hazard for it: the setting is transaction-local and is
always set before it is read within the same transaction. A session-level temp table would be a
hazard there.

### SQL Server: `inline` on descriptor and candidate metadata; `#page` stays on P1 and P3

**Mechanism.**

- **P4, descriptor GET-many.** Selection fills `@page_keyset nvarchar(max)` with
  `FOR JSON PATH`, where `#descriptor_page` was filled. The fences, owner rows, and boundary read
  it through `OPENJSON`.
- **P2, candidate metadata.** The same carrier, selected once in the batch. The batch keeps its
  three result sets.
- **P1, resource GET-many, and P3, selected-page fallback** keep `#page`, byte-identical to `main`.
  The compiled `SelectByKeysetSql` joins stay on `[#page]`.

P2's keyset relation therefore has to be rendered for the candidate batch only, not through the
dialect-wide keyset contract that P1 and P3 compile into the mapping set. The prototype rendered
both through `AppendRelation` and gives no guidance on this point; story 1 designs it.

**Why, in AC5 order.**

1. **The create and drop go where nothing regresses.** P2 and P4 create 0 temp tables in all their
   `I` windows, measured with the calibrated XE count. Their `PAGELATCH` contention (12.7–17.2
   waits and 8–62 ms per request at 32 sessions) is gone. Replay p50 and p95 at concurrency 1 fall
   to 0.45–0.93×, throughput at 32 rises ×1.89–×2.68, and logical reads fall (descriptor 6,607 →
   5,755 at 500 rows; candidate 4,066 → 3,276). Every API cell stays within its non-regression
   bound. One cost is not gated: the candidate batch's p99 at 500 rows and 32 sessions rises from
   70.8 to 314 ms (×4.4, consistent across the three sessions, CV 16%) while its p95 falls to
   0.31×. Story 1 reports that p99 beside N8, so its PR shows whether the production design keeps
   the tail.
2. **Hydration is not a SQL Server problem.** `#page` has statistics (estimate 25), every child
   statement seeks in both variants, and neither variant recompiled after warmup. The DMS-1248
   recompile concern did not occur on these paths.
3. **P1 and P3 keep `#page` because `inline` fails this priority there.** At 500 rows every keyset
   statement re-parses the JSON carrier, about 1–2 ms of CPU each. The resource batch costs
   1.17/1.15× at concurrency 1, and the fallback batch 1.34/1.33× at concurrency 1 and 1.14× p95
   with ×0.79 throughput at 32. Removing `#page` there would trade tempdb latch contention for a
   CPU regression on the busiest path. AC5 allows an engine to keep its temp table when the evidence
   does not justify a fix, as long as the reason is explained. Here, the only measured removal that
   avoids the regression (`memtablevar`, below) is unvalidated and needs DDL.

**Provisioning: none** for the recommended paths.

**Parameter binding.** Unchanged on every path. P2 binds `offset` and `limit`, P4 binds
`resourceKeyId`, `offset`, and `limit`, and P3 keeps its one `nvarchar(max)` JSON parameter at every
page size. Nothing approaches the 2,100-parameter budget at `MaximumPageSize`.

**Plan behavior.**

- Each batch is one `sp_executesql` entry, reused from the plan cache. There were 0
  `sql_statement_recompile` events and no change in `plan_generation_num` after warmup, in both
  variants.
- `OPENJSON` keeps a fixed estimate of 50 rows per execution, at every page size, with no
  statistics to go stale.
- `#page`'s plan, which stays on P1 and P3, is compiled for the first page (estimate 25) and reused
  at 500 rows without a recompile. Its child statements seek in both cases.

**Read-only behavior.** A readable secondary permits `#` temp tables in tempdb, so the retained
`#page` does not reproduce EDFI-2956 on SQL Server. P2 and P4 under `inline` issue no DDL at all.

**Correctness.** No type (b) failure: 491 of 492 SQL Server API integration tests passed (one
pre-existing `[Ignore]`), and the backend read-path suite passed except 2 type (d) and 1 type (a).
The type (d) adaptation of `AuthoritativeTimestampReadConsistencyScenario` carries `@page_keyset`
across the test's split command. Story 2 needs it at the descriptor split site; the resource
site keeps `#page` on SQL Server.

**Combined forced-miss request.** Under this recommendation, a forced-miss request runs the `inline`
candidate batch, then the `#page` fallback batch. That makes one temp table per request instead of
two. The two batches are separate commands and were each measured, but not this combination; story
1's evidence covers it.

**Support floor.** SQL Server 2025 (RTM-CU7) is the tested environment. The supported minimum is
unconfirmed, and that does not block this recommendation. `OPENJSON` and `FOR JSON PATH` are
SQL Server 2016-level constructs (compatibility level 130), already required by `main`'s selected
page. Using them is a conservative choice; it is not evidence that SQL Server 2016 is supported.

**Unvalidated lead for P1 and P3: `memtablevar`.** A memory-optimized table variable passed 16 of 16
screening criteria. It created 0 temp tables, took 0 latch waits, and had 0 recompiles. Its p50 and
p95 at concurrency 1 were 0.55–0.96×, and its throughput at 32 sessions ×1.18–×3.19. Screening does
not exercise binding, readers, anchors, retries, or isolation, and it has not completed the three
steps under
[Status and the validation `memtablevar` still needs](#status-and-the-validation-memtablevar-still-needs).
Adopting it would also need:

- **Re-provisioning:** every database needs a `MEMORY_OPTIMIZED_DATA` filegroup and a
  memory-optimized table type. SQL Server 2022 and older can never remove the filegroup; SQL Server
  2025 can, only after dropping the type and completing the undeploy procedure (see
  [What `memtablevar` costs](#sql-server-screening)). Existing databases would need
  re-provisioning or a migration step.
- **A `RelationalMappingVersion` decision** under the AGENTS.md cadence when it lands, separate from
  re-provisioning: `v3` if it lands before 8.1 ships, the `v4` candidate after, and never a second
  bump within a release line. This spike prescribes no bump.
- **Memory:** 52.8 MB of XTP memory at 32 sessions × 500 rows.
- **Availability:** In-Memory OLTP must be confirmed for every supported edition and hosting tier,
  and the RCSI and snapshot transaction paths must be exercised.

The task 11 review decides whether to spend more machine time on it. Until then, no story adopts it.

### Interim PostgreSQL mitigation (`ANALYZE`)

Task 3's mitigation verdict is favorable, and PostgreSQL has a validated replacement. Under the
plan, the conditional story 3 qualifies only if story 1 cannot ship in 8.2. **Not recommended
otherwise**:

- It keeps the lifecycle that AC5 ranks first.
- It adds `pg_statistic` writes.
- It regressed the 25-row candidate batch under concurrency.
- It fails on read-only connections (EDFI-2956).

If the task 11 review finds that story 1 cannot ship in 8.2, the mitigation should be limited to
the hydration and descriptor batches, not the candidate batch. The single-run task 3 evidence
supports that limit.

### Story split and sizing

| Story | Scope | Sized from | Proposed estimate |
|---|---|---|---|
| 1. Resource multi-document hydration without a keyset temp table | PostgreSQL P1, P2, P3; SQL Server P2. The shared keyset-contract work: contract, `AppendRelation`, batch builder, binding, the guarded single-document keyset without the fast path (implement or remove; the prototype throws), goldens, and design docs | Unit churn (task 4 (c), task 7). Of the 112 failing unit tests, about 60–70 are PostgreSQL text or parameter pins. The SQL Server hydration pins stay, because P1 and P3 keep `#page`, and the 18 descriptor pins move to story 2. The 8 manifest goldens hold both dialects (2,494 changed lines across both; about half are PostgreSQL's). Also 1 PostgreSQL integration mechanism pin, and the type (d) splice in `HydrationExecutorTests` on PostgreSQL. New tests: deterministic no-temp-table integration tests (`pg_stat_xact_sys_tables` in a test-owned transaction, with a positive control; SQL Server batch-text check), carrier reuse in one transaction and failure, `MaximumPageSize` binding, read-only GET-many, and two pinned orderings. Evidence: three interleaved PostgreSQL sessions (`main` and the story branch, about 2.5 h of machine time) and the SQL Server read-acceleration and resource fixtures (N11, E5) | 8 |
| 2. Descriptor GET-many without `descriptor_page` | P4 on both engines: `DescriptorReadHandler` (outside the seam; both its GET-many and read-acceleration candidate callers), the 18 descriptor text pins, the SQL Server type (d) adaptation at the descriptor split site of `AuthoritativeTimestampReadConsistencyScenario` (the PostgreSQL split already shares one transaction), and `DescriptorReadCommandExecutor`, which needs no change because the markers stay. Reconciles the design drift | Task 4 (c), task 7; descriptor evidence from `-Fixtures resource,descriptor` sessions (the resource run calibrates the observer; the descriptor run takes about 2–5 min per engine) | 3 |
| 3. Interim `ANALYZE` mitigation | Only if story 1 cannot ship in 8.2 (task 11) | Task 3 | 2, if triggered |
| 4. Provisioning | Not triggered: no recommended mechanism adds DDL | – | – |

Story 2 is blocked by story 1 where it consumes the shared contract: the PostgreSQL carrier name
and the set-before-read rule, and the SQL Server carrier type. The drafted stories are indexed in
[candidate-implementation-stories.md](candidate-implementation-stories.md).

## Retained fallback

Written by task 9 (AC6). **SQL Server keeps `#page` on P1 and P3. PostgreSQL retains no temp
table.**

**Selection conditions.** `#page` is created when all three hold:

- the dialect is SQL Server (`SqlDialect.Mssql`), and
- the batch hydrates a page of documents through `HydrationBatchBuilder`, either with a
  `PageKeysetSpec.Query` (resource GET-many, P1) or with a `PageKeysetSpec.SelectedPage`
  (read-acceleration fallback on cache misses, P3), and
- it is not a single-document request. Every production `PageKeysetSpec.Single` caller passes
  `UseSingleDocumentFastPath: true` (C5) and creates no temp table.

It does not depend on page size, offset, cursor mode, filters, or authorization strategy. The DDL is
`MssqlPlanDialect.AppendCreateKeysetTempTable`, unchanged from `main`. Candidate metadata (P2) and
descriptor GET-many (P4) do not create it under the recommendation.

**Supporting measurements** (task 6, three interleaved sessions on SQL Server 2025, RCSI):

| Measurement | `#page` (`main`) | `inline` on the same path |
|---|---|---|
| Temp tables per request (calibrated XE count) | 1 | 0 |
| `PAGELATCH` waits per request, 32 sessions | 14.5–15.9 (18.6–108.2 ms) | 0 |
| Recompiles after warmup | 0 | 0 |
| Child statement plans | index seek + key lookup; `#page` estimate 25 | identical; `OPENJSON` estimate 50 |
| Resource batch, 500 rows, c = 1, p50 / p95 | 21.3 / 22.7 ms | 24.9 / 26.1 ms (1.17 / 1.15; bound 1.09 / 1.10) |
| Fallback batch, 500 rows, c = 1, p50 / p95 | 21.0 / 22.1 ms | 28.0 / 29.4 ms (1.34 / 1.33; bound 1.05) |
| Fallback batch, 500 rows, c = 32, p95 / throughput | 181.1 ms / 226.8 per s | 206.3 ms / 180.0 per s (1.14 / ×0.79; bounds 1.05 / 0.95) |
| Batch CPU, 500 rows (resource / fallback) | 27.0 / 27.3 ms | 34.7 / 35.0 ms (×1.28) |
| Resource API p50 at 500 rows (offset 0) | 128.2 ms | 129.3 ms (1.01) |

`#page` is kept because the only measured removal that keeps its throughput, `memtablevar`, is an
unvalidated lead that needs provisioning. Its cost is real but concurrency-bound: at 32 sessions,
`#page` costs about 15 contended latch waits per request on these paths. End to end at concurrency 1 it
is invisible; resource GET-many API latency is within ±5% under either variant.

Reconsider `#page` when one of these holds:

- `memtablevar` completes its three validation steps and the provisioning is accepted; or
- a form that parses the page once into an indexed row source, without tempdb metadata, passes
  Gate 2 on P1 and P3.

## Thresholds

Written by task 9 (AC7). Each story's acceptance criteria use these thresholds as written.

**Definitions.**

- **Ratio:** candidate ÷ baseline. The candidate is the story branch, the baseline is `main`, and
  both are measured on the same machine in the same interleaved session set.
- **Margin:** m = min(max(2 × CV, 5%), 20%), where CV is the baseline's run-to-run coefficient of
  variation for that metric across three sessions. A story computes m from its own baseline runs.
  The spike's values are shown for orientation.
- **Noisy baseline cells.** The 20% cap keeps every bound a guard. Without it, `main`'s own
  variability decides the bound: on SQL Server, `main`'s latch contention gave the spike a
  cache-hit p95 bound of 3.09 and a c = 8 throughput bound of 0.51, and a rate bound stops being
  falsifiable once 2 × CV reaches 100%. When `main`'s 2 × CV for a gated cell exceeds 20%, the
  story reruns its three-session set. If the rerun still exceeds 20%, the PR reports that cell as
  inconclusive, with both variants' values, for reviewer sign-off; it does not count as met.
- **Cost metrics**, where lower is better (latency, child time, buffers, logical reads, WAL bytes,
  plan time): improvement means ratio ≤ T − m, and non-regression means ratio ≤ 1 + m.
- **Rate metrics**, where higher is better (throughput at concurrency 8 and 32): improvement means
  ratio ≥ T + m, and non-regression means ratio ≥ 1 − m.
- **Improvement target:** T = (1 + r) / 2, rounded up to two decimals, where r is the worst ratio
  the prototype measured for that metric. A story must deliver at least half of the prototype's
  worst-case gain, beyond the variability.
- Improvement bounds apply only to the problematic hydration workload (PostgreSQL, 25 and 500 rows).
  Every other path and metric gets a non-regression bound.

**Exact thresholds** (calibrated observer protocol: a known temp-table workload must register in
proportion, and plain `SELECT`s must read 0, before any zero counts):

| Id | Engine | Metric | Bound | Spike result | Reproduce |
|---|---|---|---|---|---|
| E1 | PostgreSQL | Catalog `n_tup_ins` + `n_tup_del` (`pg_class`, `pg_attribute`, `pg_type`, `pg_depend`, `pg_index`, `pg_constraint`, `pg_statistic`) per request, net of idle, on the resource, candidate, fallback, and descriptor batches | = 0 (`main`: 44; 42 descriptor) | 0 in 24 of 24 windows | R1. The stories' CI tests use the transaction-local `pg_stat_xact_sys_tables` instead |
| E2 | SQL Server | Temp tables per request on the candidate (P2) and descriptor (P4) batches: XE `object_created` (user table, tempdb, `ddl_phase = 1`, measured database). `Temp Tables Creation Rate` is reported beside it but does not gate: it is server-wide and picks up background temp tables (task 4). A table variable increments only that counter, so the stories' CI batch-text check (no `CREATE TABLE`, no `DECLARE … TABLE`) is the table-variable guard | XE = 0 in every window (`main`: 1); a window whose server counter reads non-zero is repeated, not failed | 0 in every P2 and P4 window, on both counters | R2 |
| E3 | SQL Server | `sql_statement_recompile` events and summed `plan_generation_num` change after warmup, all four batch kinds | = 0 | 0 in both variants | R2 |
| E4 | PostgreSQL | GET-many on each of P1–P4 in a transaction with `default_transaction_read_only = on`, with `main`'s prelude failing with 25006 as the positive control | succeeds, exact rows | probe above (P1 and P3 shapes) | story 1 and story 2 integration tests |
| E5 | SQL Server | SHA-256 of the P1 and P3 batch texts at 25 and 500 rows | equal to `main`'s | – (recommendation) | R2 `cells.json` and `batches/` |
| E6 | Both | Database commands per request (resource 1, descriptor 1, cache hit 2) | equal to `main` | equal in every run | R1, R2 |

**PostgreSQL improvement thresholds** (story 1; `primary-500k`, 25 and 500 rows; the resource
cells at offset 0, shallow, and deep):

| Id | Metric | Prototype worst r | T | Bound (m = 5%) | Spike range | Reproduce |
|---|---|---|---|---|---|---|
| P1 | Resource GET-many API p50 | 0.47 | 0.74 | ≤ 0.69 | 0.29–0.47 | R1 `cells.json` |
| P2 | Resource GET-many API p95 | 0.48 | 0.74 | ≤ 0.69 (0.65 at the spike's largest m, 8.8%) | 0.30–0.48 | R1 `cells.json` |
| P3 | Child-collection execution time, summed per request (resource and fallback, driver path `auto_explain`) | 0.021 | 0.52 | ≤ 0.47 | 0.002–0.021 | R1 `autoexplain/` |
| P4 | Child-collection buffers, summed per request (resource and fallback) | 0.466 | 0.74 | ≤ 0.69 | 0.023–0.466 | R1 `autoexplain/` |
| P5 | Fallback batch replay p50 and p95 at concurrency 1, and p95 at 32 | 0.17 | 0.59 | ≤ T − m (m capped at 20%, so ≤ 0.39 at worst) | 0.02–0.17 | R1 `replays.json` |

Child statements must be shown under both custom and generic plans: the API window and the
one-connection fallback window. Their node types (`Index Scan` expected) are reported as supporting
evidence, not as a threshold.

**Non-regression thresholds** (ratio ≤ 1 + m for cost, ≥ 1 − m for rate). The "Spike `1 + m`"
column shows the spike's uncapped values; under the cap no bound exceeds 1.20, and a cell whose
`main` CV would exceed it is rerun or reported as inconclusive (see Definitions):

| Id | Engine | Path and metric | Spike ratio | Spike `1 + m` | Reproduce |
|---|---|---|---|---|---|
| N1 | PostgreSQL | Descriptor API p50 / p95, 25 and 500 | 0.73–0.99 | 1.05–1.07 | R1 |
| N2 | PostgreSQL | Cache-hit API p50 / p95, 25 and 500 | 0.84–0.88 | 1.05–1.15 | R1 |
| N3 | PostgreSQL | Forced-miss complete API p50 / p95, 25 and 500 | 0.69–0.90 | 1.15–1.26 | R1 |
| N4 | PostgreSQL | Descriptor and candidate replay p50 at c = 1, p95 at c = 32; throughput at c = 8 and 32 | latency 0.09–0.59; ×1.8–×8.8 | per cell | R1 |
| N5 | PostgreSQL | Plan time per request (`pg_stat_statements`, c = 1 window), all four batches | 0.06–0.95 (resource and fallback) | per cell | R1 |
| N6 | PostgreSQL | WAL bytes per request in clean windows, all four batches | 0 | – | R1 |
| N7 | SQL Server | Descriptor and cache-hit API p50 / p95, 25 and 500; forced-miss complete API p50 / p95 | 0.37–1.01 | 1.05–3.09 | R2 |
| N8 | SQL Server | Descriptor and candidate replay p50 / p95 at c = 1, p95 at c = 32 | 0.05–0.93 | per cell | R2 |
| N9 | SQL Server | Descriptor and candidate replay throughput at c = 8 and 32 | ×1.89–×2.68 at 32 | per cell | R2 |
| N10 | SQL Server | Logical reads per request, descriptor and candidate batches (`SET STATISTICS IO`) | 0.81–0.87 | 1.05 | R2 `plans/` |
| N11 | SQL Server | Resource GET-many API p50 / p95 (P1 unchanged; guards the shared-contract work) | 0.94–1.02 | 1.07–1.23 | R2 |

**Reproduce.** The runner and run script are in `experiments/` (see
[Appendix: reproduction](#appendix-reproduction)). On a story branch the `prototype.patch` variants
do not apply, so each session runs the runner's `B0` label twice: once on `main`, once on the
story branch. The runner recognizes a selected page's `bigint[]` by its type, whatever the story
names it. It captures PostgreSQL bindings through the public
`HydrationBatchBuilder.AddParameters(DbCommand, PageKeysetSpec)` (`SpikeCommandCapture.cs`), on a
command that already carries the captured batch text, as production does, so a binder that binds
the array only when the text reads it is captured correctly. A story that changes that signature
updates this one call site before running R1.

| Id | Command (repeat three times, alternating `main` and the story branch, from a clean tree) | Analysis |
|---|---|---|
| R1 | `pwsh -File reference/spikes/DMS-1521/experiments/Invoke-KeysetSpike.ps1 -Engine postgresql -Sequence B0 -SequenceId s<n>-<main\|story>` | `t5_analyze.py` (attached to DMS-1521), with its session and label lists pointed at the new run ids |
| R2 | `pwsh -File reference/spikes/DMS-1521/experiments/Invoke-KeysetSpike.ps1 -Engine mssql -Sequence B0 -SequenceId s<n>-<main\|story>` | `t6_analyze.py`, adapted the same way |
| R3 | `pwsh -File reference/spikes/DMS-1521/experiments/Invoke-KeysetSpike.ps1 -Engine postgresql -Sequence I -Fixtures probes,descriptorprobes` (prototype) | Carrier probes against the prototype only. `-Sequence I` needs `prototype.patch` applied and the probes require `DMS1521_KEYSET_VARIANT=inline`, so neither runs on a story branch; the stories' own carrier tests (one transaction) take their place there |

## Design-document impact

Written by task 9. Each edit is made by the story named, with the warrant **mandated by this spike's
decision** ([Recommendation](#recommendation)). This spike edits none of them. Line numbers are as
of `3621b1eb6`.

| Document | Section (lines) | Edit | Story |
|---|---|---|---|
| `flattening-reconstitution.md` | § 6.1 "Single round-trip: materialize a `page` keyset" (849–904) | Make the keyset per dialect. On PostgreSQL, the transaction-local carrier, or a `bigint[]` for a selected page, with no DDL. On SQL Server, `#page` for resource hydration and selected pages. Keep one round trip and the first-result-set contract | 1 |
| `flattening-reconstitution.md` | § 7 sketches (1818–1828 `SelectByKeysetSql` "expects a keyset table"; 2576–2582 reconstituter step 1) | "joins the page keyset relation" instead of a materialized table; step 1 "select the page keyset" | 1 |
| `compiled-mapping-set.md` | step 3 (733–736) | Replace "temp table / table variable … create/clear + insert" with the per-dialect keyset of § 6.1 | 1 |
| `mpack-format-v1.md` | `KeysetTableContract` (89, 276, 295), `select_by_keyset_sql` (695) | Describe the per-dialect keyset: on PostgreSQL, a relation over the carrier, not a `page` table; on SQL Server, `#page` for P1 and P3. Pack emission is not implemented, so no pack was built with the old contract | 1 |
| `change-queries.md` | keyset DDL example (2239–2262) | Replace the PostgreSQL `CREATE TEMP TABLE "page"` example with the carrier form, keeping the anchor and `Ordinal` semantics | 1 |
| `link-injection.md` | keyset join sketch (164–217) | "materialized page keyset" becomes "page keyset relation" | 1 |
| `summary.md` | Query reconstitution (282) | "materialize a page keyset" becomes "select a page keyset" | 1 |
| `transactions-and-concurrency.md` | standalone hydration (644) | "after keyset materialization" becomes "after keyset selection". Add: on PostgreSQL, resource GET-many and the read-acceleration batches issue no DDL, so they run in read-only transactions (DMS-1687, from EDFI-2956) | 1 |
| `partitioned-cursor-paging.md` | § Carrying the selected-keyset boundary (687) | On PostgreSQL the selecting statement returns the anchor beside the ids in the first result set, and the carrier holds ids only. On SQL Server the anchor travels in `#page` where it stays (P1, P3) or in the JSON carrier (P2). Invariant 3 (89) is preserved and needs no change | 1 |
| `partitioned-cursor-paging.md` | descriptor paragraph (711) | Reconcile the [drift](#design-drift-descriptor_page): descriptor selection is carried in the carrier on both engines, with no keyset temp table | 2 |
| `transactions-and-concurrency.md` | descriptor acquisition (660) | "materializes its authorized DocumentId keyset once" becomes "selects … once and carries it in the batch" | 2 |

## Disposition

*Pending (task 11).*

## Appendix: reproduction

Everything runs from the repository root, one engine at a time. The recorded subject commit
`3621b1eb6` identifies the code under test (its `src/dms/backend` is identical to `main` at
`3774fc844`); it holds only the findings skeleton, and every run manifest lists `experiments/` as a
dirty path. To reproduce, run the `experiments/` folder from the DMS-1521 branch head, or the
spike's merge commit, against that backend. The runner received the review-round fixes after the
runs (exact batch-text writes, the dirty-path allow-list, binding capture with the batch text, the
enforced calibration verdict); none of them changes a measurement. Raw evidence lands in
`C:\perf\dms-1521\<run id>\` and is attached to DMS-1521 with a checksum
index (task 12).

**Patch procedure** (variants `A`, `B`, `I`): record `git status --porcelain` and the SHA-256 of
each file the patch touches, and refuse to continue if any of them has local changes. Apply with
`patch -p1 --forward -i reference/spikes/DMS-1521/experiments/prototype.patch`, and remove it with
`patch -p1 -R -i …` on every exit path. Then confirm that the tree matches the recorded state and
rebuild. Variant `B0` needs the patch absent. The run script checks the patch state with a
read-only `git apply --check -R` and never applies or removes the patch itself.

| Task | Command | Runs | Analysis |
|---|---|---|---|
| 2, smoke and calibration | `pwsh -File reference/spikes/DMS-1521/experiments/Invoke-KeysetSpike.ps1 -Engine <postgresql\|mssql> -Sequence B0 -Smoke` | `smoke1`–`smoke6` | `calibration.json`, `parity.json` |
| 3, Gate 1 | `… -Engine postgresql -Sequence B0` (no patch), then `… -Sequence A` (patch applied) | `t3-postgresql-*-{B01,A1}-*` | `autoexplain/`, `plans/` |
| 4, checks | `… -Sequence B -SequenceId t4baseline` and `… -Sequence I -Smoke -SequenceId t4smokeI` per engine; `… -Engine postgresql -Sequence I -Fixtures probes,descriptorprobes -SequenceId t4probes`; unit suites with `DMS1521_KEYSET_VARIANT=inline` | `t4baseline-*`, `t4smokeI-*`, `t4probes-*` | batch SHA-256 in `cells.json` |
| 5 | `… -Engine postgresql -Sequence B,I -SequenceId t5s<n>`, three sessions | `t5s1`–`t5s3` | `t5_analyze.py` → `t5_report.md` |
| 6 | `… -Engine mssql -Sequence B,I -SequenceId t6s<n>`, three sessions | `t6s1`–`t6s3` | `t6_analyze.py` → `t6_report.md` |
| 7 | `t7-lanes.ps1` and `t7-rerun.ps1` (patch applied, `$env:DMS1521_KEYSET_VARIANT = 'inline'`) | `t7-unit-inline`, `t7-pg-inline`, `t7-mssql-inline`, `t7-rerun-*` | `*.trx.failures.txt` |
| 8 | `… -Engine mssql -Sequence B0 -Fixtures forms -Forms <list>` (no patch) | `t8-*`, `t8mem-*` | `t8_analyze.py` → `t8_report.md`, `t8mem_report.md` |
| 9, read-only probe | `psql` script: a database with `default_transaction_read_only = on`, then `main`'s `DROP TABLE IF EXISTS "page"` and `inline`'s three statement shapes | (console output, quoted under [Recommendation](#postgresql-inline-on-all-four-paths)) | – |

The analysis scripts, lane scripts, and reports live beside the runs in `C:\perf\dms-1521\` and are
attached with them.

Each `batches/<kind>.<size>.sql` holds the exact command text. Its `.params.json` records that
text's `sha256` and `length`, and `cells.json` records the same hash as `batchSha256`. The
descriptor batches contain CRLF projection separators, because the batches were captured on
Windows and `DescriptorReadHandler` joins its columns with `Environment.NewLine`. Until
2026-11-12, the runner wrote these files with LF line endings, so the 42 descriptor files no
longer matched their hashes. They were restored by turning each `,\n` back into `,\r\n`, and each
file was written only after its restored hash matched the recorded one. All 283 batch files now
match their `.params.json`.

Prior-evidence extraction used the three DMS-1392 attachments and the DMS-1269 zip listed under
[Prior evidence](#prior-evidence). The figures there come from the `pg-statement-cost` rows for
statements 4–9, and from the `pg-plan-nodes` rows whose relation is `page` or a Student child
table.
