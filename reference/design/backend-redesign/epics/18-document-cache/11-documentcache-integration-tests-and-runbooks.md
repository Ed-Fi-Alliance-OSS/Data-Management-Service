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
failures, and CDC source guarantees.

This story closes the DocumentCache implementation epic by validating the story set as a coherent runtime
feature and documenting operational behavior that CDC/Kafka runbooks consume.

## Dependencies

- Depends on Stories 18-00 through 18-10 for final completion.
- Informs `17-cdc-kafka/06-ops-docs-runbooks.md`.
- Provides diagnostic expectations used by `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- Integration tests cover DocumentCache disabled mode.
- Integration tests cover async projection mode with create, update, backfill, cache hit, stale miss, and
  relational fallback.
- Integration tests cover CDC-required mode readiness before and after backfill.
- Integration tests cover projection failure, retry, dead-letter, repair/requeue, and readiness impact.
- Integration tests cover CDC-mode delete source-row materialization and non-CDC delete behavior.
- Integration tests run against PostgreSQL and SQL Server where provider support exists.
- Runbooks document:
  - configuration modes,
  - backfill/rebuild behavior,
  - cache hit/miss/stale fallback semantics,
  - retry/dead-letter handling and repair,
  - health/readiness fields,
  - CDC-mode delete blocking behavior,
  - provider verification requirements and known limitations.
- Runbooks distinguish DocumentCache operations from Kafka connector operations.
- Documentation links to the DMS-1246 decision records and to the DMS-1245 CDC/Kafka decision records.

## Tasks

1. Add integration test fixtures for DocumentCache modes and provider variants.
2. Add tests that exercise projection and read fallback end to end.
3. Add tests that exercise failure/retry/dead-letter/repair.
4. Add tests that exercise CDC-required readiness transitions.
5. Add DocumentCache runbook documentation.
6. Cross-link CDC/Kafka runbooks to DocumentCache health and delete-source guidance.

## Out of Scope

- Kafka connector setup, ACLs, offset reset, and topic management.
- Consumer application implementation guidance.
