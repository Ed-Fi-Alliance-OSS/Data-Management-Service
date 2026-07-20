---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Description

Expose DocumentCache projection health directly from the current mismatch between
`dms.Document` and `dms.DocumentCache`.

The health surface distinguishes optional cache degradation from CDC projection
completeness. It does not participate in API routing or mutation decisions;
authoritative Kafka deletes come independently from `dms.Document`.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md`,
  `18-03-async-projector-reconciliation-loop.md`, and
  `18-07-projector-stale-write-fencing.md`.
- Consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md` and
  `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.
- Supplies operational signals for `17-cdc-kafka/06-ops-docs-runbooks.md`.

## Acceptance Criteria

- Health is evaluated per explicit `(tenant key, DataStoreId)` context and does not
  depend on current HTTP route selection.
- Health reports projector mode, required table existence, and whether the in-process
  reconciliation loop is running.
- A provider-equivalent current-state query reports:
  - total mismatch count,
  - missing-row count,
  - version-mismatched-row count,
  - oldest mismatch source timestamp and age,
  - same-version stamp-integrity mismatch count.
- Health thresholds use mismatch count and oldest mismatch age.
- Process-local last scan, scan duration, last successful upsert, and last error may be
  reported as diagnostics but are not completeness evidence.
- Neither `LastScannedContentVersion` nor `LastProjectedContentVersion` is stored or used;
  tests prove a higher successful version does not hide a missing lower version.
- Connector-registration prerequisites are exposed independently of projection
  completeness and require async mode, source/cache tables, a startable loop, and guarded
  upsert support.
- Projection completeness for CDC is true only when the current mismatch count is zero.
- CDC/Kafka combines this result with explicit target/source binding, provider delete
  capture, connector snapshot/catch-up, source-position, and connector-lag checks.
- Projection health/readiness never changes normal routing and never blocks reads or
  mutations. Cache-backed reads continue through relational fallback.
- A deployment aggregate can fail while still exposing every per-data-store result; one
  unavailable target does not stop peer evaluation or reconciliation.
- Metrics/logs cover scans, candidate count, projection attempts/successes/failures,
  in-memory retry deferrals, stale skips, mismatch count/age, and cache
  hit/miss/stale fallback without exposing connection values or document data.
- Tests cover healthy async mode, disabled mode, missing table, zero and nonzero mismatch
  counts, a missing lower version with a higher projected version, oldest mismatch age,
  persistent failure with bounded retry, unblocked API deletion, and mixed targets.

## Tasks

1. Define the per-data-store health/completeness abstraction and deployment aggregate.
2. Implement provider-equivalent mismatch count and oldest-age queries.
3. Add configurable health thresholds for mismatch count and age.
4. Add structured logs and metrics.
5. Expose registration prerequisites and zero-mismatch completeness to Kafka bootstrap.
6. Add tests for health output, exact completeness, and API independence.

## Out of Scope

- Durable projection progress or failure records.
- Kafka connector status and source-position checks.
- CDC target/source-binding checks.
- Provider delete capture or ordering verification.
- External monitoring dashboard implementation.
