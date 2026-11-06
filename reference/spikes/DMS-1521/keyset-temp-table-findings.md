# DMS-1521 Keyset Temp-Table Findings

Ticket: [DMS-1521](https://edfi.atlassian.net/browse/DMS-1521), "Investigate if we need/can remove
temp tables on get-many requests". Related: DMS-848, DMS-1392, DMS-1399, DMS-1269, DMS-1401.

This report grows with the evidence. Each section names the spike task that writes it. A section
marked *Pending* has not been measured yet.

## Executive summary

*Pending (task 9).*

## Scope and paths

Inventory taken on `main` at `3774fc844` (2026-11-05). The plan was drafted against `59937bba5`;
nothing under `src/dms/backend` changed between the two commits.

### Multi-document paths that create a keyset temp table

| # | Path | Entry point | Temp table | Where the ids come from |
|---|---|---|---|---|
| P1 | Resource GET-many (query keyset, the default path) | `RelationalDocumentStoreRepository` -> `IDocumentHydrator` -> `ConsistentHydrationExecutor` -> `HydrationBatchBuilder` with `PageKeysetSpec.Query` (built by `RelationalQueryPageKeysetPlanner`) | `page` / `#page` | Page selection inside the batch (`WITH page_ids AS (...) INSERT ... RETURNING` / `OUTPUT INSERTED`) |
| P2 | Read-acceleration candidate metadata (only when `DocumentCache:ReadAcceleration:Enabled`, default `false`) | `RelationalDocumentStoreRepository.cs:2104` -> `HydrationBatchBuilder.BuildCandidateMetadataBatch` (`HydrationBatchBuilder.cs:149`, DDL at line 162) | `page` / `#page` | Page selection inside the batch |
| P3 | Read-acceleration fallback on cache misses (selected page) | `RelationalDocumentStoreRepository.cs:1998`, `PageKeysetSpec.SelectedPage` | `page` / `#page` with `Ordinal` | The client: PostgreSQL binds 2 parameters per id into a `VALUES` insert; SQL Server binds one JSON parameter and inserts from `OPENJSON` |
| P4 | Descriptor GET-many | `DescriptorReadHandler.BuildQueryCommand` (`DescriptorReadHandler.cs:2025`), added by DMS-1401 (`8a4eda2f6`, 2026-10-14) | `descriptor_page` / `#descriptor_page` | Page selection inside the batch (`INSERT INTO ... <PageDocumentIdSql>`); both version fences, the owner rows, and the `MAX(...)` boundary read it |

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

*Pending (task 2).*

## Baseline

*Pending (tasks 3, 5, and 6).*

## Cardinality hypothesis

*Pending (task 3).*

## Candidates and representative SQL

*Pending (task 4).*

## PostgreSQL results

*Pending (task 5).*

## SQL Server results

*Pending (task 6).*

## Alternatives (measured or reasoned)

*Pending (task 8).*

## Correctness validation

*Pending (task 7).* The matrix below maps each correctness requirement to the existing tests that
the prototype must pass. Every class listed was confirmed to exist at `3774fc844`.

| Requirement | Existing evidence |
|---|---|
| C1 page membership and order | `HydrationBatchBuilderTests` (Plans unit); `HydrationExecutorTests` (PostgreSQL and SQL Server backend integration); the one-command-per-request guard in `PerfScenarioExecutor`; `CursorPagingExecutionScenario`; `PostgresqlRelationalQueryAuthorizationTests`, `MssqlRelationalQueryAuthorizationTests`; `CursorPartitionAuthorizationMatrixScenario`; `Given_Postgresql_DocumentCacheReadAcceleration`, `Given_Mssql_DocumentCacheReadAcceleration` |
| C2 reconstruction | `HydrationExecutorTests`; `*DocumentReferenceLookupPageTests`; `*PageLinkInjectionIntegrationTests`; `DescriptorRuntimeScenario`; `CompactDescriptorResourceScenario` |
| C3 counts, empty and zero pages, anchors | `LimitZeroCountReadScenario` with `Given_*_LimitZeroCountReads_DocumentCache`; `CursorEmptyHydrationScenario`; `WindowedCursorAnchoringScenario`; `CursorPublicContractScenario`; `HydrationSelectedKeysetResultTests`; the task 4 carrier probes |
| C4 concurrency, authorization, isolation | `CursorPartitionConcurrencyScenario`; `AuthoritativeTimestampReadConsistencyScenario` (with the type (d) adaptation at both split sites); `*RelationalOwnershipQueryAuthorizationTests`; `*DescriptorQueryOwnershipAuthorizationTests`; zero replay errors at 32 sessions |
| C5 fast path | The single-document cases in `HydrationBatchBuilderTests`; `RelationalWriteSmokeTests` (Frontend unit); `DocumentCacheMaterializerResourceHydrationTests`, `DocumentCacheMaterializerDescriptorHydrationTests`, `DocumentCacheMaterializerContractTests`, `DocumentCacheMaterializerInvariantTests`, `DocumentCacheMaterializerCoherenceTests`; `MssqlWriteSessionCommandStreamTests` |
| C6 everything changes together | Task 4 sizing (failing pins by class, manifest-golden diff size); the type (d) adaptations, including `DescriptorReadCommandExecutor` if the descriptor markers change |

Task 7 also runs the backend read-path classes `*RelationalQueryExecutionTests`,
`*DescriptorReadQueryFilterTests`, `*WindowedPageQueryPlanTests`, and
`*DocumentCacheQueryPlanTests` on both engines.

## Recommendation

*Pending (task 9).*

## Retained fallback

*Pending (task 9).*

## Thresholds

*Pending (task 9).*

## Design-document impact

*Pending (task 9).* Known so far: `partitioned-cursor-paging.md` line 711 already disagrees with
the code (see [Design drift](#design-drift-descriptor_page)).

## Disposition

*Pending (task 11).*

## Appendix: reproduction

*Pending (tasks 2-8).* Prior-evidence extraction used the three DMS-1392 attachments and the
DMS-1269 zip listed under [Prior evidence](#prior-evidence); the figures above come from the
`pg-statement-cost` rows for statements 4-9 and the `pg-plan-nodes` rows whose relation is `page`
or a Student child table.
