# Story: Implement Locking Protocol + Deadlock Retry

## Description

Implement the strict identity-correctness locking protocol described in `reference/design/backend-redesign/transactions-and-concurrency.md`:

- Use `dms.IdentityLock` rows for shared/update locks.
- Acquire shared locks on all identity-component children first (sorted by `DocumentId`).
- Acquire update locks on parent document(s) next (sorted by `DocumentId`).
- Apply deadlock retry with bounded attempts and instrumentation.

## Acceptance Criteria

- Lock acquisition follows a deterministic order and prevents “stale-at-birth” identities.
- Deadlocks/serialization failures are retried according to a configurable policy.
- Lock acquisition times and retry counts are observable via metrics/logs.

## Tasks

1. Implement dialect-specific lock queries for shared and update locks on `dms.IdentityLock`.
2. Implement deterministic lock ordering and bounded deadlock retry logic.
3. Add tests for:
   1. ordering behavior,
   2. retry behavior (simulated),
   3. metrics/log emission.

