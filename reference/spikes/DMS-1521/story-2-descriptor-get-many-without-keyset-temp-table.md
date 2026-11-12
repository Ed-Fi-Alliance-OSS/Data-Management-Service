# Story 2: Descriptor GET-many without `descriptor_page`

[Back to DMS-1521 story index](candidate-implementation-stories.md)

## Summary

Stop creating and dropping `descriptor_page` (`#descriptor_page` on SQL Server) on every descriptor
GET-many request, on both engines. Carry the selected descriptor ids in the batch, using the keyset
carrier that story 1 defines.

- **Engines:** PostgreSQL and SQL Server.
- **Path:** P4, descriptor GET-many (`DescriptorReadHandler.BuildQueryCommand`).
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
sizes, because each batch selects its own page and each reader parses the carrier only once.

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
- **The `-- Descriptor acquisition:` and `-- Descriptor page selection boundary` markers are kept**,
  so `DescriptorReadCommandExecutor` (`Backend.Tests.Unit`) needs no change. If the markers
  change, that executor changes with them.
- **Test churn:** the 18 `Given_DescriptorReadHandler` SQL-text pins, the SQL Server
  type (d) adaptation of the descriptor split site in
  `AuthoritativeTimestampReadConsistencyScenario`, and the new tests below.
- **The design-document edits** listed below.

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
| `partitioned-cursor-paging.md` | descriptor paragraph of § Carrying the selected-keyset boundary (≈711) | Reconcile the [drift](keyset-temp-table-findings.md#design-drift-descriptor_page). Descriptor selection is carried in the keyset carrier on both engines, with no keyset temp table. The fences, owner rows, and boundary read that one selection per attempt. Keep the existing anchor-column discipline |
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
   `default_transaction_read_only = on`.
8. Binding is unchanged on both engines: `resourceKeyId`, `offset`, and `limit`, plus any filter
   parameters.

### Correctness (C1–C6)

- **C1.** Every result set keeps the authorized descriptor set and the selected-page order. Nothing
  runs per document.
- **C2.** Descriptor responses (URI, code value, namespace, and metadata) are unchanged.
- **C3.** Total counts, empty and zero-limit pages, offsets, cursor ordering modes, and
  continuation anchors are unchanged.
- **C4.** Concurrent inserts, updates, and deletes neither weaken authorization nor change the
  three-attempt acquisition and fence contract, under SQL Server locking `READ COMMITTED` as well
  as RCSI.
- **C5.** No single-document path changes.
- **C6.** The handler, its tests, the test doubles, and the design documents above change in the
  same PR.

### Performance thresholds

Measured with R1 (PostgreSQL) and R2 (SQL Server), descriptor fixture (`descriptors-25k`) at limits
25 and 500. Three interleaved sessions: `main`, then the story branch, three times, on one machine.
Ratios are story branch ÷ `main`, and `m = max(2 × CV, 5%)` from `main`'s three runs.

| Id | Metric | Bound | Spike result |
| --- | --- | --- | --- |
| E1 | PostgreSQL catalog `n_tup_ins` + `n_tup_del` per descriptor request (calibrated observer) | = 0 | 0 (`main` 42) |
| E2 | SQL Server temp tables per descriptor request: XE `object_created` **and** `Temp Tables Creation Rate` | = 0 on both | 0 (`main` 1) |
| E3 | SQL Server `sql_statement_recompile` events and `plan_generation_num` change after warmup | = 0 | 0 |
| E4 | PostgreSQL descriptor GET-many under `default_transaction_read_only = on` | succeeds, exact rows | – |
| E6 | Database commands per request | 1, equal to `main` | 1 |
| N1 | PostgreSQL descriptor API p50 / p95 | ratio ≤ 1 + m | 0.73–0.99 |
| N4 | PostgreSQL descriptor replay p50 at c = 1, p95 at c = 32; throughput at c = 8 and 32 | latency ≤ 1 + m; throughput ≥ 1 − m | 0.12–0.46; ×1.8–×8.8 |
| N5 | PostgreSQL plan time per descriptor request | ratio ≤ 1 + m | (plans per call 0.25–0.26, against 0.91–1.02 on `main`) |
| N6 | PostgreSQL WAL bytes per descriptor request | ratio ≤ 1 + m | 0 |
| N7 | SQL Server descriptor API p50 / p95 | ratio ≤ 1 + m | 0.98–1.01 |
| N8 | SQL Server descriptor replay p50 / p95 at c = 1, p95 at c = 32 | ratio ≤ 1 + m | 0.05–0.93 |
| N9 | SQL Server descriptor replay throughput at c = 8 and 32 | ratio ≥ 1 − m | ×2.15–×2.25 at 32 |
| N10 | SQL Server descriptor logical reads per request | ratio ≤ 1 + m | 0.87 at 500 rows |

## Test requirements

**Unit** (`Backend.Tests.Unit`):

- Update the 18 `Given_DescriptorReadHandler` SQL-text pins on both engines.
- Add behavior tests for the result-set sequence and markers on non-empty, empty, zero-limit, and
  `ContentVersion`-anchored pages.
- Pin the order of operations (AGENTS.md):
  - the carrier is set before the leading fence reads it;
  - the boundary comes from selection, not from the owner rows.
- `DescriptorReadCommandExecutor` must keep recognizing the batch and computing the boundary
  without a change. If it needs one, the markers changed and C6 applies.

**Integration** (`Backend.Postgresql.Tests.Integration`, `Backend.Mssql.Tests.Integration`):

- **No temp tables** (E1, E2). Use the deterministic, per-session signals of story 1. The
  calibrated counter protocol stays in the R1 and R2 evidence only.
  - PostgreSQL: run the descriptor batch inside a test-owned transaction. Before commit, the seven
    catalog tables' `n_tup_ins` in `pg_stat_xact_sys_tables` reads 0. `main`'s `descriptor_page`
    prelude, run the same way, is the positive control and reads more than 0.
  - SQL Server: the descriptor batch text contains no `CREATE TABLE` and no `DECLARE … TABLE`.

  Each test must fail if the change is reverted.
- **Carrier** (PostgreSQL). The setting is transaction-local, so the leak test shares one
  test-owned transaction: a non-empty page, an empty page (offset past the end), a zero-limit
  page, and a non-empty page again. Each returns exactly its own boundary, version ids, and owner
  rows, and the empty and zero-limit pages return none. It fails if the zero-limit path skips
  `set_config`, as `main`'s zero-limit shortcut skips selection (`DescriptorReadHandler.cs`
  ≈2047–2058). Separately, on one pooled physical connection, a failed batch followed by an empty
  page, with the carrier reading `''` after each, catches a session-level carrier.
- **Residual estimate risk** (PostgreSQL). The descriptor readers have no parameters, so their
  plan is always generic, and its keyset estimate is frozen at the first page planned on that
  connection. Read a 1-row page and then a 500-row page on the same connection, and report the
  500-row batch's time against a fresh connection. The spike did not measure this sequence.
- **Read-only** (E4), with `main`'s DDL prelude failing with SQLSTATE 25006 as the positive
  control.
- `*DescriptorReadQueryFilterTests` and `*DescriptorQueryOwnershipAuthorizationTests` pass on
  both engines.

**API integration** (`Tests.Integration`, both engines, full suites): `DescriptorRuntimeScenario`,
`CompactDescriptorResourceScenario`, `LimitZeroCountReadScenario`, the cursor scenarios, and
`AuthoritativeTimestampReadConsistencyScenario` pass.

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

**Performance evidence.** Attach R1 and R2 artifacts for the descriptor fixture (about 2–5 min per
run on each engine), from three interleaved sessions on one machine.

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

**3 story points**: one handler with two dialect branches, 18 text pins, one SQL Server test-double
adaptation, four integration tests, two design-document edits, and a short performance session
set on each engine.
