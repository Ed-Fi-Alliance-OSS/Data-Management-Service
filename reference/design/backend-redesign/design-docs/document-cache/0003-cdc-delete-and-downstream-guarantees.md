---
status: proposed
date: 2026-07-07
jira: DMS-1246
related:
  - DMS-1245
  - DMS-1232
---

# Decision Record: CDC Delete and Downstream Guarantees

## Decision

DMS-1245 owns the relational Kafka topic, key, envelope, connector, and tombstone
contract. DMS-1246 owns the `dms.DocumentCache` source guarantees that make that
contract implementable.

When `DataManagement:KafkaCdc:Enabled = true`, `dms.DocumentCache` is the required
Debezium source table. DMS does not use Change Queries, `dms.Document`, normalized
resource tables, or request-path Kafka dual writes as an alternate source for the v1
document-state stream.

CDC mode keeps ordinary create/update projection asynchronous, but it makes delete source
materialization mandatory. DMS must not delete `dms.Document` unless the delete path can
prove that Debezium will observe a `dms.DocumentCache` row delete keyed by
`DocumentUuid`.

## Delete Path Guarantee

For a supported CDC-mode API delete:

1. Resolve and authorize the target document using canonical relational sources.
2. Serialize same-document write/projector activity with a row lock, advisory lock, or
   equivalent per-document fence.
3. Before the resource row is removed, verify that a fresh `dms.DocumentCache` row exists
   for the target `DocumentId`.
4. If the row is missing or stale, synchronously reconstitute the current pre-delete
   representation and upsert the cache row under the same freshness and stale-write
   guard used by the projector.
5. If source-row verification/materialization fails, fail the API delete with a
   retryable server-side error.
6. Delete the concrete resource row, or the `dms.Descriptor` row for descriptors, so the
   update-tracking delete trigger can write the Change Queries tombstone while
   `dms.Document` still exists.
7. Delete the `dms.Document` row. The `ON DELETE CASCADE` path removes
   `dms.DocumentCache`; that row delete is the Debezium event that the connector turns
   into the Kafka tombstone.

Steps 3 through 7 are part of the supported delete operation. In non-CDC modes, missing
or stale cache rows do not block deletes.

Provider-specific implementation must prove that the selected database and Debezium
configuration emit the required `dms.DocumentCache` delete when a missing cache source
row is materialized during the delete operation. If a provider suppresses the needed
logical change for a materialize-then-delete sequence, CDC readiness for that provider
must fail until the implementation uses a provider-specific alternative, such as
requiring a committed source row before accepting CDC-mode deletes or adding another
durable source-row mechanism.

## Stale-Write and Post-Delete Fencing

Projection, backfill, retry, and CDC pre-delete materialization must all use the same
monotonic guard:

- A lower `ContentVersion` must not overwrite a higher `ContentVersion` cache row.
- A work item for a deleted `DocumentId` must not recreate `dms.DocumentCache`.
- A stale retry must either no-op or requeue the current `dms.Document` stamp.
- A CDC pre-delete materialization must write the current pre-delete stamp, not an older
  queued projection result.

The simplest enforcement model is an insert/update that joins to the current
`dms.Document` row and checks the target `ContentVersion` and
`ContentLastModifiedAt` before writing `dms.DocumentCache`.

## Downstream Observations

CDC consumers observe projection state, not original resource-table write intent.

For creates and updates:

- a non-null Kafka value means upsert current state for the `DocumentUuid`,
- projection may lag behind the API write that caused it,
- duplicate/replayed upserts are allowed,
- `contentVersion` is the consumer-side idempotency and stale-message guard,
- ordering is guaranteed only per Kafka key/partition, not globally across documents.

For deletes:

- the public Kafka delete signal is a record-level tombstone with the same
  `DocumentUuid` key,
- the v1 stream does not publish a separate `deleted=true` value and does not guarantee a
  deleted document body,
- consumers that route tombstones by resource type must retain enough local state from
  prior upserts to route the delete,
- when the cache was missing or stale immediately before delete, consumers may observe a
  catch-up upsert for the pre-delete state before the tombstone if the provider emits
  both changes.

The topic remains a compacted document-state stream. It is not a complete event-history
log, and consumers must not infer user intent from the number of projection writes.

## Consequences

- CDC-mode deletes can be temporarily blocked by projector/materialization failures. This
  is intentional because completing the delete without a source-row delete would silently
  lose the Kafka tombstone.
- The pre-delete materialization step runs before the resource row is removed because
  full document reconstitution is not possible afterward.
- Normal API correctness remains independent of Kafka. The blocking behavior applies only
  when Kafka CDC is enabled and advertised as supported for the instance.
- The implementation epic must include PostgreSQL and SQL Server tests proving
  create/update projection, missing-cache delete, stale-cache delete, and tombstone key
  behavior.

## Alternatives Considered

### Publish deletes from Change Queries tables

Rejected for the v1 Kafka CDC stream. Change Queries are a polling API compatibility
surface and do not contain the materialized document body used by the DMS-1245 Kafka
contract.

### Allow CDC deletes to succeed without a cache row

Rejected. Debezium captures `dms.DocumentCache`; if no row is deleted from that table,
the connector has no reliable source event for the public Kafka tombstone.

### Add request-path writes directly to Kafka

Rejected. It would split the stream between database-log events and application side
effects, creating new delivery, retry, transaction, and replay semantics outside the
DMS-1245 Debezium/Kafka design.
