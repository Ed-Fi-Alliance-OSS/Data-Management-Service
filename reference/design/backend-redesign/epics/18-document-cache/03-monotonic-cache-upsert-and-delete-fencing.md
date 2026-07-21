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
optional direct fill. Preserve post-delete fencing without requesting or retaining an
update/write source-row lock as a commit-order fence or promising that every cache upsert
was canonical-current at commit.

## Dependencies

- Depends on 18-00, 18-02, and canonical `dms.Document` representation stamps.
- Unblocks the 18-04 projector and 18-05 cache-backed read path.

## Deliverables

1. Accept one complete, validated cache candidate and open only the short cache
   transaction needed for the shared upsert. Materialization and source-currentness checks
   remain outside this component. Do not acquire PostgreSQL `FOR NO KEY UPDATE`, SQL Server
   `UPDLOCK`, or another write-conflicting lock on `dms.Document` as a content-version
   fence. Read the singleton cache-ahead latch under a provider-equivalent shared state-row
   lock that permits concurrent cache writers but conflicts with setting or clearing the
   latch; perform no cache write when it is set or missing/malformed.
2. Serialize concurrent cache writers on the `DocumentCache(DocumentId)` row/key and
   evaluate the version predicate atomically in the cache DML against the current cache row
   after any conflicting cache writer. Do not decide from an application pre-read followed
   by an unconditional write. Implement provider-safe absent-key handling; a duplicate-key
   race must retry by re-evaluating the current cache version.
3. Insert only when the cache row is absent and update only when its existing
   `ContentVersion` is lower. Persist `StreamEtag`, `ContentVersion`, `DocumentJson`, and
   the remaining cache columns atomically; treat a same-version duplicate as already fresh
   and a higher existing cache version as a superseded-candidate no-op. The latter does not
   by itself establish that the cache is ahead of the current canonical source.
4. Copy the immutable canonical `DocumentUuid` captured by the materializer and rely on
   the cache identity-validation trigger as the database backstop. Never accept an
   independent UUID from a projector or direct-fill caller.
5. Rely on the `DocumentCache(DocumentId)` foreign key as the post-delete fence. If a cache
   insert commits before canonical deletion, cascade deletion removes it; if deletion wins,
   a delayed insert cannot recreate the row.
6. Route every projector and direct-fill write through the shared upsert. Execute one
   candidate per short cache transaction in v1.
7. Treat a coherent lower candidate that reaches the upsert before a newer canonical
   commit as ordinary monotonic projection lag when its cache write later wins. Leave the
   resulting cache-behind difference for incremental discovery, retry, or full audit. Do
   not deliberately serialize ordinary canonical version updates behind an explicit
   projection-held source-row lock; ordinary cache-row, trigger, and foreign-key
   concurrency remains provider-defined.
8. Report already-fresh, superseded-candidate no-op, successful-write, and ordinary
   database failure counts without document identifiers or payloads. Add no source-lock
   wait, source-lock timeout, or projection-specific deadlock telemetry.

## Acceptance Evidence

- Provider tests prove a coherent lower candidate can populate an absent or lower cache row
  and that a later higher candidate converges it to the newer version.
- Provider tests serialize competing lower and higher candidates in both cache-lock orders,
  with the cache row initially missing and present, and prove the DML evaluates its
  predicate against the current locked cache row: a delayed lower candidate never replaces
  a higher cache row and a same-version duplicate does not rewrite it. Duplicate-key retry
  re-evaluates rather than applying an unconditional write.
- Provider concurrency tests prove ordinary cache writers may share the latch-read lock,
  setting the latch waits for already-started writes and prevents later writes, and the
  explicit clear transaction cannot race a cache upsert. Missing/malformed latch state is
  fail-closed.
- Provider tests cover deletion racing insert/update and prove no cache row can survive or
  be recreated after canonical deletion commits.
- Provider tests prove every cache writer uses the captured canonical UUID and that the
  cache-only validation trigger rolls back an intentionally mismatched write without
  changing canonical write behavior.
- Tests prove a higher cache version supersedes a lower candidate without being reported as
  a cache-ahead invariant. Cache-ahead classification requires an independent comparison
  with the current canonical source and remains part of reconciliation and health.
- Tests prove `StreamEtag`, `ContentVersion`, and `DocumentJson` remain one coherent atomic
  cache result even when the source advances concurrently.
- PostgreSQL and SQL Server concurrency tests prove projector and direct-fill writes request
  no explicit update/write `dms.Document` lock as a content-version fence and carry no lock
  from the optimistic source check into the cache transaction. They do not make canonical
  writers wait for an explicit projection-held content-version fence. Tests preserve and
  separately expose ordinary provider locks and contention from source reads, cache rows,
  foreign-key enforcement, and the UUID-validation trigger rather than classifying them as
  source-fence behavior.
- Performance evidence compares projector-disabled and projector-enabled source-write
  throughput and p95/p99 latency for uniform updates, a hot single document, duplicate
  replicas, rebuild, and optional direct fill.

## Out of Scope

- A source/cache commit-order or linearizable-publication guarantee.
- A distributed lock manager.
- Kafka consumer stale-message handling.
- Multi-candidate cache transactions or batching.
