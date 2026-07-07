---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add CDC-Mode Pre-Delete Source-Row Materialization

## Description

Wire the relational delete path so CDC-mode deletes cannot remove `dms.Document` unless the transaction has
verified or materialized the `dms.DocumentCache` source row Debezium needs for the Kafka tombstone.

This behavior applies only when Kafka CDC is enabled. In non-CDC modes, missing or stale cache rows do not
block deletes.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md`,
  `18-02-document-materializer-service.md`, and the guarded write path from
  `18-07-projector-stale-write-fencing.md`.
- Depends on the relational delete path ordering that deletes the concrete resource/descriptor row before
  deleting `dms.Document`.
- Unblocks `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`,
  `17-cdc-kafka/04-message-contract-tests.md`, and `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- When Kafka CDC is enabled, DELETE by id verifies that a fresh `dms.DocumentCache` row exists before the
  resource/descriptor row is removed.
- If the cache row is missing or stale, the delete path synchronously materializes and upserts the current
  pre-delete representation before the resource/descriptor row is removed.
- Source-row verification/materialization runs under a same-document write/projector fence.
- If source-row verification/materialization fails, the API delete fails with a retryable server-side error and
  does not remove `dms.Document`.
- After source-row verification, the delete path preserves existing ordering:
  - delete concrete resource/descriptor row first,
  - delete `dms.Document` second,
  - rely on `ON DELETE CASCADE` to remove `dms.DocumentCache`.
- Non-CDC deletes do not require a cache source row.
- Tests cover fresh-cache delete, missing-cache delete, stale-cache delete, materialization failure, non-CDC
  delete, descriptor delete, and referenced-document conflict behavior.

## Tasks

1. Add a delete-path hook that runs only when Kafka CDC is enabled.
2. Acquire or reuse a per-document fence compatible with projector work.
3. Verify cache freshness before delete.
4. Materialize and guarded-upsert the current pre-delete representation when needed.
5. Map materialization failures to retryable server-side errors.
6. Add PostgreSQL and SQL Server integration tests for delete ordering and cache cascade cleanup.

## Out of Scope

- Kafka connector transforms and tombstone shaping.
- Provider-level Debezium proof; that belongs to `18-10-provider-cdc-delete-verification.md`.
