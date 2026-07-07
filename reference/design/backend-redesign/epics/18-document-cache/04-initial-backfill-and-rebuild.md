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

Backfill scans every existing `dms.Document` row and materializes a fresh cache row for the current
representation stamp. It must be restartable, idempotent, safe while ordinary writes continue, and visible
through projector state.

## Dependencies

- Depends on `18-01-documentcache-ddl-and-projector-state.md`,
  `18-02-document-materializer-service.md`, and the guarded write path from
  `18-07-projector-stale-write-fencing.md`.
- Unblocks CDC readiness consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md` and connector
  registration in `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.

## Acceptance Criteria

- Initial backfill starts automatically or through an explicit internal startup path when projector mode is
  `Async` or `CdcRequired` and backfill is not complete.
- Backfill scans existing `dms.Document` rows and materializes the current representation stamp for each row.
- Backfill is restartable after process restart without duplicating rows or losing progress.
- Backfill is safe while writes continue; stale lower-version work cannot overwrite newer cache rows.
- Backfill status is recorded as `NotStarted`, `Running`, `Complete`, or `Failed`.
- Backfill progress exposes scanned/projected content versions and counts where practical.
- In `Async` mode, normal API traffic can continue while backfill is running.
- In `CdcRequired` mode, CDC readiness remains false until backfill completes and no unresolved current
  projection failures are known.
- Cache truncation or rebuild resets readiness and backfill status appropriately.
- Tests cover empty database, existing documents, restart/resume, concurrent update during backfill, and cache
  truncation/rebuild.

## Tasks

1. Add backfill orchestration using the projector materialization and guarded upsert paths.
2. Persist backfill status and progress in projection state.
3. Add rebuild/reset behavior for cache truncation or operator-initiated rebuild.
4. Add health/readiness hooks for backfill status.
5. Add PostgreSQL and SQL Server integration coverage.

## Out of Scope

- Kafka connector snapshot mode.
- Production resnapshot runbook details; those belong to CDC runbooks in `17-cdc-kafka/06-ops-docs-runbooks.md`.
