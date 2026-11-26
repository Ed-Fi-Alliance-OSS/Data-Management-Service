# DMS Performance Harness — Traditional Baseline, Final Gate and Benchmark Comparison

Reproducible cross-provider measurement of traditional `limit`/`offset` GET-many paging,
capturing the pre-change baseline that the performance final gate compares against. The
harness drives the real DMS pipeline in-process (the API integration harness boot), measures
the epic's fixed six-cell matrix, replays plans on a dedicated connection, and writes
machine-readable artifacts that fail loudly whenever the run did not do the expected work.
The paging artifacts never judge timing values; only completeness and internal consistency.
The [benchmark comparison](#benchmark-comparison) builds on the same pipeline to compare an
approved reference build with a candidate and reports advisory timing warnings.

## Layout

| Area | Contents |
| --- | --- |
| `Configuration/` | `PERF_*` environment binding and validation, provider/fixture/scenario catalogs, evidence-run settings and guardrail configuration; `benchmark-profile.json` and its strict loader (`PerfBenchmarkProfile.cs`) |
| `Fixtures/` | The deterministic 500,000-row fixture definition (with its 10,000-row smoke variant): every student carries one row in each of the four child collection tables plus descriptor-backed values from a fixed descriptor catalog; per-dialect set-based loader SQL, the loader executor, and verification queries |
| `Measurement/` | Latency measurement, driver command observer, page-selection capture, plan replay per provider, environment capture, the scenario executor, and the full run pipeline; for the benchmark, the serial write executor, the page-identity read observer, the test-only slowdown injection, and the side pipeline |
| `Results/` | Versioned artifact records, JSON/CSV writers, the artifact validator, and the run-directory writer; for the benchmark, the side result, the comparison engine and its JSON/CSV/Markdown writer |
| `Smoke/` | Explicit live proofs: loader-vs-POST proof gate, instrumentation probes, six-cell executor smoke, the full-pipeline smoke at 10k scale, and the benchmark side smokes |
| `Runs/` | The evidence-run entry points, configured entirely through `PERF_*` variables, including the benchmark side runs and the comparison report step |
| `HarnessAssemblyTeardown.cs` | Drops the cached per-fixture baseline databases when the harness's tests finish, so repeated runs do not accumulate databases on the server |
| `eng/performance/BenchmarkComparison.psm1`, `invoke-benchmark-comparison.ps1`, `tests/BenchmarkComparison.Tests.ps1` | The benchmark comparison wrapper module, its thin entry script, and the CI Pester test of its orchestration |

The project name deliberately does not match the CI test-discovery globs: CI compiles it via
the solution but never executes it. All executable fixtures here are `[Explicit]`. The
CI-run smoke tests live in `EdFi.DataManagementService.Performance.Harness.Tests.Unit`.

## Prerequisites

- A local PostgreSQL reachable through `ConnectionStrings__DatabaseConnection` (the compose
  container `dms-postgresql`, host port 5435, pinned `postgres:18.6-alpine`).
- A local SQL Server 2025 reachable through `ConnectionStrings__MssqlAdmin` (the
  `dms-mssql-integration-2025` container, host port 14333). `GENERATE_SERIES` requires
  SQL Server 2022+ at database compatibility level 160+; the loader guards this.
- For evidence runs through the capture wrapper, these connection strings are
  credential/option templates only: the wrapper rewrites their endpoint (host/port for
  PostgreSQL, data source for SQL Server) to the digest-validated container's published
  port binding, and refuses to run when the container does not publish the expected port —
  so the measured run cannot lease from a different server than the pinned container.
- Databases must not run on tmpfs for evidence runs.

## Running the smokes

Each smoke leases a fresh DS 5.2 database, so runs are isolated and repeatable.

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Performance.Harness -c Release `
    --filter "FullyQualifiedName~Given_Postgresql_BaselineRunSmoke"
```

Available smokes, per provider (`Given_Postgresql_*` / `Given_Mssql_*`):

- `PerfFixtureLoaderSmoke` — loads 10k rows and runs the loader-vs-POST proof gate across
  `dms.Document`, `edfi.Student` and its child collections, `dms.Descriptor`, and tracked-change
  side effects.
- `ObserverProbe` — the instrumentation checkpoint: recorder and driver observer signals.
- `ScenarioExecutorSmoke` — the six-cell matrix with per-request guardrails.
- `BaselineRunSmoke` — the full pipeline writing validated artifacts to a temp directory.

## Evidence runs

The entry points are `Runs/Given_<Provider>_TraditionalBaselineRun`, normally invoked through
`eng/performance/invoke-traditional-baseline.ps1`, which handles the baseline worktree
overlay, image digest validation, binding the connection-string endpoint to the validated
container's published port, and environment wiring, and pins the documented
primary-run iteration plan and deep offset (10 warmups / 60 measured / 450,000) as
overridable parameters. Sixty measured iterations make p95 the third-slowest sample rather
than the second, so a tail gate needs three host-side stalls to flip instead of two; the
extra warmups absorb the stalls that cluster in the first measured iterations. The wrapper is
currently Windows-only: it drives the overlay with PowerShell and robocopy.

| Variable | Required | Meaning |
| --- | --- | --- |
| `PERF_RESULTS_DIR` | yes | Fully qualified base directory; each run writes into a run-id subdirectory |
| `PERF_RUNNER_COMMIT` | yes | 40-hex commit of the harness sources (the subject commit is read from the worktree's git HEAD) |
| `PERF_FIXTURE` | yes | `primary-500k` or `smoke-10k` |
| `PERF_IMAGE_TAG` / `PERF_IMAGE_DIGEST` | yes | The validated image pin recorded in the manifest |
| `PERF_STORAGE_NOTE` | yes | Storage caveat, for example `local docker volume, not tmpfs` |
| `PERF_WARMUP_ITERATIONS` / `PERF_MEASURED_ITERATIONS` | no | Harness floor 5 / 30 (the original DMS-1391 baseline was captured at the floor); both wrappers pin 10 / 60. May be raised, never lowered |
| `PERF_DEEP_OFFSET` | no | Default 90% of the fixture row count (450,000 for the primary fixture) |
| `PERF_ALLOW_CI` | no | Default `false`: runs refuse GitHub Actions because its databases run on tmpfs |
| `PERF_ALLOW_DIRTY_PREFIXES` | no | Semicolon-separated allowlist for dirty worktree paths, matched on path-segment boundaries; defaults to the harness overlay directory. Empty entries are invalid — allow-all exists only as the in-code `AllowAnyDirtyPath` setting the smokes use, never through the environment |

Guardrails run before any measurement: a CI environment or a dirty path outside the
allowlist aborts the run, so baseline evidence cannot be produced from a contaminated tree.

## Baseline capture (overlay procedure)

The subject-under-test commit predates this harness, so the harness is overlaid onto a
worktree of that commit — only new files, never edits:

1. `git worktree add <path> 5656477957eb2f18e827b7969e5079b424596ae0`
2. Copy this project directory (without `bin`/`obj`) into the same relative path.
3. Build the harness csproj there and run the evidence fixtures with `PERF_RUNNER_COMMIT`
   set to the harness-source commit. The run manifest records both commits and the dirty
   overlay paths.

**Legacy paging compatibility.** The current harness no longer compiles against the
subject `5656477`: since #1212 (DMS-1367) it uses the `NpgsqlDataSourceLease`/`AcquireLease`
API that commit lacks, and the benchmark comparison adds references to
`DataStoreOwnershipSnapshot` and `IDataStoreOwnershipReconciler`, which it also lacks. To
recapture the DMS-1391 baseline, use the triple recorded in the retained DMS-1391 run
manifests (`dms-1392-traditional-baseline-raw.zip`, attached to DMS-1392), together:

| Part | Value |
| --- | --- |
| Runner (harness and wrapper) | `0d3241bf92f34a8807e2e444c9b920a2e1afb8b5`, a commit of DMS-1391's PR #1191 that is not on `main`: fetch it with `git fetch origin pull/1191/head` |
| Subject | `5656477957eb2f18e827b7969e5079b424596ae0` |
| PostgreSQL image | `postgres:16.8-alpine@sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0` |
| SQL Server image | SQL Server 2025 `@sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a` |

Run `eng/performance/invoke-traditional-baseline.ps1` from a worktree checked out at that
runner commit, so its overlay, digest pins and harness are the ones that produced the
baseline. Start the historical `postgres:16.8-alpine` container first: it differs from the
current `postgres:18.6-alpine` prerequisite, and the wrapper's digest guard refuses any other
image. The pair is verified to build: a `git archive` export of `5656477` on a short `subst`
drive (the paths exceed `MAX_PATH` otherwise), overlaid with the runner commit's harness
directory, builds with `dotnet build <harness csproj> -c Release -p:NuGetAudit=false`
(`NuGetAudit` only silences advisories published after that commit). Its runtime behavior is
attested by the original DMS-1391 run. If the retained manifests are unavailable, the fallback
runner is `d45b508d2`, the last harness/wrapper commit on `main` before #1212, whose wrapper
pins the same two digests.

## Artifact layout (schema 1.3.0)

| File | Content |
| --- | --- |
| `run-manifest.json` | Run/commit/fixture/iteration identity and the full environment identity |
| `results.json` | Six scenario rows: app latency, the driver-observed execute interval (diagnostic only), full-batch replay metrics, plan reference, SQL hash |
| `results.csv` | The same rows in a fixed 29-column order |
| `fixture-manifest.json` | The verified fixture definition and its analytic values |
| `plans/` | Full-hydration-batch replay evidence per cell. PostgreSQL: one `.explain.json` listing every batch statement with the raw `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` document of each DML/SELECT statement. SQL Server: one `.plans.json` index pointing at the per-statement actual `.sqlplan` XML files (arrival order) and the raw `.stats.txt` |
| `sql/` | The one page-selection text, the one hydration-batch text, and per-cell bound parameter values |

The `database` metrics in `results.json`/`results.csv` come from replaying the full captured
hydration batch — the one DbCommand the measured request executed — with the same bound
paging parameters, not from replaying the page-selection statement alone. The page-selection
text and its SHA-256 are still captured separately for the textual SQL gate.

The fixture's one intentionally zero-row hydration statement is the person document-reference
resolution: the optional `personReference` stays null by design, because a faithful nonzero
shape would need one Person document per student (doubling `dms.Document` and shifting every
Document-join measurement) and a shared person would be an unfaithful fan-in. The statement
still executes and is replayed every run; every collection-hydration and descriptor-URI
statement does real, uniform work at every offset.

The replay is an out-of-band, one-shot execution on a dedicated connection: on PostgreSQL
each statement runs under a fresh `EXPLAIN`, which the server plans as a custom plan. What the
measured requests ran under depends on which of three connection regimes produced the run, so
read retained timing and plan evidence against the right one:

1. **The historical pinned baseline (`5656477`, the DMS-1391 runs).** PostgreSQL data sources
   were cached per connection string for the process lifetime, with auto-prepare after 3 uses
   (`AutoPrepareMinUsages = 3`, `MaxAutoPrepare = 256`); SQL Server used SqlClient's default
   pooling. Warm measured requests may therefore execute server-prepared (possibly generic)
   plans the replay does not reproduce: the replay evidences plan shape and work volume, not
   the measured requests' exact plan-caching regime. The effective
   `npgsql_auto_prepare_min_usages` / `npgsql_max_auto_prepare` values, read from a data source
   built by the production `NpgsqlDataSourceCache` code path, are recorded in the run
   manifest's settings.
2. **The current paging host (the traditional-baseline and final-gate entry points).** The
   in-process host never publishes data-store ownership, so when a request's lease on the
   connection string ends, the backend retires that string and clears its pool: every request
   opens a new physical connection and nothing stays prepared. Measured requests run custom
   plans, like the replay, and their latency includes connection setup.
3. **The benchmark comparison host.** Each benchmark side publishes data-store ownership for
   its leased primary right after host start and before any warmup, so connections stay
   pooled and PostgreSQL auto-prepares, as in production. The side records its effective
   connection settings, and the comparison requires them to equal the profile's
   `workload.connection.<provider>` (the drivers' defaults: pooling on, minimum 0, maximum 100,
   plus `NoResetOnClose` and auto-prepare 3/256 on PostgreSQL).

## Final gate (partitioned cursor paging)

The final gate is **manual and off-CI**, like the baseline capture: its fixtures are
`[Explicit]`, the harness project is never discovered by CI, and the in-pipeline guardrails
refuse CI databases. It measures the epic's closed 36-cell matrix and evaluates every
acceptance gate against the DMS-1391 baseline.

Entry points under `Runs/`, configured through the same `PERF_*` conventions:

- `Given_<Provider>_FinalGatePrimaryRun` — one `primary-500k` load measured across three
  ordered phase tests over one shared leased database: pristine (the traditional rerun,
  unfiltered cursor cells, and partitions 1/10/200, with authorization bypassed exactly
  like the baseline capture), authorized (after the set-based association seeding, under
  the relationship claim), and filtered (after the birth-date overlay). The filtered phase
  writes the run directory.
- `Given_<Provider>_FinalGateDescriptorRun` — the separate `descriptors-25k` fixture under
  the real namespace principal (`PERF_DESCRIPTOR_FIXTURE` selects `descriptors-25k` or
  `descriptors-smoke-2k`; default `descriptors-25k`).
- `Given_FinalGateReportRun` — the report step: no database, artifacts in, report out. It
  reads `PERF_REPORT_DIR` plus per-provider directory triplets
  (`PERF_BASELINE_DIR_<PROVIDER>`, `PERF_FINAL_PRIMARY_DIR_<PROVIDER>`,
  `PERF_FINAL_DESCRIPTORS_DIR_<PROVIDER>`, provider `POSTGRESQL` or `MSSQL`), loads and
  revalidates each run (including that every referenced plan-evidence file exists),
  evaluates the gates, and writes `final-report.md` and `final-report.json`.

The wrapper `eng/performance/invoke-final-gate.ps1` sequences everything: it requires the
harness and wrapper sources to be committed clean (HEAD is both runner and subject commit),
validates the running containers against the pinned digests, rewrites the connection-string
endpoints to the validated containers' published ports, runs both evidence fixtures per
provider, and finishes with the report step against the baseline directories.

```powershell
./eng/performance/invoke-final-gate.ps1 -Provider postgresql,mssql `
    -ResultsDirectory C:\perf\final-gate
```

`-ReportOnly` regenerates the report from existing artifact directories without rerunning
any measurement. Baseline directories are always passed explicitly: measured artifacts are
attached to their Jira story rather than kept in the repository, so extract the DMS-1391
baseline attachment and point `-PostgresqlBaselineDirectory` / `-MssqlBaselineDirectory` at
the extracted runs:

```powershell
./eng/performance/invoke-final-gate.ps1 -ReportOnly `
    -ReportDirectory C:\perf\final-gate\final-report `
    -PostgresqlPrimaryDirectory C:\perf\final-gate\postgresql-final-primary-... `
    -PostgresqlDescriptorsDirectory C:\perf\final-gate\postgresql-final-descriptors-...
```

Final-gate artifacts use schema `2.0.0` (the baseline stays at its frozen `1.3.0`). Each
provider produces two run directories — `<provider>-final-primary-<fixture>-<timestamp>`
and `<provider>-final-descriptors-<fixture>-<timestamp>` — with the same file shapes as
the baseline plus: rows carry family/variant/phase, cursor range and start anchor, and
partition request/return counts; the run manifest records the run kind and the phase log
(the association seeding and the overlay, with their analytic facts); `sql/` holds
per-cell selection text, hydration batch text where it is a separate statement, and a
`parameters.json` naming whether the replay values came from the hydration keyset or the
recorded relational command.

The report marks each gate **PASS**, **FAIL**, or **INCONCLUSIVE**. Latency gates are
app-level p50/p95 ratios (1.20x/1.30x; partition 200-vs-1 at 1.25x p50, unfiltered
primary only). Cross-run gates — the traditional shallow regression and the first-cursor
entry cost — additionally require the same fixture identity and a comparable environment
(machine fingerprint, image digest, server version) as the baseline; otherwise they report
INCONCLUSIVE rather than judging numbers measured on different ground. Deep-offset results
are recorded as an observation and are never a gate. FAIL or INCONCLUSIVE in the report is
the finding itself; the report step's test only fails when the report cannot be produced.

Notes for comparison work: app-level p50/p95 can be dominated by hydration work that is
invariant across page offsets, so a page-selection improvement can be diluted to noise in
app-level ratios — the final-gate comparison (DMS-1392) must treat the per-statement plan
metrics, above all the page-selection statement's, as first-class inputs wherever that
dilution applies. `driver_execute_*_ms` is the driver-observed execute/dispatch interval
from provider diagnostics and is diagnostic evidence only: it is not guaranteed to include
reader/result-set consumption — on SQL Server the diagnostic "after" event fires when
`ExecuteReader` returns, before the rows and subsequent result sets are consumed — so it
must not be used as a SQL Server gate quantity. SQL Server `db_cpu_ms`/`db_elapsed_ms` are
indicative only, because `STATISTICS TIME` reports whole milliseconds per statement and the
batch totals sum that integer rounding across every planned statement. The reliable SQL
Server comparison inputs are app-level latency for end-to-end behavior, `db_logical_reads`,
and the per-statement plan/IO evidence under `plans/`. And the epic's ratio gates assume
the final-gate runs reuse the same machine and pinned configuration recorded in the
manifest.

## Benchmark comparison

One unattended command compares an **approved reference** build with a **candidate** build on
one provider. It runs the six traditional paging cells plus one serial Student update that
changes a nested collection, in alternating-order pairs, each side in a fresh process, and
writes a comparison with advisory timing warnings. Timing regressions never fail the command;
an invalid comparison (wrong workload, failed work, unsuitable environment) does.

### Platform and prerequisites

One execution platform is supported, and every comparison checks it:

- Windows 11 x64 on AC power, with English (en-US) performance counter names; Docker Desktop
  with the WSL2 backend; PowerShell 7; the repository's .NET SDK.
- The provider's database container running the pinned image, with its data on a Docker
  **volume** (never tmpfs or a bind mount). The defaults are `dms-postgresql`
  (`postgres:18.6-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873`)
  and `dms-mssql-integration-2025` (SQL Server 2025
  `@sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a`); pass
  `-PostgresContainerName` / `-MssqlContainerName` and the digest parameters for others.
- A memory limit on the SQL Server container (for example
  `docker update --memory 6g --memory-swap 6g <container>`). Without one, SQL Server sizes
  itself to the whole Docker VM, the VM takes that memory from Windows, and the host-memory
  rule invalidates every side.
- **Only one provider's container running**: stop the other one. No other benchmark or
  harness process, and a quiet host: close heavy applications, because idle host CPU above
  15% on any side invalidates the comparison.
- `ConnectionStrings__DatabaseConnection` (always) and `ConnectionStrings__MssqlAdmin` (for
  `mssql`) set as credential templates. The wrapper replaces their endpoint with the pinned
  container's published port. Do not add pool options: the effective connection settings must
  equal the profile's, which are the drivers' defaults.
- The harness directory, `benchmark-profile.json`, the wrapper module and its entry script
  committed clean: the wrapper records the source `HEAD` as the harness commit.
- Every invocation run from the same kind of session, either always elevated or always not.
  Git refuses a worktree created by an elevated session when a normal session uses it
  ("dubious ownership"), so mixing them breaks setup until `-WorktreeRoot` changes.

### The command and the profile

```powershell
./eng/performance/invoke-benchmark-comparison.ps1 -Provider postgresql `
    -BaselineCommit df7e23425beef2de46fa98b3b5b48578cb2d3692 `
    -CandidateCommit <commit> -ResultsDirectory C:\perf\bench -WorktreeRoot C:\dmsb
```

| Parameter | Meaning |
| --- | --- |
| `-Provider` | `postgresql` or `mssql`; one provider per invocation |
| `-BaselineCommit` | Mandatory; must be the profile's `approvedReferenceCommit` |
| `-CandidateCommit` | The build under test; defaults to the baseline |
| `-Pairs` | Comparison pairs; defaults to the profile's `comparison.pairs` (5) |
| `-Purpose` | `Candidate` (default), `Calibration`, `Sizing`, `Repeatability`, `Detection` or `BoundaryProbe`; see [calibration, sizing and qualification](#calibration-sizing-and-qualification) |
| `-InjectSlowdown` | `<scenarioId>=<factor>;...`, the test-only slowdown on candidate sides; only for `Sizing` or `Candidate` |
| `-Smoke` | Run the profile's `smokeWorkload` instead of its `workload`; a smoke comparison is never evidence |
| `-WorktreeRoot` | Where one worktree per commit is created and reused; keep it short (`MAX_PATH`). Defaults to `dms-bench` beside the repository |
| `-ResultsDirectory` | Each invocation writes a fresh `<provider>-<purpose>-<yyyyMMddTHHmmssZ>-<candidate8>` directory here |

`Configuration/benchmark-profile.json` is the versioned definition of everything a comparison
depends on:

- the supported range and the approved reference commit;
- the `workload`: fixture `primary-500k`, deep offset 450,000, reads 50 warmups / 60 measured
  per cell, writes 1,000 / 200, one request in flight, authorization bypassed, Serilog `Warning`,
  published ownership, and the pinned connection settings;
- the `smokeWorkload` for rehearsals: `smoke-10k`, reads 5 / 30, writes 5 / 20, and fixed
  injection factors;
- the required environment, the comparison rule and thresholds, the detection rule, and the
  environment rules.

Its SHA-256 is recorded in the run plan and in every side, and the report refuses a profile
whose hash differs from the one the run used. A profile at version 1.0.0 or later is frozen: it
must carry each provider's thresholds, qualified environment fingerprint, and detection
factors.

### What one invocation does

1. **Setup.** Any failure returns 1 before any side runs. The wrapper:
   - takes the exclusive host lock `%ProgramData%\EdFi\dms-benchmark.lock`;
   - requires the harness, profile, module and script to be committed clean;
   - resolves both commits and applies the supported-range and purpose rules;
   - creates the invocation directory;
   - creates or reuses one worktree per commit, overlays the harness exactly, and builds it in
     Release;
   - checks the container's image digest and rewrites the connection-string endpoint;
   - writes `run-plan.json` with a new `runId`.
2. **Each side**, in the recorded order (odd pairs run the baseline first):
   - start the collector and wait until it has both host and Docker samples (30 s at most,
     otherwise the side fails);
   - write `environment.json`;
   - mark the `settle` phase for 60 s and the `idle` phase for 10 s;
   - mark `side` and run the side in a fresh `dotnet test` process
     (`Given_<Provider>_BenchmarkSideRun`), teeing its output to `side.log`;
   - wait at most 10 s until every metric has a sample stamped after the process exited, then
     stop the collector and rewrite `environment.json` with the phase windows and the side
     process's exit code. The side writes `benchmark-side.json` before its test teardown, so a
     side that exits nonzero, or has no recorded exit code, makes the comparison invalid even
     when its evidence is complete.

   The first failed side stops the loop.
3. **The report step** (`Given_BenchmarkComparisonRun`, no database) always runs over the
   invocation directory and writes `comparison.json`, `.csv` and `.md`.

Inside a side, the harness:

- leases a freshly provisioned database built from that commit's own generated DDL and loads
  the deterministic fixture;
- refreshes statistics over every fixture table (`VACUUM (ANALYZE)` on PostgreSQL,
  `UPDATE STATISTICS` on SQL Server);
- on PostgreSQL, compacts every fixture table with `VACUUM (FULL, ANALYZE)`. The stamp
  triggers update each root row during the load, and the dead space a plain `VACUUM` keeps
  varies from load to load. The size of `dms.Document` decides between two near-equal-cost
  plans for the hydration join to the page temp table (an index nested loop or a full-scan
  hash join), so without the rewrite, sides of the same build ran different plans. The
  paging and final-gate runs do not compact;
- publishes data-store ownership;
- runs the six read cells and then the write scenario, each with its warmups before its
  measured iterations. Nothing is cleared between warmup and measurement: the warmups are the
  warm-cache procedure, and they also bring the in-process host to JIT steady state. With 20
  write warmups, .NET was still recompiling the write path during the measured writes
  (about 1 s of compilation inside a 1.6 s phase, a different amount on every side); with
  1,000, the host's CPU per measured write fell from 6–8 ms to about 2 ms. On PostgreSQL, a
  `CHECKPOINT` runs right before the writes. It restarts the server's `checkpoint_timeout`
  timer, so the periodic checkpoint, whose fsyncs slow every commit, cannot overlap the few
  seconds of writes.

The reads mutate nothing. Each write is a `PUT` of a target student's full fixture document
with only `otherNames[0].firstName` changed (`Perf` to `PerfUpdated`). Targets are selected
by a fixed stride with no randomness, and warmup and measured targets are disjoint. A
set-based check before and after the writes proves that exactly the targeted rows changed and
that each child table kept its row count. Every request's status, returned page identities,
hydrated collections, and command count are checked outside the timed window. Provisioning,
loading, warmup and verification are never timed, and every side starts from a fresh
database, so no state carries over between sides; the side drops its databases when it
finishes.

### Environment contract

Two different fingerprints are compared:

- **Within one comparison**, every side must agree on the profile SHA-256, the harness commit,
  the fixture, the log level, the ownership flag, the effective connection settings, the
  instrumentation, and every environment-fingerprint field. Permitted to differ, and listed in
  the report: the subject commit, `EffectiveSchemaHash`, the OS build, the .NET patch version,
  and the Docker version.
- **Against qualification**, when the profile sets `qualifiedEnvironments.<provider>`, every
  side's fingerprint must equal it: OS family and architecture, machine fingerprint, CPU model
  and cores, host memory, power plan and AC state, Docker VM CPUs and memory, the database
  container's CPU and memory limits, image digest, server version, database settings, storage
  mount type, and effective connection settings. It never includes the profile hash or the
  harness commit, so editing the profile does not invalidate it. When it is null, the result
  is labelled `uncalibrated`.

The database container's CPU limit is the smallest of its `--cpus`, its CFS quota
(`--cpu-quota` / `--cpu-period`), the number of CPUs its `--cpuset-cpus` allows, and the Docker
VM's CPUs, so a quota or a CPU pinning changes the fingerprint even though Docker records them
apart from `--cpus`.

Required values are asserted on every side directly, not only compared between sides: Serilog
`Warning`, ownership published, connection settings equal to `workload.connection.<provider>`,
Windows, x64, AC power online, and a volume mount. Two sides that agree on a disallowed value
are still invalid.

The collector writes `samples.csv` (`timestampUtc,phase,metric,value`):

- the host through `Get-Counter` (CPU %, available MB), at a nominal 1 s interval;
- the database container through `docker stats --no-stream` (CPU %, memory % of its limit,
  cumulative block I/O bytes), at a nominal 2 s interval. A Docker sample is stamped when its
  poll completes.

`Get-Counter` fires about every 1.01 s and one Docker poll takes 1 to 2 s, so two host
samplers run offset by half an interval and three Docker pollers run on a fixed schedule
offset by a third. Samples therefore arrive about every half interval.

These rules make a comparison invalid, with the reason:

| Rule | Threshold |
| --- | --- |
| Idle host CPU, mean over the `idle` phase | at most 15% |
| Idle database CPU, mean over the `idle` phase | at most 5% |
| Host available memory, any sample | at least 2048 MB |
| Database memory, any sample | at most 90% of its limit |
| Completeness, for each metric and each phase | at least 90% of the phase duration divided by the metric's nominal interval |
| Measured-window coverage, for each scenario's measured window and each metric (`i` = its nominal interval) | a sample in `[start − i, start]`, one in `[end, end + i]`, and no gap over `2 × i` across `[start − i, end + i]` |
| Competing harness process, the other provider's container running, tmpfs storage | none |

Workload CPU and I/O during measurement never invalidate a run. The limits of these checks:

- the host counter sees the whole WSL2 VM as host load;
- nothing attributes background activity to its source;
- the competing-process check matches only harness command lines;
- the other-container check knows only the other provider's container name.

They catch a noisy or misconfigured host. They do not prove a quiet one.

### Supported range and baseline approval

`supportedRange.floorCommit` (`df7e2342`) is the oldest commit the pinned harness and fixture
definition are known to measure without adaptation. Setup rejects each of these with exit
code 1 and the reason:

- a commit that does not resolve to a full commit id;
- a commit that does not descend from the floor;
- a baseline that is not `approvedReferenceCommit`.

Broader historical comparison is out of scope. A new reference is approved by a pull request
that changes `approvedReferenceCommit` in the profile; candidates are never promoted
automatically. The reference is re-run beside every candidate, so historical timing artifacts
are supporting evidence only.

### Environment changes and recalibration

A comparison whose sides differ from the frozen `qualifiedEnvironments.<provider>` fingerprint
is invalid. After a host, Docker, image, database-setting or connection change, recalibrate
that provider (see [calibration, sizing and qualification](#calibration-sizing-and-qualification)),
then update its thresholds, fingerprint, and factors in the profile through a pull request.
`Calibration` and `Sizing` runs do not check the frozen fingerprint, so they can derive those
replacements on the changed environment; a mismatch only labels them `uncalibrated`. Every
other purpose stays invalid there until the updated profile is merged.

### Results and exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | A valid comparison: `acceptable`, or `suspected-regression` (warnings are advisory) |
| `1` | Setup failed or the pair was rejected (no side ran), or the report could not be produced or read |
| `2` | `invalid`: a failed or missing side, the wrong workload, failed operations, a violated rule, or an incompatible environment |

A scenario is a **suspected regression** when the median of its per-pair
`candidate p50 / baseline p50` ratios exceeds `1 + threshold`. The provisional threshold is
10% for every provider and scenario until calibration freezes per-scenario values. Each
provider is compared only with its own baseline.

### Artifacts and interpretation

The invocation directory holds:

- `run-plan.json`: run id, purpose, commits, profile hash, injection, and order;
- `wrapper.log`;
- `comparison.json`, `.csv`, `.md`, and `.log`;
- `pairs/NN/<role>/` for each side: `benchmark-side.json`, `environment.json`, `samples.csv`,
  `side.log`, and the side's paging run directory with its unchanged schema 1.3.0 artifacts,
  including the plan evidence under `plans/`.

`benchmark-side.json` names its paging run (`readRun.runId`, the directory's name) and records
the SHA-256 of that run's `results.json`, where the read latencies come from. The report reads
only that run and rejects the side when the run's manifest or results are not the ones the side
measured. In a same-build run the commits cannot tell the sides apart, so this is what keeps one
side's timings from passing as another's. The other side files are already tied to their side:
`benchmark-side.json` and `environment.json` by run, pair and role, and `samples.csv` by the
measured-window coverage rule.

Build logs (`build-<commit12>.log`) stay in the results directory.

- `comparison.json` (`benchmark-comparison 1.0.0`) carries:
  - the result, exit code, and purpose;
  - `evidence` (false for smoke) and `qualificationEligible`;
  - labels: `demonstration` when injected, `uncalibrated`, `smoke`;
  - the run-plan profile hash, the invalid and not-eligible reasons, and the permitted
    differences;
  - every scenario's statistics, and the calibration, detection, or sizing block.

  The wrapper returns its `exitCode`.
- `comparison.csv` has one row per scenario and pair plus a `combined` row per scenario. The
  `diagnostic_` columns are read diagnostics, blank for the write.
- `comparison.md` is the human-readable report.
- **Latency.** p50 drives the verdict. p95 is always reported, but its warnings are off unless
  the profile enables them, so a regression that only affects the tail can be missed. p99 is
  descriptive and limited by the sample size: nearest-rank p99 over 60 read samples is the
  maximum, and over 200 write samples it is the third slowest.
  No throughput is reported: this is serial latency with one request in flight, not capacity.
- **Command counts** are driver command executions per request, counted from Npgsql's
  `ActivitySource` (activities carrying a statement) and SqlClient's `DiagnosticListener`
  command events. Connection opens, pool resets and transaction begin/commit are not counted,
  although they add network round trips, so a command count is not a round-trip count. Each read executes exactly one command (the executor rejects anything
  else); the write records its distribution, currently two commands per `PUT` on both
  providers.
- The driver-observed execute time is diagnostic only (see the final-gate notes above).

### Reproducing a reported regression

1. Read `comparison.md`: which scenarios are flagged, their per-pair ratios and spread, and
   whether the profile was `uncalibrated` for that provider.
2. Rerun the same command with the same commits on the same host, ideally with more pairs
   (`-Pairs 10`). A real regression flags again with a similar median ratio; noise does not.
3. Compare the flagged scenario's sides: the command counts in `comparison.csv`, the plan
   evidence under each side's `plans/`, and the environment facts in `environment.json`.
4. Narrow the change down with `-Purpose Candidate` runs on commits between the reference and
   the candidate. Every candidate must descend from the floor commit.

### Calibration, sizing and qualification

Detection is declared before any evidence run: a **+20%** client p50 increase on
`student-put-collection-update` and `traditional-offset-zero/25`. A test-only
`IStartupFilter` injects it on candidate sides only, busy-waiting `factor × the request's own
pipeline time` and counting the requests it affected. An injected run detects the declared
slowdown only when all of these hold:

- both demonstration scenarios are flagged;
- each one's observed increase lies in
  `[max(target − 2 pp, threshold + 5 pp), target + 5 pp]`, which is 18–25% while the
  threshold is at most 13%;
- no other scenario is flagged.

A demonstration scenario whose threshold exceeds 13% cannot qualify. Recalibrate it with more
samples or pairs, never by raising the target or widening the band.

Every purpose except `Candidate` requires baseline = candidate = the approved reference, so
the injection is the only controlled difference:

| Purpose | Injection | Pairs | Output |
| --- | --- | --- | --- |
| `Calibration` | none | 5 or more | `calibration`: each scenario's derived threshold `max(5%, 2 × max \|pair ratio − 1\|)`, rounded up to a whole percent, and the observed fingerprint |
| `Sizing` | `-InjectSlowdown`, required | 5 | `sizing`: for each demonstration scenario, `in-band` (20% ± 2 pp), `adjust` with `suggestedFactor = factor × 0.20 / observed`, or `inconclusive`; `nextFactor` is always defined |
| `Repeatability` | none | 5 or more | must show no warnings; qualification evidence |
| `Detection` | the frozen `detection.injectionFactors` | 5 or more | `detection`: the frozen rule above; qualification evidence |
| `BoundaryProbe` | the frozen factors × `threshold / 0.20` | the profile's pair count | the outcomes, reported whatever they are |

Only `Repeatability` and `Detection` can be `qualificationEligible`, and only with a frozen
profile, at least five pairs, the full workload, and exactly the injection the purpose
requires. Everything else is a diagnostic.

The procedure, for each provider, one provider at a time:

1. Run one `Calibration` with at least 5 pairs.
2. Size each demonstration scenario with 5-pair `Sizing` runs: start at factor 0.20, then use
   each scenario's `nextFactor`. Stop when both are in band, after four runs, or after two
   consecutive inconclusive runs for one scenario.
3. Once both providers have in-band factors and every derived demonstration threshold is at
   most 13%, write all thresholds, fingerprints, and factors into the profile in one edit and
   bump `profileVersion` to 1.0.0.
4. Qualify: one `Repeatability` run, three `Detection` runs, and one `BoundaryProbe` run. Keep
   every run, including unsuccessful ones.

`-Smoke` runs any purpose on the small workload to rehearse the mechanics. Smoke runs are
never evidence.

### Qualification evidence

The profile is **not qualified**. Calibration and sizing ran in four rounds on both providers
(DMS-1019, November 2026), and three of the four provider and demonstration-scenario
combinations stayed above the 13% threshold limit or never sized into the band, so the
profile was not frozen. It stays at the provisional version 0.3.0:

- comparisons are labelled `uncalibrated` and use the provisional 10% p50 threshold for every
  provider and scenario, so their warnings are advisory only;
- `Detection` and `BoundaryProbe` cannot start, because `detection.injectionFactors` is null;
- a `Repeatability` run is a diagnostic, never qualification evidence.

Every run is kept, including the invalid ones, in the evidence archive attached to DMS-1019.
Each run's `run-plan.json` records its harness commit and profile hash.

**Final calibration**, round 4: profile 0.3.0, 5 pairs per provider, baseline = candidate =
`df7e2342`, each scenario's spread followed by its derived threshold:

| Scenario | PostgreSQL | SQL Server |
| --- | --- | --- |
| `traditional-offset-zero/25` (demonstration) | 10.6% → **22%** | 9.7% → **20%** |
| `traditional-offset-zero/500` | 5.0% → 10% | 3.1% → 7% |
| `traditional-offset-shallow/25` | 2.5% → 5% | 10.7% → 22% |
| `traditional-offset-shallow/500` | 7.1% → 15% | 2.8% → 6% |
| `traditional-offset-deep/25` | 7.0% → 15% | 2.1% → 5% |
| `traditional-offset-deep/500` | 6.0% → 13% | 2.0% → 5% |
| `student-put-collection-update` (demonstration) | 30.4% → **61%** | 6.1% → **13%** |

**Sizing history**, SQL Server, round 4, 5 pairs per run, shown as factor → observed p50
increase:

| Run | `student-put-collection-update` | `traditional-offset-zero/25` |
| --- | --- | --- |
| 1 | 0.20 → 23.4% (adjust) | 0.20 → 23.9% (adjust) |
| 2 | 0.1711 → 14.0% (adjust) | 0.1675 → 15.1% (adjust) |
| 3 | 0.2446 → 28.8% (adjust) | 0.2223 → 25.2% (adjust) |
| 4 | 0.1698 → 20.8% (**in band**) | 0.1767 → 22.7% (adjust) |

The SQL Server write met the freeze criteria, a 13% threshold and an in-band factor of 0.1698,
but it is not qualified: the qualification runs need a frozen profile. Its offset-zero/25 read
did not size into the band after four runs. PostgreSQL was not sized in round 4, because
both of its demonstration thresholds were already over the limit.

**Demonstrated sensitivity.** All four of the round-4 SQL Server sizing runs flagged both
injected scenarios, with observed increases of 14–29%, and flagged no other scenario.
Earlier 2-pair sizing runs flagged the injected scenarios whenever the observed increase
exceeded the provisional threshold. Two of the PostgreSQL ones also flagged scenarios that
were not injected: five in round 1, from the plan flip, and `traditional-offset-shallow/500`
in round 2. Near-boundary probes were not run, since they need frozen
factors. The injection is the test-only `IStartupFilter`: on candidate sides only, it
busy-waits `factor × the request's own pipeline time` and counts the requests it affected,
and every comparison checked those counts.

**What calibration changed in the harness.** Each round exposed a source of same-build
variance:

| Round | Profile | Finding | Change |
| --- | --- | --- | --- |
| 1 | 0.1.0 | PostgreSQL sides of one build ran two plans for the hydration join (read thresholds up to 155%) | Benchmark sides compact the fixture with `VACUUM (FULL, ANALYZE)` |
| 2 | 0.1.0 | PostgreSQL write split per side (82%); SQL Server's fast reads too noisy for 2-pair sizing | A `CHECKPOINT` before the writes; 300 measured reads (0.2.0); 5-pair sizing |
| 3 | 0.2.0 | The write still split. About 1 s of JIT compilation ran inside the 1.6 s write phase | 1,000 write and 50 read warmups, back to 60 measured reads (0.3.0) |
| 4 | 0.3.0 | Host CPU per write fell from 6–8 ms to about 2 ms; the remaining spread came from single disturbed pairs | None: closed as unqualified |

**Why it did not qualify:**

- The derived threshold uses the single worst pair. On PostgreSQL, the quiet pairs agreed
  within about 2% (read ratios 1.006–1.012, write ratios 0.977–0.988), while the pairs measured
  at 16–24% host CPU set the thresholds.
- On the quiet SQL Server calibration, one pair of the five set the offset-zero/25 threshold:
  its ratios were 1.097, 0.959, 1.020, 1.005 and 1.029.
- Even with 5 pairs, the observed increase moved 4–7 pp between runs at nearly the same
  factor, against the ±2 pp sizing band.
- The PostgreSQL write varies 5–6.5 ms between fresh sides even when the host is quiet, with
  identical plans and statement times. The rest of the request time is commit flush and
  Docker networking on the Windows/WSL2 host, outside the harness.

**Environment:**

- Windows 11 build 26300 x64 on AC power, Balanced power plan; an Intel Family 6 Model 141
  CPU with 16 logical cores and 32 GB of memory.
- Docker 29.9.3 with a WSL2 VM of 16 CPUs and 16 GB.
- PostgreSQL 18.6 (`postgres:18.6-alpine@sha256:77f58511…`) with no container limits.
- SQL Server 2025 CU7 (`@sha256:86cc6144…`) limited to 6 GB.
- .NET 10.0.15, Npgsql 8.0.4, Microsoft.Data.SqlClient 6.1.5.

The host was a shared workstation, not a dedicated benchmark machine. One SQL Server
calibration was invalid because host available memory fell below 2,048 MB with desktop
applications open: start a SQL Server run with at least 12 GB available.

**Limitations:**

- This is pilot evidence for repeatability, not a statistical guarantee.
- Warnings are p50-only, so a regression that only affects the tail can be missed.
- One platform and one host were measured.
- Fast scenarios (about 5–10 ms) vary more between fresh processes and databases than the
  ±2 pp sizing band allows on this host.

To qualify the profile, repeat the procedure above on a dedicated, idle host.
