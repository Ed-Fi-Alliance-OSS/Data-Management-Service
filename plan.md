# Plan: DocumentCache Representative Performance Qualification

## Goal

Implement a long-running DocumentCache representative benchmark harness that can produce and
validate the measured evidence needed by a follow-up performance ticket.

DMS-1317 must not claim representative-scale qualification from bounded CI guards alone.
It is complete when the harness, threshold catalog, validator, and operator runbook are in
place. Real measured PostgreSQL and SQL Server qualification artifacts are out of scope for
this branch and belong to the follow-up performance ticket.

## Required Outcome

Create a benchmark and validation workflow that can prove, with durable artifacts in the
follow-up performance ticket:

- PostgreSQL and SQL Server representative runs were executed against 500,000 canonical
  documents.
- Projector outage behavior was exercised with 50,000 distinct-document writes.
- Same-document enqueue/ack contention used 32 contenders.
- The lifecycle and administrative paths covered `Disabled`, `Tracking`, `Resetting`,
  `Rebuilding`, activation, online rebuild, interrupted rebuild restart, scrub, and status.
- Every provider threshold in
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness/Configuration/DocumentCacheQualification.cs`
  has one measured `threshold-results.json` row.
- Every row has a measured value, maximum, unit, pass/fail result, evidence path, and reviewer
  note.
- Every referenced evidence path exists.
- If any interrupted restart-from-beginning threshold fails, the result records the
  durable-baseline-cursor Jira ticket and the branch does not claim production qualification
  without that prerequisite.

## Artifact Contract

The follow-up performance ticket uses a committed release-validation result directory under:

```text
reference/document-cache/qualification-results/<run-id>/
```

Each completed run directory must contain:

```text
qualification-summary.md
threshold-results.json
query-plan-guards/
writer-contention-evidence/
outage-drain-evidence/
provider-metrics/postgresql-wal-vacuum-bloat.md
provider-metrics/mssql-log-ghost-index.md
command-transcripts/
phase-metrics/
```

`threshold-results.json` must be lower-camel JSON and must include one row per provider
threshold. Use `System.Text.Json` for the schema, serialization, and validation. Do not add
Newtonsoft.Json.

Suggested row shape:

```json
{
  "provider": "postgresql",
  "thresholdId": "postgresql-baseline-completion-minutes",
  "area": "baselineCompletion",
  "measurement": "Offline activation or first baseline completes for 500,000 documents.",
  "measuredValue": 18.4,
  "maximum": 30,
  "unit": "minutes",
  "passed": true,
  "evidencePath": "command-transcripts/postgresql-offline-activation.log",
  "reviewerNote": "Measured against 500000 canonical documents.",
  "durableBaselineCursorTicket": null
}
```

## Implementation Steps

### 1. Add Result Model And Validator

Add DocumentCache-specific result types to the performance harness:

- `DocumentCacheQualificationResult`
- `DocumentCacheQualificationRunManifest`
- `DocumentCacheQualificationArtifact`
- `DocumentCacheQualificationArtifactValidator`
- `DocumentCacheQualificationArtifactWriter`

The validator must:

- Load the threshold catalog from `DocumentCacheQualification.Thresholds`.
- Require one and only one row for each threshold id.
- Require `provider`, `thresholdId`, `area`, `measurement`, `measuredValue`, `maximum`,
  `unit`, `passed`, `evidencePath`, and `reviewerNote`.
- Verify `maximum`, `unit`, and `area` match the catalog.
- Verify every `evidencePath` resolves inside the run directory.
- Verify required top-level artifacts exist.
- Verify provider-specific evidence exists for both PostgreSQL and SQL Server.
- Require `durableBaselineCursorTicket` when a failed threshold has area
  `restartFromBeginning`, `databaseCpu`, `databaseLog`, or `queueDmlAmplification` and is
  tied to interrupted baseline/rebuild restart behavior.
- Reject path traversal and absolute evidence paths.

### 2. Extend The PowerShell Entrypoint

Update `eng/performance/invoke-documentcache-qualification.ps1` with explicit modes:

- Default: run existing bounded guards and write the scaffold summary.
- `-RunRepresentative`: run the long-running representative benchmark.
- `-ValidateResults <path>`: validate an existing committed result directory without running
  the benchmark.
- `-RunExplicitWriterEvidence`: keep current behavior and attach the existing explicit writer
  evidence tests.

The script should copy or route bounded guard TRX/output into `query-plan-guards/`, run the
representative harness when requested, and always fail if validation fails.

### 3. Add Explicit Harness Entry Points

Add explicit NUnit entry points under
`src/dms/tests/EdFi.DataManagementService.Performance.Harness/Runs/`:

- `Given_Postgresql_DocumentCacheRepresentativeRun`
- `Given_Mssql_DocumentCacheRepresentativeRun`

These tests must be `[Explicit]`, `[NonParallelizable]`, and categorized as `Performance` and
`DocumentCacheRepresentativeQualification`. They must not run in ordinary CI.

Configuration should be environment-driven, following the existing `PERF_*` pattern:

- results directory
- provider
- fixture kind, defaulting to the 500k fixture
- page size / high-water mark / concurrency
- warmup and measured status samples
- storage note
- image tag and digest
- dirty-path allowlist
- optional operator note

### 4. Reuse The 500k Fixture Loader

Reuse `PerfFixtureLoader` with `PerfFixtureKind.Primary500k`.

The loader already creates the canonical `dms.Document`, `edfi.Student`, child collection,
descriptor, referential identity, and statistics state needed for a representative source
cardinality without sending 500,000 HTTP POSTs.

Before measurement, record:

- fixture manifest
- row counts for `dms.Document`, `dms.DocumentCache`, and `dms.DocumentProjectionWork`
- database/server identity
- storage note
- git commit and dirty-path state

### 5. Build The Benchmark Pipeline

Create `DocumentCacheQualificationRunPipeline` in the harness. It should orchestrate these
phases for each provider:

1. Guard CI, tmpfs, dirty worktree, fixture size, provider prerequisites, and connection
   identity.
2. Load and verify the 500k fixture.
3. Measure disabled canonical write samples.
4. Run offline activation / first baseline and measure time to `Tracking`.
5. Measure caught-up tracking canonical write samples and calculate write-overhead ratio.
6. Sample status and oldest-work latency while work is empty.
7. Run online rebuild and measure clear, reseed, drain, and return to `Tracking`.
8. Start rebuild, interrupt it after `Rebuilding` and partial progress are observed, then run
   the replacement command and measure restart-from-beginning completion.
9. Create 50,000 distinct-document outage writes while the projector target is not draining.
10. Measure work-row growth versus distinct touched documents.
11. Drain the outage backlog and measure completion time.
12. Sample status and oldest-work latency with small and large work inventories.
13. Run same-document enqueue/ack contention with 32 contenders and record p95 lock wait,
    retry, and deadlock counts.
14. Run explicit scrub and record admission, duration, and final state.
15. Collect post-run maintenance metrics and final counts.

Each phase must write a command transcript and a structured phase-metrics JSON file.

### 6. Implement Provider Metric Capture

PostgreSQL capture:

- WAL LSN before and after baseline, rebuild, interrupted restart, and outage drain.
- WAL bytes per projected document through `pg_wal_lsn_diff`.
- `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` samples for projection, status, and oldest-work
  queries.
- `pg_stat_user_tables` before run, after run, and after `VACUUM` for `DocumentCache` and
  `DocumentProjectionWork`.
- Bloat extension output when available; otherwise record why no bloat estimator was allowed.

SQL Server capture:

- `sys.dm_db_log_stats(DB_ID())` before and after baseline, rebuild, interrupted restart, and
  outage drain.
- Transaction log bytes per projected document.
- `SET STATISTICS IO` and actual plan XML samples for projection, status, and oldest-work
  queries.
- `sys.dm_db_index_physical_stats` after run and after cleanup/index maintenance for
  `DocumentCache` and `DocumentProjectionWork`.
- Ghost row ratio and fragmentation observations.

Host/database CPU and IO capture may require an operator-supplied metrics file when the local
database does not expose reliable per-database CPU. The validator should require the file and
the threshold row evidence path, not fabricate the value.

### 7. Generate Threshold Rows

Map phase outputs into `threshold-results.json` rows for all threshold ids:

- baseline completion
- online rebuild completion
- interrupted restart-from-beginning completion
- average database CPU
- read blocks or logical reads per projected document
- WAL/log growth per projected document
- queue DML amplification
- p95 status/oldest-work latency
- p95 canonical write overhead ratio
- outage backlog rows versus distinct touched documents
- outage drain completion
- p95 same-document enqueue/ack lock wait
- dead tuple or ghost row ratio after maintenance

The generated JSON should be deterministic: stable ordering by provider and threshold id,
lower-camel names, invariant-culture numeric formatting, and no machine-specific secrets.

### 8. Performance Engineer Run Instructions

Use this section as the release-validation runbook after the representative benchmark and
validation mode have been implemented.

Run the benchmark only on dedicated PostgreSQL and SQL Server targets whose storage is not
tmpfs and whose provider metrics can be captured for the whole run window. Before starting,
record the database host identity, image tag/digest or managed-service version, storage note,
runner commit, subject commit, and any dirty-path allowlist used for the evidence run.

First run the bounded guard path and explicit writer evidence:

```bash
pwsh ./eng/performance/invoke-documentcache-qualification.ps1 \
  -Provider postgresql,mssql \
  -ResultsDirectory /tmp/document-cache-qualification \
  -RunExplicitWriterEvidence
```

Then run the representative path on the qualified performance targets:

```bash
pwsh ./eng/performance/invoke-documentcache-qualification.ps1 \
  -Provider postgresql,mssql \
  -ResultsDirectory /path/to/document-cache-qualification \
  -RunRepresentative \
  -RunExplicitWriterEvidence
```

The representative run must produce a result directory containing:

```text
qualification-summary.md
threshold-results.json
query-plan-guards/
writer-contention-evidence/
outage-drain-evidence/
provider-metrics/postgresql-wal-vacuum-bloat.md
provider-metrics/mssql-log-ghost-index.md
command-transcripts/
phase-metrics/
```

Validate the produced artifact directory before using it as evidence:

```bash
pwsh ./eng/performance/invoke-documentcache-qualification.ps1 \
  -ValidateResults /path/to/document-cache-qualification/<run-id>
```

Also search the repository for the required evidence terms:

```bash
rg -n "threshold-results|qualification-summary|outage-drain|provider-metrics|durable-baseline-cursor" \
  reference/document-cache eng/performance tasks.json
```

If every threshold passes, copy or move the validated result directory under:

```text
reference/document-cache/qualification-results/<run-id>/
```

Commit the complete result directory only after validation succeeds.

If any required threshold fails:

- create the durable-baseline-cursor Jira ticket when required by the failure rule.
- record that ticket in `threshold-results.json`.
- update `qualification-summary.md` to state that production qualification is not complete.
- keep the task incomplete until the prerequisite is resolved or scope is explicitly changed.

Do not commit placeholder values, synthetic pass/fail rows, or local Docker measurements that
do not satisfy the representative-run environment requirements.

After real artifacts exist, update the evidence claims in:

- `reference/document-cache/performance-qualification.md`
- `reference/document-cache/cdc-inv-evidence.md`
- `reference/document-cache/README.md`
- `tasks.json`

The documentation must say exactly what evidence exists. It must not imply that bounded
query-plan guards or the small explicit writer evidence test are representative-scale
qualification.

After implementation or documentation edits, format touched C# areas:

```bash
dotnet csharpier format src/dms/tests/EdFi.DataManagementService.Performance.Harness
```

Finish by rerunning validation against the committed path:

```bash
pwsh ./eng/performance/invoke-documentcache-qualification.ps1 \
  -ValidateResults ./reference/document-cache/qualification-results/<run-id>
```

## Risks And Decisions To Make

- Decide whether the representative benchmark should call the public `dms-document-cache`
  process or invoke shared administrative primitives in-process. Process invocation gives
  stronger operator realism; in-process execution gives better deterministic interruption and
  telemetry capture. Prefer in-process orchestration for measurement plus one transcripted CLI
  smoke unless reviewers require process-level evidence for every phase.
- Decide where host CPU/IO metrics come from in local Docker, managed database, and release
  validation environments. The validator should support operator-supplied metrics files with a
  strict schema.
- Decide whether PostgreSQL bloat estimation requires a specific extension. If not available,
  the evidence file must state the approved substitute and still include WAL/dead-tuple data.
- Decide how to interrupt rebuild deterministically. The benchmark should observe `Rebuilding`
  plus partial work progress, cancel or terminate the owning command, then run the replacement
  command and measure from the beginning.
- Expect long runtime. This benchmark is release-validation evidence, not a pull-request CI
  gate.
