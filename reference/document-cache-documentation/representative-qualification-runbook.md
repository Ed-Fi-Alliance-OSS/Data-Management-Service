# DocumentCache Representative Qualification Runbook

This is the release-validation procedure for the DocumentCache representative benchmark.
Use it with the threshold contract in
[performance-qualification.md](performance-qualification.md). This reference defines how
to run and validate the benchmark; it does not assume representative result artifacts
already exist under `reference/document-cache/qualification-results/<run-id>/`.

DocumentCache evidence stops at the projection boundary. Kafka connector, topic, offset,
consumer, and downstream publication qualification belongs to the Kafka/CDC validation
scope.

## Environment Requirements

Run the representative qualification only on dedicated PostgreSQL and SQL Server targets.
The database storage must not be tmpfs, and CPU, IO, WAL or transaction-log, and
maintenance metrics must be capturable for the full run window.

Do not use local Docker, CI, laptop, or shared development measurements as committed
representative evidence unless the environment satisfies the same storage and metric
capture requirements. Placeholder values, synthetic pass/fail rows, and non-representative
local Docker measurements must not be committed as qualification evidence.

Before starting, record and verify:

- Database host identity for each provider.
- Database image tag and digest, or managed-service version.
- Storage note, including why the target is not tmpfs.
- Runner commit, supplied as `PERF_RUNNER_COMMIT`.
- Subject commit from the worktree under test.
- Dirty-path allowlist, supplied as `PERF_ALLOW_DIRTY_PREFIXES` when it differs from the
  harness default.
- Provider connection strings for the PostgreSQL and SQL Server integration harnesses.
- Operator CPU and IO metrics source and export path.

Required evidence environment variables include `PERF_RUNNER_COMMIT`, `PERF_IMAGE_TAG`,
`PERF_IMAGE_DIGEST`, and `PERF_STORAGE_NOTE`. Optional workload overrides include
`PERF_DOCUMENTCACHE_PAGE_SIZE`, `PERF_DOCUMENTCACHE_PROJECTOR_CONCURRENCY`,
`PERF_DOCUMENTCACHE_OUTAGE_WRITES`, and `PERF_DOCUMENTCACHE_SAME_DOCUMENT_CONTENDERS`;
leave them at the release contract defaults unless the evidence notes explain the change.

## Operator Metrics File

Representative mode requires a strict lower-camel JSON CPU/IO metrics file. Pass it with
`-OperatorMetricsFile` or set `PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE`. The run copies it
to `provider-metrics/operator-cpu-io.json`, and database CPU/IO threshold rows must point to
that file.

The file must cover the full benchmark window and include one `providerMetrics` row for
`postgresql` and one for `mssql` when both providers are run.

## Commands

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
  -RunExplicitWriterEvidence \
  -OperatorMetricsFile /path/to/operator-cpu-io.json
```

The representative run uses the primary-500k fixture by default: 500,000 canonical
documents, 50,000 distinct-document outage writes, and 32 same-document enqueue/ack
contenders.

## Required Result Contents

The result directory for a representative run must contain:

```text
qualification-summary.md
threshold-results.json
query-plan-guards/
writer-contention-evidence/
outage-drain-evidence/
provider-metrics/operator-cpu-io.json
provider-metrics/postgresql-wal-vacuum-bloat.md
provider-metrics/mssql-log-ghost-index.md
command-transcripts/
phase-metrics/
```

`threshold-results.json` must contain measured PostgreSQL and SQL Server rows for every
threshold in `DocumentCacheQualification.Thresholds`. Every `evidencePath` must resolve
inside the result directory.

## Validation

Validate the produced artifact directory before using it as evidence:

```bash
pwsh ./eng/performance/invoke-documentcache-qualification.ps1 \
  -ValidateResults /path/to/document-cache-qualification/<run-id>
```

Also search the repository for the required evidence terms before committing:

```bash
rg -n "threshold-results|qualification-summary|outage-drain|provider-metrics|durable-baseline-cursor" \
  reference/document-cache eng/performance tasks.json
```

After validation passes and every threshold result is acceptable, copy or move the
validated directory under:

```text
reference/document-cache/qualification-results/<run-id>/
```

Validate the committed path before opening the review:

```bash
pwsh ./eng/performance/invoke-documentcache-qualification.ps1 \
  -ValidateResults ./reference/document-cache/qualification-results/<run-id>
```

Commit the complete result directory only after validation succeeds from the committed
location.

## Failure Handling

If interrupted restart-from-beginning, database-load, log-pressure, or queue-DML thresholds
fail, record a durable-baseline-cursor remediation as a production prerequisite before
claiming production qualification. Capture the remediation reference or decision record in
`threshold-results.json`, and update `qualification-summary.md` to state that production
qualification is not complete.

Do not declare representative evidence complete until the failed threshold is resolved
or the release scope is explicitly changed. Bounded query-plan guards and explicit writer
evidence are useful diagnostics, but they are not representative-scale qualification.

After real artifacts exist, update the evidence claims in
[performance-qualification.md](performance-qualification.md),
[cdc-inv-evidence.md](cdc-inv-evidence.md), [README.md](README.md), and `tasks.json` so
they describe exactly what evidence was committed.
