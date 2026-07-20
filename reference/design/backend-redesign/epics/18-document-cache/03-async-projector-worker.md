---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Asynchronous DocumentCache Projector Worker

## Description

Implement the DMS-owned background projector that keeps `dms.DocumentCache` caught up with relational
representation changes.

The hosted supervisor consumes work by `(tenant key, DataStoreId, DocumentId, target ContentVersion)`, dispatches
it through an explicit non-HTTP execution context for that data store, materializes the caller-agnostic document,
and upserts `dms.DocumentCache` only when the target version is still current. Within one database, the persisted
work/fencing key remains `(DocumentId, target ContentVersion)`.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md`,
  `18-01-documentcache-ddl-and-projector-state.md`, and
  `18-02-document-materializer-service.md`.
- Depends on stale-write enforcement from `18-07-projector-stale-write-fencing.md` for final correctness; the
  worker can be developed against a provisional guard before that story is complete.
- Provides ongoing projection behavior and lag semantics consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`,
  `17-cdc-kafka/05-e2e-kafka-scenarios.md`, and `17-cdc-kafka/06-ops-docs-runbooks.md`.

## Acceptance Criteria

- DMS starts the projector only when projector mode is `Async` or `CdcRequired`.
- A supervisor enumerates all loaded tenant/data-store configurations and runs one logically isolated projector
  execution context per `(tenant key, DataStoreId)` with a usable connection string.
- Background execution explicitly selects the target data store in a new service scope; it does not depend on
  request-scoped route resolution, JWT claims, or the most recently handled request.
- Each projector scans its own database's `dms.Document` in `ContentVersion` order for missing or stale cache
  rows.
- The projector can also accept queued/enqueued work from write/read paths for specific `(DocumentId,
  ContentVersion)` targets, but any shared queue retains `(tenant key, DataStoreId)` as part of the dispatch key.
- The projector materializes documents through the shared materialization service.
- The projector does not write cache rows when the materialized `DocumentJson` server metadata disagrees with
  the cache columns; it records the attempt as a projection failure.
- The projector writes `dms.DocumentCache` through the stale-write guarded upsert path.
- The projector updates projection state for scanned/projected versions and last successful projection time.
- The projector skips work for deleted documents without recreating cache rows.
- The projector stops gracefully during application shutdown and does not leave partial cache rows.
- The supervisor snapshots its projection execution targets at startup. For entries in Story 00's explicit CDC
  target list, it retains the startup physical source binding and does not follow a CMS change to a different
  source. Same-source credential or operational connection-setting refreshes may be adopted after provider-specific
  identity verification; confirmed source drift makes CDC readiness false and requires coordinated deployment.
- Work queues, concurrency limits, failures, and cancellation are isolated so one unavailable data store does not
  stop projection for its peers.
- Tests cover multiple statically configured tenants/data stores with colliding `DocumentId`/`ContentVersion`
  values, create, update, stale queued work, deleted-document work, failure isolation, restart with a changed
  configured target list, same-source credential refresh, physical-source drift, and disabled-mode behavior.

## Tasks

1. Add hosted supervisor lifecycle wiring and a non-HTTP per-data-store execution-scope factory.
2. Create execution contexts from the tenant-partitioned projection inventory and apply Story 00's explicit CDC
   target bindings to listed contexts.
3. Implement candidate scanning over each target database's `dms.Document` and stale/missing cache detection.
4. Add an internal enqueue API whose process-level work key includes tenant and data-store identity.
5. Call the shared materializer and guarded cache upsert in the explicitly selected data-store scope.
6. Surface materializer invariant failures through the projection failure path.
7. Persist projector progress in each database's `dms.DocumentCacheProjectionState`.
8. Add multi-instance and provider integration tests for PostgreSQL and SQL Server.

## Out of Scope

- Initial full backfill orchestration.
- Retry/dead-letter policy beyond basic failure propagation.
- Kafka connector readiness checks.
