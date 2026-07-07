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

The projector work unit is:

```text
(DocumentId, target ContentVersion)
```

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

`Etag` is stored with the cache row and must correspond to the cached full-resource
representation, but it is not the primary freshness comparator. `ComputedAt` is
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
3. Reconstitute the caller-agnostic full resource document from relational tables.
4. Compute the full-resource `_etag` and `_lastModifiedDate` using the update-tracking
   rules.
5. Upsert `dms.DocumentCache` only when the target row is still current.

The upsert must be guarded by the current `dms.Document` stamp. If the document's current
`ContentVersion` or `ContentLastModifiedAt` no longer matches the work item, the
projector skips that stale work and lets the newer version be processed. If
`dms.Document` no longer exists, the projector skips the work and must not insert a cache
row.

The implementation may use an in-memory queue, persisted state, or both, but the database
write itself must enforce the stale-write guard so process restarts and retry races do
not corrupt cache state.

## Backfill and Rebuild

Initial backfill is a first-class projector phase. It scans every existing
`dms.Document` row and materializes a fresh `dms.DocumentCache` row for the current
representation stamp.

Backfill must be:

- restartable,
- idempotent,
- safe to run while ordinary writes continue,
- fenced by the same monotonic stale-write guard as normal projection.

For `Projector:Mode = Async`, DMS may serve normal API traffic before backfill completes
because cache misses fall back to relational reconstitution. Health should report
backfill progress and lag.

For `Projector:Mode = CdcRequired`, CDC readiness is false until initial backfill has
completed for every existing `dms.Document` row and no unresolved current projection
failures are known.

Cache truncation or rebuild follows the same rule:

- In non-CDC modes, DMS may truncate/rebuild the cache and rely on read fallback while
  backfill catches up.
- In CDC mode, truncation/rebuild makes CDC not ready. Operators must either quiesce CDC
  expectations until backfill completes again or use a provider-specific resnapshot
  procedure from the CDC runbook.

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
