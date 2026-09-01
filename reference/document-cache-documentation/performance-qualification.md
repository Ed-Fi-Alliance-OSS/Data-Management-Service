# DocumentCache Performance Qualification

This is the DMS-1317 harness and runbook contract for E18 DocumentCache scale behavior. It
turns the design-level
[Projection Performance Qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
requirements into executable guard commands, representative-run thresholds, and result
artifact expectations.

DMS-1317 qualification stops at the DocumentCache projection boundary. Kafka connector,
topic, offset, ACL, consumer, and downstream publication qualification belongs to E19.
DMS-1317 delivers the harness, threshold catalog, validator, and operator procedure; the
actual PostgreSQL and SQL Server representative performance runs and committed
`reference/document-cache/qualification-results/<run-id>/` artifacts belong to a follow-up
performance ticket.

## Entry Point

Use the
[representative qualification runbook](representative-qualification-runbook.md)
for the release-validation operator procedure. This page defines the thresholds,
artifacts, and provider observations that the runbook executes.

Run the bounded guards and create the qualification summary folder:

```powershell
./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql,mssql -ResultsDirectory C:\perf\document-cache
```

For release-validation runs that should attach component writer timing evidence:

```powershell
./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql,mssql -ResultsDirectory C:\perf\document-cache -RunExplicitWriterEvidence
```

The script always runs the CI-appropriate guards first:

- `dotnet test src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/EdFi.DataManagementService.Performance.Harness.Tests.Unit.csproj --filter FullyQualifiedName~DocumentCacheQualification`
- `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj --filter FullyQualifiedName~DocumentCacheQueryPlan`
- `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj --filter FullyQualifiedName~DocumentCacheQueryPlan`

The ordinary guards use bounded query-plan and statistics assertions, including the
160-document, 80-work-row `DocumentCacheQueryPlan` fixtures. They must not load
representative production volume in CI.

## Representative Run

Run release qualification on dedicated PostgreSQL and SQL Server targets whose storage is
not tmpfs and whose CPU, IO, WAL/log, and maintenance metrics can be captured for the run
window. Follow the step-by-step
[representative qualification runbook](representative-qualification-runbook.md)
when producing committed evidence in the follow-up performance ticket.

Before the representative run, prepare a strict operator metrics JSON file and pass it
through `-OperatorMetricsFile` or `PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE`. The harness
copies the validated file to `provider-metrics/operator-cpu-io.json`. CPU threshold rows
must reference that file rather than synthetic provider DMV values. Operator IO utilization
is required contextual evidence for the run window; database IO threshold rows use provider
read-cost evidence: PostgreSQL shared read blocks and SQL Server logical reads per projected
document.

Minimum representative workload:

| Workload item | Value |
| --- | ---: |
| Canonical documents | 500,000 |
| Distinct-document outage writes | 50,000 |
| Same-document enqueue/ack contenders | 32 |
| Lifecycle paths | `Disabled`, `Tracking`, `Resetting`, `Rebuilding` |
| Administrative paths | activation, online rebuild, interrupted rebuild restart, scrub, status |

The run must capture:

- baseline/rebuild completion time and restart-from-beginning completion time.
- database CPU, IO, and log pressure during baseline, rebuild, and outage drain.
- repeated queue DML or write amplification against `dms.DocumentProjectionWork`.
- status and oldest-work latency while the source cardinality is large and while work is
  empty, small, and large.
- canonical write overhead by comparing lifecycle `Disabled` and caught-up `Tracking`.
- queue growth and drain after a projector outage.
- same-document enqueue/ack lock waits and deadlock or retry counts.
- PostgreSQL WAL, vacuum, and bloat observations for `dms.DocumentCache` and
  `dms.DocumentProjectionWork`.
- SQL Server transaction-log growth, ghost rows, and index fragmentation observations for
  `dms.DocumentCache` and `dms.DocumentProjectionWork`.

## Thresholds

These pass/fail thresholds are also recorded in
`src/dms/tests/EdFi.DataManagementService.Performance.Harness/Configuration/DocumentCacheQualification.cs`.
Measured-result placeholders must appear after this table in a result artifact, never
before the thresholds.

| Provider | Measurement | Maximum | Evidence |
| --- | --- | ---: | --- |
| PostgreSQL | Offline activation or first baseline for 500,000 documents | 30 minutes | command log and final caught-up status |
| PostgreSQL | Online rebuild clear, reseed, drain, return to `Tracking` | 40 minutes | `rebuild-online` log and final status JSON |
| PostgreSQL | Interrupted `Rebuilding` restart from beginning | 40 minutes | interrupted rebuild transcript and final status JSON |
| PostgreSQL | Average database CPU during baseline/rebuild | 70 percent | host or managed database metric |
| PostgreSQL | Shared read blocks per projected document | 16 blocks/document | `EXPLAIN (ANALYZE, BUFFERS)` samples |
| PostgreSQL | WAL growth per projected document | 32768 bytes/document | `pg_wal_lsn_diff` before/after |
| PostgreSQL | Queue DML amplification | 1.25 ratio | queue DML counters and row-count deltas |
| PostgreSQL | p95 status/oldest-work latency | 250 ms | repeated `dms-document-cache status` timings |
| PostgreSQL | p95 canonical write overhead in `Tracking` vs `Disabled` | 1.20 ratio | matched API write samples |
| PostgreSQL | Outage backlog rows vs distinct touched documents | 1.05 ratio | replay count and work-row count |
| PostgreSQL | Drain 50,000 distinct-document outage backlog | 15 minutes | projector drain transcript |
| PostgreSQL | p95 same-document enqueue/ack lock wait | 500 ms | writer telemetry histogram |
| PostgreSQL | Dead tuple ratio after maintenance | 1 percent | `pg_stat_user_tables` before/after and after `VACUUM` |
| SQL Server | Offline activation or first baseline for 500,000 documents | 45 minutes | command log and final caught-up status |
| SQL Server | Online rebuild clear, reseed, drain, return to `Tracking` | 60 minutes | `rebuild-online` log and final status JSON |
| SQL Server | Interrupted `Rebuilding` restart from beginning | 60 minutes | interrupted rebuild transcript and final status JSON |
| SQL Server | Average database CPU during baseline/rebuild | 70 percent | host or managed database metric |
| SQL Server | Logical reads per projected document | 24 pages/document | `SET STATISTICS IO` samples |
| SQL Server | Transaction log growth per projected document | 49152 bytes/document | `sys.dm_db_log_stats` before/after |
| SQL Server | Queue DML amplification | 1.25 ratio | queue DML counters and row-count deltas |
| SQL Server | p95 status/oldest-work latency | 250 ms | repeated `dms-document-cache status` timings |
| SQL Server | p95 canonical write overhead in `Tracking` vs `Disabled` | 1.20 ratio | matched API write samples |
| SQL Server | Outage backlog rows vs distinct touched documents | 1.05 ratio | replay count and work-row count |
| SQL Server | Drain 50,000 distinct-document outage backlog | 20 minutes | projector drain transcript |
| SQL Server | p95 same-document enqueue/ack lock wait | 750 ms | writer telemetry histogram |
| SQL Server | Ghost row ratio after cleanup | 1 percent | `sys.dm_db_index_physical_stats` and cleanup observation |

If interrupted baseline or rebuild restart-from-beginning exceeds the provider completion
time, database-load, log-pressure, or repeated queue-DML thresholds above, create a
durable-baseline-cursor Jira ticket and make it a production prerequisite. Do not qualify
v1 production use by accepting repeated full restart cost without that ticket.

## Result Artifacts

Each follow-up release-validation run directory must contain:

| Artifact | Contents |
| --- | --- |
| `qualification-summary.md` | run ID, provider list, configuration, command transcript location, operator notes |
| `threshold-results.json` | one provider/threshold row per table entry with measured value, pass/fail, evidence path, and reviewer note |
| `query-plan-guards/` | test result files and plans from the bounded `DocumentCacheQueryPlan` guards |
| `writer-contention-evidence/` | explicit writer performance evidence attachments when `-RunExplicitWriterEvidence` is used |
| `outage-drain-evidence/` | outage write replay counts, queue growth, drain transcript, and final status JSON |
| `provider-metrics/operator-cpu-io.json` | strict operator-supplied CPU and IO metrics for the full benchmark window |
| `provider-metrics/postgresql-wal-vacuum-bloat.md` | PostgreSQL WAL, vacuum, bloat, dead tuple, and relevant index observations |
| `provider-metrics/mssql-log-ghost-index.md` | SQL Server log, ghost row, fragmentation, and relevant index observations |

`threshold-results.json` must use lower-camel property names and include these required
properties:

```json
{
  "provider": "postgresql",
  "thresholdId": "postgresql-status-oldest-work-p95-ms",
  "area": "statusOldestWorkLatency",
  "measurement": "p95 status/oldest-work observation latency at representative cardinality.",
  "measuredValue": 42.5,
  "maximum": 250,
  "unit": "milliseconds",
  "passed": true,
  "evidencePath": "query-plan-guards/postgresql-status-latency.md",
  "reviewerNote": "Measured with 500000 source documents and 50000 work rows."
}
```

## PostgreSQL Observations

Capture WAL position before and after each baseline/rebuild/outage-drain phase:

```sql
SELECT pg_current_wal_lsn();
```

Use `pg_wal_lsn_diff` to calculate bytes per projected document. Capture table maintenance
state before the run, after the run, and after the intended maintenance action:

```sql
SELECT relname, n_live_tup, n_dead_tup, vacuum_count, autovacuum_count
FROM pg_stat_user_tables
WHERE schemaname = 'dms'
  AND relname IN ('DocumentCache', 'DocumentProjectionWork');
```

For bloat and index review, attach the DBA-approved extension or managed-service equivalent
available in the target environment. If no bloat estimator is permitted, state that in
`provider-metrics/postgresql-wal-vacuum-bloat.md` and preserve the WAL and dead-tuple
evidence.

## SQL Server Observations

Capture log growth before and after each baseline/rebuild/outage-drain phase:

```sql
SELECT database_id, total_log_size_mb, active_log_size_mb, log_since_last_log_backup_mb
FROM sys.dm_db_log_stats(DB_ID());
```

Capture ghost and index state for the DocumentCache tables after the run and after the
intended cleanup or index maintenance window:

```sql
SELECT object_name(object_id) AS tableName, index_id, avg_fragmentation_in_percent, ghost_record_count
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'SAMPLED')
WHERE object_name(object_id) IN ('DocumentCache', 'DocumentProjectionWork');
```

Attach `SET STATISTICS IO` or actual-plan evidence for sampled projection, status, and
oldest-work statements so logical reads per projected document can be reviewed.

## Operator CPU/IO Metrics File

The operator metrics file must use lower-camel JSON with this strict shape:

```json
{
  "schemaVersion": "1.4.0",
  "capturedAtUtc": "2026-09-01T00:00:00Z",
  "runWindowStartedAtUtc": "2026-09-01T00:00:00Z",
  "runWindowEndedAtUtc": "2026-09-01T00:10:00Z",
  "source": "managed database metrics export",
  "providerMetrics": [
    {
      "provider": "postgresql",
      "sampleCount": 120,
      "averageDatabaseCpuPercent": 42.5,
      "averageDatabaseIoUtilizationPercent": 37.25,
      "reviewerNote": "CPU and IO averaged across the full representative run window."
    }
  ]
}
```

Include one `providerMetrics` row for each provider in the representative run.
