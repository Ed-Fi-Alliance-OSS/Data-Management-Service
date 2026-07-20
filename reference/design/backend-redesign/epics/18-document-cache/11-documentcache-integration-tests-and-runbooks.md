---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Description

Add end-to-end DocumentCache coverage and operator guidance for projection, cache-backed reads, backfill,
failures, and CDC upsert readiness.

This story closes the DocumentCache implementation epic by validating the story set as a coherent runtime
feature and documenting operational behavior that CDC/Kafka runbooks consume.

## Dependencies

- Depends on the remaining implementation stories in this epic for final completion.
- Informs `17-cdc-kafka/06-ops-docs-runbooks.md`.
- Provides diagnostic expectations used by `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- Integration tests cover DocumentCache disabled mode.
- Integration tests cover async projection mode with create, update, backfill, cache hit, stale miss, and
  relational fallback.
- Integration tests cover CDC projection readiness before and after a bounded backfill epoch completes.
- Integration tests cover backfill high-watermark behavior: writes above the captured target are handled by
  normal projector catch-up and lag readiness, not by moving the epoch target.
- Integration tests cover `DocumentJson` server-metadata consistency with cache row `DocumentUuid` and
  `LastModifiedAt`.
- Integration tests cover projection failure, retry, dead-letter, repair/requeue, and readiness impact.
- Integration tests prove cache absence, projector failure, and rebuild never block API
  deletion and never require synchronous cache materialization.
- Integration tests prove cache truncation/rebuild starts a new epoch and that projection
  health changes remain observational.
- Integration tests run against PostgreSQL and SQL Server where provider support exists.
- Runbooks document:
  - configuration modes,
  - backfill/rebuild behavior,
  - backfill epoch id and target content version semantics,
  - cache hit/miss/stale fallback semantics,
  - retry/dead-letter handling and repair,
  - health/readiness fields,
  - cache/domain lifecycle separation,
  - the fact that CDC target binding, provider delete capture, and connector recovery
    belong to the CDC/Kafka runbook.
- Runbooks distinguish DocumentCache operations from Kafka connector operations.
- Documentation links to the DMS-1246 decision records and to the DMS-1245 CDC/Kafka decision records.

## Tasks

1. Add integration test fixtures for DocumentCache modes and provider variants.
2. Add tests that exercise projection and read fallback end to end.
3. Add tests that exercise failure/retry/dead-letter/repair.
4. Add tests that exercise CDC projection-readiness transitions without changing API
   behavior.
5. Add tests for cache rebuild and delete-path independence.
6. Add DocumentCache runbook documentation.
7. Cross-link CDC/Kafka runbooks to DocumentCache upsert health and lifecycle-separation guidance.

## Out of Scope

- Kafka connector setup, ACLs, offset reset, and topic management.
- Consumer application implementation guidance.
