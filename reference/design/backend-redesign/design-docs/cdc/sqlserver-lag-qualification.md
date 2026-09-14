# SQL Server lag telemetry: qualification evidence and resolved requirement gap

Date: 2026-09-08
Story: `reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md` (DMS-1323)
Task: T12 in `tasks.json`; remains `completed: false`.

## Resolution: current lag required, historical statistics optional

On 2026-09-08 the user explicitly approved simplifying the requirements to the existing
SQL Server connector's capabilities; a Debezium fix/backport is not an option. Both
providers now require current source lag only. Minimum, maximum, average, P50, P95, and
P99 are optional diagnostics, validated when exported, and absent when unsupported.
Core percentile fields retain explicit null values. Current-lag thresholds, freshness,
worker/task identity, connector isolation, heartbeat/committed-offset progress, and the
independent provider source-position barrier remain required. No replacement statistics
are calculated from scrapes.

The original blocker below is historical evidence, not an active stop condition. This
record was moved from root `PROBLEMS.md` so the implementation loop can resume.
T12 remains incomplete until all remaining qualification, deployment wiring, and
publication work is finished; the preserved candidate is not a qualified delivery image.

## Verification after the scope revision

- Core CDC unit tests: **338 passed, 0 failed, 0 skipped**, including 30 new cases
  covering explicit null percentiles for both providers, within-threshold/exceeded states,
  missing or invalid required lag values, and independent lag/barrier checks for readiness
  and initial admission.
- Real-provider telemetry tests against the exact preserved candidate digest below:
  **PostgreSQL passed; SQL Server passed; 0 skipped**, 2 tests total, with
  `CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true`. Each run includes heartbeat/committed-offset
  progress, current lag and worker metrics, task stop/resume, and worker restart. Exported
  optional statistics are checked when present; their absence is accepted.
- CSharpier check passed for all four changed/new C# files in this scope; `git diff --check`
  and `tasks.json` parsing passed. Temporary provider/broker/worker resources were cleaned
  up; the existing dms-local containers remained running.
- Current logs: `/tmp/dms-1323-current-lag-core.log` and
  `/tmp/dms-1323-current-lag-telemetry.log`. TRX reports:
  `/tmp/dms-1323-current-lag-core/core-cdc.trx` and
  `/tmp/dms-1323-current-lag-telemetry/telemetry.trx`.

This clears the statistics requirement blocker. Connector-isolation qualification,
worker inspection/startup/Compose wiring, CI publication, and the T15 telemetry adapter
remain unfinished. T12 and T15 stay `completed: false`; no image is published or newly
pinned as qualified by this revision. T15 must discard unusable optional statistics
before the Core handoff while retaining all required current-lag and identity checks.

## Original blocking requirement and scope

Before the scope revision, T12 required a standard Prometheus JMX Exporter agent and real PostgreSQL/SQL Server qualification of current source lag **and minimum, maximum, average, P50, P95, P99**, before publishing and consuming a qualified image. The original design required those statistics from Debezium 3.6 with `statistics.metrics.enabled=true`.

The pinned Debezium 3.6.0.Final SQL Server connector does not expose the six statistical attributes on its streaming JMX bean. A standard exporter's mapping cannot export absent JMX attributes. Satisfying that original requirement would need a Debezium connector fix/backport or a compatible upstream runtime containing that fix, beyond this story's exporter packaging/mapping and existing-plugin reuse scope. The approved scope revision above resolves the requirement gap; fabricating statistics remains prohibited.

Stopped according to `.orc/loop/prompts/implementation-loop-prompt.md`: an integration failure determined to be outside this story must be recorded here, then work stops.

## Observed evidence

- The pre-existing PostgreSQL pinned-image heartbeat/offset/restart smoke test passed: 1/1, no skips (`/tmp/dms-1323-baseline.log`).
- Built a local candidate from the companion repository with checksum-pinned JMX Exporter 1.5.0, fixed mappings and Java-agent activation on port 9404.
- Final real-provider telemetry run: **PostgreSQL passed; SQL Server failed; 0 skipped**, 2 tests total, with `CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true`.
- PostgreSQL exported seven finite, nonnegative millisecond gauges, worker JVM start time and heap. Stopping the task removed its metric set; resuming restored it. Worker restart supplied a new JVM start time and fresh streaming metrics.
- SQL Server reached streaming and committed heartbeat offsets. Its `/metrics` response contained `edfi_cdc_source_lag_current_milliseconds{connector="dms-binding-g7",provider="sql_server"}`, worker start time/heap and `jmx_scrape_error 0.0`, but no statistical lag gauges. The test fails on missing `edfi_cdc_source_lag_min_milliseconds`.
- The existing renderer sets `statistics.metrics.enabled=true` in `CdcConnectorTemplateRenderer.cs`; this is not an omitted configuration switch.
- Inspected the actual image's `debezium-connector-sqlserver-3.6.0.Final.jar` and `debezium-connector-common-3.6.0.Final.jar` with `javap`. `SqlServerStreamingPartitionMetricsMXBean` extends `StreamingMetricsMXBean` and `SqlServerPartitionMetricsMXBean`, **not** `StreamingStatisticsMXBean`. Its implementation exposes `getMilliSecondsBehindSource()` but none of the six statistical getters. Task/parent beans do not supply them either.
- PostgreSQL's shared `StreamingChangeEventSourceMetricsMXBean` does extend `StreamingStatisticsMXBean`, which declares all six missing getters.

Primary source confirmation:

- [Pinned SQL Server JMX interface](https://github.com/debezium/debezium/blob/v3.6.0.Final/debezium-connector-sqlserver/src/main/java/io/debezium/connector/sqlserver/metrics/SqlServerStreamingPartitionMetricsMXBean.java)
- [Pinned SQL Server metric implementation](https://github.com/debezium/debezium/blob/v3.6.0.Final/debezium-connector-sqlserver/src/main/java/io/debezium/connector/sqlserver/metrics/SqlServerStreamingPartitionMetrics.java)
- [Shared streaming JMX interface](https://github.com/debezium/debezium/blob/v3.6.0.Final/debezium-connector-common/src/main/java/io/debezium/pipeline/metrics/StreamingChangeEventSourceMetricsMXBean.java)

The SQL Server interface on upstream `main` was also inspected on 2026-09-08 and still lacked the statistics interface. Published documentation lists the statistics, but that does not match the pinned executable/source evidence. No compatible fixed runtime was established.

## Exact tested artifacts

- Companion checkout: `/home/brad/work/dms-root/Ed-Fi-Kafka-Connect`, new local branch `DMS-1323`, based on `8a25ba3ddef16ee2b49eb2d04dcc3ba34deff12d` plus the uncommitted draft packaging.
- Base: `quay.io/debezium/connect:3.6.0.Final@sha256:698f0559e667a242f962221079e75917b2b7a3ad4de62661e977628da0e33b45`.
- Exporter: `jmx_prometheus_javaagent-1.5.0.jar`, SHA-256 `0315f3f657876302c6205a98d4036ec775dca529c5d0419ca60ee669c688239f`, downloaded from the official GitHub release.
- Local candidate: `ed-fi-kafka-connect@sha256:1d5ef40b125ef7be52d5cddbb008ee5fba83ddf24911feedf6fcb7a8368f69b4`. This is local image evidence, **not a published or qualified delivery digest**.
- PostgreSQL: `postgres@sha256:9a8afca54e7861fd90fab5fdf4c42477a6b1cb7d293595148e674e0a3181de15` (18.4 Alpine).
- SQL Server: `mcr.microsoft.com/mssql/server@sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a` (2025).
- Broker: `docker.redpanda.com/redpandadata/redpanda@sha256:9a47c1f8d6736f98fa2616f6f0b715c051cb0bdac1a1176e38321bf45a5b572d`.

## Reproduction

Build the preserved candidate from the companion repository:

```bash
docker build -t ed-fi-kafka-connect:dms-1323-candidate \
  --build-context parentdir=. -f kafka/Dockerfile kafka
```

From this DMS checkout, set `CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true`, `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` to the candidate's actual immutable local RepoDigest, and `CDC_CONNECTOR_TEMPLATE_REDPANDA_IMAGE`, `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE`, `CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE` to the provider/broker references above. Then run:

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj \
  --filter 'Category=CdcConnectorTelemetryQualification' \
  --logger 'trx;LogFileName=telemetry.trx' \
  --results-directory /tmp/dms-1323-telemetry-evidence
```

Final evidence: `/tmp/dms-1323-telemetry-evidence.log` and `/tmp/dms-1323-telemetry-evidence/telemetry.trx`. Build log: `/tmp/dms-1323-image-build.log`.

## Preserved work and next iteration

Uncommitted DMS changes:

- `CdcConnectorTemplatePinnedImageFixture.cs`: partial fixture, metrics port, HTTP-client replacement for Docker's changed ephemeral ports after restart.
- `CdcConnectorTelemetryQualificationTests.cs`: real-provider reproducer and reusable metric/type/unit assertions; task stop/resume and worker restart checks. Connector-isolation qualification is still outstanding.

Uncommitted companion changes:

- `kafka/Dockerfile`: checksum-pinned standard agent, fixed external-to-config-volume mapping path, activation and management port.
- `kafka/metrics/cdc.yaml`: fixed provider and JVM metric mappings, with exact attribute boundaries to keep statistical values separate from current lag.

Both modified DMS files pass CSharpier; `git diff --check` passes in both repositories. Final integration compilation passed. Temporary provider/broker/worker resources were cleaned up; existing dms-local containers were not changed. Authoritative input fixtures were not modified.

No image was published, no branch was pushed, no commit was created, and no task was marked complete. Worker inspection/startup/Compose wiring, CI publication and the remaining T12 qualification are unfinished. The scope is now explicitly revised as recorded above. Resume under the current-lag contract; do not pin this candidate as qualified until the remaining T12 checks and publication are complete.
