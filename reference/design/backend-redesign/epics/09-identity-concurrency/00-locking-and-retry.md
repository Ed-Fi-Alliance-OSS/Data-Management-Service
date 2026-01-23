---
jira: DMS-996
jira_url: https://edfi.atlassian.net/browse/DMS-996
---

# Story: Implement Deadlock Retry Policy for Cascade/Trigger Writes

## Description

Implement a bounded deadlock retry policy for write transactions, including identity updates that can fan out via FK cascades and triggers.

Align with `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (“Deadlock + retry policy”).

SQL Server operational guidance: prefer MVCC reads (`READ_COMMITTED_SNAPSHOT ON`, optionally `ALLOW_SNAPSHOT_ISOLATION ON`) to reduce reader/writer blocking and deadlocks, but still implement retries for correctness.

## Acceptance Criteria

- Deadlocks/serialization failures are retried according to a configurable policy.
- Retry counts and transaction durations are observable via metrics/logs.

## Tasks

1. Implement bounded deadlock retry around the full write transaction (pgsql + mssql).
2. Define which error codes are retryable (PostgreSQL `40P01`; SQL Server `1205`, optionally `1222`).
3. Add tests for:
   1. retry behavior (simulated),
   2. metrics/log emission.
