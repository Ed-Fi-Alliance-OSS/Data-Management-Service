# Traditional Paging Baseline (DMS-1391)

The pre-change traditional `limit`/`offset` baseline the partitioned-cursor-paging epic's
final performance gate (DMS-1392) compares against. These artifacts are frozen evidence: they
are regenerated only by rerunning the capture, never edited.

## Provenance

| Field | Value |
| --- | --- |
| Subject-under-test commit | `5656477957eb2f18e827b7969e5079b424596ae0` — the parent of the DMS-1385 shared page-selection compiler change; the measured DMS behavior predates DMS-1385/DMS-1386 |
| Runner commit | `7cf512d15` on branch `DMS-1391` — the harness sources overlaid onto the subject worktree; the capture wrapper refuses to run from dirty harness/wrapper sources, so this commit is exactly the code that ran. The wrapper also rewrites the connection-string endpoint to the digest-validated container's published port binding before measuring, so each manifest's `connectionStringShape` endpoint is the pinned container's |
| Runs | `postgresql-primary-500k-20260821210127`, `mssql-primary-500k-20260821211341` (captured 2026-08-21 UTC) |
| Fixture | `primary-500k`: 500,000 DS 5.2 students with deterministic sparse `DocumentId`s (gaps ≥ 10% of the id space), each carrying one row in all four child collection tables (identification documents, other names, personal identification documents, visas) and descriptor-backed values from a fixed five-descriptor catalog, loader-verified — see the harness `PerfFixtureDefinition`. The optional person reference stays null by design (a faithful nonzero shape would double `dms.Document` with one Person per student; a shared person would be unfaithful fan-in), so the batch's person-reference-resolution statement is the one intentionally zero-row statement |
| Scenarios | Offsets 0 / page-size / 450,000 at page sizes 25 and 500; 5 warmups + 30 measured warm-cache iterations per cell |
| Environment | Machine fingerprint `92b6869f0fdb8eeb` — a pseudonym of the machine name, meaningful only together with the OS/CPU/core/memory/.NET facts recorded beside it (developer workstation, Windows 11, local docker volumes — not tmpfs); PostgreSQL 16.8 pinned by digest; SQL Server 2025 (RTM-CU7) 17.0.4065.4 pinned by resolved digest; full identity in each `run-manifest.json` |
| Worktree state | Clean at the subject commit apart from the harness overlay directory, as recorded in each manifest's `worktreeDirtyPaths` |

## Comparison constraints for DMS-1392

- The epic's latency gates are **ratios on the same environment**: rerun the final matrix on
  the machine identified by the fingerprint above, with the same pinned images and recorded
  configuration. Results from a different environment make these artifacts provisional
  reference points, not gate baselines.
- **App-level p50/p95 can be diluted by invariant hydration work.** On PostgreSQL
  especially, most of a request's database work is collection hydration that costs the same
  at offset 0 and offset 450,000, so a page-selection improvement can shrink to noise in
  app-level latency ratios. DMS-1391's role is fixed: it captures the representative full
  hydration-batch baseline. DMS-1392 must treat the per-statement evidence — the
  page-selection statement's plan metrics and the per-statement documents under `plans/` —
  as first-class gate inputs wherever app-level p50/p95 is diluted, not as supporting
  detail.
- `results.json` rows carry `pageSelectionSqlSha256`; the traditional page-selection SQL text
  is expected to be byte-identical post-change (the DMS-1385 textual gate).
- The `database` metrics and plan evidence come from replaying the full captured hydration
  batch — the one DbCommand each measured request executed — with the request's bound paging
  parameters, so buffer/read totals include collection hydration, not just page selection.
  The per-statement evidence shows nonzero rows and reads for all four child-collection
  statements and the descriptor-URI-resolution statement on both providers; the
  person-reference-resolution statement is the one intentionally zero-row statement (see
  the fixture row above).
- The replay is an out-of-band one-shot `EXPLAIN` on a dedicated connection, which PostgreSQL
  plans as a custom plan. The measured requests run through the application's pooled Npgsql
  connections with auto-prepare enabled — the effective values are recorded in each
  run-manifest's settings (`npgsql_auto_prepare_min_usages=3`, `npgsql_max_auto_prepare=256`,
  read back through the production data-source code path rather than inferred from the raw
  connection string) — so warm requests may execute server-prepared plans the replay does not
  reproduce. The plan evidence proves plan shape and work volume, not the measured requests'
  exact plan-caching regime.
- SQL Server `db_cpu_ms`/`db_elapsed_ms` are **indicative only**: `STATISTICS TIME` reports
  whole milliseconds per statement, and the batch totals sum that integer rounding across
  every planned statement, so small values carry accumulated quantization error rather than
  precision. `db_logical_reads` and the driver-observed `db_command_p50_ms`/`db_command_p95_ms`
  are the reliable SQL Server comparison quantities.

## Contents per run directory

`run-manifest.json` (identity, commits, environment), `results.json` / `results.csv` (the six
scenario rows), `fixture-manifest.json`, `plans/` (full-hydration-batch replay evidence per
cell: PostgreSQL one `.explain.json` listing every batch statement with its raw
`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` document; SQL Server one `.plans.json` index
pointing at the per-statement actual `.sqlplan` XML files and the raw `.stats.txt`), and
`sql/` (the single page-selection text, the single hydration-batch text, and per-cell bound
parameter values). Artifact schema `1.2.0`, validated on write and on reload by the harness.

## Regeneration

```powershell
./eng/performance/invoke-traditional-baseline.ps1 -Provider postgresql,mssql `
    -ResultsDirectory <staging> -ReuseWorktree
```

See `src/dms/tests/EdFi.DataManagementService.Performance.Harness/README.md` for
prerequisites, environment variables, and the guardrails the capture enforces.
