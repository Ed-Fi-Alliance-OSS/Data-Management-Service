---
status: proposed
date: 2026-07-20
jira: DMS-1246
related:
  - DMS-1245
---

# Decision Record: DocumentCache Projector Freshness and Reconciliation

## Decision

DMS owns the v1 `dms.DocumentCache` projector. It runs as an application-hosted
background reconciler and uses the current relational document set as its durable work
inventory. There are no projection queues, enqueue APIs, persisted cursors, backfill
epochs, or per-document retry records in v1.

The reconciliation query finds current documents whose projection is absent or does not
represent the current version:

```text
dms.Document
LEFT JOIN dms.DocumentCache ON DocumentId
WHERE DocumentCache.DocumentId IS NULL
   OR DocumentCache.ContentVersion <> Document.ContentVersion
ORDER BY Document.ContentVersion, Document.DocumentId
```

The current missing or version-mismatched `dms.Document` row is itself the durable retry
item. A restart reruns the query and rediscovers all remaining work. Database triggers,
database jobs, and external workers are not the v1 projector ownership model.

## Freshness Contract

A cache row is usable only when its monotonic representation stamp matches the current
`dms.Document` row:

```text
DocumentCache.ContentVersion == Document.ContentVersion
```

`ContentVersion` is the sole freshness and reconciliation key. `LastModifiedAt` remains
payload metadata and is not compared again for freshness. This avoids provider
timestamp-precision concerns without losing a distinct correctness guarantee: every
representation change allocates a new `ContentVersion`. `_etag` is not stored with the
cache row. It is composed from `ContentVersion` and the active `variantKey` at the serving
or stream-shaping boundary. `ComputedAt` is operational metadata only; it must not affect
API response semantics, `_etag`, `_lastModifiedDate`, Change Queries, or the Kafka value
contract.

When a cache row is missing or stale:

- GET/query reads fall back to relational reconstitution.
- The read path does not enqueue projection work. The reconciliation loop will discover
  the mismatch from durable database state.
- The read path may directly perform the shared guarded cache upsert after
  reconstitution as an optional optimization, but correctness must not require it.

Authorization and query candidate selection are performed using relational
authorization/query sources before cached JSON is used for response-body assembly.

## Reconciliation Loop

Each effective projection target has one logical reconciliation loop. The loop:

1. Selects a bounded batch of current `dms.Document` rows whose cache row is missing or
   has another `ContentVersion`.
2. Captures the candidate's `(DocumentId, ContentVersion)` freshness stamp and
   `ContentLastModifiedAt` payload metadata.
3. Uses the dedicated cache-projection materializer to reconstitute the caller-agnostic
   full resource document from relational tables.
4. Computes `_lastModifiedDate` using the update-tracking rules and returns one coherent
   projection result containing the cache columns and `DocumentJson`.
5. Validates that `DocumentJson.id` and `DocumentJson._lastModifiedDate` match
   `DocumentUuid` and `LastModifiedAt`.
6. Upserts `dms.DocumentCache` only if `dms.Document` still exists at the captured
   `ContentVersion`.
7. Repeats until no mismatch remains, then polls again after a bounded idle delay.

The cache-internal metadata invariant check is part of projection correctness. If
embedded server metadata and cache columns disagree, the projector logs the failure and
does not write `dms.DocumentCache`. This payload check does not add `LastModifiedAt` to
the source-versus-cache freshness contract.

The database write enforces the stale-write guard. If the source version changed during
materialization, the guarded write is a no-op and the next scan discovers the new
version. If the document was deleted, the guarded write is a no-op and cannot recreate
the cache row. A lower `ContentVersion` can never overwrite a higher cache version.

Failures use bounded in-memory exponential backoff with jitter, keyed by the data store,
`DocumentId`, and current `ContentVersion`. A failed candidate is skipped until its next
attempt time so it cannot hot-loop or starve other candidates. The backoff entry is
dropped when the source version changes, the document is deleted, or the cache becomes
fresh. Structured logs and metrics record the error; no retry classification,
dead-letter state, requeue API, or manual resolution workflow is required.

### Multi-Data-Store and Multi-Instance Supervision

The application-hosted supervisor creates one isolated reconciliation execution context
per loaded `(tenant key, DataStoreId)`. It enumerates tenant-partitioned data-store
configuration independently of JWTs and route qualifiers. Each loop creates non-HTTP
service scopes, explicitly selects its target connection, and uses the shared repository
and materialization services. It must not rely on `ResolveDataStoreMiddleware` or reuse
request-scoped `IDataStoreSelection`.

The supervisor snapshots the effective projection target set at startup. That set is
derived from standalone DocumentCache enablement, read acceleration, and explicit Kafka
CDC targets as defined in
[0001-role-and-enablement.md](0001-role-and-enablement.md). Adding, removing, or changing
a target requires a configuration change and deployment/restart. An unavailable database
delays only its own loop.

When multiple DMS replicas run the projector, deployments may designate projector hosts
to avoid duplicate scans. Correctness does not require a distributed lease: duplicate
reconciliation is safe because candidate discovery is read-only and every write uses the
same idempotent stale-write guard. A persistent coordinator is deferred unless operating
measurements show that duplicate work warrants one.

CDC/Kafka may consume per-data-store reconciliation health for its explicit target list,
but target/source binding and connector reconciliation are not projector responsibilities.
The projector does not manage Kafka Connect.

## Initial Population, Restart, and Rebuild

Initial population, steady-state projection, restart recovery, retry, and rebuild are not
separate projector phases:

- An empty cache makes every current `dms.Document` row a reconciliation candidate.
- Ordinary writes change `ContentVersion` and make the affected row a candidate.
- A process restart rediscovers every remaining mismatch from the same query.
- Cache truncation or row eviction makes the affected current documents candidates.
- Fixing a persistent materialization problem allows the next attempt to succeed without
  repairing workflow state.

There is no bounded backfill epoch or high-watermark. Completeness is established by an
exact anti-join/version-mismatch query over current source rows, not by progress through
the version sequence. In particular, a maximum or
`LastProjectedContentVersion` cannot prove completeness: version 100 may be fresh while
version 99 is still missing.

Whenever projection is selected, normal API traffic continues while mismatches exist
because cache misses fall back to relational reconstitution.

For every entry in `KafkaCdc:Targets`, connector capture is established before projector
writes that must be observed. The entry itself selects projection for that data store.
Projection completeness for CDC is observed when the mismatch count is zero. DMS-1245
then combines that observation with connector snapshot/catch-up and source-position
checks. No persisted projector epoch or cutover marker is needed. A subsequent document
write may temporarily create a new mismatch and is reflected by the same health query.

Cache deletion remains projection maintenance, not domain deletion. The connector ignores
cache deletes; reconciliation publishes guarded upserts for documents that still exist.
Schema reprovisioning must not reuse cache rows across incompatible effective schemas.

## Consequences

- One idempotent reconciliation mechanism covers initial population, ongoing projection,
  restart, rebuild, and retries.
- Durable projector state consists only of canonical `dms.Document` rows and their
  materialized `dms.DocumentCache` rows.
- Upsert projection may lag behind API writes. This is acceptable for read acceleration,
  indexing, and the DMS-1245 Kafka state stream while mismatch count and age are visible
  and consumers use `ContentVersion` as their stale-message guard.
- The projector does not need reverse dependency expansion. Direct changes and indirect
  reference-identity changes already bump the affected document's `ContentVersion`.
- CDC does not make projection synchronous and does not add request-path
  materialization. Canonical deletes are captured independently from `dms.Document`.

Persistent workflow state may be reconsidered only after measurements demonstrate a
specific requirement that the mismatch inventory, bounded in-memory backoff, logs, and
metrics cannot meet.

## Alternatives Considered

### Persist queues, backfill epochs, progress rows, and failure records

Rejected for v1. They duplicate the durable mismatch already represented by
`dms.Document` and `dms.DocumentCache`, split one reconciliation problem into several
workflows, and create repair semantics that are not needed for correctness.

### Build `DocumentJson` in database triggers

Rejected. Trigger-based JSON assembly would duplicate application reconstitution logic,
increase dialect-specific complexity, and make profile/link/etag behavior harder to keep
aligned with GET/query responses.

### Require read-through synchronous cache population

Rejected as a correctness requirement. Direct read-through population may be an
optimization, but GET/query behavior remains correct when cache writes fail, are
disabled, or lag.

### Use a high-watermark as the completeness signal

Rejected. A maximum successfully scanned or projected version records progress but cannot
show that every lower current version has a fresh row. The exact mismatch query is both
the work inventory and the completeness test.

### Use `ComputedAt` as freshness

Rejected. `ComputedAt` is useful for diagnostics, but representation freshness is defined
by the stored DMS `ContentVersion` stamp alone. `LastModifiedAt` is payload metadata.
