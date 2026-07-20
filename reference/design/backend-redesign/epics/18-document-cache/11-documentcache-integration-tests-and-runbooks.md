---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Description

Add end-to-end DocumentCache coverage and operator guidance for reconciliation,
cache-backed reads, rebuild, failures, and CDC upsert readiness.

This story closes the DocumentCache implementation epic by validating the story set as a coherent runtime
feature and documenting operational behavior that CDC/Kafka runbooks consume.

## Dependencies

- Depends on the remaining implementation stories in this epic for final completion.
- Informs `17-cdc-kafka/06-ops-docs-runbooks.md`.
- Provides diagnostic expectations used by `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- Integration tests cover DocumentCache disabled mode.
- Integration tests cover async projection mode with create, update, empty-cache initial
  population, cache hit, stale miss, and relational fallback.
- Integration tests cover CDC projection completeness before and after the mismatch count
  reaches zero.
- Integration tests prove a projected higher `ContentVersion` does not hide a missing
  lower version.
- Integration tests cover `DocumentJson` server-metadata consistency with cache row `DocumentUuid` and
  `LastModifiedAt`.
- Integration tests cover transient and persistent projection failures, fair bounded
  in-memory retry, restart rediscovery, and mismatch-health impact.
- Integration tests prove cache absence, projector failure, and rebuild never block API
  deletion and never require synchronous cache materialization.
- Integration tests prove cache truncation/rebuild is recovered by the ordinary
  reconciliation query and that health changes remain observational.
- Integration tests run against PostgreSQL and SQL Server where provider support exists.
- Runbooks document:
  - configuration modes,
  - initial population/rebuild through the ordinary reconciliation loop,
  - cache hit/miss/stale fallback semantics,
  - bounded in-memory retry and fixing the underlying failure,
  - health/readiness fields,
  - cache/domain lifecycle separation,
  - the fact that CDC target binding, provider delete capture, and connector recovery
    belong to the CDC/Kafka runbook.
- Runbooks distinguish DocumentCache operations from Kafka connector operations.
- Documentation links to the DMS-1246 decision records and to the DMS-1245 CDC/Kafka decision records.

## Tasks

1. Add integration test fixtures for DocumentCache modes and provider variants.
2. Add tests that exercise projection and read fallback end to end.
3. Add tests that exercise failure, bounded retry, restart, and automatic recovery.
4. Add tests that exercise CDC projection-readiness transitions without changing API
   behavior.
5. Add tests for cache rebuild and delete-path independence.
6. Add DocumentCache runbook documentation.
7. Cross-link CDC/Kafka runbooks to DocumentCache upsert health and lifecycle-separation guidance.

## Out of Scope

- Kafka connector setup, ACLs, offset reset, and topic management.
- Consumer application implementation guidance.
