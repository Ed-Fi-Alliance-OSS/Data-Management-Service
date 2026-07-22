---
status: proposed
date: 2026-07-20
jira:
  - DMS-1245
  - DMS-1246
related:
  - DMS-1232
  - DMS-1089
---

# Decision Record: Relational CDC Projector and Sources

## Decision

DMS owns one asynchronous `dms.DocumentCache` projector. The cache is rebuildable
projected state, not canonical persistence. Capabilities that need projected documents
select the projector; ordinary DMS correctness does not depend on it.

One Debezium connector uses two complementary document sources plus one internal
source-position heartbeat source:

| Source event | Public document-state result |
| --- | --- |
| `dms.DocumentCache` create, update, or snapshot/read | Document upsert |
| `dms.Document` delete | Kafka tombstone |
| `dms.DocumentCache` delete or truncate | Ignore |
| Any other `dms.Document` operation or snapshot/read | Ignore |
| Any `dms.CdcHeartbeat` operation or Debezium heartbeat | Ignore; advance only the internal source offset |

`dms.DocumentCache.DocumentJson` supplies the caller-agnostic API-shaped upsert payload,
and `dms.DocumentCache.StreamEtag` supplies the DMS-computed ETag for that fixed stream
representation. `dms.Document` supplies the authoritative lifecycle delete and stable
`DocumentUuid`. Cache deletion has no domain meaning.

`dms.DocumentCache` retains `DocumentId` as its compact primary/foreign key and stores
`DocumentUuid` as a non-indexed denormalized connector-key column. Provider-specific cache
insert/update triggers compare that value with `dms.Document.DocumentUuid` through the
existing `DocumentId` primary key and reject a mismatch. Canonical `DocumentUuid` is
immutable, the parent already enforces its uniqueness, and the cache permits one row per
`DocumentId`; therefore both captured sources have the same logical UUID key without a
cache UUID index or a new composite index on the canonical table.

The projector uses the current source/cache difference as both durable work inventory
and completeness evidence. Row-level freshness is exactly `ContentVersion` equality with
the current `dms.Document.ContentVersion`. `LastModifiedAt` remains payload/diagnostic
metadata; `ComputedAt` remains operational metadata. Neither is another freshness test.
A version-equal row is nevertheless ineligible for use while the database singleton
`dms.DocumentCacheState.CacheAheadRecoveryRequired` latch is set.
Frequent candidate discovery uses a disposable process-local content-version cursor;
periodic full source/cache anti-join audits remain the only completeness proof.
The in-process projector uses one serialized loop per target, bounded pages, and a
process-wide target-concurrency gate. Incremental and audit intervals, page size,
concurrency, and maximum ready audit age are configurable with implementation-tuned
defaults; the authoritative design owns their scheduling and coalescing semantics.
For a SQL Server projection target, `READ_COMMITTED_SNAPSHOT ON` is a required provider
prerequisite. Every source/cache classification uses one `READ COMMITTED` statement after
validating that option on the same open target connection. A failed or unreadable validation
stops projection and cache use for that target before classification, so it cannot create a
durable cache-ahead incident from locking `READ COMMITTED`. This does not make RCSI a
requirement for unlisted relational-only SQL Server data stores, and runtime DMS never
changes the database option.

Missing cache rows and rows whose version is behind the canonical source are ordinary
repair work. A cache row whose version is ahead of the canonical source is instead an
invariant violation: the projector atomically sets the durable per-database recovery
latch, makes all cache rows ineligible for reads and further projection writes, makes
readiness false, and neither repairs nor overwrites the row automatically. Supported
same-source writes and monotonic projection cannot produce that state, so it indicates
cache corruption, an in-place source restore/reset, or unsupported reuse of projected
state against another canonical source. A later equal source/cache version never clears
the latch. Internal-only recovery clears the full cache and latch in one transaction and
rebuilds it. If a connector or another ordered downstream consumer may have observed the
higher version, recovery first stops the old publication path and uses a new downstream
state namespace; Kafka CDC uses a new binding generation, topic, consumer state namespace,
and snapshot. The lower canonical version is never published as an in-place correction to
the old namespace.

Cache writes deliberately do not use a source-row commit-order fence. After materialization,
an optimistic current-visibility statement rejects the result if the current source row is
missing or its `ContentVersion` no longer matches the captured version. It must not reuse a
repeatable/snapshot view fixed before hydration. This prevents a source change committed
during multi-result-set reconstitution from producing mixed-version cache content. The
statement requests no update/write lock and carries no lock acquired by that check into the
cache transaction, although ordinary provider read locking may still block briefly. A short
single-document transaction then inserts a missing cache row or updates only a lower cache
`ContentVersion`; it never takes a PostgreSQL `FOR NO KEY UPDATE`, SQL Server `UPDLOCK`, or
another write-conflicting `dms.Document` lock as a commit-order fence. The cache transaction
may still acquire ordinary provider-specific parent-row or key locks through foreign-key
enforcement and the UUID-validation trigger; those integrity locks remain required and are
not the rejected content-version fence. An older coherent materialization may therefore
still commit after a newer canonical version. It cannot replace a higher cache row and
remains ordinary cache-behind work for reconciliation. The cache foreign key remains the
post-delete fence. The same optimistic check and monotonic upsert support ordinary
reconciliation and optional direct fill after relational read fallback.

Initial population, restart, rebuild, and readiness require a full audit. At startup or
restart, the projector captures the initial incremental boundary before that audit and
resumes incremental scanning from exactly that pre-audit key, never from a later maximum
that could skip a post-audit commit. Steady-state catch-up uses the incremental cursor and
the required `dms.Document(ContentVersion, DocumentId)` index. The cursor is never durable
work inventory or readiness evidence because sequence allocation is not transaction
commit order and cache work can appear below it. V1 adds no durable projection queue,
progress/high-watermark, backfill epoch, retry record, or database-backed repair workflow.
The one-bit cache-ahead latch is durable invariant safety state, not work inventory or
completeness evidence. An exact zero finishing audit count is projection completeness at
its observation only when that latch is clear; connector/source-position catch-up is a
separate deployment-owned CDC readiness concern. Because a lower-version transaction can
commit below the cursor after a live audit, initial combined readiness uses a fresh startup
audit while the newly provisioned database is still offline and has never admitted a
canonical mutation. V1 does not implement a cross-replica admission gate or transaction
drain and makes no exact-baseline claim after first-write admission.

API deletion remains independent of projection. It deletes the canonical relational
document and lets the connector derive the tombstone from that delete. It does not wait
for or materialize cache state. A tombstone without a preceding projected upsert is valid
state-stream behavior.

Deployment-owned initial readiness uses the captured heartbeat only to establish a provider
source-position barrier after a fresh startup exact-zero projection audit on that new
offline database. Later
operational status remains eventual-consistency health and does not claim another exact
baseline. PostgreSQL compares the database WAL position with Debezium's committed
completely-processed LSN; SQL Server
compares a post-audit heartbeat change's commit/change LSN position with the committed
connector offset. Running/task state or lag alone is not barrier evidence. The
authoritative design defines the provider capture and comparison algorithms.

The authoritative configuration, reconciliation algorithm, health model, provider
deployment, and readiness sequence are specified in
[Relational CDC and Document Projection](../../../cdc-streaming.md). The public record
contract is specified in
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md).

## Rationale

Canonical relational resource tables are normalized and do not contain the full public
JSON document. `dms.DocumentCache` already provides the stable, caller-agnostic
projection needed for CDC and optional read acceleration, but its lifecycle is unsuitable
for domain deletes because operators must be free to evict or rebuild it.

`dms.Document` already carries stable identity and authoritative lifecycle. Capturing its
deletes alongside cache upserts keeps API mutation correctness independent of projection,
makes cache maintenance safe, and avoids application/Kafka dual-write transactions.

The database difference is sufficient durable repair-work inventory. Every representation
change allocates a monotonic `ContentVersion`, making it an efficient incremental discovery
key, while full audits recover lower versions that commit late or cache rows lost below
the cursor. Timestamp comparison adds provider precision risks without adding correctness.
A monotonic idempotent upsert makes duplicate projectors and restart rediscovery safe;
refusing to lower an ahead cache row preserves the stream's non-null upsert ordering
contract. Because version equality cannot prove that a formerly ahead row now contains the
same canonical state, the singleton latch durably preserves that invariant observation
until explicit recovery.

This choice is conscious: cache-row transitions and consumer-applied non-null upserts are
monotonic, and the stream is eventually convergent, but raw at-least-once Kafka delivery
may contain duplicates or lower-version replays. Because a tombstone has no
`contentVersion`, replay may temporarily place an older upsert after a tombstone; the
subsequent replayed tombstone restores deleted state. V1 guarantees convergence after
connector catch-up, not monotonic applied state across that delete boundary. V1 also does
not guarantee that every cache upsert was canonical-current at its database commit. A
consumer that has not yet seen the newer version may temporarily retain the older
projection. Avoiding the stronger guarantee keeps optional projection and direct fill from
taking source-row locks that can conflict with canonical writers. A future downstream
requirement for linearizable publication requires a separate design and performance
decision.

## Consequences

- V1 projection and CDC support applies only to new physical databases provisioned with the
  completed E18 create-only schema before DMS mutations are admitted. It provides no
  in-place upgrade for an older `dms.DocumentCache` and no later CDC retrofit for an
  already-provisioned database.
- `dms.DocumentCache` is always provisioned. Every deployment-selected Kafka CDC target
  must also be an explicit DMS `DocumentCache:Targets` entry; non-target data stores leave
  the table empty and run no projector.
- A SQL Server `DocumentCache:Targets` entry is projection-eligible only while RCSI is
  validated. Failure disables projection and cache use for that target without gating its
  canonical relational API or unrelated targets; `ALLOW_SNAPSHOT_ISOLATION` is not required
  by the v1 projector.
- Authorization, identity, writes, Change Queries, and correct GET/query results continue
  to use relational sources.
- Reads may use only fresh cache rows while the durable cache-ahead latch is clear and
  always retain relational fallback.
- Projection lag and failure are observable but never gate normal API traffic.
- Initial combined CDC readiness is supported only while a newly provisioned database is
  offline and has not been published to a DMS replica or other writer. First-write admission
  waits through a fresh startup audit and the later provider publication barrier. After
  admission opens, projection and CDC status remain eventual; exact baseline replacement is
  deferred until a separately owned deployment capability can fence replicas and external
  writers and drain admitted work. DMS projection health does not activate such a gate.
- Projector and direct-fill cache writes take no explicit write-conflicting source-row lock
  as a content-version fence. They do not deliberately serialize ordinary canonical
  version updates behind cache commits; ordinary cache-row, trigger, and foreign-key
  concurrency can still block or abort one participant under provider transaction
  semantics, especially during deletion.
- Ordinary updates use indexed incremental discovery; full relationship scans are
  reserved for startup, rebuild, periodic audit, and readiness verification.
- Repair failures retain only capped target-scoped backoff and repair-required state. The
  active page bounds candidate memory, no failed document/version retry map survives page
  turnover, and full audits rediscover failures from the current database difference.
- Full audits repair missing and cache-behind rows. Cache-ahead rows durably latch the
  database, disable cache reads and writes, and keep readiness false across version
  equality and restart until explicit CDC-aware recovery completes.
- Cache clear/rebuild emits no domain tombstones, and consumers order equal-version rows by
  later Kafka offset. A production baseline-replacing rebuild or incompatible-contract
  cutover after first-write admission is deferred until deployment owns the required writer
  fence and drain. An explicitly offline byte-changing repair may use the out-of-band
  representation-restamp utility and publish higher canonical versions eventually without
  certifying another exact CDC baseline. Loss of PostgreSQL WAL/slot or SQL Server CDC
  source-history continuity is never repaired by resnapshotting the existing topic and is
  an unrecoverable terminal condition for that binding in v1.
- Both document source tables use `DocumentUuid` as the connector key and share one
  connector task so a committed upsert preceding canonical deletion retains per-key
  order. The cache key column is non-indexed; its equality and logical uniqueness are
  consequences of the cache-validation trigger, compact `DocumentId` primary/foreign
  key, and canonical UUID uniqueness.
- DMS, not Kafka Connect or a downstream consumer, owns stream ETag derivation; the
  connector copies the projected opaque value into the public message shape.
- Consumers tolerate duplicate/replayed upserts and tombstones without a prior upsert.
  `contentVersion` rejects lower non-null state, while the later per-key partition offset
  replaces an equal-version projection. Across a tombstone, at-least-once replay may
  temporarily restore an older upsert until the replayed tombstone arrives.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Capture only `dms.DocumentCache` | Rejected: projection maintenance would become domain deletion and cache failure would enter the API delete path. |
| Capture only `dms.Document` | Rejected: it has identity and stamps but no reconstituted JSON payload. |
| Capture normalized resource tables directly | Rejected: it exposes physical storage and requires consumers to reproduce joins, extensions, descriptors, and reconstitution. |
| Use Change Queries tables | Rejected: they are a polling compatibility surface, not a complete document-state payload source. |
| Add a relational outbox | Deferred until DMS needs explicit domain-event semantics rather than current document state. |
| Make cache population/use mandatory or describe it only as a read cache | Rejected: the table is always provisioned, while its optional multi-consumer projection role remains configuration-selected. |
| Configure a projector mode or separate Kafka boolean | Rejected: consuming capabilities already determine the exact target set and avoid invalid flag combinations. |
| Persist queues, epochs, progress, retry, or per-document failure rows | Rejected for v1: the current source/cache difference preserves repairable work. The singleton cache-ahead safety latch is the only durable incident state; add pending-work state only if indexed incremental-discovery and full-audit benchmarks require it. |
| Retain process-local retry entries keyed by document/version | Rejected: entry count would grow with persistent failures. Target-scoped backoff limits failure state, and full audits rediscover repairable work from the database. |
| Use the full mismatch anti-join for every steady-state poll | Rejected: it makes ordinary high-version update discovery scale with the complete document set. |
| Build JSON in database triggers | Rejected: it duplicates application reconstitution and increases provider-specific logic. |
| Add `(DocumentId, DocumentUuid)` as a canonical unique key and composite cache foreign key | Rejected: it adds a redundant wide index to the canonical `dms.Document` table for an optional projection. |
| Make cache `DocumentUuid` a primary or unique key | Rejected: it adds a random 16-byte cache index and, on SQL Server, would make a poor clustered insertion key. Trigger-enforced denormalization preserves the compact `DocumentId` key. |
| Trust only the projector to copy the right UUID | Rejected: a defective or unsupported cache writer could publish an upsert and tombstone under different keys. The cache-only validation trigger aborts the write before CDC can observe it. |
| Require synchronous read-through population | Rejected for correctness; direct fill remains an optional monotonic optimization. |
| Add a source-row commit-order fence | Rejected for v1: it would make optional projection take write-conflicting locks on canonical `dms.Document` rows. Monotonic cache upserts, fresh-read validation, reconciliation, consumer version ordering, and the cache foreign key provide the selected eventual-consistency and delete guarantees. |
| Require every cache upsert to be canonical-current at commit | Rejected for v1: a lower captured version may commit after a newer canonical version as ordinary monotonic projection lag. A consumer that has not yet observed the newer version may temporarily retain the lower state until reconciliation publishes the newer projection. |
| Use the incremental cursor, a high-watermark, `ComputedAt`, or `LastModifiedAt` as completeness evidence | Rejected: none proves that every current document is projected at its current representation version. |
| Store retry state on the cache row | Rejected: missing rows have nowhere to store it and operational fields would enter the captured row contract. |
| Automatically overwrite a cache-ahead row with the lower canonical version | Rejected: the state cannot result from supported same-source concurrency and the lower record may be ignored by consumers that retained the higher published version. Explicit recovery distinguishes internal-only cache repair from downstream state reset. |
| Clear a cache-ahead observation when source/cache versions later become equal | Rejected: equality does not prove payload identity and could make corrupt possibly published state appear fresh. The durable latch clears only with the full-cache recovery transaction. |
| Fail normal reads when projection is unhealthy | Rejected: relational fallback preserves API correctness. |
| Treat connector status alone as CDC readiness | Rejected: a running connector cannot supply current documents that remain unprojected. |
| Establish an initial complete baseline under live writes with correlated canonical, audit, and publication boundaries | Deferred for v1: exact initial readiness is limited to a new offline database before first-write admission. |
| Derive tombstones from cache deletes | Rejected: cache and domain lifecycles are intentionally independent. |
| Publish from the API delete path | Rejected: it introduces a distributed write/retry boundary. |
