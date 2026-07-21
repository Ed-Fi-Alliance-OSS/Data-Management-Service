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

One Debezium connector uses two complementary sources:

| Source event | Public document-state result |
| --- | --- |
| `dms.DocumentCache` create, update, or snapshot/read | Document upsert |
| `dms.Document` delete | Kafka tombstone |
| `dms.DocumentCache` delete or truncate | Ignore |
| Any other `dms.Document` operation or snapshot/read | Ignore |

`dms.DocumentCache.DocumentJson` supplies the caller-agnostic API-shaped upsert payload,
and `dms.DocumentCache.StreamEtag` supplies the DMS-computed ETag for that fixed stream
representation. `dms.Document` supplies the authoritative lifecycle delete and stable
`DocumentUuid`. Cache deletion has no domain meaning.

The projector uses the current source/cache difference as both durable work inventory
and completeness evidence. A cache row is fresh exactly when its `ContentVersion` equals
the current `dms.Document.ContentVersion`. `LastModifiedAt` remains payload/diagnostic
metadata; `ComputedAt` remains operational metadata. Neither is another freshness test.
Frequent candidate discovery uses a disposable process-local content-version cursor;
periodic full source/cache anti-join audits remain the only completeness proof.
The in-process projector uses one serialized loop per target, bounded pages, and a
process-wide target-concurrency gate. Incremental and audit intervals, page size,
concurrency, and maximum ready audit age are configurable with implementation-tuned
defaults; the authoritative design owns their scheduling and coalescing semantics.

Missing cache rows and rows whose version is behind the canonical source are ordinary
repair work. A cache row whose version is ahead of the canonical source is instead an
invariant violation: the projector reports it, makes readiness false, and neither retries
nor overwrites it automatically. Supported same-source writes and guarded projection
cannot produce that state, so it indicates cache corruption, an in-place source
restore/reset, or unsupported reuse of projected state against another canonical source.
Internal-only recovery deletes the incompatible projected row and rebuilds it. If a
connector or another ordered downstream consumer may have observed the higher version,
recovery uses a new downstream state namespace; Kafka CDC uses a new binding generation,
topic, consumer state namespace, and snapshot. The lower canonical version is never
published as an in-place correction to the old namespace.

All cache writes use a strong commit-order fence: after materialization, a short
single-document transaction locks the current `dms.Document` row with a lock that
conflicts with canonical version updates, verifies equality with the captured
`ContentVersion` on the locked current row, and retains that lock through the monotonic
cache upsert and commit. This prevents an older result from committing after a newer
canonical version, replacing a newer cache row, or recreating cache state after canonical
deletion. The same guard supports ordinary reconciliation and optional direct fill after
relational read fallback. The authoritative design specifies the PostgreSQL and SQL
Server locking semantics.

Initial population, restart, rebuild, and readiness require a full audit. At startup or
restart, the projector captures the initial incremental boundary before that audit and
resumes incremental scanning from exactly that pre-audit key, never from a later maximum
that could skip a post-audit commit. Steady-state catch-up uses the incremental cursor and
the required `dms.Document(ContentVersion, DocumentId)` index. The cursor is never durable
work inventory or readiness evidence because sequence allocation is not transaction
commit order and cache work can appear below it. V1 adds no durable projection queue,
progress/high-watermark, backfill epoch, failure table, or database-backed repair
workflow. An exact zero finishing audit count is projection completeness at its
observation; connector/source-position catch-up is a separate deployment-owned CDC
readiness concern.

API deletion remains independent of projection. It deletes the canonical relational
document and lets the connector derive the tombstone from that delete. It does not wait
for or materialize cache state. A tombstone without a preceding projected upsert is valid
state-stream behavior.

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

The database difference is sufficient durable projector state and invariant evidence.
Every representation change allocates a monotonic `ContentVersion`, making it an
efficient incremental discovery key, while full audits recover lower versions that
commit late or cache rows lost below the cursor. Timestamp comparison adds provider
precision risks without adding correctness. A guarded idempotent upsert makes duplicate
projectors and restart rediscovery safe; refusing to lower an ahead cache row preserves
the stream's monotonic consumer contract.

## Consequences

- Every deployment-selected Kafka CDC target must also be an explicit DMS
  `DocumentCache:Targets` entry, but non-target data stores may run without
  `dms.DocumentCache`.
- Authorization, identity, writes, Change Queries, and correct GET/query results continue
  to use relational sources.
- Reads may use only fresh cache rows and always retain relational fallback.
- Projection lag and failure are observable but never gate normal API traffic.
- Guarded writes add one short per-document source-row lock after materialization.
  Different documents remain concurrent; a hot canonical writer or duplicate projector
  may wait through only the guarded cache upsert and commit. V1 does not hold source-row
  locks during materialization or across a batch of candidates.
- Ordinary updates use indexed incremental discovery; full relationship scans are
  reserved for startup, rebuild, periodic audit, and readiness verification.
- Full audits repair missing and cache-behind rows. Cache-ahead rows are invariant
  violations that keep readiness false until explicit CDC-aware recovery completes.
- Cache clear/rebuild emits no domain tombstones. A compatible projection correction
  rebuilds into the existing topic and publishes equal-version rows at later offsets; an
  intentional topic rebuild for an incompatible contract uses connector snapshot/topic
  recovery.
- Both source tables use `DocumentUuid` as the connector key and share one connector task
  so a committed upsert preceding canonical deletion retains per-key order.
- DMS, not Kafka Connect or a downstream consumer, owns stream ETag derivation; the
  connector copies the projected opaque value into the public message shape.
- Consumers tolerate duplicate/replayed upserts and tombstones without a prior upsert.
  `contentVersion` rejects lower canonical state, while the later per-key partition offset
  replaces an equal-version projection.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Capture only `dms.DocumentCache` | Rejected: projection maintenance would become domain deletion and cache failure would enter the API delete path. |
| Capture only `dms.Document` | Rejected: it has identity and stamps but no reconstituted JSON payload. |
| Capture normalized resource tables directly | Rejected: it exposes physical storage and requires consumers to reproduce joins, extensions, descriptors, and reconstitution. |
| Use Change Queries tables | Rejected: they are a polling compatibility surface, not a complete document-state payload source. |
| Add a relational outbox | Deferred until DMS needs explicit domain-event semantics rather than current document state. |
| Make the cache mandatory or only a read cache | Rejected: both descriptions lose its optional multi-consumer projection role. |
| Configure a projector mode or separate Kafka boolean | Rejected: consuming capabilities already determine the exact target set and avoid invalid flag combinations. |
| Persist queues, epochs, progress, retry, or failure rows | Rejected for v1: the current source/cache difference preserves repairable work and invariant evidence; add a small pending-work table or flag only if indexed incremental-discovery and full-audit benchmarks require it. |
| Use the full mismatch anti-join for every steady-state poll | Rejected: it makes ordinary high-version update discovery scale with the complete document set. |
| Build JSON in database triggers | Rejected: it duplicates application reconstitution and increases provider-specific logic. |
| Require synchronous read-through population | Rejected for correctness; direct fill remains an optional guarded optimization. |
| Allow a stale materialization to commit when only the cache version is monotonic | Rejected: read freshness and reconciliation would preserve API correctness, but CDC could capture an old cache upsert after a newer canonical version commits, contradicting the selected stale-write fence. |
| Use the incremental cursor, a high-watermark, `ComputedAt`, or `LastModifiedAt` as completeness evidence | Rejected: none proves that every current document is projected at its current representation version. |
| Store retry state on the cache row | Rejected: missing rows have nowhere to store it and operational fields would enter the captured row contract. |
| Automatically overwrite a cache-ahead row with the lower canonical version | Rejected: the state cannot result from supported same-source concurrency and the lower record may be ignored by consumers that retained the higher published version. Explicit recovery distinguishes internal-only cache repair from downstream state reset. |
| Fail normal reads when projection is unhealthy | Rejected: relational fallback preserves API correctness. |
| Treat connector status alone as CDC readiness | Rejected: a running connector cannot supply current documents that remain unprojected. |
| Derive tombstones from cache deletes | Rejected: cache and domain lifecycles are intentionally independent. |
| Publish from the API delete path | Rejected: it introduces a distributed write/retry boundary. |
