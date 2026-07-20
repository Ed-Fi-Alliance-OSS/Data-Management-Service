---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add the Asynchronous DocumentCache Reconciliation Loop

## Description

Implement the DMS-owned background reconciler that keeps `dms.DocumentCache` aligned
with current relational representations.

For each data store, the loop queries `dms.Document` for rows whose cache row is absent,
has another `ContentVersion`, or violates the paired timestamp invariant. It materializes
a bounded batch, performs the guarded upsert, and repeats. That one loop handles an empty
cache, ongoing writes, process restart, cache rebuild, and retry. No request-path enqueue
API or persisted projector workflow state is introduced.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md`,
  `18-02-document-materializer-service.md`, and the core `dms.Document` /
  `dms.DocumentCache` DDL from E02.
- Depends on the guarded write contract from
  `18-07-projector-stale-write-fencing.md` for final correctness.
- Provides projection completeness and mismatch-age signals consumed by
  `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`,
  `17-cdc-kafka/05-e2e-kafka-scenarios.md`, and
  `17-cdc-kafka/06-ops-docs-runbooks.md`.

## Acceptance Criteria

- DMS starts reconciliation only when projector mode is `Async`.
- A supervisor snapshots all loaded tenant/data-store configurations at startup and runs
  one isolated logical loop per `(tenant key, DataStoreId)` with a usable connection
  string.
- Background execution explicitly selects the target data store in a non-HTTP service
  scope; it does not depend on route resolution, JWT claims, or the most recent request.
- Each loop selects bounded batches from its own database where:
  - `dms.DocumentCache` is absent for the `DocumentId`, or
  - cached `ContentVersion` differs from current `dms.Document.ContentVersion`.
- `ContentVersion` is the sole freshness key. `LastModifiedAt` remains payload metadata
  and is not a reconciliation candidate condition.
- The loop materializes candidates through the shared materialization service and writes
  only through the guarded cache upsert.
- Metadata-invariant failures do not produce cache rows and emit sanitized structured
  diagnostics.
- A source update during materialization causes a stale-write no-op; the next scan
  discovers the new current version.
- A delete during materialization cannot recreate a cache row.
- Failures use bounded in-memory exponential backoff with jitter keyed by data store,
  `DocumentId`, and current `ContentVersion`.
- Backoff candidates are skipped without starving other mismatches; entries disappear
  when the version changes, the document is deleted, or the cache becomes fresh.
- Restart discards only in-memory delay and rediscovers all remaining work from the
  database mismatch query.
- Empty-cache initial population and truncation/rebuild require no separate phase, epoch,
  or reset operation.
- No projection queue, enqueue API, persisted cursor/high-watermark, projection-state
  row, failure row, retry classification, dead-letter transition, requeue, or manual
  resolution workflow is implemented.
- A zero current mismatch count is the completeness signal. A highest scanned or
  projected `ContentVersion` is never used to infer completeness.
- One unavailable data store does not stop reconciliation for peers.
- Duplicate loops from multiple DMS replicas remain correct through idempotent guarded
  upserts; deployments may designate projector hosts to avoid redundant scans without a
  required distributed lease.
- Tests cover colliding local ids across data stores, empty-cache population, create,
  update, concurrent source change, delete, transient and persistent failures, restart,
  truncation/rebuild, failure isolation, multiple replicas, and disabled mode.

## Tasks

1. Add hosted supervisor lifecycle wiring and a non-HTTP per-data-store execution-scope
   factory.
2. Implement the bounded anti-join/version-mismatch candidate query for PostgreSQL and
   SQL Server; measure realistic plans and add a provider-appropriate ordered-scan index
   only if needed.
3. Call the shared materializer and guarded cache upsert for each candidate.
4. Add fair bounded in-memory retry backoff and idle polling.
5. Expose current mismatch count and oldest mismatch timestamp to the health abstraction.
6. Add graceful cancellation and sanitized structured telemetry.
7. Add provider and multi-data-store integration tests.

## Out of Scope

- Durable workflow or retry state.
- Kafka connector readiness and source-position checks.
- Dynamic CMS projection-target discovery.
