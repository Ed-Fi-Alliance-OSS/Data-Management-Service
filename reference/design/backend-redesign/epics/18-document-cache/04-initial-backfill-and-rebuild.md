---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Restartable Initial Backfill and Rebuild Support

## Description

Add first-class backfill and rebuild support for `dms.DocumentCache`.

Backfill starts a bounded epoch by capturing `BackfillTargetContentVersion =
max(dms.Document.ContentVersion)`, then materializes fresh cache rows for current documents at or below that
target. It must be restartable, idempotent, safe while ordinary writes continue, and visible through projector
state. Writes that advance beyond the target while backfill runs are handled by normal projector catch-up and
lag readiness rather than extending the backfill target.

## Dependencies

- Depends on `18-01-documentcache-ddl-and-projector-state.md`,
  `18-02-document-materializer-service.md`, and the guarded write path from
  `18-07-projector-stale-write-fencing.md`.
- Unblocks CDC readiness consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md` and connector
  registration in `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.

## Acceptance Criteria

- Initial backfill starts automatically or through an explicit internal startup path when projector mode is
  `Async` and backfill is not complete.
- Backfill creates a `BackfillEpochId` and captures `BackfillTargetContentVersion` at epoch start.
- Backfill scans existing `dms.Document` rows with current `ContentVersion <= BackfillTargetContentVersion` and
  materializes the current representation stamp for each still-current row.
- Backfill is restartable after process restart without duplicating rows or losing progress.
- Restart/resume uses the same incomplete epoch id and target content version rather than recapturing a moving
  target.
- Backfill is safe while writes continue; stale lower-version work cannot overwrite newer cache rows.
- Documents deleted or updated beyond the target while backfill is running are skipped or resolved under the
  same stale-write guard.
- Backfill status is recorded as `NotStarted`, `Running`, `Complete`, or `Failed`.
- Backfill progress exposes epoch id, target content version, scanned/projected content versions, and counts
  where practical.
- In `Async` mode, normal API traffic can continue while backfill is running.
- When Kafka CDC is enabled, projection readiness remains false until the bounded
  backfill epoch completes, no unresolved current projection failures are known, and
  normal projector lag above the epoch target is within threshold. This never gates API
  availability.
- The completed backfill epoch id and target content version are exposed as the CDC readiness cutover marker:
  versions at or below the target are covered by the bounded epoch, and versions above the target are covered by
  normal projector catch-up lag.
- Cache truncation or rebuild resets projection readiness, starts a new epoch, and
  captures a new target content version. Connector filtering ensures cache deletion
  publishes no domain tombstones.
- Tests cover empty database, existing documents, restart/resume, concurrent update during backfill, and cache
  truncation/rebuild.

## Tasks

1. Add backfill orchestration using the projector materialization and guarded upsert paths.
2. Persist backfill epoch id, target content version, status, and progress in projection state.
3. Add rebuild/reset behavior for cache truncation or operator-initiated rebuild.
4. Add health/readiness hooks for backfill status.
5. Add PostgreSQL and SQL Server integration coverage for bounded epoch completion and cutover readiness.

## Out of Scope

- Kafka connector snapshot mode.
- Production resnapshot runbook details; those belong to CDC runbooks in `17-cdc-kafka/06-ops-docs-runbooks.md`.
