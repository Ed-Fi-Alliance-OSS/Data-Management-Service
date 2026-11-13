# Story 1: Resource multi-document hydration without a keyset temp table

[Back to DMS-1521 story index](candidate-implementation-stories.md)

## Summary

Stop creating and dropping a keyset temp table on every resource GET-many request on PostgreSQL,
and on the read-acceleration candidate-metadata batch on SQL Server. Carry the selected document
ids inside the batch instead.

- **PostgreSQL:** resource GET-many (P1), read-acceleration candidate metadata (P2), and the
  read-acceleration selected-page fallback (P3).
- **SQL Server:** candidate metadata (P2) only. Resource GET-many (P1) and the selected-page
  fallback (P3) keep `#page`, byte-identical to `main`.
- **Owns** the shared keyset-contract work that story 2 consumes.
- **Fixes** [DMS-1687](https://edfi.atlassian.net/browse/DMS-1687) (cloned from [EDFI-2956](https://edfi.atlassian.net/browse/EDFI-2956)) for the
  resource paths.
- **Evidence:** [DMS-1521 findings](keyset-temp-table-findings.md), with the prototype in
  [`experiments/prototype.patch`](experiments/prototype.patch).

## Description

Every multi-document hydration batch starts by creating a keyset temp table (`page` on PostgreSQL,
`#page` on SQL Server), filling it with the page's `DocumentId`s, and joining every hydration
statement to it. DMS-1521 measured what that costs.

- **PostgreSQL** never analyzes the temp table, so the planner estimates every page at 2,040 rows,
  whatever it holds. At that estimate, each child-collection statement hash-joins a full scan of
  its child table. On the 500,000-student fixture, the four Student child statements took about
  175 ms of a 25-row request. The create and drop also write 44 catalog tuples and about 10 KB of
  WAL per request, and they invalidate the prepared statements, so almost every statement is
  re-planned. Because the batch starts with `DROP TABLE IF EXISTS`, GET-many fails in a read-only
  transaction (DMS-1687).
- **SQL Server's** `#page` has statistics (its plan, compiled at the first page's estimate of 25,
  still seeks at 500 rows) and caused no recompiles. Its cost is tempdb
  metadata latch contention: 13–17 `PAGELATCH` waits per request at 32 sessions.

The spike's `inline` prototype carries the ids in the batch and creates no catalog object. On
PostgreSQL it cut resource GET-many API p50 and p95 to 0.29–0.48× of `main` and child-collection
time to 0.002–0.021×. It wrote no catalog rows or WAL and regressed no path. On SQL Server it
helped the candidate batch (×1.9–×2.7 throughput at 32 sessions), although that batch's p99 at
500 rows and 32 sessions rose from 71 to 314 ms in all three sessions while its p95 fell. It
regressed the 500-row resource and fallback batches by 11–37% in a single session, because every
statement re-parses the JSON carrier, and under concurrency it cut the fallback batch's throughput
at 32 sessions and the resource batch's at 8 sessions in two of three runs. So SQL Server keeps `#page` there. Correctness validation found no behavior change on either engine.

## Scope

**In scope:**

- **A keyset contract that does not require a table.** The keyset joined by every compiled
  `SelectByKeysetSql`, descriptor projection, document-reference lookup, metadata select, and
  version fence becomes a relation:
  - On PostgreSQL, it reads the selected ids from the transaction-local carrier, or from the bound
    `bigint[]` for a selected page.
  - On SQL Server, P1 and P3 keep the `#page` table. The candidate batch (P2) reads its own JSON
    carrier.
- **PostgreSQL query keyset (P1, P2).** The statement that selects the page also sets the carrier
  and returns the selected keys, plus the anchor when the keyset is anchored. It is the batch's
  first result set, in the same position and shape as today. An empty or zero-size page clears
  the carrier to `''`. No extra result set and no extra round trip.
- **PostgreSQL selected page (P3).** One `bigint[]` parameter replaces the two parameters per id,
  in the caller's order.
- **SQL Server candidate metadata (P2).** The batch selects its page once into an `nvarchar(max)`
  variable with `FOR JSON PATH`, and reads it with `OPENJSON`. It keeps its three result sets:
  selected keys, count, and metadata.
- **Binding.** Bind the PostgreSQL array only when the batch text reads it, so the
  single-document fast path gains no parameter. SQL Server binding does not change.
- **The guarded single-document keyset without the fast path** (`BuildGuardedSingleDocumentBatch`
  with `UseSingleDocumentFastPath: false`). No production caller uses it (C5), and the prototype
  throws `NotSupportedException`. Implement it under the new contract. Do not remove it:
  `flattening-reconstitution.md` (≈944) names it as the fallback for a dialect that declines the
  fast path. The work is PostgreSQL only: SQL Server keeps `#page` for this batch, as for P1 and P3.
  Neither PostgreSQL mechanism above fits it, for two reasons:
  - Its id is not known when the batch is bound. The only caller co-batches it into the composite
    write command and replaces the `@DocumentId` token with the composite carrier's in-database
    expression after the batch is built (`RelationalWriteFirstPhase.cs` ≈1062–1087;
    `NULLIF(current_setting('dms.composite_target_documentid', true), '')::bigint`). So the
    `bigint[]` cannot carry it, and the composite command binds no keyset parameter.
  - A `SELECT set_config(…)` statement would add a result set. The composite reader skips this
    batch by `HydrationExecutor.GetResultSetCount`, which counts no selected-keys result set for a
    `Single` keyset.

  So the guarded batch reads the `@DocumentId` token under the guard predicate, adds no statement,
  and binds no array. For example, its keyset relation becomes
  `unnest(CASE WHEN <guard> THEN ARRAY[@DocumentId] END) WITH ORDINALITY`, which yields no rows when
  the guard is false. The relation is compiled into every table's `SelectByKeysetSql`, so the
  builder substitutes the relation text when it builds the guarded batch.
- **The performance harness's PostgreSQL plan capture**
  (`src/dms/tests/EdFi.DataManagementService.Performance.Harness/Measurement/PgsqlPlanCapture.cs`).
  `PerfBaselineRunPipeline` and `PerfFinalGateRunPipeline` replay every PostgreSQL resource
  hydration batch through it, for `eng/performance/invoke-traditional-baseline.ps1` and
  `invoke-final-gate.ps1`. Today it would fail on this story's batch in two ways:
  - `SplitHydrationBatch` throws on any string literal, and this batch has several
    (`'dms.page_keyset'`, `','`, `''`). Teach it single-quoted literals, including `''`, as the
    spike's `SpikePgsqlPlanCapture` splitter does.
  - The replay binds only the page-selection values, but every keyset reader references the keyset
    parameter. Bind it as production does: an untyped NULL for a query keyset.
- **Test churn and new tests** (see Test requirements).
- **Manifest goldens.** Regenerate with `UPDATE_GOLDENS=1` and commit them in the same PR. CI never
  regenerates goldens.
- **The design-document edits** listed below.

**Out of scope:**

- Descriptor GET-many (P4), `descriptor_page`, and `DescriptorReadHandler` (story 2).
- SQL Server P1 and P3. They keep `#page` (findings,
  [Retained fallback](keyset-temp-table-findings.md#retained-fallback)), and the memory-optimized
  table-variable lead is not adopted.
- The single-document fast path (C5), version fences, the retry policy, and the readers'
  result-set contract. None of these change.
- DDL, provisioning, and `RelationalMappingVersion`. Plan SQL is not an input to
  `EffectiveSchemaHash`.
- `ANALYZE` on the temp table.

## Design documents to update

Each edit is **mandated by DMS-1521's decision**
([Recommendation](keyset-temp-table-findings.md#recommendation)). Line numbers are as of
`3621b1eb6`; the paths are under `reference/design/backend-redesign/design-docs/`.

| Document | Section | Edit |
| --- | --- | --- |
| `flattening-reconstitution.md` | § 6.1 "Single round-trip: materialize a `page` keyset" (≈849–904) | The keyset is per dialect. On PostgreSQL: the transaction-local carrier, or a `bigint[]` for a selected page, with no DDL. On SQL Server: `#page` for resource hydration and selected pages (the retained fallback and its selection conditions), and a JSON carrier for candidate metadata. Keep one round trip and the first-result-set contract |
| `flattening-reconstitution.md` | § 7 sketches (≈1818–1838, ≈2576–2582) | `SelectByKeysetSql` "joins the page keyset relation" instead of "expects a keyset table". The `KeysetTableContract` sketch (≈1833–1838) describes the per-dialect keyset, as in the `mpack-format-v1.md` row. Reconstituter step 1 becomes "select the page keyset" |
| `flattening-reconstitution.md` | § 6.1 "Single-document fast path" rationale (≈908–909) | The `CREATE`, `INSERT`, and `DROP` the fast path avoids are SQL Server's `#page` cost (P1, P3). On PostgreSQL the page keyset issues no DDL; the fast path still saves the carrier and a join per result set |
| `flattening-reconstitution.md` | § 7.10 reconstitution inner-loop sketch (≈2595–2596) | "materialize page keyset" becomes "select the page keyset"; "joined to page" becomes "joined to the page keyset relation" |
| `compiled-mapping-set.md` | step 3 (≈733–736) | Replace "temp table / table variable … create/clear + insert" with the per-dialect keyset |
| `mpack-format-v1.md` | `KeysetTableContract` (≈89, 276, 295), `select_by_keyset_sql` (≈695) | Describe the per-dialect keyset: on PostgreSQL, a relation over the carrier, not a `page` table; on SQL Server, `#page` for P1 and P3. Pack emission is not implemented, so no pack was built with the old contract |
| `change-queries.md` | keyset DDL example (≈2239–2262) | Replace the PostgreSQL `CREATE TEMP TABLE "page"` example with the carrier form, keeping the anchor and `Ordinal` semantics |
| `link-injection.md` | keyset join sketch (≈164–217) | "materialized page keyset" becomes "page keyset relation" |
| `summary.md` | Query reconstitution (≈282) | "materialize a page keyset" becomes "select a page keyset" |
| `transactions-and-concurrency.md` | standalone hydration (≈644) | "after keyset materialization" becomes "after keyset selection". Add: on PostgreSQL, resource GET-many and the read-acceleration batches issue no DDL and run in read-only transactions |
| `partitioned-cursor-paging.md` | § Carrying the selected-keyset boundary (≈680–693) | The selected keys are no longer "inserted keys" returned by `RETURNING "DocumentId"` / `OUTPUT INSERTED.[DocumentId]` (≈680–685) on PostgreSQL or on SQL Server P2: the selecting statement returns them. On PostgreSQL the selecting statement returns the anchor beside the ids in the first result set; the carrier holds ids only. On SQL Server the anchor travels in `#page` (P1, P3) or the JSON carrier (P2). Invariant 3, "No extra roundtrip" (≈89), holds unchanged |

## Acceptance criteria

### Behavior

1. No PostgreSQL resource GET-many, candidate-metadata, or selected-page batch creates, drops, or
   analyzes a table.
2. No SQL Server candidate-metadata batch creates a temp table or declares a table variable.
3. The SQL Server resource GET-many and selected-page batch texts are byte-identical to `main` at
   every page size.
4. The selected keys, and the `ContentVersion` anchor where the keyset is anchored, remain the
   batch's first result set, in the same shape. This includes empty and zero-size pages.
   Continuation reflects the selected keys even when every selected document is deleted before
   hydration.
5. Query keysets carry a NULL ordinal, so metadata orders by `DocumentId`. Selected pages return
   documents in the caller's order, including an order that is not ascending.
6. On PostgreSQL, the carrier is set or cleared in the same statement that selects the page,
   before any later statement reads it. A batch never reads a previous batch's ids. That holds on
   a reused pooled connection (`NoResetOnClose`), after a failed batch, after an empty page, and
   across two hydrations in one transaction.
7. On PostgreSQL, resource GET-many, candidate selection, and the selected-page fallback succeed
   in a transaction with `default_transaction_read_only = on`.
8. Every multi-document request uses the same number of database commands as on `main`: resource
   GET-many 1, cache hit 2.
9. Binding is unchanged on SQL Server. On PostgreSQL:
   - a selected page binds one `bigint[]` at every page size up to `MaximumPageSize` (500);
   - a query keyset binds at most one parameter more than `main`;
   - the single-document fast-path command binds exactly the parameters it binds on `main`.

### Correctness (C1–C6)

- **C1.** Every hydration result set keeps the fixed authorized document set and the selected-page
  order. Nothing runs per document.
- **C2.** Root and child ordering, descriptor projections, reference lookups, and the
  reconstructed responses are unchanged.
- **C3.** Total counts, empty and zero-limit pages, offsets, cursor ordering modes, and
  continuation anchors are unchanged.
- **C4.** Concurrent inserts, updates, and deletes neither weaken authorization nor change the
  isolation contract. The version fences and the three-attempt retry behave as on `main`.
- **C5.** The single-document fast path (GET by id, writes, cache materialization) produces the
  same SQL and binding as on `main`.
- **C6.** Plan contracts, SQL generation, binding, readers, manifests, goldens, tests, the
  performance harness's PostgreSQL plan capture, and the design documents above change in the same
  PR.

### Performance thresholds

Measured with R1 (PostgreSQL) and R2 (SQL Server, read-acceleration fixture plus the resource
fixture for N11). Three interleaved sessions: `main`, then the story branch, three times, on one
machine. Ratios are story branch ÷ `main`, and `m = min(max(2 × CV, 5%), 20%)` from `main`'s
three runs. A cell whose `2 × CV` exceeds 20% is rerun; if it still exceeds 20%, the PR reports it
as inconclusive for reviewer sign-off instead of counting it as met. PostgreSQL cells are on `primary-500k` at 25 and 500 rows; resource cells at offset 0, shallow,
and deep.

| Id | Metric | Bound | Spike result |
| --- | --- | --- | --- |
| E1 | PostgreSQL catalog `n_tup_ins` + `n_tup_del` per request on the resource, candidate, and fallback batches (calibrated observer) | = 0 | 0 (`main` 44) |
| E2 | SQL Server temp tables per candidate batch: XE `object_created`. `Temp Tables Creation Rate` is reported, not gated (server-wide); the CI batch-text check guards against table variables | XE = 0 in every window; a window whose server counter reads non-zero is repeated | 0 (`main` 1) |
| E3 | SQL Server `sql_statement_recompile` events and `plan_generation_num` change after warmup | = 0 | 0 |
| E4 | PostgreSQL P1–P3 under `default_transaction_read_only = on` | succeed, exact rows | probe passed |
| E5 | SHA-256 of the SQL Server P1 and P3 batch texts | equal to `main` | – |
| E6 | Database commands per request | equal to `main` | equal |
| P1 | PostgreSQL resource GET-many API p50 | ratio ≤ 0.74 − m | 0.29–0.47 |
| P2 | PostgreSQL resource GET-many API p95 | ratio ≤ 0.74 − m | 0.30–0.48 |
| P3 | Child-collection execution time per request (resource and fallback; driver-path `auto_explain`) | ratio ≤ 0.52 − m | 0.002–0.021 |
| P4 | Child-collection buffers per request (resource and fallback) | ratio ≤ 0.74 − m | 0.023–0.466 |
| P5 | Fallback batch replay p50 and p95 at concurrency 1, and p95 at 32 | ratio ≤ 0.59 − m | 0.02–0.17 |
| N2 | PostgreSQL cache-hit API p50 / p95 | ratio ≤ 1 + m | 0.84–0.88 |
| N3 | PostgreSQL forced-miss complete API p50 / p95 | ratio ≤ 1 + m | 0.69–0.90 |
| N4 | PostgreSQL candidate replay p50 at c = 1, p95 at c = 32; throughput at c = 8 and 32 | latency ≤ 1 + m; throughput ≥ 1 − m | 0.19–0.43; ×2.1–×5.9 |
| N5 | PostgreSQL plan time per request (resource, candidate, fallback) | ratio ≤ 1 + m | 0.06–0.95 (resource and fallback) |
| N6 | PostgreSQL WAL bytes per request, clean windows | ratio ≤ 1 + m | 0 |
| N7 | SQL Server cache-hit and forced-miss complete API p50 / p95 | ratio ≤ 1 + m | 0.37–1.00 |
| N8 | SQL Server candidate replay p50 / p95 at c = 1, p95 at c = 32 | ratio ≤ 1 + m | 0.06–0.78 |
| N9 | SQL Server candidate replay throughput at c = 8 and 32 | ratio ≥ 1 − m | ×1.89–×2.68 at 32; ×1.20–×1.30 at 8 (`main` CV 22–24%: rerun under the noisy-cell rule) |
| N10 | SQL Server candidate logical reads per request | ratio ≤ 1 + m | 0.81 |
| N11 | SQL Server resource GET-many API p50 / p95 (P1 unchanged) | ratio ≤ 1 + m | 0.94–1.02 |

The PR reports child-collection cost under both PostgreSQL plan types: custom (the API window) and
generic (the one-connection fallback window after five executions). It also reports the plan node
types (`Index Scan` expected) as supporting evidence; they are not a threshold. Beside N8 it also
reports the SQL Server candidate batch's p95 and p99 at concurrency 8 and 32, at 25 and 500 rows
(reported, not gated): the prototype raised the p99 at 32 sessions from 70.8 to 314 ms at 500 rows,
and in one of three sessions its p95 at 8 sessions and 25 rows from 5.9 to 63.2 ms. If the story's
design keeps either tail, the PR names its cause (for example, memory-grant or parallelism waits on the `OPENJSON`
statements).

## Test requirements

**Unit** (`Backend.Plans.Tests.Unit`, `Backend.Tests.Unit`):

- Each batch kind in scope keeps its result-set sequence: query keyset, selected page, and
  candidate metadata. Cover non-empty, empty, zero-size, zero-limit, and `ContentVersion`-anchored
  pages on each engine in scope. These are behavior tests of positions and shapes, not tests that
  a name is absent.
- Pin the order of operations (AGENTS.md):
  - the PostgreSQL carrier is set in the selecting statement, before any statement that reads it;
  - the selected keys come from selection, not from hydration.
- Binding: the PostgreSQL selected page binds one `bigint[]` at 1, 25, and `MaximumPageSize`; the
  fast-path command's parameters are unchanged; SQL Server binding is unchanged.
- SQL Server P1 and P3: the existing pins stay green unchanged. They are the byte-identity guard.
- Guarded single-document keyset without the fast path, PostgreSQL: the batch contains no
  `set_config`, binds no keyset parameter, reads `@DocumentId` under the guard predicate, and
  `GetResultSetCount` is unchanged. The existing
  `It_does_not_return_selected_ids_from_the_guarded_materialization` checks only that `RETURNING`
  and `OUTPUT INSERTED` are absent, so a `set_config` statement would pass it. The SQL Server
  guarded test (`It_should_emit_the_sql_server_guarded_keyset_batch_when_the_feature_flag_is_disabled`)
  stays green unchanged.
- Performance harness (`Performance.Harness.Tests.Unit`): add a `PgsqlPlanCaptureTests` case that
  splits this story's PostgreSQL resource batch shape (literals included) and binds its keyset
  parameter. The existing cases split hand-written text with no literals, so they cannot catch this,
  and the harness pipelines refuse to run in CI unless `PERF_ALLOW_CI` is set.
- Update the pins of the old PostgreSQL text and parameter lists, and the candidate pins on both
  engines. Task 7 counted 112 failing unit tests for the whole prototype. About 60–70 of them are
  in this story, in `Given_HydrationBatchBuilder_*`, `Given_ReadPlanCompiler`,
  `Given_PgsqlPlanDialect`, `Given_PlanSqlWriterExtensions`, `Given_A_Keyset_That_Carries_No_Anchor`,
  and `Given_RelationalQueryRequestPreprocessor+Given_CompactDescriptorFilters`. Regenerate the
  8 manifest goldens; about half of the 2,494 changed lines the prototype produced are
  PostgreSQL's.

**Integration** (`Backend.Postgresql.Tests.Integration`, `Backend.Mssql.Tests.Integration`):

- **No temp tables** (E1, E2). A GET-many request through the production executor creates no
  temp table. The CI tests use deterministic, per-session signals. The calibrated counter protocol
  stays in the R1 and R2 evidence only, for two reasons. It waits for statistics to flush, and its
  SQL Server counter is server-wide, while `Backend.Mssql.Tests.Integration` runs fixtures in
  parallel and P1 and P3 keep `#page`.
  - PostgreSQL: run the hydration inside a test-owned transaction, as
    `Given_Two_Postgresql_Hydration_Batches_On_The_Same_Transaction` does. Before commit, read
    `n_tup_ins` for the seven catalog tables (`pg_class`, `pg_attribute`, `pg_type`, `pg_depend`,
    `pg_index`, `pg_constraint`, `pg_statistic`) from `pg_stat_xact_sys_tables`, and expect 0. As
    the positive control, `main`'s keyset prelude, run the same way, reads more than 0.
  - SQL Server, candidate batch: the batch text names no `#` temp table (no `#`-prefixed
    identifier, which also covers `SELECT … INTO #`) and contains no `DECLARE … TABLE`. Only the
    server-wide counter sees a table variable, and a shared CI server cannot isolate it.

  Each test must fail if the change is reverted.
- **Carrier.** The setting is transaction-local, so a commit or rollback discards it, and a batch
  can read another batch's ids only inside one transaction. The leak tests therefore share one
  test-owned transaction:
  - `Given_Postgresql_Query_Keysets_In_One_Transaction`: a 25-id query keyset, then a zero-size
    page (`limit = 0`), then a 1-id page, then a page whose offset is past the end. Each empty
    page directly follows a non-empty one, so a skipped `set_config` would return the previous
    page's documents. Each page returns exactly its own documents and selected keys, and the two
    empty pages return none. It fails if the empty or zero-size path skips `set_config`, for
    example by copying `main`'s zero-page shortcut (`AppendEmptyKeysetMaterialization`) or by
    evaluating `set_config` per selected row instead of in a one-row aggregate.
  - `Given_A_Query_Keyset_Then_An_Empty_Selected_Page_In_One_Transaction`: a 25-id query keyset,
    then an empty selected page. The second hydration returns no documents. A `NULL` parameter
    makes the relation read the carrier, so an empty selected page bound as `NULL` would return
    the first page's documents.

  Outside a shared transaction, on one pooled physical connection: a non-empty page, a failed
  batch (`SELECT 1 / 0` after the carrier is set), then an empty page. The carrier reads `''`
  after each commit or rollback, which catches a session-level (`is_local = false`) carrier.
- **Read-only** (E4). Resource GET-many, candidate selection, and a selected page in a transaction
  with `default_transaction_read_only = on`. `main`'s DDL prelude, run as a positive control,
  fails with SQLSTATE 25006.
- **Selected-page order.** Keep `Given_A_Postgresql_Selected_Page_Keyset_With_Nonascending_DocumentIds`
  green.
- **Type (d) adaptation.** `Given_A_Pgsql_Query_Keyset_That_Returns_Its_Selected_Ids`, both
  deleted-before-hydration tests. The test splices `DELETE`s after the temp-table
  `INSERT … RETURNING`. Move the splice to after the statement that selects the page and sets the
  carrier, which is still between selection and hydration. Its SQL Server twin stays unchanged.
- **Type (a) pin.** Delete
  `Given_Two_Postgresql_Hydration_Batches_On_The_Same_Transaction.It_keeps_the_pgsql_temp_table_alive_until_commit_between_hydration_calls`.
  `Given_Postgresql_Query_Keysets_In_One_Transaction` (above) replaces it. The fixture's
  behavioral sibling stays, but it is not carrier coverage: it hydrates `PageKeysetSpec.Single`
  with the fast path off, which binds the `bigint[]` and never sets or reads the carrier.
- **C5.** `RelationalWriteSmokeTests`, the `DocumentCacheMaterializer*Tests` classes, the
  `*Single_Document_Fast_Path*` classes, and `MssqlWriteSessionCommandStreamTests` pass unchanged.

**API integration** (`Tests.Integration`, both engines, full suites). These scenarios must pass:
`CursorPagingExecutionScenario`, `CursorPartitionAuthorizationMatrixScenario`,
`CursorPartitionConcurrencyScenario`, `CursorEmptyHydrationScenario`,
`WindowedCursorAnchoringScenario`, `CursorPublicContractScenario`, `LimitZeroCountReadScenario`
(including `Given_*_LimitZeroCountReads_DocumentCache`),
`AuthoritativeTimestampReadConsistencyScenario`, and `Given_*_DocumentCacheReadAcceleration`. The
PostgreSQL resource split site in `AuthoritativeTimestampReadConsistencyScenario` needs no change:
its two commands share one transaction, so the carrier survives. The SQL Server resource site
needs no change either, because P1 keeps `#page`.

**Performance evidence.** Attach R1 and R2 artifacts (see
[Thresholds](keyset-temp-table-findings.md#thresholds)), from three interleaved sessions on one
machine, run from a clean tree. That is about 2.5 h of machine time for PostgreSQL. On SQL Server,
the read-acceleration and resource fixtures take about 1.7 h per session pair (task 6: 49–60 min
per `main` run). Budget a second SQL Server session set: the spike's `main` CV at concurrency 8
(22–24%) predicts that N9's c = 8 cells trip the noisy-cell rerun.

## Implementation guidance (non-binding)

`experiments/prototype.patch` (`DMS1521_KEYSET_VARIANT=inline`) passed every check for these paths.
It is a starting point:

- **The seam.** Every P1–P3 keyset join renders through `PlanSqlWriterExtensions.AppendRelation`.
  The prototype's PostgreSQL relation is:

  ```sql
  (SELECT u."DocumentId",
          CASE WHEN CAST(@page_document_ids AS bigint[]) IS NULL THEN NULL
               ELSE CAST(u."Ordinal" - 1 AS int) END AS "Ordinal"
   FROM unnest(COALESCE(CAST(@page_document_ids AS bigint[]),
                        CAST(string_to_array(current_setting('dms.page_keyset', true), ',') AS bigint[])))
        WITH ORDINALITY AS u("DocumentId", "Ordinal"))
  ```

  The `COALESCE` exists because PostgreSQL has no statement that takes bind parameters, sets a
  value, and returns no result set. A selected page has no result set that could carry
  `set_config`, so the relation reads the parameter when it is bound and the carrier when it is
  NULL. The NULL is bound untyped, and the SQL's `CAST` types it, so `Backend.Plans` needs no
  Npgsql reference. An empty selected page binds an empty array, never `NULL`; the prototype binds
  `selected.DocumentIds.ToArray()`. A production design may use a different shape, for example a
  separate relation per keyset kind, if it meets the criteria.
- **The query keyset statement** (PostgreSQL):

  ```sql
  WITH page_ids AS (/* existing page selection */)
  SELECT u."DocumentId"
  FROM (SELECT set_config('dms.page_keyset',
               COALESCE(string_agg(CAST(p."DocumentId" AS text), ',' ORDER BY p."DocumentId"), ''),
               true) AS keyset
        FROM page_ids p) c
  CROSS JOIN LATERAL unnest(CAST(string_to_array(c.keyset, ',') AS bigint[])) AS u("DocumentId");
  ```

  The aggregate always yields one row, so `set_config` runs and clears the carrier even for an
  empty page. An anchored keyset also carries the anchor.
- **SQL Server P2.** The prototype rendered the candidate batch through the same `AppendRelation`
  as every hydration batch. That would change P1 and P3 too, which this story must not do. Give
  the candidate batch its own relation, for example by passing the keyset relation to
  `DocumentMetadataColumns.AppendDocumentMetadataSelectBody`. Keep the dialect-wide compiled
  `SelectByKeysetSql` on `[#page]`. The prototype's candidate SQL is
  `DECLARE @page_keyset nvarchar(max); WITH page_ids AS (…) SELECT @page_keyset = (SELECT [DocumentId] FROM page_ids FOR JSON PATH);`,
  and its readers use
  `OPENJSON(@page_keyset) WITH ([DocumentId] bigint '$.DocumentId', [Ordinal] int '$.Ordinal')`.
- **Prepared plans.** Under `plan_cache_mode = auto`, the parameterized readers take five custom
  plans with an exact estimate, then a generic plan that estimates 10 rows. Both were measured as
  cheap on `primary-500k`. Do not force a plan mode. In the prototype, a query keyset binds
  `@page_document_ids` as an untyped NULL and a selected page as `bigint[]`, against the same reader
  text. Npgsql keys auto-prepared statements by SQL text and runs an execution whose parameter
  types differ unprepared, so on one connection only the kind that prepares the readers first gets
  their prepared plans; the other re-plans every call, as `main` does (spike probe: 7 prepared
  executions of 16 alternating uses per reader). Either bind the query keyset's NULL as a typed
  `bigint[]`, so both kinds share one prepared statement per reader, or keep the untyped NULL and
  state the trade-off in the PR.
- **Byte-identity check.** Before the PR, compare the SHA-256 of each captured batch (`cells.json`
  in R1 and R2) between `main` and the branch. SQL Server P1 and P3 must match exactly (E5).
- **Existing carrier.** The write path already uses this mechanism: `PgsqlCompositeTargetCarrier`
  and `MssqlCompositeTargetCarrier`
  (`src/dms/backend/EdFi.DataManagementService.Backend/Composite/RelationalCompositeCommandDialect.cs`,
  DMS-1332). Follow their conventions: a `dms.<purpose>` setting set with `set_config(…, true)`,
  and SQL Server batch-local names reserved from the bound parameters, because SqlClient rejects a
  batch-local that shares a bound parameter's name.
- **Do not keep** `Dms1521KeysetVariant`, the environment switch, or the `pg-analyze` variant.

## Dependencies

- **Blocks** story 2, which reuses the carrier name, the set-before-read rule, and the SQL Server
  JSON carrier.
- **Relates to** DMS-1521 (the spike), DMS-848, and DMS-1687.
- **Fixes** DMS-1687 for the resource paths. The descriptor path follows in story 2.
- **Starts** after the DMS-1521 approval gate accepts the PostgreSQL transaction-local carrier for
  production.

## Estimate

**3 story points.** The prototype exists, but production work remains:

- a keyset contract that separates PostgreSQL from SQL Server P1 and P3, while SQL Server P2 gets
  its own relation;
- the guarded single-document keyset under the new contract (PostgreSQL only, reading the
  substituted `@DocumentId` token);
- the performance harness's PostgreSQL plan capture (literals and the keyset parameter) and its
  unit test;
- about 60–70 updated unit pins and the golden regeneration;
- six new integration tests (no temp tables on each engine, three carrier tests, read-only) and
  the binding unit tests;
- eleven design-document edits;
- one interleaved performance session set on each engine, plus the likely SQL Server rerun (N9).
