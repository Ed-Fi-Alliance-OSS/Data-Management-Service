---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Enforce Projector Stale-Write and Post-Delete Fencing

## Description

Implement the database-enforced guard that makes reconciliation and optional read-through
materialization monotonic per document.

The guard prevents a candidate captured at an older source version from overwriting a
newer cache row or recreating a cache row after the corresponding `dms.Document` row has
been deleted.

## Dependencies

- Depends on the `dms.Document` representation stamps from update tracking.
- Required by `18-03-async-projector-reconciliation-loop.md`.
- Supplies upsert-projection ordering guarantees to `17-cdc-kafka`.

## Acceptance Criteria

- All writes to `dms.DocumentCache` use a single guarded upsert path or equivalent shared guard.
- The guard writes a cache row only when the current `dms.Document` row still exists.
- The guard writes a cache row only when current `dms.Document.ContentVersion` and
  `ContentLastModifiedAt` match the target work item.
- A lower captured `ContentVersion` cannot overwrite a higher cache version.
- A work item for a deleted `DocumentId` cannot recreate `dms.DocumentCache`.
- The guard works consistently for reconciliation and optional read-through fill.
- Guard failures are observable as stale skips rather than generic unexpected errors.
- Tests cover out-of-order candidates, concurrent update during projection, delete racing
  materialization, and provider parity for PostgreSQL and SQL Server.

## Tasks

1. Define the guarded cache write contract.
2. Implement PostgreSQL guarded upsert.
3. Implement SQL Server guarded upsert.
4. Route reconciliation and read-through writes through the shared guard.
5. Add stale-skip metrics and diagnostic logging.
6. Add concurrency-focused integration tests for both providers.

## Out of Scope

- Distributed lock manager design beyond the per-document fencing needed by cache writes.
- Kafka consumer stale-message handling.
