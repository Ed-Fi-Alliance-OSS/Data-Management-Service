# DMS-1437 initial operational assessment (step 0.2)

Status: **approved** (2026-09-23). This is the spec §6.2 step 0.2 assessment. Its first version (`23294d57c`) and its follow-up (`5b0b864c5`) were reviewed; §0 records the review decisions and the follow-up verification of the adopted SQL. Step 0.2 is complete, and Q3 is closed for the initial settings decision: spec v3.2, the retained defaults, and both adjusted bounds are approved. Step 2.11 remains required. It verifies the implemented repositories and fences, keeps the 100 ms renewal threshold, and reconsiders A5 only if measurements justify it. The workload assumptions remain provisional. No production code exists yet. Every number below comes from isolated raw-SQL probes against scratch tables, not from repositories or hosted services. Step 2.11 re-verifies these results against the implemented repositories. **Step 2.11 is complete (§11): every threshold held on both providers through the implemented repositories and fences; A5 is not adopted, and every value is confirmed.**

## 0. Review decisions and follow-up verification

The review of the first version decided the proposals in §8; spec v3.2 applies them (spec §0.00).

| Item | Decision |
| --- | --- |
| A1 index-ordered claim | Adopted on both providers, with `CK_Job_NextAttemptAt_Active` and `NextAttemptAt = CreatedAt` on every enqueue path; retry eligibility, reclaim, and ordering preserved |
| A2 inline D-9 PostgreSQL form | Approved |
| A3 timeouts | Approved as a principle, with revised fence wording: acquisition has its own command timeout above its lock wait; the fence deadline comes from fresh database time after the lock and is never raised; `RenewalTimeout` bounds the whole renewal or outcome-write operation |
| A4 bounded `Exhaust` | Adopted: at most 1 000 rows per batch, each batch its own transaction, cancellation checked between batches |
| A5 SQL Server exhaust index | Deferred to step 2.11 unless bounded-exhaust measurements show a need sooner. The follow-up does not show one |
| A6 renewal probe definition, A7 file names | Approved |
| Candidate defaults | All retained; `RenewalInterval` minimum 12 s and `RetentionBatchSize` maximum 2 000 accepted. The 2 000-row cap rests on these measurements, not on a universal guarantee against escalation, since SQL Server also escalates under lock-memory pressure |
| PostgreSQL renewal p99 102.2 ms (one of six runs) | Documented step 0.2 exception. The 100 ms threshold stays; step 2.11 re-checks it, and another miss requires investigation, not an automatic threshold increase |
| Workload assumptions (§2) | Provisional engineering baseline, explicitly unconfirmed by product |

**What the follow-up changed in the probes.**

- The planned claim variant is removed. Its measurements remain in §5 and the Appendix as recorded at `23294d57c`.
- Every probe now uses the adopted SQL:
  - the scratch schema carries `CK_Job_NextAttemptAt_Active`;
  - `IX_Job_Claim` is `(NextAttemptAt, Id)`;
  - seeded and scheduled jobs get `NextAttemptAt = CreatedAt`. The scheduled insert uses one time sample: PostgreSQL `now()`, SQL Server a single `SYSUTCDATETIME()` assigned to both columns;
  - `Exhaust` runs as bounded sweeps, each batch committed in its own transaction with a cancellation check before the next.
- Probe 3 was rewritten to check batch sizes, cancellation between batches, eventual exhaustion, live-lease preservation, and SQL Server lock footprint per batch.
- Probe 6 now claims the materialized jobs.
- New probe 7 (`Given_claims_over_fresh_retried_released_and_reclaimable_jobs`) covers claim order, retry eligibility, reclaim, and the active-row constraint.

**Follow-up run.** One run per provider, commands as in §4. PostgreSQL passed **36/36**, SQL Server **38/38**.

| Check | PostgreSQL | SQL Server |
| --- | --- | --- |
| Probe 1: claim at a 10 000-job backlog | p99 9.2 ms, max 19.1 ms; 467 claims/s; 0 empty, 0 duplicate | p99 12.3 ms, max 46.1 ms; 543 claims/s; 0 empty, 0 duplicate; one claim holds 2 KEY locks and no table lock |
| Probe 1: idle poll against 10 000 leased rows | p99 11.4 ms | p99 25.6 ms |
| Probe 2: renewal after release, 4 s hold | p99 50.7 ms; 100/100 success | p99 59.4 ms; 100/100 success |
| Probe 2: expired during wait / 6 s hold | 20/20 rejected / 10/10 lock timeout at ≤ 5 018.8 ms | 20/20 rejected / 10/10 lock timeout at ≤ 5 030.7 ms |
| Probe 3: first sweep (100 at-limit rows) | one batch of 100, 67.6 ms | one batch of 100, 158.5 ms |
| Probe 3: steady-state sweep (0 rows) | p99 21.7 ms | p99 117.1 ms |
| Probe 3: `MaxAttempts` lowered to 1 (10 000 rows) | ten committed batches of 1 000, then a batch of 0; batch transaction p99 78.1 ms | the same batches; batch transaction p99 139.5 ms; each full batch holds 3 000 KEY locks (1 000 per index) and no table lock |
| Probe 3: cancellation after the first batch | the sweep stopped with exactly 1 000 rows committed and resumed to completion | the same |
| Probe 3: live leases after the sweep | 9 900 / 9 900 untouched | 9 900 / 9 900 untouched |
| Probe 4: retention batch of 500 | max 13.6 ms | max 88.0 ms |
| Probe 5: ≥ 1 000 mixed operations | 1 005 operations; 0 deadlocks, lock timeouts, lost ownership, or empty claims | the same |
| Probe 6: coalescing | 16/16 vectors, 12/12 live; transaction p99 18.1 ms | 17/17 vectors, 12/12 live; transaction p99 74.5 ms |
| Probe 6: materialized jobs | 12/12 with `NextAttemptAt = CreatedAt`; all 12 claimed, in enqueue order | the same |
| Probe 7: order and eligibility | claims `reclaimable, released, fresh_a, fresh_b`; the retry in backoff, the live lease, and the row at the limit are untouched; the reclaim has attempt 2 and token 2 | the same |
| Probe 7: active-row constraint | insert and update without `NextAttemptAt` rejected (`23514`) | rejected (`547`) |

A5 stays deferred. The bounded SQL Server steady-state sweep still scans the clustered index (p99 117.1 ms at 100 000 rows), which is well inside the 500 ms threshold.

## 1. Summary

- **All §6.1 candidate values: retain.** No probe gives a reason to change any default.
- **Two validation bounds: adjust.** `RenewalInterval` has a lower bound of 1 s, which lets `RenewalTimeout` fall below `WriteLockWait`. `RetentionBatchSize` has an upper bound of 10 000, which lets SQL Server escalate to a table lock.
- **Spec SQL: amendments A1–A7, decided at review and applied in spec v3.2 (§0, §8).** The largest one: on SQL Server, the planned claim (D-3 with the §3 `IX_Job_Claim`) sorts every eligible row and escalates to an **exclusive table lock** at a 10 000-job backlog. When escalation cannot happen, a concurrent claim skips every row the first one read and comes back empty. This happened in all three runs: 4–7 empty claims against a 10 000-row backlog, and 20–155 in the mixed workload. The adopted index-ordered claim (A1) holds two key locks, finds no false empty claims, and roughly triples SQL Server claim throughput. It also speeds up PostgreSQL claims by about 50 %.
- **Thresholds (§6.2).** Every acceptance threshold was met on both providers under the proposed shape, with one exception. The PostgreSQL renewal-after-release p99 was 102.2 ms against a 100 ms limit, in one of six runs (§5.2). The review accepted this as a documented step 0.2 exception; step 2.11 re-checks it against the unchanged threshold.

| Probe (threshold) | PostgreSQL, proposed shape (adopted as A1) | SQL Server, proposed shape (adopted as A1) | Planned shape (v3.1, measured at `23294d57c`; variant since removed) |
| --- | --- | --- | --- |
| 1 Claim, 10 000 pending, 3 claimers (p99 ≤ 250 ms) | p99 ≤ 12.5 ms | p99 ≤ 16.0 ms | PG p99 ≤ 15.4 ms. **SQL Server p99 ≤ 38.5 ms, but table-lock escalation and 4–7 false empty claims per run** |
| 2 Renewal after a held row lock releases (p99 ≤ 100 ms; rejected when expired) | p99 52.5–102.2 ms over six runs; rejection 60/60 | p99 61.3–92.0 ms; rejection 60/60 | shape-independent |
| 3 `Exhaust` at 100 000 rows (≤ 500 ms) | first ≤ 49.9 ms, steady p99 ≤ 17.3 ms | first ≤ 210.4 ms, steady p99 ≤ 86.1 ms | PG ≤ 44.8 / 28.9 ms; SQL Server ≤ 87.7 / 45.8 ms |
| 4 Retention batch of 500 at 100 000 finished rows (≤ 1 s) | max ≤ 38.9 ms | max ≤ 107.2 ms | shape-independent |
| 5 1 000 mixed operations, 3 sessions (0 deadlocks, 0 lost updates) | 0 / 0, 0 empty claims | 0 / 0, 0 empty claims | PG 0 / 0 / 0. **SQL Server 0 / 0, but 20–155 false empty claims** |
| 6 D-9 coalescing vectors (exact match) | 16/16 vectors and 12/12 live | 17/17 vectors and 12/12 live | shape-independent |

## 2. Workload assumptions

These are carried over from spec §6.2. The review accepted them as a **provisional engineering baseline, explicitly unconfirmed by product**. They do not establish production capacity or product agreement on recovery and retention expectations. Each one maps to probe parameters as follows:

| Assumption | Probe parameter |
| --- | --- |
| ≤ 3 CMS replicas | 3 concurrent claimers (probe 1) and 3 sessions (probe 5) |
| Steady queue < 100 jobs; bursts ≤ 10 000 (bulk refresh) | a 10 000-row pending backlog (probe 1) and 10 000 pending rows beside the finished history (probe 3) |
| Durations from seconds to tens of minutes | not simulated. Claim and renewal overhead is measured on its own, and `LeaseDuration`/`RenewalInterval` are judged against durations analytically (§7) |
| Database round trip < 50 ms | local Docker, well under that (uncontended round trips are single-digit ms) |
| CMS database shared with request traffic | not simulated. Lock footprint is measured directly (§5.1, §6 F1, F5), because escalation is what would reach request traffic |
| Retention holds up to about ten bursts inside the seven-day window | 100 000 rows in total (probes 3 and 4) |
| PostgreSQL 16 and SQL Server 2025, as in CI/compose | PostgreSQL 16.3 (the CI image) and SQL Server 2025 RTM-CU7 |

The probes run as tight loops: back-to-back claims with no `PollInterval` sleep. That is a heavier claim rate than three polling replicas would produce.

## 3. Environment

| Item | Value |
| --- | --- |
| Host | Windows 11 Pro 10.0.26200, AMD Ryzen AI 7 350 (16 logical CPUs), 31 GB RAM, WD PC SN5000S NVMe |
| Container runtime | Docker Desktop 29.4.1 on WSL2 (kernel 6.18.33.2), 16 CPUs / 15.2 GB to the VM, overlayfs storage |
| PostgreSQL | container `postgres:16.3-alpine` (the CI image) with defaults: `shared_buffers` 128 MB, `fsync` on, `synchronous_commit` on, trust auth. Host port 55432 |
| SQL Server | container `mcr.microsoft.com/mssql/server:2025-latest`, 17.0.4065.4 Developer. Defaults: no memory cap, overlay storage. Probe database created with server defaults (READ COMMITTED, `is_read_committed_snapshot_on = 0`, as DbUp `EnsureDatabase` creates the CMS database). Host port 14334 |
| Client | .NET SDK 10.0.401, Npgsql 8.0.4, Microsoft.Data.SqlClient 6.1.4, NUnit 4.2.2 |
| Isolation | dedicated containers used by nothing else; each probe creates and drops its own schema, `dmscs_probe`, in its own database, `edfi_cms_job_probe`; runs were sequential |

## 4. Method and commands

The probes live in `src/config/backend/EdFi.DmsConfigurationService.Backend.{Postgresql,Mssql}.Tests.Integration/Jobs/JobOperationalProbeTests.cs`. Each fixture carries `[Explicit]` and `[Category("OperationalProbe")]`. CI runs these assemblies unfiltered, so `[Explicit]` is what keeps the probes out of CI, as in the DMS precedent `PostgresqlAnchoredAuthorizationQueryPlanTests`. Each fixture's `OneTimeSetUp` creates `dmscs_probe.{Tenant,JobSchedule,Job}` with the §3 columns, constraints, and indexes. It then seeds the data, runs the measured statements through raw `NpgsqlCommand`/`SqlCommand`, and records results. `OneTimeTearDown` drops the schema. The `It_*` tests assert the §6.2 thresholds and each probe's correctness invariants.

```powershell
docker run -d --name cms-probe-pg-1437 -e POSTGRES_HOST_AUTH_METHOD=trust -p 127.0.0.1:55432:5432 postgres:16.3-alpine
docker run -d --name cms-probe-mssql-1437 -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=<password>' -e MSSQL_PID=Developer `
  -p 127.0.0.1:14334:1433 mcr.microsoft.com/mssql/server:2025-latest

# PostgreSQL (the probe database is created on this server when absent)
$env:ConnectionStrings__JobProbePostgresql = 'host=localhost;port=55432;username=postgres;database=postgres'
$env:CMS_JOB_PROBE_RESULTS = "$PWD/pg-results.tsv"   # optional: one tab-separated line per measurement
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration --filter "Category=OperationalProbe"

# SQL Server. Use 127.0.0.1: "localhost" resolves to ::1 first, and a container published only on 127.0.0.1
# makes SqlClient time out instead of falling back to IPv4.
$env:ConnectionStrings__MssqlAdmin = 'Server=127.0.0.1,14334;User Id=sa;Password=<password>;TrustServerCertificate=true'
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration --filter "Category=OperationalProbe"
```

The full probe set ran three times per provider, alternating providers, one process at a time. Probe 2 ran three more times on PostgreSQL after run 3 (§5.2). Each run takes about 4 minutes per provider. The lock-escalation diagnostics in §6 F5 were run separately with `sqlcmd`, using the same DDL and statements taken from the probe source.

Differences from the step 0.2 text in spec §6.2/§9, each to be confirmed by the reviewer:

1. **File name.** The files are `JobOperationalProbeTests.cs`, not `JobOperationalProbes.cs`, because `src/.editorconfig` disables S101 (the `Given_*` fixture names) only for `**/*Tests.cs`. Step 2.11's `JobRepositoryProbes.cs` needs the same suffix.
2. **Probe 2 brackets the lock hold.** The spec says the other session holds the lock for 5 s, but that equals the fixed 5 s `WriteLockWait`, so the outcome would be a race. The probe instead uses three holds: 4 s (the renewal must wait and then succeed), 6 s (the renewal must report a lock timeout), and 3 s over a lease that expires after 2 s (the renewal must be rejected once it holds the lock). The 4 s holders start 100 ms apart so that no two releases coincide. Latency after release is measured from just before the holder's `COMMIT` is sent, so it includes that commit's round trip.
3. **Two claim shapes (first version only).** At `23294d57c`, probes 1, 3, and 5 ran twice, as `ClaimShape.Planned` (spec v3.1) and `ClaimShape.IndexOrdered` (A1). On SQL Server the planned variant failed four assertions in every run, and that failure was the evidence for A1. With A1 adopted, the follow-up removed the planned variant; its results stay in §5 and the Appendix.
4. **Additional measurements.** Probe 1 also measures idle polls against 10 000 leased rows, claim throughput, and, on SQL Server, the locks held by one claim. On PostgreSQL it also records the plan. Probe 3 also measures one sweep after `MaxAttempts` is lowered to 1, which exhausts 10 000 rows. Probe 6 also runs 12 live D-8 materialization transactions and compares each advance with the reference computed from the database time the statement returned.

## 5. Results

Values are the worst of three runs; the Appendix has each run. Latencies are client-side wall-clock times per statement or transaction, and percentiles are nearest-rank. "Proposed" is `IndexOrdered`, since adopted as A1; "planned" is spec v3.1. These are the first-version runs at `23294d57c`, taken before bounded `Exhaust` (A4) was adopted. §0 has the follow-up run of the adopted SQL.

### 5.1 Probe 1: claim at a 10 000-job backlog, three claimers × 1 000 claims

| | PG proposed | PG planned | SQL Server proposed | SQL Server planned |
| --- | --- | --- | --- | --- |
| claim p50 / p99 / max (ms) | 4.5 / 12.5 / 29.1 | 8.6 / 15.4 / 33.5 | 6.2 / 16.0 / 65.1 | 19.4 / 38.5 / 82.2 |
| throughput (claims/s) | 410–427 | 271–293 | 437–452 | 147–154 |
| empty claims with backlog | 0 | 0 | 0 | **4, 7, 5** |
| duplicate claims | 0 | 0 | 0 | 0 |
| idle poll against 10 000 leased rows p99 (ms) | 13.7 | 14.6 | 35.7 | 32.8 |
| locks still held by one claim | n/a (PostgreSQL row locks do not escalate) | n/a | 2 KEY X, no table lock | **16 × OBJECT X (table escalation)** |
| plan | ordered `Index Scan using IX_Job_Claim` → `LockRows` → `Limit` | `Seq Scan` → `Sort` → `LockRows` → `Limit` | ordered `Index Scan IX_Job_Claim` → `Top`, 31 logical reads | `Clustered Index Scan` → `Sort (TOP 1)`, 246 logical reads |

### 5.2 Probe 2: lock-then-validate renewal under a held row lock (shape-independent)

| | PostgreSQL | SQL Server |
| --- | --- | --- |
| 4 s hold: renewals succeeding | 100/100 in every run | 100/100 in every run |
| 4 s hold: completion after release p99 (ms), per run | 62.6, 66.3, **102.2**, 52.5, 70.9, 85.4 | 61.3, 92.0, 68.3 |
| 4 s hold: max after release (ms) | 144.2 | 141.2 |
| 3 s hold over a lease that expires at 2 s: renewals rejected (0 rows) | 20/20 in every run, and no row's lease extended | 20/20 in every run |
| 6 s hold: lock-timeout outcome / time to timeout p99 | 10/10, ≤ 5 023.7 ms | 10/10, ≤ 5 031.6 ms |

PostgreSQL run 3 missed the 100 ms threshold by 2.2 ms. That run's p95 was 40.2 ms, and its two slowest of 100 samples were 102.2 and 104.6 ms. Three reruns met the threshold (p99 52.5, 70.9 and 85.4 ms). This looks like scheduling noise on Docker Desktop/WSL2 plus the included commit round trip. It is a threshold miss all the same, and the reviewer should rule on it; see §7 and §8 A6. Rejection works as designed: the lease expired while the renewal waited, and once the renewal held the lock, the fresh-time predicate (`clock_timestamp()` / a separate `SYSUTCDATETIME()` statement) refused it.

### 5.3 Probe 3: `Exhaust` over 100 000 rows

The rows are 80 000 finished; 10 000 pending and 9 900 leased, both under the limit; and 50 pending plus 50 expired-leased at the limit.

| | PG proposed | PG planned | SQL Server proposed | SQL Server planned |
| --- | --- | --- | --- | --- |
| first sweep (exhausts exactly the 100 at-limit rows), ms | 49.9 | 44.8 | 210.4 | 87.7 |
| steady-state sweep (0 rows) p99, ms | 17.3 | 28.9 | 86.1 | 45.8 |
| `MaxAttempts` lowered to 1: one sweep of 10 000 rows, ms | 293–589 | 221–566 | 176–230 | 164–340 |
| rows under the limit untouched | 19 900/19 900 | 19 900/19 900 | 19 900/19 900 | 19 900/19 900 |

With the proposed index, the SQL Server steady-state sweep runs a parallel clustered scan (4 940 logical reads), because `Status` is no longer the leading key. It stays within the threshold; see §8 A5. The lowered-limit sweep escalates to a table lock on SQL Server under either shape (§6 F5).

### 5.4 Probe 4: retention batch of 500

The table holds 60 000 finished rows past seven days, 40 000 inside the window, and 2 000 old active rows.

| | PostgreSQL | SQL Server |
| --- | --- | --- |
| batch p50 / max over 20 batches, ms | 4.7–5.1 / 38.9 | 17.0–17.6 / 107.2 |
| rows per batch | 500 exactly | 500 exactly |
| recent and active rows deleted | 0 | 0 |
| locks held by one batch | n/a | 2 000 KEY X (4 per row), no escalation. 1 000 and 2 000 rows do not escalate; **4 000 rows escalate to a table X lock** |

### 5.5 Probe 5: three sessions × claim → renew → complete, `Exhaust` every tenth iteration, ≥ 1 000 operations

| | PG proposed | PG planned | SQL Server proposed | SQL Server planned |
| --- | --- | --- | --- | --- |
| operations | 1 002–1 008 | 1 002–1 005 | 1 005 | 1 002–1 004 |
| deadlocks / lock timeouts / ownership lost | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| jobs claimed twice (`AttemptCount` or `FencingToken` > 1) | 0 | 0 | 0 | 0 |
| every claimed job completed exactly once | yes | yes | yes | yes |
| empty claims while jobs remained | 0 | 0 | 0 | **33, 155, 20** |
| p99 claim / renew / complete, ms | 17.4 / 19.6 / 22.2 | 14.3 / 18.0 / 16.2 | 20.7 / 27.0 / 30.0 | 32.3 / 36.1 / 29.5 |

### 5.6 Probe 6: D-9 coalescing and D-8 materialization

- **Vectors, injected time, same expression text as the live statement: exact match with the C# reference and hand-computed expectations** (PostgreSQL 16/16, SQL Server 17/17). The set covers the review's worked example (`12:00:00.900` / `12:01:00.100` → `12:01:00.900`); boundaries 100 ms before, at, and after, both on whole seconds and at `.900`; second counts below and above the true elapsed time; microsecond boundaries (and a 100 ns tick on SQL Server); due exactly at `now`; 663 missed 5-minute intervals; 3.5 missed days; the maximum interval of 527 040 minutes; and a row not yet due.
- **Live D-8 transactions, 12 schedules from 100 ms to 800 days overdue: every advance equals the reference computed from the returned database time.** Each advance satisfies `NextRunAt' > now` and `NextRunAt' − Interval ≤ now`. Each schedule produced exactly one occurrence, whose `ScheduledOccurrence` equals `LastEnqueuedOccurrence`, and every lease was cleared. Transaction p99: PostgreSQL 37.4 ms, SQL Server 149.9 ms.

## 6. Findings

- **F1 (SQL Server, blocking): the planned claim sorts every eligible row and escalates to a table lock.** `ORDER BY COALESCE(NextAttemptAt, CreatedAt), Id` cannot be served by `IX_Job_Claim (Status, NextAttemptAt, Id)`. On SQL Server the claim runs as `Clustered Index Scan` → `Sort (TOP 1)` under `UPDLOCK`. At 10 000 eligible rows it ends holding an exclusive table lock (16 `OBJECT X`, one per lock partition). For the rest of that statement, every other claim, renewal, fence, enqueue, and `GET /v3/jobs/{jobId}` against `Job` waits. When escalation is blocked by other sessions' locks, the claim keeps a U lock on every eligible row it read, and a concurrent claim's `READPAST` skips all of them. That claim returns nothing even though work is waiting. A worker that gets a false empty claim idles for a full `PollInterval`. Correctness held in every run: no duplicate claim, lost update, or deadlock. The problem is liveness and blocking. PostgreSQL is not affected in the same way: `LockRows` sits under `Limit`, so only one row is locked. It still sorts every eligible row on each claim. Proposal: A1.
- **F2 (PostgreSQL, syntax): the D-9 PostgreSQL statement is invalid as written.** `UPDATE … FROM t, LATERAL (SELECT "IntervalMinutes" …)` fails with `invalid reference to FROM-clause entry for table "s"`, because a FROM item cannot reference the UPDATE target. The probe writes the same formula inline in `SET`, with one `clock_timestamp()` sample from a CTE; probe 6 matched exactly. Proposal: A2.
- **F3 (both providers): an ownership write's command timeout must exceed its lock wait.** In the first probe run, the lock statement's client `CommandTimeout` (5 s) equaled the server-side lock wait (5 s). Npgsql then surfaced the lock timeout at about 7.08 s: the 5 s wait plus its default 2 s `CancellationTimeout`. That outcome depends on timing and can arrive as a timeout exception, which D-4 would classify as `ResultUnknown`, instead of `55P03`. With a 30 s command timeout (the candidate `RenewalTimeout`), the lock timeout fires at 5.01–5.03 s on both providers. The spec leaves this unstated: it does not give command timeouts for the outcome writes, and the `RenewalInterval` lower bound of 1 s allows a `RenewalTimeout` of 1 s, which is below `WriteLockWait`. Proposal: A3 and the `RenewalInterval` bound in §7.
- **F4 (probe definition): the step 0.2/2.11 renewal probe as written is a race.** A 5 s hold against a 5 s `WriteLockWait` does not have a defined outcome. The bracketing in §4 item 2 replaces it. Proposal: A6.
- **F5 (SQL Server): statements that touch thousands of rows escalate.** Unbounded `Exhaust` over 10 000 rows (the lowered-`MaxAttempts` case, or a mass lease expiry) and retention batches of about 4 000 rows or more escalate to an exclusive table lock for their duration (0.2–0.3 s at these sizes). Lock escalation is triggered at about 5 000 locks on one index or heap. Proposals: A4 and the `RetentionBatchSize` bound in §7.
- **F6 (probe validity, fixed in the probe).** In the first run, the expiring-lease case opened 40 connections after setting the 2 s lease, and some holders reached the row after the lease had already expired. The probe now opens every connection first, sets the lease immediately before locking, and asserts that the lease was still live when contention began. All samples in all later runs satisfied this.

## 7. Decision per candidate setting (Q3)

| Setting (§6.1) | Candidate | Decision | Evidence and reasoning |
| --- | --- | --- | --- |
| WorkerEnabled / SchedulerEnabled / RetentionEnabled | true (false in Test) | **Retain** | No load concern. Each is an independent switch |
| PollInterval | 5 s (1 s – 5 min) | **Retain** | An idle poll costs one claim plus one `Exhaust`: p99 ≤ 36 ms and ≤ 86 ms against 10 000–100 000 rows. At 3 replicas that is about 1.2 statements/s. The expected claim delay is about 2.5 s on average and 5 s at worst |
| LeaseDuration | 5 min (30 s – 1 h) | **Retain** | The §6.3 inequality gives 160 s ≤ 300 s. Measured renewals take ≤ 30 ms uncontended and ≤ 5.03 s when they hit the lock wait, both negligible against the 50 s safety margin. Crash recovery waits at most one lease (5 min), which suits jobs lasting seconds to tens of minutes |
| RenewalInterval | 60 s (1 s – LeaseDuration/3) | **Retain the value. Adjust the lower bound to 12 s** (accepted) | `RenewalTimeout = RenewalInterval/2` must exceed the 5 s `WriteLockWait` with at least 1 s of margin (F3), so `RenewalInterval ≥ 12 s`. The §6.3 inequality already puts the effective minimum `LeaseDuration` at 39 s |
| FenceTimeout | 10 s (1 s – 1 min) | **Retain; verify at 2.11** | No fence code exists yet. The underlying row-lock wait behaves exactly as measured for renewal (timeout at 5.0 s on both providers). Step 2.11 measures fence-held renewal |
| MaxAttempts | 5 (1 – 20) | **Retain** | A functional choice; sweep cost does not depend on it. Lowering it later triggers one large sweep, which A4 bounds |
| RetryBackoffBase / RetryBackoffMaximum | 30 s / 15 min | **Retain** | A functional choice with no database cost. With `MaxAttempts` 5, the four retries wait 30 s, 1 min, 2 min, and 4 min |
| MaxConcurrentJobs | 2 (1 – 32) | **Retain** | The claim path sustains more than 400 claims/s on both providers under the proposed shape, so throughput is bounded by job duration, not claims. Each running job uses a connection only during renewals and fences, so the worst case is about 3 per replica |
| FinishedJobRetention | 7 d (1 h – 365 d) | **Retain** | 100 000 rows (about ten bursts) cost nothing measurable in claim or `Exhaust` latency |
| RetentionInterval | 1 h (1 min – 24 h) | **Retain** | One 500-row batch takes ≤ 107 ms, so an hourly run clears a full burst in about 20 batches (≈ 2 s) |
| RetentionBatchSize | 500 (1 – 10 000) | **Retain the value. Adjust the upper bound to 2 000** (accepted) | In these measurements, 2 000 rows did not escalate on SQL Server and 4 000 rows escalated to an exclusive table lock (F5). The cap rests on these measurements and is not a universal guarantee against escalation, which SQL Server also triggers under lock-memory pressure |

Fixed constants (§6.1): **retain all.**

| Constant | Evidence |
| --- | --- |
| `WriteLockWait` 5 s | Fires at 5.0 s on both providers |
| `FenceLockWait` 5 s | Same lock mechanics as `WriteLockWait`; verified at step 2.11 |
| `FenceMinimumRemainingLease` 2 s | Verified at step 2.11 |
| `RenewalTimeout = RenewalInterval/2` | 30 s, well above `WriteLockWait` (F3 bound) |
| Claim/`Exhaust` command timeout 5 s | Worst statement is 589 ms (the unbounded lowered-limit sweep). A4 bounds it further |
| `ScheduleMaterializationTimeout` 10 s | p99 ≤ 150 ms |
| Retention batch command timeout 30 s | Batch max ≤ 107 ms |

On the PostgreSQL renewal miss (run 3, 102.2 ms against 100 ms): **no setting depends on it.** Renewal lateness of about 0.1 s is irrelevant against a 50 s safety margin. **Review decision:** this is a documented step 0.2 exception. The 100 ms threshold stays, step 2.11 re-checks it, and another miss requires investigation, not an automatic threshold increase. The follow-up run measured p99 50.7 ms (§0).

## 8. Spec amendments and review decisions

These were decided at the step 0.2 review and applied in spec v3.2 (spec §0.00). Each item keeps its proposal text, followed by the decision.

- **A1: index-ordered claim (D-3, §3), both providers for parity.** Set `NextAttemptAt` to the enqueue time on enqueue and schedule materialization, so every active row carries its eligibility time. It is already set by retry and release (D-6). Enforce this with a check that active rows have a non-null `NextAttemptAt`. Replace `IX_Job_Claim` with:
  - PostgreSQL: `("NextAttemptAt", "Id") WHERE "Status" IN ('Pending','InProgress')`
  - SQL Server: `(NextAttemptAt, Id) INCLUDE (Status, LeaseExpiresAt, AttemptCount) WHERE Status IN (N'Pending', N'InProgress')`

  Order claims by `NextAttemptAt, Id`, which is the planned order for every active row. Add the redundant `Status IN ('Pending','InProgress')` conjunct to the claim; without it, SQL Server does not match the filtered index. Evidence: §5.1 and §5.5. **Decision: adopted** on both providers, including the constraint and every enqueue path (manual enqueue and schedule materialization). Retry eligibility, reclaim behavior, and ordering are preserved; probes 6 and 7 verify this (§0).
- **A2: D-9 PostgreSQL form.** Write the advance expression inline in `SET`, over a single `WITH t AS (SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS now)` sample, as `JobProbeSql.Advance` does. The formula is unchanged. **Decision: approved.**
- **A3: command timeouts for ownership-dependent writes (D-4, D-5).** Every lock-then-validate statement (`Renew`, `Complete`, `FailTransient`, `FailTerminal`, `ReleaseToPending`, and the fence lock and revalidation) uses a command timeout that exceeds its lock wait by at least 1 s. Use `RenewalTimeout` for the outcome writes. For the fence, use the smaller of `FenceTimeout` and the remaining lease minus 1 s, but never less than `FenceLockWait + 1 s`. `JobOptionsValidator` enforces `RenewalTimeout ≥ WriteLockWait + 1 s`. **Decision: approved as a separation principle, with revised fence wording.** The proposal's fence rule contradicted the existing deadline: it required at least `FenceLockWait + 1 s` while also capping the deadline at `min(FenceTimeout, remaining lease − 1 s)`. With 3 s of lease remaining, both cannot hold. As adopted:
  - Lock acquisition has its own command timeout above its lock wait (`FenceLockWait + 1 s` for the fence).
  - After the lock is held, the fence deadline is computed from fresh database time as `min(FenceTimeout, remaining − 1 s)` and preserved through work, revalidation, and commit. It is never raised to satisfy a command-timeout floor.
  - `RenewalTimeout` bounds the whole renewal or outcome-write operation rather than granting each statement its own allowance.
  - `RenewalTimeout ≥ WriteLockWait + 1 s` is validated (spec D-4, D-5, §6.3).
- **A4: bounded `Exhaust` (D-6).** Run `Exhaust` in batches of at most 1 000 rows (`LIMIT` inside the skip-locked subquery / `UPDATE TOP (1000)` with `READPAST`), looping until a batch comes back short. On SQL Server, add the same `Status IN (…)` conjunct as A1. This bounds lock footprint and statement time after `MaxAttempts` is lowered or after a mass lease expiry. **Decision: adopted**, with each batch in its own transaction and a cancellation check between batches. The follow-up verified the actual SQL on both providers (§0): committed batches of at most 1 000, a clean stop at cancellation, eventual exhaustion, live leases preserved, and row-level locks only per SQL Server batch.
- **A5 (optional): SQL Server `Exhaust` access path.** Under A1, the steady-state sweep scans the clustered index (p99 86 ms at 100 000 rows, within threshold). An optional `IX_Job_Exhaust (AttemptCount) INCLUDE (Status, LeaseExpiresAt) WHERE Status IN (N'Pending', N'InProgress')` would make it a seek. The decision can wait until step 2.11. **Decision: deferred to step 2.11**, unless bounded-exhaust measurements establish a need sooner. The follow-up's steady-state p99 of 117.1 ms on SQL Server does not.
- **A6: step 2.11 renewal probe definition (§6.2).** Replace the "5 s hold" with the §4 item 2 bracketing (4 s success, 6 s lock timeout, 3 s hold over an expiring 2 s lease), and define "after release" as measured from the holder's `COMMIT` request. Keep p99 ≤ 100 ms; if step 2.11 misses it again on this hardware, revisit the threshold rather than the settings. **Decision: approved**, including the measurement origin. A repeat miss at step 2.11 requires investigation, not an automatic threshold increase.
- **A7: file names (§9 steps 0.2 and 2.11).** Use the `*Tests.cs` suffix (§4 item 1). **Decision: approved.**

## 9. Limitations

- The runs used one developer machine on Docker Desktop/WSL2 with overlay storage and default database configuration, so production hardware and managed services will differ. The thresholds are generous relative to the measured values, except the renewal tail in §5.2.
- Request traffic and job handlers were not simulated. Blocking is inferred from the measured lock footprint, not from a mixed HTTP load.
- The probes run the planned SQL through raw commands. Repository code, retries, connection handling, the execution gate, and fences are all covered by step 2.11 instead.
- The workload assumptions (§2) still need product confirmation.

## 10. What step 2.11 re-verifies

Step 2.11 re-verifies probes 1–7 through the implemented repositories and `IJobFence`, with the adopted claim (A1) and bounded `Exhaust` (A4), and adds fence-held renewal. It re-checks the 100 ms renewal threshold (a repeat miss requires investigation) and decides A5. Each approved or adjusted value above gets a confirm-or-adjust note, and any regression against these thresholds blocks Phase 3. The results are in §11.

## 11. Verification (step 2.11)

Status: **complete; awaiting the Phase 3 gate review** (2026-09-24). Step 2.11 re-ran the step 0.2 probes through the implemented repositories and `IJobFence`, and added fence-held renewal. **Every §6.2 threshold and every correctness invariant held in all three runs on both providers**. No regression blocks Phase 3. The 100 ms renewal threshold held with a wide margin, so the step 0.2 exception did not recur. A5 is not adopted. Every §6.1 value is confirmed.

### 11.1 Method

The probes live in `src/config/backend/EdFi.DmsConfigurationService.Backend.{Postgresql,Mssql}.Tests.Integration/Jobs/JobRepositoryProbeTests.cs`. Every concrete fixture carries `[Explicit]`, and the base class carries `[Category("OperationalProbe")]` and `[Category("RepositoryProbe")]`.

What changed from step 0.2:

- **Real schema.** Each fixture drops and recreates the database `edfi_cms_job_repository_probe`, then deploys the full CMS schema with the real DbUp migrations (`DatabaseDeploy`, through `0033`). Step 0.2 used scratch tables that copied §3. The database is dropped when the fixture ends.
- **Real code path.** Seeding is raw SQL, since it is not under test. Every measured operation is a repository or fence call, with its deadlines, `JobDatabaseSession`, result classification, and seams:
  - claims, renewals, completions, and `Exhaust` go through `JobLeaseRepository`;
  - fences go through `PostgresqlJobFenceFactory`/`MssqlJobFenceFactory`;
  - retention goes through `JobRetentionRepository`;
  - materialization goes through `JobScheduleRepository`.

  The timings are the §6.1 candidates: `RenewalTimeout` 30 s (`RenewalInterval / 2`) and `FenceTimeout` 10 s.
- **Environment.** The same host and the same containers as §3 (`cms-probe-pg-1437` on 55432, `cms-probe-mssql-1437` on 14334), with the same client libraries. Runs were sequential, one process at a time, alternating providers: PostgreSQL 1, SQL Server 1, PostgreSQL 2, SQL Server 2, and so on. Each run takes about 1.5 minutes per provider.

```powershell
$env:ConnectionStrings__JobProbePostgresql = 'host=127.0.0.1;port=55432;username=postgres;database=postgres'
$env:CMS_JOB_PROBE_RESULTS = "$PWD/pg-run1.tsv"
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration --filter "Category=RepositoryProbe"

$env:ConnectionStrings__MssqlAdmin = 'Server=127.0.0.1,14334;User Id=sa;Password=<password>;TrustServerCertificate=true'
$env:CMS_JOB_PROBE_RESULTS = "$PWD/mssql-run1.tsv"
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration --filter "Category=RepositoryProbe"
```

Differences from the step 0.2 probe set, each for the reviewer to confirm:

1. **Probe 2, expiring lease.** The lock holder sets the lease to expire 2 s later inside its own transaction, immediately after it takes the row lock. This makes the expiry deterministic relative to the wait, so the step 0.2 F6 care about connection order is not needed. The renewal still waits about 3 s and is then refused by the fresh-time predicate.
2. **Probe 2, fence-held (added).** Two variants:
   - **At the database.** The row lock is held by a fence's transaction, with fence work of 4 s (released 100 ms apart) or 6 s, while a `Renew` waits on that lock. Latency after release is measured from the fence session's `Commit` operation (the `BeforeOperation` hook), the fence counterpart of the holder's `COMMIT` request.
   - **At the execution gate**, which is how the runtime serializes them. A renewal of the same execution calls `EnterForRenewalAsync` during a 1 s fence. The probe asserts that it enters after the fence's commit request, the timestamp taken just before the commit is sent, and measures its completion from the fence's return. Comparing with the commit request does not by itself show that admission followed the commit's completion; step 3.7 verifies the full ordering (corrected at the step 2.11 review).
3. **Probe 6, live only.** The injected-time vectors test the SQL expression, not the repository. The repository-level equivalents are already in the integration suites:
   - `It_advances_to_first_future_boundary_with_fractional_seconds` on both providers;
   - `It_advances_from_fresh_time_when_paused_after_insert`;
   - on SQL Server, the deterministic overshoot fixture `Given_a_next_run_whose_fraction_is_later_than_now`.
4. **Probe 7, active-row constraint.** Not repeated: a repository cannot produce the row the constraint rejects, and `JobSchemaTests` covers it on both providers.
5. **Lock footprints (SQL Server).** Measured through the repositories' `BeforeClaimCommit` and `BeforeExhaustBatchCommit` seams for one claim at the backlog and for every batch of the lowered-limit sweep. The retention batch's footprint was not re-measured: its statement shape is unchanged since step 0.2, and `RetentionBatchSize` stays capped at 2 000.

### 11.2 Results

Values are the worst of three runs. §11.5 has each run. Latencies are client-side wall-clock times per repository or fence call, with nearest-rank percentiles.

| Probe (threshold) | PostgreSQL | SQL Server | Verdict |
| --- | --- | --- | --- |
| 1 Claim, 10 000 pending, 3 claimers × 1 000 (p99 ≤ 250 ms) | p99 4.2 ms, max 13.3 ms; 1 266–1 304 claims/s; 0 empty, 0 duplicate, 0 failed | p99 11.0 ms, max 142.3 ms; 562–582 claims/s; 0 empty, 0 duplicate, 0 failed; one claim holds 2 KEY locks and no coarse lock | met |
| 1 Idle poll against 10 000 leased rows (p99 ≤ 250 ms) | p99 5.1 ms; 0 claims | p99 12.6 ms; 0 claims | met |
| 2 Renewal after a raw-SQL holder releases, 4 s hold (p99 ≤ 100 ms) | p99 27.4 ms; 300/300 success | p99 27.1 ms; 300/300 success | met |
| 2 Renewal after a fence commits, 4 s fence work (p99 ≤ 100 ms) | p99 9.7 ms; 90/90 success | p99 10.7 ms; 90/90 success | met |
| 2 Renewal behind a fence at the execution gate (p99 ≤ 100 ms) | p99 5.7 ms; 30/30 success, none admitted before the fence's commit request | p99 10.6 ms; the same | met |
| 2 Lease expired during a 3 s hold (rejected) | 60/60 `OwnershipLost`; no lease extended | the same | met |
| 2 6 s hold / 6 s fence work (lock timeout at `WriteLockWait`) | 30/30 and 30/30 `FailureUnknown(55P03)` at ≤ 5 017.4 ms; every fence committed (120/120) | 30/30 and 30/30 `FailureUnknown(1222)` at ≤ 5 013.0 ms; the same | met |
| 3 `Exhaust`, first sweep over 100 000 rows (≤ 500 ms) | exactly 100 rows, ≤ 26.1 ms | exactly 100 rows, ≤ 82.0 ms | met |
| 3 `Exhaust`, steady-state sweep (≤ 500 ms) | p99 6.8 ms, 0 rows | p99 11.4 ms, 0 rows | met |
| 3 `MaxAttempts` lowered to 1: 10 000 rows (≤ 500 ms per batch) | stopped after 1 committed batch of 1 000, resumed as 9 × 1 000 then 0; batch p99 28.8 ms | the same batches; batch p99 61.2 ms; each full batch holds 3 000 KEY locks and no coarse lock | met |
| 3 Live leases after the sweeps | 9 900 / 9 900 untouched | 9 900 / 9 900 untouched | met |
| 4 Retention, 20 batches of 500 over 102 000 rows (≤ 1 s) | 500 each; max 7.2 ms; 40 000 recent and 2 000 active rows kept | 500 each; max 64.3 ms; the same | met |
| 5 Three sessions, ≥ 1 000 mixed operations (0 deadlocks, 0 lost updates) | 1 002–1 005 operations, all `Success`; 0 empty claims, 0 jobs claimed twice, every claim completed | the same | met |
| 6 Live D-8 materializations, 12 schedules from 100 ms to 800 days overdue (exact match) | 12/12 match `ScheduleOccurrenceMath.Advance` on the returned pair; transaction p99 21.4 ms; then `NoneDue` | 12/12; transaction p99 47.0 ms; then `NoneDue` | met |
| 6 Materialized jobs | 12/12 with `NextAttemptAt = CreatedAt`, all claimed, in materialization order | the same | met |
| 7 Claim order and eligibility | `reclaimable, released, fresh_a, fresh_b`; the reclaim has attempt 2 and token 2; the live lease, the backoff, and the row at the limit are untouched | the same | met |

The repository numbers are lower than the step 0.2 raw-SQL numbers on the same containers. Examples:

- the PostgreSQL renewal after release, p99 27.4 ms against 50.7–102.2 ms;
- the SQL Server steady-state `Exhaust`, p99 11.4 ms against 117.1 ms.

Both measure the same statements, now running in the migrated schema. This step did not investigate the difference. The verdicts compare against the §6.2 thresholds, not against step 0.2.

### 11.3 Re-checks required by step 0.2

- **PostgreSQL renewal after release, 100 ms threshold (the step 0.2 exception): no miss.** The worst p99 across three runs was 27.4 ms against a raw-SQL holder, 9.7 ms behind a fence, and 5.7 ms at the gate. The threshold stays at 100 ms, and no investigation is needed.
- **A5 (SQL Server `IX_Job_Exhaust`): not adopted.** Through the repository, the steady-state sweep over 100 000 rows runs at p99 11.4 ms, and a full lowered-limit batch at p99 61.2 ms with row locks only. Both are far inside the 500 ms threshold, so the measurements show no need for another index and its write cost.
- **Fence timings.** A renewal waiting on a fence's row lock is bounded by its own `WriteLockWait` (5 s), not by `FenceLockWait`: it ends in the provider's lock timeout at 5.0 s on both providers, and every fence still commits (corrected at the step 2.11 review). No probe makes a fence's own acquisition wait, so `FenceLockWait` rests on the step 2.5/2.6 fence tests, including the fence lock wait that outlasts the lease. The 10 s `FenceTimeout` cap left the 4 s and 6 s fence work well inside the deadline in all 120 counted fences per provider. The probes do not exercise `FenceMinimumRemainingLease` (2 s), because every lease was 300 s. Its rejection path is covered by the fence tests of steps 2.5 and 2.6.

### 11.4 Confirm or adjust, per candidate (Q3 re-verified)

| Setting (§6.1) | Step 0.2 decision | Step 2.11 | Evidence |
| --- | --- | --- | --- |
| WorkerEnabled / SchedulerEnabled / RetentionEnabled | retain | **confirm** | independent switches; no load concern |
| PollInterval 5 s (1 s – 5 min) | retain | **confirm** | an idle poll through the repository: claim p99 ≤ 12.6 ms plus a steady `Exhaust` p99 ≤ 11.4 ms |
| LeaseDuration 5 min (30 s – 1 h) | retain | **confirm** | renewals take ≤ 27.4 ms after contention, or end at 5.0 s when they hit the lock wait; negligible against the 50 s margin |
| RenewalInterval 60 s (12 s – LeaseDuration/3) | retain; bound adjusted | **confirm** | `RenewalTimeout` 30 s held every renewal and outcome write; lock timeouts arrived as the provider's code at 5.0 s, never as a client timeout |
| FenceTimeout 10 s (1 s – 1 min) | retain; verify at 2.11 | **confirm** | 120 fences per provider with 4–6 s of work, all committed inside the deadline; a renewal behind a fence at the gate completes ≤ 10.6 ms after the fence |
| MaxAttempts 5 (1 – 20) | retain | **confirm** | a lowered limit exhausts 10 000 rows in committed batches of 1 000, each ≤ 61.2 ms |
| RetryBackoffBase / RetryBackoffMaximum 30 s / 15 min | retain | **confirm** | no database cost |
| MaxConcurrentJobs 2 (1 – 32) | retain | **confirm** | the repository claim path sustains ≥ 562 claims/s |
| FinishedJobRetention 7 d (1 h – 365 d) | retain | **confirm** | 100 000 finished rows cost nothing measurable in claim or `Exhaust` latency |
| RetentionInterval 1 h (1 min – 24 h) | retain | **confirm** | a 500-row batch takes ≤ 64.3 ms |
| RetentionBatchSize 500 (1 – 2 000) | retain; bound adjusted | **confirm** | the batch statement is unchanged since step 0.2 (row locks only at 2 000) |

| Fixed constant | Step 2.11 | Evidence |
| --- | --- | --- |
| `WriteLockWait` 5 s | **confirm** | lock timeouts at ≤ 5 017.4 ms (PostgreSQL) and ≤ 5 013.0 ms (SQL Server) |
| `FenceLockWait` 5 s | **confirm** | not exercised by the probes: a renewal behind a fence waits under `WriteLockWait` (≤ 5 008.6 ms and ≤ 5 007.1 ms). Covered by the step 2.5/2.6 fence tests of a fence's acquisition wait |
| `FenceMinimumRemainingLease` 2 s | **confirm** | not exercised by the probes (300 s leases); covered functionally by the step 2.5/2.6 fence tests |
| `RenewalTimeout = RenewalInterval/2` | **confirm** | 30 s, above `WriteLockWait + 1 s`; no renewal reached it |
| Claim/`Exhaust` command timeout 5 s | **confirm** | worst claim 142.3 ms; worst `Exhaust` sweep 82.0 ms |
| `ExhaustBatchSize` 1 000 | **confirm** | 3 000 KEY locks per full batch on SQL Server, no escalation |
| `ScheduleMaterializationTimeout` 10 s | **confirm** | materialization p99 ≤ 47.0 ms |
| Schedule upsert, disable, and list deadline 30 s (added at step 2.9) | **confirm** | not a probe subject. It exceeds the 10 s materialization a write may wait behind (spec D-10) |
| Retention batch deadline 30 s | **confirm** | batch max ≤ 64.3 ms |

The limitations in §9 still apply, except that repository code, connection handling, the execution gate, and fences are now covered. The workload assumptions (§2) are still unconfirmed by product.

### 11.5 Per-run results (step 2.11)

Each value is as the probes recorded it (`CMS_JOB_PROBE_RESULTS`), and "worst" is the maximum across runs. The renewal counts in §11.2 sum the three runs.

#### PostgreSQL (runs 1–3)
| probe | metric | run 1 | run 2 | run 3 | worst |
| --- | --- | --- | --- | --- | --- |
| exhaust | first_sweep | 100 rows in 26.08ms | 100 rows in 24.95ms | 100 rows in 24.34ms | 100 rows, max 26.08 ms |
| exhaust | steady_state_sweeps | p50 2.93 / p99 5.48 / max 5.48 (n=50) | p50 2.88 / p99 5.91 / max 5.91 (n=50) | p50 2.99 / p99 6.84 / max 6.84 (n=50) | p99 6.84 / max 6.84 |
| exhaust | lowered_limit_batches | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 |
| exhaust | lowered_limit_batch | p50 27.13 / p99 28.80 / max 28.80 (n=11) | p50 26.59 / p99 28.15 / max 28.15 (n=11) | p50 25.63 / p99 28.47 / max 28.47 (n=11) | p99 28.80 / max 28.80 |
| exhaust | live_leases_untouched | 9900 | 9900 | 9900 | 9900 |
| order | claims | reclaimable,released,fresh_a,fresh_b | reclaimable,released,fresh_a,fresh_b | reclaimable,released,fresh_a,fresh_b | reclaimable,released,fresh_a,fresh_b |
| coalescing | live_materializations | 12 | 12 | 12 | 12 |
| coalescing | live_mismatches | 0 | 0 | 0 | 0 |
| coalescing | materialization_transaction | p50 5.09 / p99 21.24 / max 21.24 (n=12) | p50 4.70 / p99 21.20 / max 21.20 (n=12) | p50 4.72 / p99 21.41 / max 21.41 (n=12) | p99 21.41 / max 21.41 |
| coalescing | materialized_jobs_claimed_in_order | True | True | True | True |
| renewal | after_release_4s_hold | p50 6.38 / p99 27.44 / max 34.73 (n=100) | p50 6.41 / p99 12.74 / max 13.67 (n=100) | p50 6.47 / p99 12.04 / max 12.15 (n=100) | p99 27.44 / max 34.73 |
| renewal | outcomes_4s_hold | Success=100 | Success=100 | Success=100 | Success=100 |
| renewal | outcomes_6s_hold | FailureUnknown:55P03=10 | FailureUnknown:55P03=10 | FailureUnknown:55P03=10 | FailureUnknown:55P03=10 |
| renewal | time_to_lock_timeout_6s_hold | p50 5012.36 / p99 5012.52 / max 5012.52 (n=10) | p50 5017.37 / p99 5017.39 / max 5017.39 (n=10) | p50 5011.97 / p99 5012.00 / max 5012.00 (n=10) | p99 5017.39 / max 5017.39 |
| renewal | outcomes_expired_during_3s_hold | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 |
| renewal | expiring_leases_extended | 0 | 0 | 0 | 0 |
| renewal | after_fence_commit_4s_fence | p50 5.09 / p99 9.68 / max 9.68 (n=30) | p50 5.22 / p99 6.32 / max 6.32 (n=30) | p50 5.03 / p99 6.70 / max 6.70 (n=30) | p99 9.68 / max 9.68 |
| renewal | outcomes_4s_fence | Success=30 | Success=30 | Success=30 | Success=30 |
| renewal | outcomes_6s_fence | FailureUnknown:55P03=10 | FailureUnknown:55P03=10 | FailureUnknown:55P03=10 | FailureUnknown:55P03=10 |
| renewal | time_to_lock_timeout_6s_fence | p50 5005.61 / p99 5005.64 / max 5005.64 (n=10) | p50 5008.55 / p99 5008.61 / max 5008.61 (n=10) | p50 5006.11 / p99 5006.16 / max 5006.16 (n=10) | p99 5008.61 / max 5008.61 |
| renewal | fences_committed | 40 | 40 | 40 | 40 |
| renewal | after_fence_release_at_gate | p50 5.27 / p99 5.67 / max 5.67 (n=10) | p50 5.08 / p99 5.57 / max 5.57 (n=10) | p50 5.27 / p99 5.57 / max 5.57 (n=10) | p99 5.67 / max 5.67 |
| renewal | outcomes_behind_fence_at_gate | Success=10 | Success=10 | Success=10 | Success=10 |
| retention | batch_500 | p50 2.76 / p99 7.24 / max 7.24 (n=20) | p50 2.41 / p99 7.12 / max 7.12 (n=20) | p50 2.30 / p99 6.61 / max 6.61 (n=20) | p99 7.24 / max 7.24 |
| retention | rows_left | expired=50000 recent=40000 active=2000 | expired=50000 recent=40000 active=2000 | expired=50000 recent=40000 active=2000 | expired=50000 recent=40000 active=2000 |
| claim | backlog_claims | p50 2.28 / p99 4.04 / max 11.95 (n=3000) | p50 2.25 / p99 3.43 / max 13.27 (n=3000) | p50 2.31 / p99 4.21 / max 11.73 (n=3000) | p99 4.21 / max 13.27 |
| claim | backlog_throughput_claims_per_s | 1288 | 1304 | 1266 | 1266–1304 |
| claim | empty_claims_with_backlog | 0 | 0 | 0 | 0 |
| claim | claim_failures | 0 | 0 | 0 | 0 |
| claim | duplicate_claims | 0 | 0 | 0 | 0 |
| claim | idle_polls_10000_leased | p50 2.87 / p99 4.56 / max 5.72 (n=300) | p50 2.86 / p99 5.09 / max 5.80 (n=300) | p50 2.77 / p99 4.90 / max 7.33 (n=300) | p99 5.09 / max 7.33 |
| mixed | claim | p50 2.20 / p99 5.65 / max 12.26 (n=335) | p50 2.01 / p99 5.74 / max 12.36 (n=335) | p50 2.26 / p99 4.27 / max 13.53 (n=334) | p99 5.74 / max 13.53 |
| mixed | complete | p50 3.91 / p99 9.54 / max 9.99 (n=335) | p50 3.72 / p99 6.40 / max 8.79 (n=335) | p50 4.23 / p99 6.72 / max 8.78 (n=334) | p99 9.54 / max 9.99 |
| mixed | exhaust | p50 1.39 / p99 2.79 / max 2.79 (n=33) | p50 1.30 / p99 4.33 / max 4.33 (n=33) | p50 1.45 / p99 2.02 / max 2.02 (n=33) | p99 4.33 / max 4.33 |
| mixed | renew | p50 3.89 / p99 8.28 / max 8.87 (n=335) | p50 3.71 / p99 8.17 / max 10.82 (n=335) | p50 4.21 / p99 7.46 / max 9.53 (n=334) | p99 8.28 / max 10.82 |
| mixed | operations | 1005 | 1005 | 1002 | 1002–1005 |
| mixed | outcomes | complete:Success=335 exhaust:Success=33 renew:Success=335 | complete:Success=335 exhaust:Success=33 renew:Success=335 | complete:Success=334 exhaust:Success=33 renew:Success=334 | every operation Success |
| mixed | claimed_twice | 0 | 0 | 0 | 0 |
| mixed | completed | 335 of 335 claimed | 335 of 335 claimed | 334 of 334 claimed | every claim completed |

#### SQL Server (runs 1–3)
| probe | metric | run 1 | run 2 | run 3 | worst |
| --- | --- | --- | --- | --- | --- |
| exhaust | first_sweep | 100 rows in 82.04ms | 100 rows in 77.02ms | 100 rows in 75.74ms | 100 rows, max 82.04 ms |
| exhaust | steady_state_sweeps | p50 7.30 / p99 11.15 / max 11.15 (n=50) | p50 7.18 / p99 10.61 / max 10.61 (n=50) | p50 8.10 / p99 11.43 / max 11.43 (n=50) | p99 11.43 / max 11.43 |
| exhaust | lowered_limit_batches | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 | 1000 \| 1000,1000,1000,1000,1000,1000,1000,1000,1000,0 |
| exhaust | lowered_limit_batch | p50 41.14 / p99 58.20 / max 58.20 (n=11) | p50 38.88 / p99 61.19 / max 61.19 (n=11) | p50 39.93 / p99 58.59 / max 58.59 (n=11) | p99 61.19 / max 61.19 |
| exhaust | locks_held_by_one_batch | max_coarse=0 max_key=3000 | max_coarse=0 max_key=3000 | max_coarse=0 max_key=3000 | max_coarse=0 max_key=3000 |
| exhaust | live_leases_untouched | 9900 | 9900 | 9900 | 9900 |
| order | claims | reclaimable,released,fresh_a,fresh_b | reclaimable,released,fresh_a,fresh_b | reclaimable,released,fresh_a,fresh_b | reclaimable,released,fresh_a,fresh_b |
| coalescing | live_materializations | 12 | 12 | 12 | 12 |
| coalescing | live_mismatches | 0 | 0 | 0 | 0 |
| coalescing | materialization_transaction | p50 6.37 / p99 46.96 / max 46.96 (n=12) | p50 6.46 / p99 37.84 / max 37.84 (n=12) | p50 6.78 / p99 39.38 / max 39.38 (n=12) | p99 46.96 / max 46.96 |
| coalescing | materialized_jobs_claimed_in_order | True | True | True | True |
| renewal | after_release_4s_hold | p50 7.80 / p99 13.53 / max 21.30 (n=100) | p50 7.76 / p99 12.49 / max 24.23 (n=100) | p50 7.72 / p99 27.14 / max 30.54 (n=100) | p99 27.14 / max 30.54 |
| renewal | outcomes_4s_hold | Success=100 | Success=100 | Success=100 | Success=100 |
| renewal | outcomes_6s_hold | FailureUnknown:1222=10 | FailureUnknown:1222=10 | FailureUnknown:1222=10 | FailureUnknown:1222=10 |
| renewal | time_to_lock_timeout_6s_hold | p50 5005.71 / p99 5013.04 / max 5013.04 (n=10) | p50 5003.99 / p99 5007.21 / max 5007.21 (n=10) | p50 5004.65 / p99 5009.93 / max 5009.93 (n=10) | p99 5013.04 / max 5013.04 |
| renewal | outcomes_expired_during_3s_hold | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 |
| renewal | expiring_leases_extended | 0 | 0 | 0 | 0 |
| renewal | after_fence_commit_4s_fence | p50 5.90 / p99 8.16 / max 8.16 (n=30) | p50 5.92 / p99 10.71 / max 10.71 (n=30) | p50 6.05 / p99 10.04 / max 10.04 (n=30) | p99 10.71 / max 10.71 |
| renewal | outcomes_4s_fence | Success=30 | Success=30 | Success=30 | Success=30 |
| renewal | outcomes_6s_fence | FailureUnknown:1222=10 | FailureUnknown:1222=10 | FailureUnknown:1222=10 | FailureUnknown:1222=10 |
| renewal | time_to_lock_timeout_6s_fence | p50 5006.08 / p99 5006.67 / max 5006.67 (n=10) | p50 5006.76 / p99 5007.05 / max 5007.05 (n=10) | p50 5006.33 / p99 5006.88 / max 5006.88 (n=10) | p99 5007.05 / max 5007.05 |
| renewal | fences_committed | 40 | 40 | 40 | 40 |
| renewal | after_fence_release_at_gate | p50 10.06 / p99 10.64 / max 10.64 (n=10) | p50 8.97 / p99 9.88 / max 9.88 (n=10) | p50 10.03 / p99 10.44 / max 10.44 (n=10) | p99 10.64 / max 10.64 |
| renewal | outcomes_behind_fence_at_gate | Success=10 | Success=10 | Success=10 | Success=10 |
| retention | batch_500 | p50 9.17 / p99 56.86 / max 56.86 (n=20) | p50 8.04 / p99 56.73 / max 56.73 (n=20) | p50 8.13 / p99 64.27 / max 64.27 (n=20) | p99 64.27 / max 64.27 |
| retention | rows_left | expired=50000 recent=40000 active=2000 | expired=50000 recent=40000 active=2000 | expired=50000 recent=40000 active=2000 | expired=50000 recent=40000 active=2000 |
| claim | backlog_claims | p50 4.91 / p99 10.69 / max 136.88 (n=3000) | p50 4.89 / p99 11.00 / max 142.27 (n=3000) | p50 4.84 / p99 8.42 / max 133.91 (n=3000) | p99 11.00 / max 142.27 |
| claim | backlog_throughput_claims_per_s | 562 | 566 | 582 | 562–582 |
| claim | empty_claims_with_backlog | 0 | 0 | 0 | 0 |
| claim | claim_failures | 0 | 0 | 0 | 0 |
| claim | duplicate_claims | 0 | 0 | 0 | 0 |
| claim | locks_held_by_one_claim | coarse=0 key=2 | coarse=0 key=2 | coarse=0 key=2 | coarse=0 key=2 |
| claim | idle_polls_10000_leased | p50 6.39 / p99 11.52 / max 18.54 (n=300) | p50 6.30 / p99 10.56 / max 23.51 (n=300) | p50 6.13 / p99 12.55 / max 24.20 (n=300) | p99 12.55 / max 24.20 |
| mixed | claim | p50 4.47 / p99 16.23 / max 130.47 (n=335) | p50 4.43 / p99 12.05 / max 126.33 (n=335) | p50 4.87 / p99 20.58 / max 126.57 (n=334) | p99 20.58 / max 130.47 |
| mixed | complete | p50 7.08 / p99 13.84 / max 15.47 (n=335) | p50 6.85 / p99 11.39 / max 13.74 (n=335) | p50 7.68 / p99 14.17 / max 19.43 (n=334) | p99 14.17 / max 19.43 |
| mixed | exhaust | p50 3.09 / p99 11.08 / max 11.08 (n=33) | p50 2.90 / p99 13.59 / max 13.59 (n=33) | p50 3.23 / p99 13.52 / max 13.52 (n=33) | p99 13.59 / max 13.59 |
| mixed | renew | p50 7.00 / p99 14.44 / max 17.15 (n=335) | p50 6.97 / p99 13.25 / max 14.19 (n=335) | p50 7.72 / p99 13.40 / max 16.42 (n=334) | p99 14.44 / max 17.15 |
| mixed | operations | 1005 | 1005 | 1002 | 1002–1005 |
| mixed | outcomes | complete:Success=335 exhaust:Success=33 renew:Success=335 | complete:Success=335 exhaust:Success=33 renew:Success=335 | complete:Success=334 exhaust:Success=33 renew:Success=334 | every operation Success |
| mixed | claimed_twice | 0 | 0 | 0 | 0 |
| mixed | completed | 335 of 335 claimed | 335 of 335 claimed | 334 of 334 claimed | every claim completed |

## Appendix: per-run results

Each value appears as recorded by the probes (`CMS_JOB_PROBE_RESULTS`). "worst" is the maximum across runs. PostgreSQL renewal runs 4–6 (probe 2 only) recorded after-release values of p50 18.86 / p99 52.47 / max 100.10, p50 19.06 / p99 70.89 / max 97.77, and p50 18.99 / p99 85.35 / max 88.84 ms, with 100/100 successes in each.

### PostgreSQL (runs 1–3)
| probe | metric | run 1 | run 2 | run 3 | worst |
| --- | --- | --- | --- | --- | --- |
| exhaust[IndexOrdered] | first_sweep_ms | 39.26 | 36.82 | 49.91 | 49.91 |
| exhaust[IndexOrdered] | first_sweep_rows | 100 | 100 | 100 | 100.00 |
| exhaust[IndexOrdered] | steady_state_sweeps | p50 8.07 / p99 17.25 / max 17.25 (n=50) | p50 7.03 / p99 11.87 / max 11.87 (n=50) | p50 8.00 / p99 13.04 / max 13.04 (n=50) | p99 17.25 / max 17.25 |
| exhaust[IndexOrdered] | lowered_limit_sweep_ms | 293.56 | 317.16 | 588.79 | 588.79 |
| exhaust[IndexOrdered] | lowered_limit_sweep_rows | 10000 | 10000 | 10000 | 10000.00 |
| exhaust[Planned] | first_sweep_ms | 44.78 | 36.54 | 31.15 | 44.78 |
| exhaust[Planned] | first_sweep_rows | 100 | 100 | 100 | 100.00 |
| exhaust[Planned] | steady_state_sweeps | p50 7.34 / p99 20.17 / max 20.17 (n=50) | p50 6.86 / p99 28.88 / max 28.88 (n=50) | p50 7.33 / p99 23.65 / max 23.65 (n=50) | p99 28.88 / max 28.88 |
| exhaust[Planned] | lowered_limit_sweep_ms | 293.48 | 565.72 | 221.19 | 565.72 |
| exhaust[Planned] | lowered_limit_sweep_rows | 10000 | 10000 | 10000 | 10000.00 |
| renewal | after_release_4s_hold | p50 18.64 / p99 62.61 / max 144.19 (n=100) | p50 19.51 / p99 66.33 / max 79.00 (n=100) | p50 20.36 / p99 102.21 / max 104.55 (n=100) | p99 102.21 / max 144.19 |
| renewal | total_4s_hold | p50 4026.53 / p99 4068.53 / max 4157.93 (n=100) | p50 4027.46 / p99 4088.15 / max 4114.89 (n=100) | p50 4028.77 / p99 4116.46 / max 4128.66 (n=100) | p99 4116.46 / max 4157.93 |
| renewal | outcomes_4s_hold | Success=100 | Success=100 | Success=100 | Success=100 |
| renewal | outcomes_expired_during_3s_hold | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 |
| renewal | outcomes_6s_hold | LockTimeout=10 | LockTimeout=10 | LockTimeout=10 | LockTimeout=10 |
| renewal | time_to_lock_timeout_6s_hold | p50 5009.34 / p99 5018.50 / max 5018.50 (n=10) | p50 5009.84 / p99 5017.92 / max 5017.92 (n=10) | p50 5006.65 / p99 5023.73 / max 5023.73 (n=10) | p99 5023.73 / max 5023.73 |
| retention | batch_500 | p50 4.80 / p99 38.94 / max 38.94 (n=20) | p50 5.10 / p99 8.89 / max 8.89 (n=20) | p50 4.66 / p99 8.76 / max 8.76 (n=20) | p99 38.94 / max 38.94 |
| coalescing | vectors | 16 | 16 | 16 | 16.00 |
| coalescing | vector_mismatches | 0 | 0 | 0 | 0.00 |
| coalescing | live_materializations | 12 | 12 | 12 | 12.00 |
| coalescing | live_mismatches | 0 | 0 | 0 | 0.00 |
| coalescing | materialization_transaction | p50 10.16 / p99 21.91 / max 21.91 (n=12) | p50 8.90 / p99 24.71 / max 24.71 (n=12) | p50 17.89 / p99 37.40 / max 37.40 (n=12) | p99 37.40 / max 37.40 |
| claim[IndexOrdered] | plan | Update on "Job" j / CTE candidate / Limit / LockRows / Index Scan using "IX_Job_Claim" on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j | Update on "Job" j / CTE candidate / Limit / LockRows / Index Scan using "IX_Job_Claim" on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j | Update on "Job" j / CTE candidate / Limit / LockRows / Index Scan using "IX_Job_Claim" on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j | Update on "Job" j / CTE candidate / Limit / LockRows / Index Scan using "IX_Job_Claim" on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j |
| claim[IndexOrdered] | backlog_claims | p50 4.49 / p99 11.69 / max 29.14 (n=3000) | p50 4.15 / p99 12.24 / max 25.68 (n=3000) | p50 4.32 / p99 12.50 / max 19.10 (n=3000) | p99 12.50 / max 29.14 |
| claim[IndexOrdered] | backlog_throughput_claims_per_s | 410 | 427 | 415 | 427.00 |
| claim[IndexOrdered] | empty_claims_with_backlog | 0 | 0 | 0 | 0.00 |
| claim[IndexOrdered] | idle_polls_10000_leased | p50 5.66 / p99 11.29 / max 13.51 (n=300) | p50 5.74 / p99 13.68 / max 16.90 (n=300) | p50 5.89 / p99 12.32 / max 13.67 (n=300) | p99 13.68 / max 16.90 |
| claim[Planned] | plan | Update on "Job" j / CTE candidate / Limit / LockRows / Sort / Seq Scan on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j | Update on "Job" j / CTE candidate / Limit / LockRows / Sort / Seq Scan on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j | Update on "Job" j / CTE candidate / Limit / LockRows / Sort / Seq Scan on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j | Update on "Job" j / CTE candidate / Limit / LockRows / Sort / Seq Scan on "Job" / Nested Loop / CTE Scan on candidate / Index Scan using "PK_Job" on "Job" j |
| claim[Planned] | backlog_claims | p50 8.02 / p99 15.12 / max 26.67 (n=3000) | p50 7.79 / p99 15.26 / max 33.48 (n=3000) | p50 8.62 / p99 15.41 / max 24.66 (n=3000) | p99 15.41 / max 33.48 |
| claim[Planned] | backlog_throughput_claims_per_s | 285 | 293 | 271 | 293.00 |
| claim[Planned] | empty_claims_with_backlog | 0 | 0 | 0 | 0.00 |
| claim[Planned] | idle_polls_10000_leased | p50 5.92 / p99 10.29 / max 12.75 (n=300) | p50 4.87 / p99 10.04 / max 11.34 (n=300) | p50 5.76 / p99 14.64 / max 18.22 (n=300) | p99 14.64 / max 18.22 |
| mixed[IndexOrdered] | claim | p50 3.47 / p99 10.08 / max 19.68 (n=334) | p50 3.55 / p99 14.78 / max 34.95 (n=335) | p50 3.62 / p99 17.37 / max 25.50 (n=336) | p99 17.37 / max 34.95 |
| mixed[IndexOrdered] | complete | p50 6.61 / p99 16.25 / max 24.09 (n=334) | p50 6.50 / p99 17.98 / max 72.27 (n=335) | p50 7.01 / p99 22.17 / max 49.35 (n=336) | p99 22.17 / max 72.27 |
| mixed[IndexOrdered] | exhaust | p50 2.16 / p99 3.34 / max 3.34 (n=32) | p50 2.24 / p99 6.90 / max 6.90 (n=32) | p50 2.34 / p99 5.46 / max 5.46 (n=32) | p99 6.90 / max 6.90 |
| mixed[IndexOrdered] | renew | p50 6.58 / p99 16.94 / max 19.84 (n=334) | p50 6.54 / p99 17.78 / max 23.56 (n=335) | p50 6.94 / p99 19.61 / max 74.14 (n=336) | p99 19.61 / max 74.14 |
| mixed[IndexOrdered] | operations | 1002 | 1005 | 1008 | 1008.00 |
| mixed[IndexOrdered] | deadlocks | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | lock_timeouts | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | ownership_lost | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | empty_claims_while_jobs_remain | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | unexpected_errors | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | claim | p50 3.42 / p99 10.42 / max 15.86 (n=334) | p50 3.75 / p99 10.91 / max 21.48 (n=335) | p50 3.59 / p99 14.28 / max 43.77 (n=334) | p99 14.28 / max 43.77 |
| mixed[Planned] | complete | p50 6.26 / p99 11.62 / max 17.18 (n=334) | p50 6.80 / p99 16.16 / max 20.25 (n=335) | p50 6.39 / p99 14.87 / max 20.03 (n=334) | p99 16.16 / max 20.25 |
| mixed[Planned] | exhaust | p50 2.13 / p99 3.73 / max 3.73 (n=32) | p50 2.25 / p99 5.41 / max 5.41 (n=32) | p50 2.03 / p99 6.77 / max 6.77 (n=32) | p99 6.77 / max 6.77 |
| mixed[Planned] | renew | p50 6.30 / p99 16.28 / max 20.46 (n=334) | p50 6.89 / p99 17.98 / max 21.22 (n=335) | p50 6.58 / p99 15.64 / max 28.77 (n=334) | p99 17.98 / max 28.77 |
| mixed[Planned] | operations | 1002 | 1005 | 1002 | 1005.00 |
| mixed[Planned] | deadlocks | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | lock_timeouts | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | ownership_lost | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | empty_claims_while_jobs_remain | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | unexpected_errors | 0 | 0 | 0 | 0.00 |

### SQL Server (runs 1–3)
| probe | metric | run 1 | run 2 | run 3 | worst |
| --- | --- | --- | --- | --- | --- |
| exhaust[IndexOrdered] | first_sweep_ms | 171.38 | 210.38 | 159.50 | 210.38 |
| exhaust[IndexOrdered] | first_sweep_rows | 100 | 100 | 100 | 100.00 |
| exhaust[IndexOrdered] | steady_state_sweeps | p50 55.71 / p99 86.07 / max 86.07 (n=50) | p50 51.93 / p99 84.92 / max 84.92 (n=50) | p50 57.78 / p99 70.80 / max 70.80 (n=50) | p99 86.07 / max 86.07 |
| exhaust[IndexOrdered] | lowered_limit_sweep_ms | 228.84 | 230.47 | 175.62 | 230.47 |
| exhaust[IndexOrdered] | lowered_limit_sweep_rows | 10000 | 10000 | 10000 | 10000.00 |
| exhaust[Planned] | first_sweep_ms | 87.67 | 72.63 | 79.10 | 87.67 |
| exhaust[Planned] | first_sweep_rows | 100 | 100 | 100 | 100.00 |
| exhaust[Planned] | steady_state_sweeps | p50 18.29 / p99 36.61 / max 36.61 (n=50) | p50 16.95 / p99 23.26 / max 23.26 (n=50) | p50 18.05 / p99 45.75 / max 45.75 (n=50) | p99 45.75 / max 45.75 |
| exhaust[Planned] | lowered_limit_sweep_ms | 164.10 | 339.54 | 278.29 | 339.54 |
| exhaust[Planned] | lowered_limit_sweep_rows | 10000 | 10000 | 10000 | 10000.00 |
| renewal | after_release_4s_hold | p50 21.80 / p99 61.32 / max 141.23 (n=100) | p50 24.83 / p99 91.98 / max 105.96 (n=100) | p50 22.97 / p99 68.26 / max 78.85 (n=100) | p99 91.98 / max 141.23 |
| renewal | total_4s_hold | p50 4029.27 / p99 4078.90 / max 4145.92 (n=100) | p50 4031.74 / p99 4109.10 / max 4113.64 (n=100) | p50 4029.45 / p99 4078.25 / max 4103.91 (n=100) | p99 4109.10 / max 4145.92 |
| renewal | outcomes_4s_hold | Success=100 | Success=100 | Success=100 | Success=100 |
| renewal | outcomes_expired_during_3s_hold | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 | OwnershipLost=20 |
| renewal | outcomes_6s_hold | LockTimeout=10 | LockTimeout=10 | LockTimeout=10 | LockTimeout=10 |
| renewal | time_to_lock_timeout_6s_hold | p50 5017.64 / p99 5031.59 / max 5031.59 (n=10) | p50 5011.49 / p99 5023.69 / max 5023.69 (n=10) | p50 5013.37 / p99 5019.86 / max 5019.86 (n=10) | p99 5031.59 / max 5031.59 |
| retention | batch_500 | p50 17.30 / p99 86.65 / max 86.65 (n=20) | p50 17.59 / p99 107.16 / max 107.16 (n=20) | p50 17.00 / p99 100.06 / max 100.06 (n=20) | p99 107.16 / max 107.16 |
| coalescing | vectors | 17 | 17 | 17 | 17.00 |
| coalescing | vector_mismatches | 0 | 0 | 0 | 0.00 |
| coalescing | live_materializations | 12 | 12 | 12 | 12.00 |
| coalescing | live_mismatches | 0 | 0 | 0 | 0.00 |
| coalescing | materialization_transaction | p50 31.13 / p99 79.07 / max 79.07 (n=12) | p50 18.30 / p99 149.90 / max 149.90 (n=12) | p50 13.95 / p99 90.65 / max 90.65 (n=12) | p99 149.90 / max 149.90 |
| claim[IndexOrdered] | locks_held_by_one_claim | object_non_intent=0 key=2 | object_non_intent=0 key=2 | object_non_intent=0 key=2 | object_non_intent=0 key=2 |
| claim[IndexOrdered] | backlog_claims | p50 6.24 / p99 15.13 / max 60.61 (n=3000) | p50 6.13 / p99 14.03 / max 46.13 (n=3000) | p50 6.17 / p99 15.97 / max 65.07 (n=3000) | p99 15.97 / max 65.07 |
| claim[IndexOrdered] | backlog_throughput_claims_per_s | 440 | 452 | 437 | 452.00 |
| claim[IndexOrdered] | empty_claims_with_backlog | 0 | 0 | 0 | 0.00 |
| claim[IndexOrdered] | claim_errors |  |  |  |  |
| claim[IndexOrdered] | idle_polls_10000_leased | p50 7.34 / p99 35.71 / max 46.61 (n=300) | p50 6.97 / p99 22.49 / max 34.00 (n=300) | p50 7.52 / p99 19.78 / max 36.78 (n=300) | p99 35.71 / max 46.61 |
| claim[Planned] | locks_held_by_one_claim | object_non_intent=16 key=0 | object_non_intent=16 key=0 | object_non_intent=16 key=0 | object_non_intent=16 key=0 |
| claim[Planned] | backlog_claims | p50 18.56 / p99 33.56 / max 82.15 (n=3000) | p50 19.23 / p99 35.01 / max 67.94 (n=3000) | p50 19.37 / p99 38.48 / max 66.51 (n=3000) | p99 38.48 / max 82.15 |
| claim[Planned] | backlog_throughput_claims_per_s | 154 | 150 | 147 | 154.00 |
| claim[Planned] | empty_claims_with_backlog | 4 | 7 | 5 | 7.00 |
| claim[Planned] | claim_errors |  |  |  |  |
| claim[Planned] | idle_polls_10000_leased | p50 11.43 / p99 32.84 / max 56.30 (n=300) | p50 10.70 / p99 28.03 / max 63.51 (n=300) | p50 10.18 / p99 27.35 / max 35.14 (n=300) | p99 32.84 / max 63.51 |
| mixed[IndexOrdered] | claim | p50 5.11 / p99 16.80 / max 44.24 (n=335) | p50 5.13 / p99 20.73 / max 48.34 (n=335) | p50 5.42 / p99 18.77 / max 60.50 (n=335) | p99 20.73 / max 60.50 |
| mixed[IndexOrdered] | complete | p50 11.11 / p99 30.03 / max 39.53 (n=335) | p50 11.78 / p99 22.53 / max 25.19 (n=335) | p50 12.38 / p99 26.16 / max 44.43 (n=335) | p99 30.03 / max 44.43 |
| mixed[IndexOrdered] | exhaust | p50 2.77 / p99 16.91 / max 16.91 (n=33) | p50 2.35 / p99 23.92 / max 23.92 (n=33) | p50 2.36 / p99 15.48 / max 15.48 (n=33) | p99 23.92 / max 23.92 |
| mixed[IndexOrdered] | renew | p50 10.96 / p99 23.48 / max 34.20 (n=335) | p50 11.64 / p99 27.04 / max 35.82 (n=335) | p50 11.91 / p99 26.10 / max 31.82 (n=335) | p99 27.04 / max 35.82 |
| mixed[IndexOrdered] | operations | 1005 | 1005 | 1005 | 1005.00 |
| mixed[IndexOrdered] | deadlocks | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | lock_timeouts | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | ownership_lost | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | empty_claims_while_jobs_remain | 0 | 0 | 0 | 0.00 |
| mixed[IndexOrdered] | unexpected_errors |  |  |  |  |
| mixed[Planned] | claim | p50 5.56 / p99 30.30 / max 78.60 (n=356) | p50 5.38 / p99 32.33 / max 57.57 (n=438) | p50 6.00 / p99 29.15 / max 98.11 (n=348) | p99 32.33 / max 98.11 |
| mixed[Planned] | complete | p50 11.05 / p99 27.17 / max 47.18 (n=323) | p50 11.37 / p99 29.47 / max 29.80 (n=283) | p50 11.63 / p99 28.89 / max 37.20 (n=328) | p99 29.47 / max 47.18 |
| mixed[Planned] | exhaust | p50 1.93 / p99 29.90 / max 29.90 (n=34) | p50 1.95 / p99 21.42 / max 21.42 (n=42) | p50 2.19 / p99 21.45 / max 21.45 (n=33) | p99 29.90 / max 29.90 |
| mixed[Planned] | renew | p50 10.69 / p99 36.12 / max 53.71 (n=323) | p50 11.50 / p99 33.50 / max 71.28 (n=283) | p50 11.89 / p99 33.14 / max 61.14 (n=328) | p99 36.12 / max 71.28 |
| mixed[Planned] | operations | 1002 | 1004 | 1004 | 1004.00 |
| mixed[Planned] | deadlocks | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | lock_timeouts | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | ownership_lost | 0 | 0 | 0 | 0.00 |
| mixed[Planned] | empty_claims_while_jobs_remain | 33 | 155 | 20 | 155.00 |
| mixed[Planned] | unexpected_errors |  |  |  |  |
