---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Description

Expose DocumentCache projection health and the signals that CDC readiness consumes for
document upserts.

The health surface distinguishes optional cache degradation from an incomplete CDC upsert
projection. It does not participate in API routing or mutation decisions; authoritative
Kafka deletes come from `dms.Document` and remain independent of these signals.

The surface distinguishes connector registration prerequisites from completed projection
readiness so DMS-1245 can register capture before initial backfill and then wait for
projection and connector catch-up before advertising CDC as ready.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md`,
  `18-01-documentcache-ddl-and-projector-state.md`,
  `18-04-initial-backfill-and-rebuild.md`,
  `18-07-projector-stale-write-fencing.md`, and
  `18-08-projection-retry-dead-letter-and-repair.md`.
- Consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md` and
  `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.
- Supplies operational signals for `17-cdc-kafka/06-ops-docs-runbooks.md`.

## Acceptance Criteria

- Health reports projector mode and whether required `dms.DocumentCache` objects exist.
- Health is addressable per `(tenant key, DataStoreId)` and does not depend on the current
  HTTP route selection.
- Health reports initial backfill status, epoch id, target content version, and progress.
- Health reports projector lag by `ContentVersion` and by age of oldest missing/stale work
  where practical.
- Health reports unresolved failure count, oldest unresolved failure age, last failure
  kind, and last successful projection timestamp.
- Health exposes whether connector-registration prerequisites are satisfied independently
  of initial backfill completion and steady-state projector lag.
- Projection readiness is false when:
  - projector mode is not `Async`,
  - required cache/state objects are missing,
  - the bounded initial backfill epoch is incomplete,
  - stale-write fencing is unavailable,
  - unresolved current projection failures exist, including dead-lettered failures,
  - projector lag above the completed backfill target exceeds the configured threshold.
- CDC/Kafka owns configured-target source binding, provider delete capture, connector
  status, and end-to-end readiness. It combines those signals with this story's
  projection result.
- Projection readiness never changes normal request routing and never blocks reads or
  mutations. Cache-backed reads continue through relational fallback.
- A deployment aggregate may fail when any projected data store is unhealthy, but it also
  exposes each data-store result and one failure does not stop health evaluation or
  projection for peers.
- Metrics/logs cover projection attempts, successes, retries, failures, stale skips,
  backfill target capture/progress, cache hit/miss/stale fallback, and projector lag
  without exposing connection values or document data.
- Tests cover healthy async mode, disabled mode, missing objects, incomplete backfill,
  unresolved current failures, dead letters, excessive lag, unblocked API deletion, and
  mixed healthy/unhealthy targets.

## Tasks

1. Define the per-data-store DocumentCache health/readiness abstraction and deployment
   aggregate.
2. Implement health checks and projection-readiness diagnostics.
3. Add metrics and structured logs.
4. Add configuration for CDC projection-lag thresholds.
5. Expose registration prerequisites and projection readiness to the Kafka bootstrap
   abstraction.
6. Add tests for readiness failure reasons, health output, and API independence.

## Out of Scope

- Kafka connector status checks.
- CDC target/source-binding checks.
- Provider delete capture or ordering verification.
- External monitoring dashboard implementation.
