# DMS Performance Harness — Traditional Baseline and Final Gate

Reproducible cross-provider measurement of traditional `limit`/`offset` GET-many paging,
capturing the pre-change baseline that the performance final gate compares against. The
harness drives the real DMS pipeline in-process (the API integration harness boot), measures
the epic's fixed six-cell matrix, replays plans on a dedicated connection, and writes
machine-readable artifacts that fail loudly whenever the run did not do the expected work.
Timing values are never judged; only completeness and internal consistency are.

## Layout

| Area | Contents |
| --- | --- |
| `Configuration/` | `PERF_*` environment binding and validation, provider/fixture/scenario catalogs, evidence-run settings and guardrail configuration |
| `Fixtures/` | The deterministic 500,000-row fixture definition (with its 10,000-row smoke variant): every student carries one row in each of the four child collection tables plus descriptor-backed values from a fixed descriptor catalog; per-dialect set-based loader SQL, the loader executor, and verification queries |
| `Measurement/` | Latency measurement, driver command observer, page-selection capture, plan replay per provider, environment capture, the scenario executor, and the full run pipeline |
| `Results/` | Versioned artifact records, JSON/CSV writers, the artifact validator, and the run-directory writer |
| `Smoke/` | Explicit live proofs: loader-vs-POST proof gate, instrumentation probes, six-cell executor smoke, and the full-pipeline smoke at 10k scale |
| `Runs/` | The evidence-run entry points, configured entirely through `PERF_*` variables |

The project name deliberately does not match the CI test-discovery globs: CI compiles it via
the solution but never executes it. All executable fixtures here are `[Explicit]`. The
CI-run smoke tests live in `EdFi.DataManagementService.Performance.Harness.Tests.Unit`.

## Prerequisites

- A local PostgreSQL reachable through `ConnectionStrings__DatabaseConnection` (the compose
  container `dms-postgresql`, host port 5435, pinned `postgres:16.8-alpine`).
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
  `dms.Document`, `edfi.Student`, `dms.ReferentialIdentity`, and tracked-change side effects.
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
each statement runs under a fresh `EXPLAIN`, which the server plans as a custom plan, while
the measured requests run through the application's pooled Npgsql connections with
auto-prepare enabled — the effective `npgsql_auto_prepare_min_usages` /
`npgsql_max_auto_prepare` values, read from a data source built by the production
`NpgsqlDataSourceCache` code path, are recorded in the run manifest's settings. Warm
measured requests may therefore execute server-prepared (possibly generic) plans the replay
does not reproduce: the replay evidences plan shape and work volume, not the measured
requests' exact plan-caching regime.

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
