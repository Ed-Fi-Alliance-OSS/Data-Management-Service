---
jira: DMS-1285
jira_url: https://edfi.atlassian.net/browse/DMS-1285
---

# Story: Close MSSQL Relational Write-Path Correctness and Resilience Coverage Gaps

## Description

Run the critical relational write-path correctness and resilience scenarios against real SQL Server through
the production MSSQL repository, generated DDL, command executor, and write-session boundaries.

Parity is behavioral. The story does not require identical PostgreSQL and MSSQL test filenames or fixture
counts. Begin with a reviewed inventory that maps each PostgreSQL-only scenario to existing equivalent MSSQL
coverage, new coverage, or a justified non-applicable dialect difference.

## Dependencies

- Blocked by `DMS-1023`, which establishes the canonical scenario catalog, fixtures, and assertion contract
  consumed by this provider-coverage story.

## Required Scenario Families

| Scenario family | Required assertions |
| --- | --- |
| Baseline create | Root, nested collection, extension, and extension-child rows are complete and reconstitute correctly. |
| Changed PUT | Omitted inlined values clear correctly; omitted collection and extension state is deleted. |
| Collection reorder | Stable `CollectionItemId` values are reused and sibling ordinals remain unique and contiguous. |
| Guarded no-op and races | Unchanged writes do not rewrite rowsets or bump content version; stale and commit-window races preserve committed state and retry semantics. |
| Multi-batch collections | Large create/update/delete operations stay within SQL Server command/parameter limits and never leave partial state. |
| POST-as-update | Ordinary update, immutable-identity rejection, and concurrent-create conversion to update behave consistently. |
| Rollback after early writes | Injected failures roll back document, root, child, extension, identity, and tracking changes atomically. |

## Design

- Reuse the shared fixture vocabulary and assertions owned by
  [`DMS-1023`](../13-test-migration/02-parity-and-fixtures.md) where practical.
- Provider-specific setup is allowed when it reaches the same production boundary and preserves equivalent
  externally visible and authoritative-storage assertions.
- Cover content-version and last-modified behavior in addition to relational row shape.
- Exercise batching at sizes that force multiple reservations, inserts, updates, or deletes under SQL Server
  parameter pressure.
- A bounded MSSQL defect exposed by the matrix may be fixed in this story. A materially separate defect may be
  split into a linked blocker, but its scenario remains incomplete while the blocker is open.
- Assign all required MSSQL scenarios to configured CI shards that fail when SQL Server is unavailable or
  misconfigured.

## Acceptance Criteria

- A reviewed inventory maps every PostgreSQL-only core write scenario to MSSQL coverage or a documented,
  justified non-applicable result.
- Real-SQL-Server integration tests cover every required scenario family above.
- Equivalent scenarios assert response behavior, document/root/child/extension state, stable collection
  identity, ordinals, update tracking, concurrency outcomes, and rollback.
- Batching persists or deletes the full requested set without exceeding compiled-command/provider limits or
  leaving partial state.
- Rejected immutable identity changes and injected failures leave all authoritative tables unchanged.
- Intentional provider differences are explicit and tested; assertions are not weakened merely for MSSQL.
- PostgreSQL relational write tests continue to pass unchanged.
- No required scenario remains PostgreSQL-only when the story closes unless a linked blocking defect remains
  open and this story also remains open.

## Non-Goals

- Identical test-file counts between providers.
- Throughput, latency, or lock-wait targets; `DMS-1019` owns benchmarking.
- Speculative optimizations; `DMS-1065` owns measured follow-up optimization.
- The Docker-stack MSSQL runner; `DMS-1284` owns that boundary.
- Foreign-key pruning.

## Design References

- [`../07-relational-write-path/03-persist-and-batch.md`](../07-relational-write-path/03-persist-and-batch.md)
- [`../07-relational-write-path/03b-profile-aware-persist-executor.md`](../07-relational-write-path/03b-profile-aware-persist-executor.md)
- [`../13-test-migration/01-backend-integration-tests.md`](../13-test-migration/01-backend-integration-tests.md)
- [`../13-test-migration/02-parity-and-fixtures.md`](../13-test-migration/02-parity-and-fixtures.md)
- [`../../design-docs/transactions-and-concurrency.md`](../../design-docs/transactions-and-concurrency.md)
