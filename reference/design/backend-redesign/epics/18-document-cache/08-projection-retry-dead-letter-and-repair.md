---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Projection Retry, Dead-Letter, and Repair Handling

## Description

Add retry and dead-letter behavior for DocumentCache projection failures.

Projection failures should be visible and actionable. In `Async` mode they degrade
cache/indexing health but do not break normal API correctness. When Kafka CDC is enabled,
unresolved current projection failures, including dead-lettered failures, make upsert
projection readiness false without changing API behavior.

## Dependencies

- Depends on `18-01-documentcache-ddl-and-projector-state.md`,
  `18-03-async-projector-worker.md`, and `18-07-projector-stale-write-fencing.md`.
- Feeds failure diagnostics consumed by `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`,
  `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`, and `17-cdc-kafka/06-ops-docs-runbooks.md`.

## Acceptance Criteria

- Transient projection failures retry with bounded backoff and jitter.
- Permanently failing work does not hot-loop.
- Failures are recorded in `dms.DocumentCacheProjectionFailure` or equivalent provider-specific state.
- Failure rows include sanitized diagnostic metadata and do not store full `DocumentJson` or request payloads.
- A failure becomes dead-lettered when classified as non-retryable or after exceeding the configured retry
  budget.
- Dead-lettered failures remain visible until resolved or requeued.
- Successful projection of a newer version can resolve older failures for the same `DocumentId`.
- Stale historical failures that have been superseded by a successful newer projection do not block CDC readiness.
- Retry preserves stale-write fencing and cannot overwrite newer rows or recreate rows after delete.
- Operators have a documented way to requeue or mark failures resolved.
- Tests cover transient retry, permanent dead letter, newer-version resolution, retry-after-delete, and
  sanitized error storage.

## Tasks

1. Add projection failure classification and retry policy.
2. Persist failure state and attempt metadata.
3. Add dead-letter threshold handling.
4. Add repair/requeue/resolution mechanics.
5. Add metrics/logging for retries, dead letters, and repair actions.
6. Add unit and provider integration tests.

## Out of Scope

- Exposing document payloads in failure state.
- CDC connector retry behavior.
