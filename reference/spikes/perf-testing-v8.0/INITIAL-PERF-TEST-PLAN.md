# Initial DMS PostgreSQL Performance Test Plan

## Purpose

Create the first repeatable performance signal for the Data Management Service using the PostgreSQL relational backend. The plan is intentionally small: establish a reliable load harness, add only the instrumentation needed to explain bottlenecks, and produce actionable follow-up work.

Live monitoring, run control, and data analysis are assumed to be performed by an interactive agent. No dashboards or other UIs are required.

## Scope

In scope:

- DMS application performance against PostgreSQL only.
- Resource API CRUD, query, and Change Query behavior through the public HTTP API.
- DMS app instrumentation for backend read/write phases and SQL command execution.
- PostgreSQL database statistics, wait signals, WAL activity, lock activity, and statement-level timing.
- Load generation with `/home/brad/work/dms-root/Suite-3-Performance-Testing/src/edfi-performance-test`.

Out of scope for this initial plan:

- SQL Server or cross-database comparison.
- UI dashboards, paid APM products, or managed monitoring services.
- Full production sizing guidance.
- DMS `/batch` performance. The performance project has `BATCH_VOLUME`, but the current DMS source does not map a `/batch` endpoint.

## Design Signals To Measure

The backend redesign makes these paths performance-critical:

- Metadata-driven Core pipeline: authentication, schema/fingerprint validation, request validation, resource authorization, document-info extraction.
- Relational writes: target lookup, request-scoped reference resolution through `dms.ReferentialIdentity`, flattening, merge/no-op detection, namespace/relationship authorization, batched DML, trigger work, commit, and committed-response re-read.
- Relational reads: keyset page selection, SQL-layer authorization, hydration batch execution with multiple result sets, descriptor/reference projection, JSON reconstitution, metadata injection, readable-profile projection.
- PostgreSQL-specific pressure: FK cascades, stamping/tracked-change triggers, WAL volume, UUID/index bloat, locks, temp files, query planning, and connection pool behavior.

## Free Tooling

Use only CLI-accessible, freely available tools:

- Load: existing Python/Locust runner in `Suite-3-Performance-Testing/src/edfi-performance-test`; use its CSV output, not the Locust UI.
- DMS logs: existing Serilog console/file output plus added structured timing logs where needed.
- DMS process counters: `dotnet-counters collect` when the process is accessible; otherwise use `docker stats`, `ps`, `top`, `vmstat`, and DMS logs.
- Short diagnostics only when needed: `dotnet-trace`, `dotnet-gcdump`, or `dotnet-dump`.
- PostgreSQL: `pg_stat_statements`, `pg_stat_activity`, `pg_stat_database`, `pg_stat_wal`, `pg_stat_bgwriter`, `pg_stat_user_tables`, `pg_stat_user_indexes`, `pg_locks`, and `EXPLAIN (ANALYZE, BUFFERS)` for selected statements.
- Host/container: `docker logs`, `docker stats --no-stream`, `iostat`/`pidstat` if installed.

## Minimal DMS Instrumentation

Start with coarse, low-overhead instrumentation. Do not time every Core middleware step unless the first runs show unexplained app-side latency.

Add structured timings and `System.Diagnostics.Metrics` counters/histograms behind a simple config flag, for example `PerformanceInstrumentation:Enabled`.

Required measurements:

- Request completion: already logged by `LoggingMiddleware`; preserve method, path, status, duration, and trace id.
- SQL command execution: instrument `PostgresqlRelationalCommandExecutor` and `SessionRelationalCommandExecutor` with command duration, parameter count, dialect, command category, success/failure, and a stable command-text hash. Do not log parameter values.
- Write executor phases in `DefaultRelationalWriteExecutor`: session open, stored authorization, reference resolution, target/current-state resolution, flatten/merge, proposed authorization, persist, committed read, commit/rollback, and final outcome.
- Read path: hydration SQL duration, hydrated document count, table result-set count, descriptor result-set count, materialization/reconstitution duration, page size, `totalCount` requested, and final outcome.
- Reference resolution: lookup count, deduped referential-id count, missing document reference count, descriptor reference failure count.
- PostgreSQL pool signal: connection/data-source creation and active connection symptoms where available through Npgsql counters or logs.

Optional after the first bottleneck is found:

- Core pipeline step timing for only the slowest request class.
- Short `dotnet-trace` captures during a saturated run to inspect CPU allocation or lock contention.

## PostgreSQL Collection

Before each measured run:

- Record git SHA, DMS image/build SHA, .NET version, PostgreSQL version, DMS config, PostgreSQL config, host/container CPU and memory limits.
- Verify DMS is using PostgreSQL: inspect `AppSettings__Datastore=postgresql`.
- Reset the test database or restore the same baseline snapshot.
- Reset PostgreSQL statistics:
  - `SELECT pg_stat_reset();`
  - `SELECT pg_stat_statements_reset();`

During each measured run, the agent should poll every 5-10 seconds:

- Active sessions and waits from `pg_stat_activity`.
- Blocking relationships from `pg_locks`.
- Database throughput and rollback/temp-file counters from `pg_stat_database`.
- WAL bytes/records from `pg_stat_wal`.
- Checkpoint/background writer counters from `pg_stat_bgwriter`.
- Top statements from `pg_stat_statements` by total time, mean time, calls, rows, shared block reads/hits, temp blocks, and WAL bytes if available.
- Container/process CPU, memory, and I/O.

After each run:

- Capture final `pg_stat_statements` top 25 by total time and top 25 by mean time.
- Capture relation sizes and table/index stats for `dms`, `edfi`, `auth`, and `tracked_changes_*`.
- Run `EXPLAIN (ANALYZE, BUFFERS)` only for the few statements the agent identifies as dominant or regressed.

## Load Harness

Use the existing performance project as the only load generator:

```bash
cd /home/brad/work/dms-root/Suite-3-Performance-Testing/src/edfi-performance-test
poetry install
```

Use command-line mode only. Set explicit cleanup and failure settings because the code defaults differ from the README:

- `PERF_DELETE_RESOURCES=true` or pass `-d`.
- `PERF_FAIL_DELIBERATELY=false`.
- Use the default DMS token endpoint `/oauth/token` unless the environment requires an override through `PERF_API_OAUTH_ENDPOINT`.

Example run shape:

```bash
poetry run python -m edfi_performance_test \
  -b "$DMS_BASE_URL" \
  -k "$DMS_KEY" \
  -s "$DMS_SECRET" \
  -t volume \
  -tl StudentVolumeTest SectionVolumeTest StudentSchoolAssociationVolumeTest \
  -c 25 \
  -r 5 \
  -m 15 \
  -o "out/dms-pg-$(date -u +%Y%m%dT%H%M%SZ)" \
  -d \
  -l INFO
```

## Load Generator Interference Check

The performance test harness will often run on the same system as DMS. Every run must therefore verify that the harness itself is not the limiting resource and is not distorting DMS measurements.

When the harness is co-located with DMS:

- Collect CPU, memory, network, and disk I/O samples for the load-generator process/container separately from DMS and PostgreSQL.
- Watch load average, runnable process count, CPU steal/throttling, and network saturation on the host.
- Treat the run as invalid if the load generator is CPU-bound, memory-constrained, swapping, saturating host networking, or causing material CPU contention with DMS/PostgreSQL.
- Prefer isolating the harness when possible: separate host, separate container CPU/memory limits, CPU affinity, or a lower client count/spawn rate.
- Confirm Locust request latency is not dominated by client-side queuing by checking that increasing load-generator capacity changes server-side throughput/latency as expected.

## Initial Workloads

Run these in order. Advance only when the current workload has a low failure rate and the agent can explain the main time consumers.

1. Environment smoke
   - `PIPECLEAN`, one client, selected basic resources: `SchoolPipecleanTest`, `StudentPipecleanTest`, `SectionPipecleanTest`.
   - Purpose: confirm auth, routing, PostgreSQL provisioning, descriptors/reference data, and cleanup.

2. Shallow write baseline
   - `VOLUME` with `SchoolVolumeTest`, then `StudentVolumeTest`.
   - Purpose: isolate root-table writes, `dms.Document`, `dms.ReferentialIdentity`, stamping, and simple GET/PUT/DELETE behavior.

3. Reference-heavy write baseline
   - `VOLUME` with `StudentSchoolAssociationVolumeTest`, `SectionVolumeTest`, `StudentSectionAssociationVolumeTest`.
   - Purpose: exercise reference resolution, authorization joins, child collections, hydration/reconstitution after writes, and trigger work.

4. Read/query baseline
   - Use existing GETs from `PIPECLEAN` first.
   - If that is not enough signal, add a minimal read-only task to the performance project that pages `students`, `sections`, and `studentSchoolAssociations` with `limit=25`, `100`, and `200`, with and without `totalCount=true`.
   - Purpose: measure keyset selection, authorization filtering, hydration result sets, JSON reconstitution, and page-size sensitivity.

5. Change Query baseline
   - `CHANGE_QUERY` with `StudentChangeQueryTest`, `SectionChangeQueryTest`, `StudentSectionAssociationChangeQueryTest`.
   - Add a minimal task for `/deletes` and `/keyChanges` on the same resources if the existing suite does not cover those endpoints.
   - Purpose: measure `ContentVersion` range queries, tracked-change table reads, total-count cost, and authorization on tracked-change endpoints.

6. Mixed representative run
   - A selected `VOLUME` test list containing `StudentVolumeTest`, `StudentSchoolAssociationVolumeTest`, `SectionVolumeTest`, `GradeVolumeTest`, `StaffSectionAssociationVolumeTest`.
   - Purpose: approximate a realistic concurrent mix without trying to cover every Ed-Fi resource.

## Run Matrix

For each workload:

- Warm-up: 3-5 minutes; exclude this interval from analysis.
- Baseline: 10-15 minutes at low concurrency, such as 5-10 users.
- Step load: 10-15 minutes each at 25, 50, 100, and then higher only if the system remains stable.
- Stress-to-knee: double concurrency until one of these occurs:
  - throughput flattens or drops while latency rises,
  - p95 latency increases by more than 50% from the previous stable step,
  - error rate exceeds 1% excluding intentional validation failures,
  - PostgreSQL or DMS CPU, memory, connection, lock, or WAL waits become the clear limiter.
- Short soak: 60 minutes at roughly 70% of the first observed knee for the mixed representative run.

Do not run a broad all-resource suite until the focused workloads are understood. It produces more noise than diagnosis in the first pass.

## Data Products

Each run directory should contain:

- Performance-suite CSV files and console log.
- DMS application logs for the run window.
- DMS metrics/counter output if available.
- PostgreSQL polling output and final snapshots.
- Container/host resource samples.
- Load-generator process/container resource samples, especially when the harness runs on the DMS host.
- Run manifest: workload, client count, spawn rate, duration, DMS commit/image, PostgreSQL version/config, database size before/after, and whether the database was freshly reset.

The agent should summarize:

- Throughput, p50/p95/p99 latency, and failure rate by endpoint/request name.
- First capacity knee and likely limiting resource.
- Top SQL statements by total time and mean time.
- Whether time is mostly in DMS CPU/GC, database CPU/I/O/WAL, locks, connection pool, authorization, hydration/reconstitution, or write trigger/cascade work.
- One short list of next experiments or code changes, ranked by expected impact.

## Stop Conditions

Stop a run early when:

- Failure ratio exceeds 5% for non-deliberate failures.
- PostgreSQL shows sustained blocking or long-running transactions that threaten cleanup.
- DMS memory grows continuously during a steady workload.
- The load generator is saturated, making server-side conclusions invalid.
- A co-located load generator materially contends with DMS or PostgreSQL for CPU, memory, disk, or network.
- The database is no longer in the expected baseline state.

## Initial Success Criteria

This plan is successful when we have:

- A repeatable PostgreSQL-only run setup.
- At least one clean smoke, one focused write baseline, one read/query baseline, and one mixed representative run.
- Correlated load, DMS, PostgreSQL, and host/container artifacts.
- A documented first capacity knee or a clear statement that the tested hardware was not saturated.
- The top 3 bottleneck hypotheses backed by measured evidence, not just request latency.
