---
status: proposed
date: 2026-07-20
jira: DMS-1246
related:
  - DMS-1245
  - DMS-1232
---

# Decision Record: Cache and Domain Lifecycle Separation

## Decision

`dms.DocumentCache` is rebuildable projected state. Deleting a cache row means only that
the projection was removed; it must never represent deletion of the domain document.

DMS-1245 owns a single Debezium connector with this source-operation mapping:

| Source event | Public document-state result |
| --- | --- |
| `dms.DocumentCache` create, update, or snapshot/read | Document upsert |
| `dms.Document` delete | Kafka tombstone |
| `dms.DocumentCache` delete or truncate | Ignore |
| Any other `dms.Document` operation or snapshot/read | Ignore |

DMS-1246 owns only the asynchronous cache projection. It does not add CDC-specific
materialization, locking, readiness checks, or failure behavior to the API delete path.

## Delete Path Boundary

A supported API delete continues to use canonical relational sources:

1. Resolve and authorize the target document.
2. Delete the concrete resource row, or `dms.Descriptor` row for a descriptor, while
   `dms.Document` still exists so Change Queries can record its own tombstone.
3. Delete `dms.Document` to finalize the authoritative lifecycle and cascade relational
   cleanup, including any `dms.DocumentCache` row.

The connector derives the Kafka tombstone from step 3. It drops the cache cascade event.
The API transaction does not:

- verify that a cache row exists,
- check cache freshness,
- synchronously reconstitute a pre-delete document,
- acquire a CDC-specific per-document lock,
- wait for projector readiness,
- fail because cache projection is missing, stale, rebuilding, or unhealthy.

This boundary also applies when create is followed immediately by delete before the
asynchronous projector writes a cache row. The authoritative delete still produces a
tombstone. A state-stream consumer must tolerate a tombstone for a key whose upsert it did
not observe.

## Projector Fencing

Projection, backfill, retry, and read-through cache writes use the same monotonic guard:

- a lower `ContentVersion` cannot overwrite a higher cache version,
- the target `dms.Document` row and representation stamp must still exist at cache-write
  time,
- a work item for a deleted `DocumentId` cannot recreate `dms.DocumentCache`,
- a stale retry either no-ops or requeues the current document stamp.

These are general projection-correctness rules. They do not serialize the API delete with
materialization and do not create a separate CDC write path.

## Rebuild and Maintenance Semantics

Cache truncation, row eviction, operator cleanup, and rebuild are projection operations:

- cache deletes are filtered before public topic routing,
- no cache maintenance operation publishes a domain tombstone,
- rebuild backfill emits upserts for canonical documents that still exist,
- cache-backed reads fall back to relational reconstitution while rows are absent or
  stale,
- CDC projection readiness may be false until the new bounded backfill epoch completes,
  but API reads and mutations continue normally.

If an operator intentionally needs to rebuild the compacted public topic, that is an
explicit connector snapshot/topic recovery procedure. It must not be simulated by
deleting cache rows.

## Downstream Observations

For creates and updates:

- a non-null Kafka value means upsert current projected state for `DocumentUuid`,
- projection may lag behind the API write,
- duplicate/replayed upserts are allowed,
- `contentVersion` is the consumer stale-message guard.

For deletes:

- a record-level null value keyed by `DocumentUuid` means the canonical
  `dms.Document` row was deleted,
- the stream does not publish a separate `deleted=true` envelope or deleted body,
- consumers that route tombstones by resource type retain enough state from prior
  upserts to route the delete.

Both source tables flow through the same connector task and route to the same topic with
the same `DocumentUuid` key. DMS-1245 provider E2E coverage must prove that a cache upsert
committed before a canonical delete appears before its tombstone in that key's routed
topic partition.

## Consequences

- Optional projection failure cannot cause an API delete to fail.
- Cache truncation/rebuild cannot look like mass domain deletion.
- DMS does not need CDC-specific per-document locks, pre-delete reconstitution, delete
  materialization telemetry, or provider materialize-then-delete verification.
- The DocumentCache epic can use one asynchronous enabled projector mode for reads,
  indexing, and CDC upserts.
- Provider-specific delete key, filtering, routing, tombstone, and ordering tests belong
  to the CDC/Kafka epic because they exercise `dms.Document`, Debezium, and Kafka rather
  than cache materialization.

## Alternatives Considered

### Derive tombstones from `dms.DocumentCache` deletes

Rejected. It conflates projection lifecycle with domain lifecycle and forces an optional
projection into the synchronous delete transaction.

### Publish deletes from Change Queries tables

Rejected for the v1 Kafka stream. Change Queries are a polling API compatibility surface
with a separate contract.

### Write directly to Kafka from the API delete path

Rejected. It would split delivery between database-log events and application side
effects, introducing distributed retry and transaction semantics.
