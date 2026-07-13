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

The projector consumes work by `(DocumentId, target ContentVersion)`, materializes the caller-agnostic document,
and upserts `dms.DocumentCache` only when the target version is still current.

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
- The projector scans `dms.Document` in `ContentVersion` order for missing or stale cache rows.
- The projector can also accept queued/enqueued work from write/read paths for specific `(DocumentId,
  ContentVersion)` targets.
- The projector materializes documents through the shared materialization service.
- The projector does not write cache rows when the materialized `DocumentJson` server metadata disagrees with
  the cache columns; it records the attempt as a projection failure.
- The projector writes `dms.DocumentCache` through the stale-write guarded upsert path.
- The projector updates projection state for scanned/projected versions and last successful projection time.
- The projector skips work for deleted documents without recreating cache rows.
- The projector stops gracefully during application shutdown and does not leave partial cache rows.
- Tests cover create, update, stale queued work, deleted-document work, and disabled-mode behavior.

## Tasks

1. Add hosted-service lifecycle wiring for the projector.
2. Implement candidate scanning over `dms.Document` and stale/missing cache detection.
3. Add an internal enqueue API for targeted projection work.
4. Call the shared materializer and guarded cache upsert.
5. Surface materializer invariant failures through the projection failure path.
6. Persist projector progress in `dms.DocumentCacheProjectionState`.
7. Add provider integration tests for PostgreSQL and SQL Server.

## Out of Scope

- Initial full backfill orchestration.
- Retry/dead-letter policy beyond basic failure propagation.
- Kafka connector readiness checks.
