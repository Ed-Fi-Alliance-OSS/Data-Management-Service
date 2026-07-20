---
status: proposed
date: 2026-07-07
jira: DMS-1246
related:
  - DMS-1245
---

# Decision Record: DocumentCache Projector, Freshness, and Backfill

## Decision

DMS owns the v1 `dms.DocumentCache` projector. It runs as an application-hosted
background service, with request-path helpers for enqueueing projection work and for the
CDC-mode delete guarantee described in
[0003-cdc-delete-and-downstream-guarantees.md](0003-cdc-delete-and-downstream-guarantees.md).

The projector uses the same relational reconstitution and materialization pipeline as
GET/query response assembly. Database triggers, database jobs, and external workers are
not the v1 projector ownership model.

Inside one database execution context, the projector work unit is:

```text
(DocumentId, target ContentVersion)
```

At the hosted-process boundary it is:

```text
(tenant key, DataStoreId, DocumentId, target ContentVersion)
```

`DocumentId` and `ContentVersion` are database-local. Every in-memory queue, retry
scheduler, or other state shared across data stores must retain the tenant-scoped data
store identity and dispatch the work into that data store's explicit execution context.
State stored inside the target database does not need redundant tenant or data-store
columns.

Projection is idempotent and monotonic per document. A retry or backfill for a lower
`ContentVersion` must not overwrite a newer cache row, and queued work must not recreate
a cache row after the corresponding `dms.Document` row has been deleted.

## Freshness Contract

A cache row is fresh only when both stored representation stamps match the current
`dms.Document` row:

```text
DocumentCache.ContentVersion == Document.ContentVersion
AND DocumentCache.LastModifiedAt == Document.ContentLastModifiedAt
```

`_etag` is not stored with the cache row. It is composed from `ContentVersion` and the
active `variantKey` at the serving or stream-shaping boundary. `ComputedAt` is
operational metadata only; it must not affect API response semantics, `_etag`,
`_lastModifiedDate`, Change Queries, or the Kafka value contract.

When a cache row is missing or stale:

- GET/query reads must fall back to relational reconstitution.
- The read path may enqueue projection work for the current `(DocumentId,
  ContentVersion)`.
- The read path may synchronously fill the cache after reconstitution as an
  implementation optimization, but correctness must not require that write.

Authorization and query candidate selection must be performed using relational
authorization/query sources before cached JSON is used for response-body assembly.

## Projector Lifecycle

The hosted projector should:

1. Scan `dms.Document` in `ContentVersion` order.
2. Find documents whose cache row is missing or whose cached freshness stamp differs from
   the current `dms.Document` stamp.
3. Use the dedicated cache-projection materializer to reconstitute the caller-agnostic
   full resource document from relational tables.
4. Compute `_lastModifiedDate` using the update-tracking rules and return a coherent
   projection result containing both cache columns and `DocumentJson`.
5. Validate that `DocumentJson.id` and `DocumentJson._lastModifiedDate` match
   `DocumentUuid` and `LastModifiedAt`.
6. Upsert `dms.DocumentCache` only when the target row is still current.

The metadata invariant check is part of projection correctness, not an optional
diagnostic. If embedded server metadata and cache columns disagree, the projector records
a projection failure and does not write `dms.DocumentCache`.

The upsert must be guarded by the current `dms.Document` stamp. If the document's current
`ContentVersion` or `ContentLastModifiedAt` no longer matches the work item, the
projector skips that stale work and lets the newer version be processed. If
`dms.Document` no longer exists, the projector skips the work and must not insert a cache
row.

The implementation may use an in-memory queue, persisted state, or both, but the database
write itself must enforce the stale-write guard so process restarts and retry races do
not corrupt cache state.

### Multi-Instance Supervision

The application-hosted component is a supervisor with one logical projector execution
context per loaded `(tenant key, DataStoreId)`. It enumerates tenant-partitioned data-store
configuration independently of JWTs and route qualifiers. For each unit of work it creates
a non-HTTP service scope, explicitly selects the target `DataStore`/connection, and then
uses the shared repository and materialization services. It must not wait for
`ResolveDataStoreMiddleware` or reuse whichever request-scoped `IDataStoreSelection`
happened to run most recently.

The supervisor periodically refreshes/reconciles the CMS-backed inventory:

- start a projector and initial backfill for a newly discovered data store,
- cancel/drain and replace the execution context when its connection/provider changes,
- leave the execution context unchanged for route-qualifier-only changes,
- stop accepting work and retire the projector when a data store or tenant is removed,
- isolate failures and concurrency limits so one unavailable database cannot stop
  projection for unrelated data stores.

Removal from one refresh is not authority to delete `dms.DocumentCache`, projector state,
Kafka topics, offsets, or database CDC artifacts. Destructive retirement remains an
explicit operator/deployment action. The supervisor publishes inventory and readiness
changes for the separate connector reconciler defined by DMS-1245; it does not manage
Kafka Connect itself.

## Backfill and Rebuild

Initial backfill is a first-class projector phase. It captures a bounded
high-watermark, scans the epoch's document set, and materializes a fresh
`dms.DocumentCache` row for each still-current representation stamp.

At backfill start, DMS must persist a bounded backfill epoch:

- `BackfillEpochId`: a stable identifier for this backfill/rebuild attempt,
- `BackfillTargetContentVersion`: the `max(dms.Document.ContentVersion)` captured when
  the epoch starts,
- `BackfillStartedAt` and `BackfillStatus = Running`.

The initial backfill epoch is responsible only for documents whose current
`ContentVersion` is less than or equal to `BackfillTargetContentVersion`. Writes that
commit after the epoch starts and advance a document beyond that target are handled by
the normal projector catch-up path and the ordinary lag readiness check. They must not
move the epoch target forward, otherwise backfill completion can be starved by ongoing
write traffic.

Backfill must be:

- restartable,
- idempotent,
- safe to run while ordinary writes continue,
- fenced by the same monotonic stale-write guard as normal projection.

A backfill epoch is complete only when:

- every non-deleted current `dms.Document` row with `ContentVersion <=
  BackfillTargetContentVersion` has a fresh `dms.DocumentCache` row,
- documents deleted or updated beyond the target while the epoch was running have been
  skipped or resolved under the same stale-write guard,
- no unresolved current projection failures are known for documents still in the bounded
  epoch set.

The CDC readiness cutover is the point where a completed `(BackfillEpochId,
BackfillTargetContentVersion)` pair becomes the bootstrap boundary. At cutover,
versions at or below the target are covered by the completed bounded epoch; versions
above the target are covered by the live projector catch-up path and its lag threshold.

A process restart resumes the same incomplete epoch and target. Cache truncation,
operator-initiated rebuild, schema reprovisioning, or another explicit reset starts a
new epoch with a newly captured target.

For `Projector:Mode = Async`, DMS may serve normal API traffic before the bounded
backfill epoch completes because cache misses fall back to relational reconstitution.
Health should report backfill progress and lag.

For `Projector:Mode = CdcRequired`, CDC readiness is false until initial backfill has
completed for the bounded epoch, no unresolved current projection failures are known,
and ongoing projector lag for versions above `BackfillTargetContentVersion` is within
the configured readiness threshold.

Cache truncation or rebuild follows the same rule:

- In non-CDC modes, DMS may truncate/rebuild the cache and rely on read fallback while
  the bounded backfill epoch catches up.
- In CDC mode, truncation/rebuild makes CDC not ready. Operators must either quiesce CDC
  expectations until a bounded backfill epoch completes again or use a
  provider-specific resnapshot procedure from the CDC runbook.

Schema reprovisioning must not reuse cache rows across incompatible effective schemas.
The existing `EffectiveSchemaHash` preflight prevents DMS from serving a database with a
mismatched mapping set. A newly provisioned or cleared database starts with an empty
cache and must backfill before CDC readiness can pass.

## Consequences

- Upsert projection may lag behind API writes. This is acceptable for read acceleration,
  indexing, and the DMS-1245 Kafka state stream as long as lag is visible and consumers
  use `ContentVersion` as their idempotency/staleness guard.
- The projector does not need reverse dependency expansion. Direct changes and indirect
  reference-identity changes already bump the affected document's
  `dms.Document.ContentVersion` through the update-tracking/stamping design.
- CDC mode does not make all projection synchronous. Only the delete source-row guarantee
  is synchronous because deletes remove the row Debezium needs for the Kafka tombstone.

## Alternatives Considered

### Build `DocumentJson` in database triggers

Rejected. Trigger-based JSON assembly would duplicate the application reconstitution
logic, increase dialect-specific complexity, and make profile/link/etag behavior harder
to keep aligned with GET/query responses.

### Require read-through synchronous cache population

Rejected as a correctness requirement. Read-through population is a useful optimization,
but GET/query behavior must remain correct when cache writes fail, are disabled, or lag.

### Use `ComputedAt` as freshness

Rejected. `ComputedAt` is useful for diagnostics and age metrics, but representation
freshness is defined by the stored DMS representation stamps: `ContentVersion` and
`ContentLastModifiedAt`.
