---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Enforce Projector Stale-Write and Post-Delete Fencing

## Design References

- [Freshness and reconciliation](../../../cdc-streaming.md#freshness-and-reconciliation)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Implement one provider-equivalent strong commit-order fence shared by reconciliation and
optional direct fill. A stale materialization cannot commit after the canonical document
has advanced beyond its captured version.

## Dependencies

- Depends on canonical `dms.Document` representation stamps and is required by 18-03.

## Deliverables

1. Materialize and validate before opening the guard transaction; do not retain a
   `dms.Document` lock during reconstitution.
2. Implement the PostgreSQL `READ COMMITTED` guard with a keyed
   `SELECT ... FOR NO KEY UPDATE` or strictly stronger equivalent. Evaluate the captured
   version on the row returned after any concurrent update wait, retain the lock through
   cache upsert and commit, and do not treat the cache foreign key or a conditional MVCC
   source read as the content-version fence.
3. Implement the SQL Server guard at `READ COMMITTED`, with or without
   `READ_COMMITTED_SNAPSHOT`, using a keyed `UPDLOCK` locking read or strictly stronger
   equivalent. Evaluate the captured version on the locked current row and retain the
   lock through cache upsert and commit; never use a row-versioned source read as the
   fence.
4. Insert only when the cache row is absent and update only when its existing
   `ContentVersion` is lower. Persist `StreamEtag`, `ContentVersion`, and `DocumentJson`
   atomically in that row; treat a same-version duplicate as already fresh and a higher
   existing cache version as an invariant violation that receives no write or retry.
   Copy `DocumentUuid` from the locked canonical source and rely on the cache identity
   validation trigger as the final database backstop; never accept an independent UUID
   from a projector or direct-fill caller.
5. Execute one candidate per short guard transaction and route every projector/direct-fill
   write through it. Do not batch source-row locks across candidates in v1.
6. Report missing/version-changed source rows as observable stale skips. Route deadlock,
   serialization, and bounded lock-timeout outcomes to retry without a cache write.
   Optional direct fill uses a short bounded wait and abandons the fill on contention
   without failing the relational response.
7. Record guard lock-wait duration and timeout, deadlock, serialization retry, stale-skip,
   already-fresh, and successful-write counts without document identifiers or payloads.

## Acceptance Evidence

- Deterministically synchronized provider integration tests cover both lock orderings for
  a concurrent source update: a source winner makes the stale projector wait and no-op;
  a projector winner commits the cache row before the source version advances.
- Provider tests cover deletion racing materialization, out-of-order candidates,
  same-version duplicate loops, multiple projector replicas, timeout/deadlock retry, and
  PostgreSQL/SQL Server parity.
- Provider tests prove every guarded writer copies the canonical UUID and that the
  cache-only validation trigger rolls back an intentionally mismatched write without
  changing canonical write behavior.
- Provider tests prove a cache-ahead row is never overwritten with the lower captured
  version and is returned as a distinct invariant result rather than a transient database
  failure or retryable stale skip.
- Tests prove a stale materialization cannot commit an old `StreamEtag`, pair an old
  `StreamEtag` with a newer cache row, or replace the ETag for a higher `ContentVersion`.
- Tests prove the PostgreSQL foreign-key lock and a plain conditional
  `INSERT ... SELECT` are insufficient substitutes for the explicit source-row lock.
- Tests prove payload timestamps do not become a second guard input.
- Telemetry distinguishes stale skips from unexpected database failures.
- Performance evidence compares projector-disabled and projector-enabled source-write
  throughput and p95/p99 latency for uniform updates, a hot single document, duplicate
  replicas, rebuild, and optional direct fill. PostgreSQL evidence includes WAL/page-write
  amplification; SQL Server evidence includes lock waits and escalation checks.

## Out of Scope

- A distributed lock manager.
- Kafka consumer stale-message handling.
- Multi-candidate guard transactions or lock batching.
