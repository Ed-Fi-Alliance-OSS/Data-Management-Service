---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Emit DocumentCache Projector State and Failure DDL

## Description

Add DDL/provisioning support for the DocumentCache projector companion state defined by DMS-1246.

`dms.DocumentCache` remains the projected document-state row. Projector progress, failures, retries, and
readiness state are tracked in companion objects so operational metadata does not leak into the CDC row
contract.

## Dependencies

- Depends on core DDL/provisioning infrastructure from the relational backend redesign.
- Unblocks `18-03-async-projector-worker.md`, `18-04-initial-backfill-and-rebuild.md`,
  `18-08-projection-retry-dead-letter-and-repair.md`, and
  `18-09-documentcache-health-readiness-and-telemetry.md`.
- Provides required source/state objects consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md` and
  `17-cdc-kafka/01-cdc-ddl-support.md`.

## Acceptance Criteria

- Generated PostgreSQL and SQL Server DDL include provider-equivalent companion objects for:
  - `dms.DocumentCacheProjectionState`,
  - `dms.DocumentCacheProjectionFailure`.
- Projection state tracks logical fields for mode, backfill epoch id, bounded backfill target content version,
  backfill status, backfill timestamps, scanned/projected content versions, last success/failure timestamps, and
  unresolved failure count.
- Projection failure state tracks logical fields for document identity, target content version, failure kind,
  sanitized error summary, attempt count, timestamps, next retry time, and resolution time.
- DDL does not add retry/dead-letter lifecycle columns to `dms.DocumentCache`.
- DDL includes an index on `dms.Document(ContentVersion, DocumentId)` or an equivalent provider-specific access
  path for projector scans.
- Existing `dms.DocumentCache` constraints remain intact:
  - primary key / FK on `DocumentId`,
  - `ON DELETE CASCADE` from `dms.Document`,
  - unique `DocumentUuid`,
  - provider-specific JSON object constraint on `DocumentJson`.
- Generated DDL and manifests make the optional projector objects visible for diagnostics and test assertions.
- DB-apply smoke coverage validates the new objects on PostgreSQL and SQL Server.

## Tasks

1. Model projector state and failure objects in the DDL inventory.
2. Emit PostgreSQL DDL for state, failure, and supporting indexes.
3. Emit SQL Server DDL for state, failure, and supporting indexes.
4. Update provisioning manifests or introspection outputs to list the optional projector objects.
5. Add deterministic DDL snapshot coverage.
6. Add PostgreSQL and SQL Server DB-apply smoke assertions.

## Out of Scope

- PostgreSQL publication/replica identity setup for Debezium; that remains `17-cdc-kafka/01-cdc-ddl-support.md`.
- SQL Server database/table CDC enablement; that remains `17-cdc-kafka/01-cdc-ddl-support.md`.
- Projector retry implementation.
