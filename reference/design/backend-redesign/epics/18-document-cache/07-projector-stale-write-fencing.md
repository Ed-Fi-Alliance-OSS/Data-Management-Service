---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Enforce Projector Stale-Write and Post-Delete Fencing

## Description

Implement the database-enforced guard that makes projection, backfill, retry, read-through, and CDC pre-delete
materialization monotonic per document.

The guard prevents older projection work from overwriting newer cache rows and prevents queued work from
recreating a cache row after the corresponding `dms.Document` row has been deleted.

## Dependencies

- Depends on the `dms.Document` representation stamps from update tracking.
- Required by `18-03-async-projector-worker.md`, `18-04-initial-backfill-and-rebuild.md`,
  `18-06-cdc-pre-delete-materialization.md`, and `18-08-projection-retry-dead-letter-and-repair.md`.
- Unblocks delete correctness requirements in `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`,
  `17-cdc-kafka/04-message-contract-tests.md`, and `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- All writes to `dms.DocumentCache` use a single guarded upsert path or equivalent shared guard.
- The guard writes a cache row only when the current `dms.Document` row still exists.
- The guard writes a cache row only when current `dms.Document.ContentVersion` and
  `ContentLastModifiedAt` match the target work item.
- A lower `ContentVersion` retry/backfill cannot overwrite a higher `ContentVersion` cache row.
- A work item for a deleted `DocumentId` cannot recreate `dms.DocumentCache`.
- The guard works consistently for projector, backfill, read-through fill, retry, and CDC pre-delete
  materialization.
- Guard failures are observable as stale skips rather than generic unexpected errors.
- Tests cover out-of-order work, concurrent update during projection, delete racing queued work, and provider
  parity for PostgreSQL and SQL Server.

## Tasks

1. Define the guarded cache write contract.
2. Implement PostgreSQL guarded upsert.
3. Implement SQL Server guarded upsert.
4. Route projector/backfill/pre-delete/read-through writes through the shared guard.
5. Add stale-skip metrics and diagnostic logging.
6. Add concurrency-focused integration tests for both providers.

## Out of Scope

- Distributed lock manager design beyond the per-document fencing needed by cache writes.
- Kafka consumer stale-message handling.
