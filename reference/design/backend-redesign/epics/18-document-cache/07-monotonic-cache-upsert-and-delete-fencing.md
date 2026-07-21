---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Implement Monotonic Cache Upsert and Post-Delete Fencing

## Design References

- [Freshness and reconciliation](../../../cdc-streaming.md#freshness-and-reconciliation)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Implement one provider-equivalent monotonic cache upsert shared by reconciliation and
optional direct fill. Preserve post-delete fencing without taking a write-conflicting
source-row lock or promising that every cache upsert was canonical-current at commit.

## Dependencies

- Depends on canonical `dms.Document` representation stamps and is required by 18-03.

## Deliverables

1. Materialize and validate before opening the cache transaction. Do not acquire
   PostgreSQL `FOR NO KEY UPDATE`, SQL Server `UPDLOCK`, or another write-conflicting lock
   on `dms.Document` as a content-version fence.
2. Insert only when the cache row is absent and update only when its existing
   `ContentVersion` is lower. Persist `StreamEtag`, `ContentVersion`, `DocumentJson`, and
   the remaining cache columns atomically; treat a same-version duplicate as already fresh
   and a higher existing cache version as an invariant violation that receives no write.
3. Copy the immutable canonical `DocumentUuid` captured by the materializer and rely on
   the cache identity-validation trigger as the database backstop. Never accept an
   independent UUID from a projector or direct-fill caller.
4. Rely on the `DocumentCache(DocumentId)` foreign key as the post-delete fence. If a cache
   insert commits before canonical deletion, cascade deletion removes it; if deletion wins,
   a delayed insert cannot recreate the row.
5. Route every projector and direct-fill write through the shared upsert. Execute one
   candidate per short cache transaction in v1.
6. Treat a captured lower version that commits after a newer canonical version as ordinary
   monotonic projection lag. Leave the resulting cache-behind difference for incremental
   discovery, retry, or full audit; do not make the canonical writer wait for projection.
7. Report already-fresh, higher-version no-op, successful-write, and ordinary database
   failure counts without document identifiers or payloads. Add no source-lock wait,
   source-lock timeout, or projection-specific deadlock telemetry.

## Acceptance Evidence

- Deterministically synchronized provider integration tests cover both commit orderings
  for a concurrent source update. A delayed lower projection may commit when the cache is
  absent or lower, remains a cache-behind difference, and converges to the newer version.
- Provider tests prove a delayed lower candidate never replaces a higher cache row and a
  same-version duplicate does not rewrite it.
- Provider tests cover deletion racing insert/update and prove no cache row can survive or
  be recreated after canonical deletion commits.
- Provider tests prove every cache writer uses the captured canonical UUID and that the
  cache-only validation trigger rolls back an intentionally mismatched write without
  changing canonical write behavior.
- Tests prove a cache-ahead row is never overwritten with the lower captured version and is
  returned as a distinct invariant result rather than a transient database failure.
- Tests prove `StreamEtag`, `ContentVersion`, and `DocumentJson` remain one coherent atomic
  cache result even when the source advances concurrently.
- PostgreSQL and SQL Server concurrency tests prove projector and direct-fill writes take
  no write-conflicting `dms.Document` lock and do not make canonical writers wait for a
  projection-held source-row lock.
- Performance evidence compares projector-disabled and projector-enabled source-write
  throughput and p95/p99 latency for uniform updates, a hot single document, duplicate
  replicas, rebuild, and optional direct fill.

## Out of Scope

- A source/cache commit-order or linearizable-publication guarantee.
- A distributed lock manager.
- Kafka consumer stale-message handling.
- Multi-candidate cache transactions or batching.
