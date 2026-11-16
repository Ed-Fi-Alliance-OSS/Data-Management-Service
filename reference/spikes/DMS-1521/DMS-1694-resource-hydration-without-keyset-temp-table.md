# Story 1 (DMS-1694): Resource multi-document hydration without a keyset temp table

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
- **Fixes** [DMS-1687](https://edfi.atlassian.net/browse/DMS-1687) (cloned from [EDFI-2956](https://edfi.atlassian.net/browse/EDFI-2956)) for
  resource GET-many (P1). P2 and P3 never run on a replica: read acceleration falls back to P1 on a
  derivative target.
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
- **The performance harness's observer probe**
  (`src/dms/tests/EdFi.DataManagementService.Performance.Harness/Smoke/ObserverProbeScenario.cs`).
  It asserts `page_ids` in the PostgreSQL batch text (≈47). It runs only by hand
  (`[Explicit]`, as `Given_Postgresql_ObserverProbe`), as the checkpoint before a harness evidence
  run, so CI would not notice a break. Keep the `page_ids` CTE name, as the prototype does, or move
  the PostgreSQL marker to the new selecting statement. Reword its comment (≈43–44), which says
  the batch is recognizable by the keyset temp table: PostgreSQL has none after this story.
  SQL Server's `[#page]` marker stays valid, because P1 keeps `#page`.
- **Test churn and new tests** (see Test requirements).
- **Manifest goldens.** Regenerate with `UPDATE_GOLDENS=1` and commit them in the same PR. CI never
  regenerates goldens. Each golden holds an `mssql` and a `pgsql` block, and the regeneration
  rewrites both. Every golden's `mssql` block must stay byte-identical to `main`: this story
  compiles no new SQL Server SQL (the candidate batch is built at run time, not compiled into the
  mapping set), so any SQL Server hunk in the diff is an accidental change. The only allowed
  exception is a deliberate change to the shared `keyset_table` shape, which the PR names, with
  every SQL Server `*_sql_sha256` still equal to `main`'s.
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
   in a transaction with `default_transaction_read_only = on`. Resource GET-many routed to a read
   replica whose database forces `default_transaction_read_only = on` returns 200 with the
   replica's rows (the DMS-1687 path).
8. Every multi-document request uses the same number of database commands as on `main`: resource
   GET-many 1, cache hit 2.
9. Binding is unchanged on SQL Server. On PostgreSQL:
   - a selected page binds one `bigint[]` at every page size (tested at 1, 25, and 500, the
     default `MaximumPageSize`);
   - a query keyset binds at most one parameter more than `main`;
   - the single-document fast-path command binds exactly the parameters it binds on `main`.

### Correctness (C1–C6)

- **C1.** Every hydration result set keeps the fixed authorized document set and the selected-page
  order. Nothing runs per document. PostgreSQL P1–P3 and SQL Server P2 lose the keyset table's
  `PRIMARY KEY ("DocumentId")`, which today fails the insert on a duplicate selected id (`23505`,
  `2627`). A duplicate would now be hydrated twice without an error. Page selection cannot produce
  one, and P3's ids come from P2's candidates: `Given_A_Postgresql_Compiled_Candidate_Relation` and
  `Given_A_Mssql_Compiled_Candidate_Relation` prove one row per `DocumentId` for every
  authorization shape, and they are the guard that must stay green. SQL Server P1 and P3 keep
  `#page` and its key.
- **C2.** Root and child ordering, descriptor projections, reference lookups, and the
  reconstructed responses are unchanged.
- **C3.** Total counts, empty and zero-limit pages, offsets, cursor ordering modes, and
  continuation anchors are unchanged.
- **C4.** Concurrent inserts, updates, and deletes neither weaken authorization nor change the
  isolation contract. The version fences and the three-attempt retry behave as on `main`.
- **C5.** The single-document fast path (GET by id, writes, cache materialization) produces the
  same SQL and binding as on `main`.
- **C6.** Plan contracts, SQL generation, binding, readers, manifests, goldens, tests, the
  performance harness's PostgreSQL plan capture and observer probe, and the design documents above
  change in the same PR. Story 2 adds the release's changelog entry for DMS-1687; if story 2
  misses the release, this story adds it for resource GET-many instead.

### Performance thresholds

Measured with R1 (PostgreSQL) and R2 (SQL Server, read-acceleration fixture plus the resource
fixture for N11). Three interleaved sessions: `main`, then the story branch, three times, on one
machine. Ratios are story branch ÷ `main`, and `m = min(max(2 × CV, 5%), 20%)` from `main`'s
three runs. A cell whose `2 × CV` exceeds 20% is rerun; if it still exceeds 20%, the PR reports it
as inconclusive for reviewer sign-off instead of counting it as met. PostgreSQL cells are on `primary-500k` at 25 and 500 rows; resource cells at offset 0, shallow,
and deep. 500 is the default `MaximumPageSize`; a deployment configured above it runs outside this
evidence (findings, [Recommendation](keyset-temp-table-findings.md#recommendation), Page-size
bound).

| Id | Metric | Bound | Spike result |
| --- | --- | --- | --- |
| E1 | PostgreSQL catalog `n_tup_ins` + `n_tup_del` per request on the resource, candidate, and fallback batches (calibrated observer) | = 0 | 0 (`main` 44) |
| E2 | SQL Server temp tables per candidate batch: XE `object_created`. `Temp Tables Creation Rate` is reported, not gated (server-wide); the CI batch-text check guards against table variables | XE = 0 in every window; a window whose server counter reads non-zero is repeated | 0 (`main` 1) |
| E3 | SQL Server `sql_statement_recompile` events and `plan_generation_num` change after warmup | = 0 | 0 |
| E4 | PostgreSQL P1–P3 under `default_transaction_read_only = on` (backend batch tests), and resource GET-many through the API from a read-only read replica | succeed, exact rows; API 200 with the replica's rows | probe passed (P1 and P3 shapes); API not run |
| E5 | SHA-256 of the SQL Server P1 and P3 batch texts | equal to `main` | – |
| E6 | Database commands per request | equal to `main` | equal |
| P1 | PostgreSQL resource GET-many API p50 | ratio ≤ 0.74 − m | 0.29–0.47 |
| P2 | PostgreSQL resource GET-many API p95 | ratio ≤ 0.74 − m | 0.30–0.48 |
| P3 | Child-collection execution time per request, 25 and 500 rows (driver-path `auto_explain`: the resource API window, custom plans; the one-connection replay windows for the resource and fallback batches, generic plans) | ratio ≤ 0.52 − m | 0.002–0.021 (the resource one-connection window was not run; see below) |
| P4 | Child-collection buffers per request, same windows as P3 | ratio ≤ 0.74 − m | 0.023–0.466 |
| P5 | Fallback batch replay p50 and p95 at concurrency 1, and p95 at 32 | ratio ≤ 0.59 − m | 0.02–0.17 |
| N2 | PostgreSQL cache-hit API p50 / p95 | ratio ≤ 1 + m | 0.84–0.88 |
| N3 | PostgreSQL forced-miss complete API p50 / p95 | ratio ≤ 1 + m | 0.69–0.90 |
| N4 | PostgreSQL candidate replay p50 at c = 1, p95 at c = 32; throughput at c = 8 and 32 | latency ≤ 1 + m; throughput ≥ 1 − m | 0.19–0.43; ×2.1–×5.9 |
| N5 | PostgreSQL plan time per request (resource, candidate, fallback) | ratio ≤ 1 + m | 0.06–0.95 (resource and fallback) |
| N6 | PostgreSQL WAL bytes per request, clean windows | ratio ≤ 1 + m | 0 |
| N7 | SQL Server cache-hit API p50 / p95. The forced-miss complete API p50 / p95 is reported, not gated: both variants sit at the 5 s direct-fill timeout (p50 5,282–5,335 ms), so it could flag only a regression above about 265 ms | ratio ≤ 1 + m (cache hit) | 0.37–1.00 |
| N8 | SQL Server candidate replay p50 / p95 at c = 1, p95 at c = 32 | ratio ≤ 1 + m | 0.06–0.78 |
| N9 | SQL Server candidate replay throughput at c = 8 and 32 | ratio ≥ 1 − m | ×1.89–×2.68 at 32; ×1.20–×1.30 at 8 (`main` CV 22–24%: rerun under the noisy-cell rule) |
| N10 | SQL Server candidate logical reads per request | ratio ≤ 1 + m | 0.81 |
| N11 | SQL Server resource GET-many API p50 / p95 (P1 unchanged) | ratio ≤ 1 + m | 0.94–1.02 |

P3 and P4 gate child-collection cost under both PostgreSQL plan types, for both batches that load
child rows. The API window runs custom plans only: the in-process host never publishes data-store
ownership, so each request runs on a new backend. Production's pooled connections auto-prepare, so
the readers reach generic plans there. R1 therefore also replays the offset-0 resource batch and the
fallback batch on one connection, 20 times, and records that connection's
`pg_prepared_statements` (`autoexplain/<label>.prepared.json`). A one-connection window counts only
when, on the story branch, it shows `generic_plans > 0` for every keyset reader; otherwise it is
rerun. This gate holds whichever binding option the story picks (see Prepared plans), and it matters
most if the story gives each keyset kind its own relation: carrier-only resource readers would have
an estimate frozen at their third use. The spike ran the one-connection window for the fallback
batch only. Under the prototype it shares the resource batch's reader text, and its child time was
0.27 ms generic against 0.30 ms custom at 25 rows, and 3.57 against 3.54 ms at 500. The PR also
reports the plan node types (`Index Scan` expected) as supporting evidence; they are not a
threshold.

Beside N8 the PR reports the SQL Server candidate batch's p95 and p99 at concurrency 8 and 32, at
25 and 500 rows (reported, not gated; findings
[Recommendation](keyset-temp-table-findings.md#sql-server-inline-on-descriptor-and-candidate-metadata-page-stays-on-p1-and-p3),
item 1, records why the tail is accepted on this opt-in path). The prototype raised the p99 at 32
sessions from 70.8 to 314 ms at 500 rows, and in one of three sessions its p95 at 8 sessions and
25 rows from 5.9 to 63.2 ms. If the story's design keeps either tail, the PR names its cause (for
example, memory-grant or parallelism waits on the `OPENJSON` statements).

## Test requirements

Each new test must fail if the change is reverted.

**Unit** (`Backend.Plans.Tests.Unit`, `Backend.Tests.Unit`, `Performance.Harness.Tests.Unit`):

- Result-set positions and shapes for each batch kind in scope (query keyset, selected page,
  candidate metadata): non-empty, empty, zero-size, zero-limit, and `ContentVersion`-anchored
  pages, on each engine in scope.
- Order of operations (AGENTS.md): the PostgreSQL carrier is set in the selecting statement,
  before any statement that reads it, and the selected keys come from selection, not hydration.
- Binding: a PostgreSQL selected page binds one `bigint[]` at 1, 25, and 500 (the default
  `MaximumPageSize`); the fast-path command's parameters are unchanged; SQL Server binding is
  unchanged.
- Reserved keyset names, modeled on
  `Given_RelationalQueryPageKeysetPlanner.It_should_assign_collision_free_parameter_names_for_reserved_and_sanitized_query_field_collisions`
  (≈462): a query field that collides gets `page_document_ids_2` (PostgreSQL) or `page_keyset_2`
  (SQL Server), and each built query-keyset and candidate command carries each name once.
- Guarded single-document keyset without the fast path, PostgreSQL: no `set_config`, no keyset
  parameter, `@DocumentId` read under the guard predicate, and `GetResultSetCount` unchanged. The
  SQL Server guarded test stays unchanged.
- `PgsqlPlanCaptureTests`: split this story's PostgreSQL resource batch (literals included) and
  bind its keyset parameter. The existing cases use literal-free text, and the harness pipelines
  do not run in CI unless `PERF_ALLOW_CI` is set.
- Update the old PostgreSQL text and parameter pins and the candidate pins on both engines: about
  60–70 of task 7's 112 failures, in `Given_HydrationBatchBuilder_*`, `Given_ReadPlanCompiler`,
  `Given_PgsqlPlanDialect`, `Given_PlanSqlWriterExtensions`, `Given_A_Keyset_That_Carries_No_Anchor`,
  and `Given_RelationalQueryRequestPreprocessor+Given_CompactDescriptorFilters`. Regenerate the 8
  manifest goldens with no hunk in an `mssql` block. The SQL Server P1 and P3 pins stay unchanged
  as the byte-identity guard.

**Integration** (`Backend.Postgresql.Tests.Integration`, `Backend.Mssql.Tests.Integration`):

- **No temp tables** (E1, E2), with deterministic per-session signals (the calibrated counters
  stay in R1 and R2).
  - PostgreSQL, in a test-owned transaction: before commit, `n_tup_ins` in
    `pg_stat_xact_sys_tables` for `pg_class`, `pg_attribute`, `pg_type`, `pg_depend`, `pg_index`,
    `pg_constraint`, and `pg_statistic` is 0. `main`'s keyset prelude, as the positive control,
    reads more than 0.
  - SQL Server candidate batch: the text has no `#`-prefixed identifier and no `DECLARE … TABLE`.
- **Carrier.** The setting is transaction-local, so the leak tests share one test-owned
  transaction:
  - `Given_Postgresql_Query_Keysets_In_One_Transaction`: a 25-id page, a zero-size page, a 1-id
    page, then an offset past the end. Each page returns exactly its own documents and keys. It
    catches an empty path that skips `set_config` (for example, a copy of
    `AppendEmptyKeysetMaterialization`) and a `set_config` evaluated per row.
  - `Given_A_Query_Keyset_Then_An_Empty_Selected_Page_In_One_Transaction`: the empty selected page
    returns no documents (bound as `NULL`, it would read the first page's carrier).
  - On one pooled connection, outside a shared transaction: a non-empty page, a failed batch
    (`SELECT 1 / 0` after the carrier is set), then an empty page. The carrier reads `''` after
    each commit or rollback, which catches `is_local = false`.
- **Read-only** (E4): resource GET-many, candidate selection, and a selected page under
  `default_transaction_read_only = on`. `main`'s prelude fails with `25006` as the control.
- Keep `Given_A_Postgresql_Selected_Page_Keyset_With_Nonascending_DocumentIds` green.
- **Adapt** `Given_A_Pgsql_Query_Keyset_That_Returns_Its_Selected_Ids` (both deleted-before-hydration
  tests): splice the `DELETE`s after the statement that selects the page and sets the carrier. Its
  SQL Server twin is unchanged.
- **Delete**
  `Given_Two_Postgresql_Hydration_Batches_On_The_Same_Transaction.It_keeps_the_pgsql_temp_table_alive_until_commit_between_hydration_calls`;
  the first carrier test replaces it.
- **Rewrite** `DescriptorProjectionFixture` (`PostgresqlDescriptorProjectionTests`, 5 classes, 13
  tests, not run by task 7). Its two SQL strings (≈61–77, ≈154–164) join the literal `"page"` and
  would fail with `42P01`. Compile the plan with `ReadPlanCompiler`, as the other descriptor
  projection tests do.
- **C5.** `RelationalWriteSmokeTests`, `DocumentCacheMaterializer*Tests`,
  `*Single_Document_Fast_Path*`, and `MssqlWriteSessionCommandStreamTests` pass unchanged, except
  two PostgreSQL negative checks that this story makes vacuous:
  - `Given_HydrationExecutor_Single_Document_Fast_Path_With_DescriptorProjection_And_DocumentReferenceLookup.It_generates_a_batch_without_the_page_temp_table`
    (`HydrationExecutorTests.cs` ≈725–730): replace its three `NotContain` checks with a positive
    check that the batch starts with the direct metadata select on
    `WHERE d."DocumentId" = @DocumentId`. Do not anchor on `set_config`.
  - `Given_HydrationBatchBuilder_With_Pgsql_Single_Document_Fast_Path.It_should_not_emit_pgsql_page_temp_table_work`:
    delete it; its fixture's `StartWith` and reader-order tests already catch a fallback.

  The SQL Server twin (≈956) stays, because SQL Server keeps `#page`.

**API integration** (`Tests.Integration`, both engines, full suites). These pass:
`CursorPagingExecutionScenario`, `CursorPartitionAuthorizationMatrixScenario`,
`CursorPartitionConcurrencyScenario`, `CursorEmptyHydrationScenario`,
`WindowedCursorAnchoringScenario`, `CursorPublicContractScenario`, `LimitZeroCountReadScenario`
(with `Given_*_LimitZeroCountReads_DocumentCache`), `AuthoritativeTimestampReadConsistencyScenario`
(its split sites need no change), and `Given_*_DocumentCacheReadAcceleration`.

- **Read-only replica (DMS-1687, E4).** Add
  `Given_Postgresql_DerivativeRouting_With_A_ReadOnly_Replica` in
  `Tests.Integration/Tests/Postgresql/`, leasing a `ReadReplica`:
  1. Seed as `DerivativeRoutingSupport.SeedDistinguishableStudentsAsync` does, while the replica
     is still writable.
  2. From `postgres`, run `ALTER DATABASE <replica> SET default_transaction_read_only = on` and
     terminate the replica's sessions, as `PostgresqlTargetReachability.SetAllowConnectionsAsync`
     does.
  3. GET `/data/ed-fi/students?totalCount=true` with no snapshot header: 200, the replica's marker
     Student, and the replica's count (2).

  On `main` the same request returns 500, and the PR quotes the logged
  `25006: cannot execute DROP TABLE in a read-only transaction`. Story 2 adds its descriptor test
  to this class.

**Performance evidence.** Attach the R1 and R2 artifacts (see
[Thresholds](keyset-temp-table-findings.md#thresholds)) from three interleaved sessions on one
machine and a clean tree: about 2.5 h for PostgreSQL and 1.7 h per SQL Server session pair.
Budget a second SQL Server set, because `main`'s CV at concurrency 8 (22–24%) predicts an N9
rerun.

## Implementation guidance (non-binding)

`experiments/prototype.patch` (`DMS1521_KEYSET_VARIANT=inline`) passed every check for these paths.
It is a starting point; a production design may differ if it meets the criteria.

- **The seam.** Every P1–P3 keyset join renders through `PlanSqlWriterExtensions.AppendRelation`.
  The prototype's PostgreSQL relation unnests
  `COALESCE(CAST(@page_document_ids AS bigint[]), <the carrier split into bigint[]>) WITH ORDINALITY`.
  A selected page binds the array (an empty page binds an empty array, never `NULL`); a query
  keyset binds an untyped NULL and reads the carrier.
- **The query keyset statement** selects `page_ids` as today, calls
  `set_config('dms.page_keyset', …, true)` in a one-row `string_agg` aggregate, so it sets or
  clears the carrier even for an empty page, and unnests the result as the first result set.
- **SQL Server P2.** Give the candidate batch its own relation, for example by passing it to
  `DocumentMetadataColumns.AppendDocumentMetadataSelectBody`: the prototype's shared
  `AppendRelation` would change P1 and P3. Its carrier is
  `SELECT @page_keyset = (SELECT [DocumentId] FROM page_ids FOR JSON PATH)`, read with
  `OPENJSON … WITH`.
- **Prepared plans.** Do not force a plan mode. An untyped NULL and a `bigint[]` against the same
  reader text share no Npgsql prepared statement: bind the NULL as a typed `bigint[]`, or state the
  trade-off in the PR.
- **Reserved names.** Follow the `PgsqlCompositeTargetCarrier` and `MssqlCompositeTargetCarrier`
  conventions (DMS-1332), but reserve both keyset names, PostgreSQL's array parameter and SQL
  Server's `page_keyset`, in `RelationalQueryPageKeysetPlanner.DeriveParameterNames` (≈516–523):
  under the prototype, a colliding query field binds the wrong value.
- **Do not keep** `Dms1521KeysetVariant`, the environment switch, or the `pg-analyze` variant.

## Dependencies

- **Blocks** story 2, which reuses the carrier name, the set-before-read rule, and the SQL Server
  JSON carrier.
- **Relates to** DMS-1521 (the spike), DMS-848, and DMS-1687.
- **Fixes** DMS-1687 for resource GET-many (P1). The descriptor path follows in story 2.
- **Ready.** DMS-1521 accepted the PostgreSQL transaction-local carrier for production (findings,
  [Disposition](keyset-temp-table-findings.md#disposition)). The conditions are this story's
  in-transaction carrier tests and generic-plan gates (P3, P4).

## Estimate

**3 story points.** The prototype exists, but production work remains:

- a keyset contract that separates PostgreSQL from SQL Server P1 and P3, while SQL Server P2 gets
  its own relation;
- the guarded single-document keyset under the new contract (PostgreSQL only, reading the
  substituted `@DocumentId` token);
- the performance harness's PostgreSQL plan capture (literals and the keyset parameter) and its
  unit test;
- about 60–70 updated unit pins, the golden regeneration, and one shared integration fixture with
  hand-written `"page"` readers;
- six new integration tests (no temp tables on each engine, three carrier tests, read-only), one
  API integration test (read-only replica), and the binding unit tests;
- eleven design-document edits;
- one interleaved performance session set on each engine, plus the likely SQL Server rerun (N9).
