---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Description

Expose DocumentCache health and CDC readiness signals.

The health surface must distinguish optional cache degradation from unsupported CDC state. CDC readiness is
stricter than ordinary read-cache health because connector registration must not advertise a supported stream
until the source table and projector guarantees are in place.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md`,
  `18-01-documentcache-ddl-and-projector-state.md`, `18-04-initial-backfill-and-rebuild.md`,
  `18-06-cdc-pre-delete-materialization.md`, `18-07-projector-stale-write-fencing.md`, and
  `18-08-projection-retry-dead-letter-and-repair.md`.
- Consumed directly by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md` and
  `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.
- Supplies operational signals for `17-cdc-kafka/06-ops-docs-runbooks.md`.

## Acceptance Criteria

- Health reports projector mode and whether required `dms.DocumentCache` objects exist.
- Health and CDC readiness are addressable per `(tenant key, DataStoreId)` and do not depend on the current HTTP
  route selection.
- Health reports initial backfill status, epoch id, target content version, and progress.
- Health reports projector lag by `ContentVersion` and by age of oldest missing/stale work where practical.
- Health reports unresolved failure count, oldest unresolved failure age, last failure kind, and last successful
  projection timestamp.
- Health reports whether CDC-mode pre-delete materialization is configured and available.
- CDC readiness fails when:
  - projector mode is not `CdcRequired`,
  - required cache/state objects are missing,
  - the bounded initial backfill epoch is incomplete,
  - stale-write fencing is unavailable,
  - pre-delete materialization is unavailable,
  - unresolved current projection failures exist, including dead-lettered failures,
  - projector lag above the completed backfill target exceeds the configured threshold,
  - provider-specific delete-source behavior has not been verified.
- Non-CDC cache-backed reads can remain available when CDC readiness is false.
- A deployment aggregate may fail when any configured CDC data store is not ready, but it also exposes each
  data-store result and a failure in one data store does not stop health evaluation or projection for others.
- Metrics/logs cover projection attempts, successes, retries, failures, stale skips, backfill epoch target
  capture, backfill progress, cache hit/miss/stale fallback, pre-delete materialization, and projector lag.
- Tests cover healthy async mode, healthy CDC-required mode, missing objects, incomplete backfill, unresolved
  current projection failures, dead letters, excessive lag above the completed backfill target, and missing
  provider verification, including mixed healthy/unhealthy data stores across tenants.

## Tasks

1. Define the per-data-store DocumentCache health/readiness abstraction and deployment aggregate.
2. Implement health checks and readiness diagnostics.
3. Add metrics and structured logs.
4. Add configuration for CDC lag thresholds.
5. Integrate readiness into the CDC prerequisite abstraction consumed by Kafka bootstrap.
6. Add tests for readiness failure reasons and health output.

## Out of Scope

- Kafka connector status checks.
- External monitoring dashboard implementation.
