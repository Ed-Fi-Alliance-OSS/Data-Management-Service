# Story 2: Descriptor GET-many without `descriptor_page`

[Back to DMS-1521 story index](candidate-implementation-stories.md)

## Summary

Stop creating and dropping `descriptor_page` (`#descriptor_page` on SQL Server) on every descriptor
GET-many request, on both engines. Carry the selected descriptor ids in the batch, using the keyset
carrier that story 1 defines.

- **Engines:** PostgreSQL and SQL Server.
- **Path:** P4, `DescriptorReadHandler.BuildQueryCommand`. It has two callers, and both change:
  descriptor GET-many (`ReadQueryRowsAsync`, `DescriptorRowProjection.FullRow`) and descriptor
  read-acceleration candidate selection (`ReadQueryCandidateRowsAsync`,
  `DescriptorRowProjection.CandidateMetadata`).
- **Reconciles** the design drift in `partitioned-cursor-paging.md`, and adapts the split-command
  consistency test double on SQL Server.
- **Completes** the [DMS-1687](https://edfi.atlassian.net/browse/DMS-1687) fix on PostgreSQL (cloned from
  [EDFI-2956](https://edfi.atlassian.net/browse/EDFI-2956)).
- **Evidence:** [DMS-1521 findings](keyset-temp-table-findings.md), with the prototype in
  [`experiments/prototype.patch`](experiments/prototype.patch).

## Description

DMS-1401 (`8a4eda2f6`) made descriptor GET-many materialize its selected page into
`descriptor_page`, so that both version fences, the owner rows, and the `MAX(...)` continuation
boundary read one selection per attempt. That restored the read-consistency contract. It also
brought back the per-request temp-table lifecycle on a path that had none. DMS-1521 measured the
cost:

- **PostgreSQL:** 42 catalog tuples and about 10 KB of WAL per request. A fixed 2,260-row
  estimate. Failure in a read-only transaction (DMS-1687).
- **SQL Server:** one temp table per request, and 12.7–14.3 contended `PAGELATCH` waits per request
  at 32 sessions.

`partitioned-cursor-paging.md` still says descriptor selection "needs no keyset temp table".

The spike's `inline` prototype carries the selected ids in the batch on both engines. Its results,
against `main`:

- **PostgreSQL:** descriptor API p50 0.73× at 25 rows and 0.99× at 500, replay throughput ×1.8–×8.8,
  and no catalog rows or WAL.
- **SQL Server:** API within ±2%, replay latency 0.45–0.93× at concurrency 1, throughput ×2.2 at
  32 sessions, fewer logical reads, and no temp table.

Correctness validation found no behavior change on either engine. The descriptor and
candidate-metadata paths are the two where SQL Server's `inline` passed every criterion at both page
sizes, because they read the carrier in only four (descriptor) or two (candidate) statements, against
about eleven in the hydration batches, so the extra parses cost less than the create, drop,
insert, and latch they replace.

## Scope

**In scope:**

- **`DescriptorReadHandler`** drops the `descriptor_page` DDL and insert
  (`DescriptorReadHandler.cs` ≈2025–2045).
  - **PostgreSQL:** page selection sets the transaction-local carrier in the boundary statement,
    which still returns `MAX(anchor)`.
  - **SQL Server:** page selection fills the `nvarchar(max)` carrier with `FOR JSON PATH` where
    `#descriptor_page` was filled.

  The leading and trailing version fences, the owner-row statement, and the boundary read the
  carrier. A zero limit still selects nothing; SQL Server rejects `FETCH NEXT 0`.
- **The `-- Descriptor acquisition:` and `-- Descriptor page selection boundary` markers are kept.**
  `DescriptorReadCommandExecutor` (`Backend.Tests.Unit`) finds the batch by these markers. It also
  picks the anchor column by matching `MAX("ContentVersion")` or `MAX([ContentVersion])` in the
  command text (`DescriptorReadCommandExecutor.cs` ≈80). The prototype's qualified
  `MAX(p."ContentVersion")` and `MAX(kj.[ContentVersion])` do not match, so the executor would
  report the highest `DocumentId` as an anchored page's boundary. Either write the anchor
  unqualified in the boundary `MAX`, or widen that check in the same PR (C6).
- **Test churn:** the 18 `Given_DescriptorReadHandler` SQL-text pins, the SQL Server
  type (d) adaptation of the descriptor split site in
  `AuthoritativeTimestampReadConsistencyScenario`, and the new tests below.
- **The design-document edits** listed below.
- **The `TEMPORARY` grant.** After this story, no PostgreSQL read creates a temp table: story 1
  removes P1–P3, this story removes P4, and these are the only two producers of `CREATE TEMP TABLE`
  in production code. Story 1 cannot make these edits alone, because `descriptor_page` still needs
  the privilege until this story lands.
  - `docs/SNAPSHOTS-AND-READ-REPLICAS.md`: drop `TEMPORARY` from the PostgreSQL recipe's grant
    (≈85), from the minimum grant set for DMS reads (≈98–102), and from the failure table, whose
    `42501: permission denied to create temporary tables` row describes GET-many (≈112). Read
    replicas use the same grants (≈204).
  - `eng/verification/PublisherSnapshotInterop/PublisherSnapshotInterop.psm1` (≈809) grants
    `CONNECT, TEMPORARY`, and `eng/verification/tests/PublisherSnapshotInterop.Tests.ps1`
    (≈137–139) asserts that grant "because DMS creates temp tables on GET-many". Grant `CONNECT`
    only, and update the assertion. Also rewrite the `Get-InteropPostgresReadOnlySql` docstring
    (`PublisherSnapshotInterop.psm1` ≈773–776), which says `TEMPORARY` "has to be granted back
    (DMS creates temp tables on a GET-many)": only `CONNECT` is granted back, beside the existing
    `USAGE` and `SELECT` grants.

**Out of scope:**

- The resource paths P1–P3 and the shared keyset contract (story 1).
- Descriptor GET by id, which takes no keyset.
- The descriptor selection SQL, authorization predicates, ordering, and response shape. Only how
  the selected ids are carried changes.
- DDL, provisioning, and `RelationalMappingVersion`.

## Design documents to update

Each edit is **mandated by DMS-1521's decision**
([Recommendation](keyset-temp-table-findings.md#recommendation)). Line numbers are as of
`3621b1eb6`; the paths are under `reference/design/backend-redesign/design-docs/`.

| Document | Section | Edit |
| --- | --- | --- |
| `partitioned-cursor-paging.md` | descriptor paragraph of § Carrying the selected-keyset boundary (≈711–715) | Reconcile the [drift](keyset-temp-table-findings.md#design-drift-descriptor_page). Descriptor selection is carried in the keyset carrier on both engines, with no keyset temp table. The fences, owner rows, and boundary read that one selection per attempt. Replace the sentence that says the page-rows statement projects an aliased anchor and the handler takes its maximum from that alias (≈713–715; stale since DMS-1401): the boundary statement takes `MAX` of the anchor from the selection. Keep the rule that the anchor is the value page selection ordered by, not the copy in `dms.Document` |
| `transactions-and-concurrency.md` | descriptor acquisition (≈660) | "materializes its authorized DocumentId keyset once" becomes "selects its authorized DocumentId keyset once and carries it in the batch", with selection-time continuation anchors kept. State that descriptor GET-many issues no DDL on PostgreSQL and runs in read-only transactions |

## Acceptance criteria

### Behavior

1. A descriptor GET-many batch creates, drops, and analyzes no table on PostgreSQL. On SQL Server
   it creates no temp table and declares no table variable.
2. Each acquisition attempt selects the page once. The leading version fence, owner rows,
   boundary, and trailing version fence all read that same selection.
3. The batch keeps its result-set sequence, its markers, and its single database command per
   request.
4. The boundary is computed from the selected keys, not from the returned rows. Continuation is
   correct when every selected descriptor is deleted before the owner rows are read.
5. Empty, zero-limit, offset, and cursor pages return the same rows, totals, and
   `Next-Page-Token` as on `main`.
6. On PostgreSQL, the carrier is set or cleared in the selecting statement, before any later
   statement reads it. No batch reads a previous batch's ids: not on a reused pooled connection
   (`NoResetOnClose`), not after an empty page, and not after a failed batch.
7. On PostgreSQL, descriptor GET-many succeeds in a transaction with
   `default_transaction_read_only = on`. Descriptor GET-many routed to a read replica whose
   database forces `default_transaction_read_only = on` returns 200 with the replica's rows (the
   DMS-1687 path).
8. Binding is unchanged on both engines: `resourceKeyId`, `offset`, and `limit`, plus any filter
   parameters.

### Correctness (C1–C6)

- **C1.** Every result set keeps the authorized descriptor set and the selected-page order. Nothing
  runs per document. On both engines the carrier drops `descriptor_page`'s
  `PRIMARY KEY ("DocumentId")`, which today fails the insert on a duplicate selected id, so a
  duplicate would now be returned twice without an error. Page selection cannot produce one: the
  descriptor planner tests in the candidate-uniqueness fixtures
  (`Given_A_{Postgresql,Mssql}_Compiled_Candidate_Relation.It_should_yield_one_row_per_document_id_for_every_descriptor_planner_consumer`)
  are the guard that must stay green.
- **C2.** Descriptor responses (URI, code value, namespace, and metadata) are unchanged.
- **C3.** Total counts, empty and zero-limit pages, offsets, cursor ordering modes, and
  continuation anchors are unchanged.
- **C4.** Concurrent inserts, updates, and deletes neither weaken authorization nor change the
  three-attempt acquisition and fence contract, under SQL Server locking `READ COMMITTED` as well
  as RCSI.
- **C5.** No single-document path changes.
- **C6.** The handler, its tests, the test doubles, the design documents above, and the
  `TEMPORARY` grant in the snapshot doc and interop harness change in the same PR, and the PR
  records a passing `CONNECT`-only PostgreSQL interop run. The PR also
  adds an entry to `docs/changelog/8.2.0.md`, the changelog for fix version v8.2 (create the file
  if the release has none yet). The entry says that resource and descriptor GET-many work on a
  PostgreSQL read replica and in any read-only transaction
  ([DMS-1687](https://edfi.atlassian.net/browse/DMS-1687)), and that the PostgreSQL reader role
  no longer needs `TEMPORARY` (`docs/SNAPSHOTS-AND-READ-REPLICAS.md`), although DMS 8.1 still
  does. It cites only the ticket and that doc.

### Performance thresholds

Measured with R1 (PostgreSQL) and R2 (SQL Server) run with `-Fixtures resource,descriptor`, judged
on the descriptor fixture (`descriptors-25k`) at limits 25 and 500. The resource run is there to
calibrate the observer: the run script calibrates only in the resource fixture, so a
descriptor-only session would read E1 and E2 zeros from an unproven observer. 500 is the default
`MaximumPageSize`; a deployment configured above it runs outside this evidence (findings,
[Recommendation](keyset-temp-table-findings.md#recommendation), Page-size bound). Three interleaved
sessions: `main`, then the story branch, three times, on one machine.
Ratios are story branch ÷ `main`, and `m = min(max(2 × CV, 5%), 20%)` from `main`'s three runs.
A cell whose `2 × CV` exceeds 20% is rerun; if it still exceeds 20%, the PR reports it as
inconclusive for reviewer sign-off instead of counting it as met.

| Id | Metric | Bound | Spike result |
| --- | --- | --- | --- |
| E1 | PostgreSQL catalog `n_tup_ins` + `n_tup_del` per descriptor request (calibrated observer) | = 0 | 0 (`main` 42) |
| E2 | SQL Server temp tables per descriptor request: XE `object_created`. `Temp Tables Creation Rate` is reported, not gated (server-wide); the CI batch-text check guards against table variables | XE = 0 in every window; a window whose server counter reads non-zero is repeated | 0 (`main` 1) |
| E3 | SQL Server `sql_statement_recompile` events and `plan_generation_num` change after warmup | = 0 | 0 |
| E4 | PostgreSQL descriptor GET-many under `default_transaction_read_only = on` (backend batch test), and through the API from a read-only read replica | succeeds, exact rows; API 200 with the replica's rows | – |
| E6 | Database commands per request | 1, equal to `main` | 1 |
| N1 | PostgreSQL descriptor API p50 / p95 | ratio ≤ 1 + m | 0.73–0.99 |
| N4 | PostgreSQL descriptor replay p50 at c = 1, p95 at c = 32; throughput at c = 8 and 32 | latency ≤ 1 + m; throughput ≥ 1 − m | 0.12–0.46; ×1.8–×8.8 |
| N5 | PostgreSQL plan time per descriptor request | ratio ≤ 1 + m | (plans per call 0.25–0.26, against 0.91–1.02 on `main`) |
| N6 | PostgreSQL WAL bytes per descriptor request | ratio ≤ 1 + m | 0 |
| N7 | SQL Server descriptor API p50 / p95 | ratio ≤ 1 + m | 0.98–1.01 |
| N8 | SQL Server descriptor replay p50 / p95 at c = 1, p95 at c = 32 | ratio ≤ 1 + m | 0.05–0.93 |
| N9 | SQL Server descriptor replay throughput at c = 8 and 32 | ratio ≥ 1 − m | ×2.15–×2.25 at 32; ×1.20–×1.49 at 8 (`main` CV 13–24%: rerun under the noisy-cell rule) |
| N10 | SQL Server descriptor logical reads per request | ratio ≤ 1 + m | 0.87 at 500 rows |

## Test requirements

**Unit** (`Backend.Tests.Unit`):

- Update the 18 `Given_DescriptorReadHandler` SQL-text pins on both engines.
- Add behavior tests for the result-set sequence and markers on non-empty, empty, zero-limit, and
  `ContentVersion`-anchored pages, for both projections: `FullRow` (GET-many) and
  `CandidateMetadata` (read-acceleration candidate selection).
- Pin the order of operations (AGENTS.md):
  - the carrier is set before the leading fence reads it;
  - the boundary comes from selection, not from the owner rows.
- Re-anchor three existing checks that locate the selection by `INSERT INTO`. Once the insert is
  gone, `IndexOf("INSERT INTO")` returns -1 and a matcher on it matches nothing, so each would pass
  without checking anything. Under the prototype, the first and third passed this way. Anchor each
  on the selecting statement, `set_config(` on PostgreSQL and `FOR JSON PATH` on SQL Server, and
  assert that the anchor is found:
  - `DescriptorReadHandlerTests.Consistency.cs` ≈100–102
    (`It_discards_mixed_descriptor_acquisitions`): the selecting statement comes before
    `-- Descriptor acquisition: leading`.
  - `DescriptorReadHandlerTests.Consistency.cs` ≈245
    (`It_retains_the_selected_boundary_after_descriptor_rows_are_deleted`): on SQL Server,
    `FOR JSON PATH` comes before `-- Descriptor page selection boundary`. On PostgreSQL the
    selection runs inside the boundary statement, so `set_config(` lies between the boundary marker
    and the leading marker; keeping the existing `boundaryQuery > IndexOf(…)` form fails there.
    This test fails under the prototype only because of its qualified `MAX` (≈241); with an
    unqualified anchor it would pass unchanged while ≈245 checks nothing.
  - `DescriptorReadHandlerTests.cs` ≈2533
    (`It_wraps_a_provider_error_raised_by_descriptor_custom_view_selected_page_fallback`): match
    the page-selection command on `-- Descriptor page selection boundary`, which only
    `BuildQueryCommand` emits, instead of `INSERT INTO`.

  The `NotContain("JOIN")` check (≈247) needs no change: on PostgreSQL its slice now also holds the
  selection, which has no join in that test, and an owner-row boundary would still add one. The
  zero-limit tests' `NotContain("INSERT INTO")` checks (`DescriptorReadHandlerTests.LimitZero.cs`
  ≈52, ≈67, ≈107) become redundant, but each test still fails through its paging-fragment check;
  ≈123's `Contain("INSERT INTO")` is one of the 18 pins.
- `DescriptorReadCommandExecutor` must keep recognizing the batch and computing the boundary (see
  its two text dependencies under Scope). Add one non-empty `ContentVersion`-anchored page whose
  highest `DocumentId` and highest `ContentVersion` differ, with the boundary computed by the
  executor (no `selectedBoundaries`), so the test fails if the executor reads the wrong column. No
  existing test covers this: every anchored descriptor test passes `selectedBoundaries` or returns
  no rows.

**Integration** (`Backend.Postgresql.Tests.Integration`, `Backend.Mssql.Tests.Integration`):

- **No temp tables** (E1, E2). Use the deterministic, per-session signals of story 1. The
  calibrated counter protocol stays in the R1 and R2 evidence only.
  - PostgreSQL: run the descriptor batch inside a test-owned transaction. Before commit, the seven
    catalog tables' `n_tup_ins` in `pg_stat_xact_sys_tables` reads 0. `main`'s `descriptor_page`
    prelude, run the same way, is the positive control and reads more than 0.
  - SQL Server: the descriptor batch text names no `#` temp table (no `#`-prefixed identifier,
    which also covers `SELECT … INTO #`) and contains no `DECLARE … TABLE`.

  Each test must fail if the change is reverted.
- **Carrier** (PostgreSQL). The setting is transaction-local, so the leak test shares one
  test-owned transaction: a non-empty page, a zero-limit page, a non-empty page, then an empty
  page (offset past the end). Each empty page directly follows a non-empty one, so a skipped
  `set_config` would return the previous page's rows. Each page returns exactly its own boundary,
  version ids, and owner rows, and the zero-limit and empty pages return none. It fails if either
  path skips `set_config`, for example by copying `main`'s zero-limit shortcut, which skips
  selection (`DescriptorReadHandler.cs` ≈2047–2058).
- **Carrier across transactions** (PostgreSQL). On one pooled physical connection: a non-empty
  page, then a failed batch (an error after the boundary statement sets the carrier), then an
  empty page. The carrier reads `''` after each commit or rollback. The read after the non-empty
  page is the one that catches a session-level (`is_local = false`) carrier: PostgreSQL reverts
  even a session-level `set_config` when its transaction aborts, and an empty page writes `''`
  itself, so neither later read can.
- **Read-only** (E4), with `main`'s DDL prelude failing with SQLSTATE 25006 as the positive
  control.
- `*DescriptorReadQueryFilterTests` and `*DescriptorQueryOwnershipAuthorizationTests` pass on
  both engines.

**Verification harness.**

- Pester: `eng/verification/tests/PublisherSnapshotInterop.Tests.ps1` passes with the updated grant
  assertion (`CONNECT` only).
- Live interop run (required). The Pester assertion checks only the generated SQL text, and E4 runs
  as the tests' admin login, so neither shows that a `CONNECT`-only reader can serve DMS. From the
  story branch, run
  `pwsh eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine postgresql -Scenario All -Rebuild`.
  It builds local images, so the snapshot reader role holds `CONNECT` only, and it reads all seven
  source surfaces through that role (GET-many, `/partitions`, cursor pages, `totalCount`,
  `/deletes`, `/keyChanges`, `/availableChangeVersions`). Isolated and NoSnapshot must both pass;
  the PR records the run folder and both pass counts. The harness runs by hand, not in CI; the
  8.1.0 PostgreSQL run took about 5 minutes. SQL Server needs no run: `TEMPORARY` is a PostgreSQL
  grant.

**API integration** (`Tests.Integration`, both engines, full suites): `DescriptorRuntimeScenario`,
`CompactDescriptorResourceScenario`, `LimitZeroCountReadScenario`, the cursor scenarios,
`Given_*_DocumentCacheReadAcceleration_With_DescriptorRuntime` (the candidate-selection caller),
and `AuthoritativeTimestampReadConsistencyScenario` pass.

- **Read-only replica (DMS-1687, E4).** Add a descriptor test to story 1's read-only variant of
  `Given_Postgresql_DerivativeRouting`. During seeding, while the replica is still writable, post
  a marker `schoolTypeDescriptors` descriptor to the replica: the three databases come from one
  baseline, so nothing else tells the replica's descriptors apart. After the replica is switched
  to `default_transaction_read_only = on`, GET `/data/ed-fi/schoolTypeDescriptors?totalCount=true`
  with no snapshot header and assert 200 with the marker. As the positive control, the same
  request on story 1's branch (or `main`) returns 500 with `25006` from the `descriptor_page`
  prelude; the PR records that run.

- **Type (d) adaptation**, SQL Server descriptor split site (≈644–690). The double re-executes the
  descriptor command as two commands at the `-- Descriptor acquisition:` markers. A batch-scoped
  variable does not survive that split, so the double must carry the full carrier across it:
  1. Append a test-only `SELECT @page_keyset AS [<sentinel>]` to the first command.
  2. Remove that result set from the buffered tables before the production reader sees them.
  3. Start the second command with `DECLARE @page_keyset nvarchar(max) = @<carrier parameter>;`,
     bound as `nvarchar(max)`.

  Selection is not repeated, and the production batch text does not change. The boundary
  statement's own result (`MAX`) cannot rebuild the selected ids, so the full carrier is required.
  Remove the special case for the `CREATE TABLE` prelude at this site. The PostgreSQL split already
  runs in one transaction, so its carrier survives and needs no adaptation. The resource split
  site is story 1's concern; on SQL Server it is unchanged.

**Performance evidence.** Attach R1 and R2 artifacts from runs with `-Fixtures resource,descriptor`
(the resource run calibrates the observer; the descriptor run itself takes about 2–5 min per
engine), from three interleaved sessions on one machine. Budget a second SQL Server session set:
the spike's `main` CV at concurrency 8 (13–24%) predicts that N9's c = 8 cells trip the noisy-cell
rerun. Also report the **residual estimate
risk** on PostgreSQL: the descriptor readers have no parameters, so once Npgsql auto-prepares them
(on their third use, `AutoPrepareMinUsages = 3`) their plan is generic, and its keyset estimate is
frozen at the page of that third use; the first two uses are planned unprepared with the exact
carrier. On one physical connection, read a 1-row page three times, confirm in
`pg_prepared_statements` that the readers' `generic_plans` is above 0, then read a 500-row page.
Report its time against a connection whose first three pages held 500 rows. Also report the plan
the boundary statement uses: it now carries the page selection, and under `plan_cache_mode = auto`
it went generic after five executions in the spike's probe (`main` re-planned it on nearly every
call). This is reported evidence, not a CI test; the spike did not measure the sequence.

## Implementation guidance (non-binding)

`experiments/prototype.patch` (`DMS1521_KEYSET_VARIANT=inline`), `DescriptorReadHandler` and the
test-adaptation section, passed every check on both engines:

- **PostgreSQL.** The selection moves into the boundary statement after the count statement,
  because PostgreSQL has no parameterized statement that sets a value and returns no result set.
  The boundary statement sets the carrier with
  `set_config('dms.page_keyset', COALESCE(string_agg(…), ''), true)` from a one-row aggregate over
  the selection, and returns `MAX(anchor)`. Each statement takes its own snapshot under
  `READ COMMITTED` either way. The fences and owner rows read
  `unnest(CAST(string_to_array(current_setting('dms.page_keyset', true), ',') AS bigint[]))`.
  Use the carrier name and helpers that story 1 lands, not the prototype's.
- **SQL Server.** Use `DECLARE @page_keyset nvarchar(max);` followed by
  `WITH page_ids AS (…) SELECT @page_keyset = (SELECT … FROM page_ids FOR JSON PATH);`. The
  boundary reads `MAX(kj.[DocumentId])` (or the anchor) from `OPENJSON(@page_keyset) WITH (…) kj`.
  `OPENJSON` estimates 50 rows per execution; on `descriptors-25k` that cost nothing measurable.
- **The descriptor batch quotes its keyset itself** (`SELECT * FROM <table>`). It does not go
  through `AppendRelation`, so a change to the shared seam alone does not reach it.
- **Do not keep** the `Dms1521KeysetVariant` switch.

## Dependencies

- **Blocked by** story 1: the carrier name, the set-before-read rule, and the SQL Server JSON
  carrier.
- **Relates to** DMS-1521 (the spike), DMS-848, DMS-1687, and DMS-1401, which introduced
  `descriptor_page`.
- **Completes** DMS-1687 for the descriptor path.

## Estimate

**2 story points**: one handler with two dialect branches, 18 text pins, one SQL Server test-double
adaptation, five integration tests (no temp tables on each engine, two carrier tests, read-only), one API integration test (read-only replica), two design-document edits, the `TEMPORARY` grant in the snapshot doc and interop harness with one live PostgreSQL interop run, the changelog entry, and a short performance session
set on each engine, plus the likely SQL Server rerun (N9).
