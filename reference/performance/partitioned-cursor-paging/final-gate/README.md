# Partitioned Cursor Paging Final Gate (DMS-1392)

The retained evidence for the partitioned-cursor-paging epic's performance final gate: the
four final-gate run directories, the combined gate report, and this provenance note. These
artifacts are frozen evidence: they are regenerated only by rerunning the capture, never
edited.

## Result

`final-report.md` / `final-report.json`: **overall PASS**, 25 of 25 gates, no cell over its
limit. Latency gates are app-level p50/p95 ratios evaluated by
`PerfFinalGateEvaluator`; the per-statement plan evidence lives beside each run under
`plans/`.

## Provenance

| Field | Value |
| --- | --- |
| Subject-under-test commit | `675dafe1cf0d3840d75ea58ef91c19d2243941a3` (branch `DMS-1392`, HEAD is both runner and subject; `worktreeDirtyPaths` empty in every manifest) |
| Baseline | `../traditional-baseline/` — `postgresql-primary-500k-20260902150522` and `mssql-primary-500k-20260902152100`, captured the same day on the same machine with the frozen DMS-1391 wrapper (`0d3241bf9`) at subject `5656477957eb`, 10 + 60 iterations |
| Runs | `postgresql-final-primary-primary-500k-20260902165913`, `postgresql-final-descriptors-descriptors-25k-20260902165955`, `mssql-final-primary-primary-500k-20260902164047`, `mssql-final-descriptors-descriptors-25k-20260902164246` (captured 2026-09-02 UTC) |
| Fixtures | `primary-500k` (500,000 DS 5.2 students, deterministic sparse `DocumentId`s, four child collections each, as in the baseline README) measured in three phases over one load: pristine (traditional rerun, unfiltered cursor, partitions 1 / 10 / 200), authorized-seeded (one school, one grade-level descriptor, one StudentSchoolAssociation per even-ordinal student, auth edges feeding the generated EducationOrganizationId-to-student view; the principal sees even ordinals only), filtered-overlay (every tenth student's birthDate set to 2010-06-15 at equal byte length; filter cells query that date). `descriptors-25k`: 25,000 descriptors, odd rows in the principal's namespace; cursor cells plus partitions `number=10` only |
| Scenarios | Cursor pages at `pageSize` 25 and 500 from `first` / `middle` / `last` anchors in each phase; partitions `number=` 1 / 10 / 200 (pristine) and 10 (authorized, filtered, descriptors); traditional offsets 0 / page-size / 450,000 rerun byte-identical against the baseline. 10 warmups + 60 measured warm-cache iterations per cell; each manifest's `cellExecutionOrder` records the order |
| Environment | Machine fingerprint `60f293b3b1997ab9` (Windows 11 build 26200, Intel64 Family 6 Model 141, 16 logical cores, 34.07 GB, .NET 10.0.11, Docker Desktop local volumes — not tmpfs); PostgreSQL 16.8 digest `951d0626…`; SQL Server 2025 (RTM-CU7) 17.0.4065.4 digest `86cc6144…` in a container started from that digest on host port 14334; Npgsql 8.0.4 with `npgsql_auto_prepare_min_usages=3`, `npgsql_max_auto_prepare=256` |
| Report inputs | `invoke-final-gate.ps1 -Provider postgresql` with the two baseline directories and the MSSQL run directories passed explicitly, so one report covers both providers. The report's `runDirectory` values are the capture machine's staging paths; the directories retained here are those artifacts |

## How the evidence was produced

The final gate was captured three times on 2026-09-02; the retained runs are the clean
ones.

1. **Attempt 1 (discarded).** Two IDE windows with Roslyn language servers and a second
   agent process started on the host inside the measurement window. PostgreSQL cells showed
   multi-minute slow waves whose driver-observed time tracked total latency, and the
   affected cells differed from the previous attempt's, which is the signature of host
   interference rather than a plan change.
2. **Attempt 2 (MSSQL retained).** Host available memory fell to about 1 GB and the host
   was hard-faulting (3,000 to 9,000 pages/s); the Docker VM itself had 9 GB free, but the
   WSL VM's memory is host-pageable. The PostgreSQL leg, which ran first, again showed
   decaying slow waves in the cells immediately after the bulk load and the auth seed. The
   SQL Server leg ran after memory was freed and is clean: no gate cell has more than two
   samples above 1.5x its median, and every SQL Server gate passes. Its two run directories
   are retained.
3. **Attempt 3 (PostgreSQL retained).** SQL Server container stopped, leaked pool databases
   dropped, browsers closed; preflight showed 10 GB host / 15 GB VM available and no hot
   processes. A host sampler (CPU, available memory, pages/s, top processes every 15 s) ran
   alongside. PostgreSQL cells are flat: authorized `last/500` p50 141.9 ms against `first`
   143.4 ms; partitions 1 / 10 / 200 at 453 / 452 / 455 ms.

## Observations and constraints

- **One disturbed cell pair.** An audio-service burst on the host (audiodg / WavesSvc64,
  6,200 pages/s at 11:58 local) coincided with the PostgreSQL filtered-overlay `first/25`
  cell (21 samples above 1.5x median, p95 520 ms, p50 147.5 ms) and touched `middle/25`
  (4 such samples). The filtered `/25` p95 ratios therefore pass against an inflated
  `first`; the p50 ratios (1.02x middle, 0.82x last) are unaffected and every other cell in
  the run has at most one such sample.
- **Same-machine ratios only.** As the baseline README states, the latency gates are ratios
  on one environment. Absolute numbers here describe a developer laptop running both the
  database container and the test host; they are not capacity figures.
- **PostgreSQL app-level latency is dominated by hydration.** Page selection is a few
  milliseconds; most of each request loads the returned documents and their collections,
  which costs the same at any depth. That is why cursor pages and traditional pages sit
  within a few milliseconds of each other on PostgreSQL while SQL Server shows the deep
  offset (450,000) at roughly five times a deep cursor page. The `plans/` evidence carries
  the page-selection cost directly.
- **Follow-up observation, not a gate input.** In the discarded attempt 2 the PostgreSQL
  unfiltered `last/500` cell alternated fast and slow on consecutive iterations for its final
  twenty samples (about 0.2 s against 1.2 to 1.7 s). That is a connection-level pattern;
  with Npgsql auto-prepare enabled, a per-connection generic-plan switch after a mid-run
  autoanalyze is a plausible mechanism. It did not reproduce in attempt 3 and the fixture
  database is dropped at run end, so it stands as an observation for a follow-up story.
- Host preflight before any capture: host available memory above roughly 8 GB, Docker VM
  `MemAvailable` above 10 GB, no `dmsfp*` pool databases left in the SQL Server container
  (the MSSQL test lease pool does not drop them at process end), one SQL Server container,
  no IDE language servers or browsers running.

## Contents per run directory

`run-manifest.json` (schema `2.0.0`: identity, commits, fixture, phase log, cell execution
order, full environment), `results.json` / `results.csv` (one row per measured cell with
latency and driver-execute samples and database metrics), `fixture-manifest.json`, `plans/`
(full-hydration-batch replay evidence per cell: PostgreSQL one `.explain.json` per cell with
the raw `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` of every batch statement; SQL Server one
`.plans.json` index per cell pointing at the per-statement actual `.sqlplan` files and the
raw `.stats.txt`), and `sql/` (per-cell page-selection text, hydration-batch text, and bound
parameter values). Validated on write and on reload by the harness.

## Regeneration

```powershell
./eng/performance/invoke-final-gate.ps1 -Provider postgresql,mssql `
    -ResultsDirectory <staging> -MssqlContainerName <pinned-CU7-container>
```

Report-only regeneration over retained directories:

```powershell
./eng/performance/invoke-final-gate.ps1 -ReportOnly -ReportDirectory <out> `
    -PostgresqlBaselineDirectory <baseline-pg> -MssqlBaselineDirectory <baseline-mssql> `
    -PostgresqlPrimaryDirectory <pg-primary> -PostgresqlDescriptorsDirectory <pg-descriptors> `
    -MssqlPrimaryDirectory <mssql-primary> -MssqlDescriptorsDirectory <mssql-descriptors>
```

See `src/dms/tests/EdFi.DataManagementService.Performance.Harness/README.md` for
prerequisites, `PERF_*` variables, and the guardrails the capture enforces.
