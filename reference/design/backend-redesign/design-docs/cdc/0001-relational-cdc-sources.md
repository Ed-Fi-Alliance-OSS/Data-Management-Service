---
status: proposed
date: 2026-07-20
jira: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1089
---

# Decision Record: Relational CDC Sources for Kafka

## Decision

When Debezium/Kafka CDC is enabled for the relational DMS backend, one source connector
captures two complementary tables:

| Source event | Public document-state result |
| --- | --- |
| `dms.DocumentCache` create, update, or snapshot/read | Document upsert |
| `dms.Document` delete | Kafka tombstone |
| `dms.DocumentCache` delete or truncate | Ignore |
| Any other `dms.Document` operation or snapshot/read | Ignore |

`dms.DocumentCache` is the payload source. Its `DocumentJson` and denormalized metadata
produce the public non-null Kafka value. It is not the authority for document existence.

`dms.Document` is the authoritative lifecycle source. It already contains the stable
`DocumentUuid`, so Debezium key-column configuration uses that column for the delete key.
Capturing this table does not make it a payload source; all `dms.Document` creates,
updates, and snapshot records are filtered out.

`dms.DocumentCache` remains optional when CDC/Kafka is not enabled. When CDC is enabled,
it and its asynchronous projector are required to produce document upserts, but cache
health never participates in the supported API delete transaction. A missing, stale, or
unhealthy cache row does not block deletion of the canonical document.

## Context

The relational backend stores canonical resource state in per-resource relational tables.
`dms.Document` carries identity, resource type, ownership, representation stamps, and the
authoritative document lifecycle, but it does not carry reconstituted JSON.

`dms.DocumentCache` is an optional, eventually consistent materialized JSON projection
intended for read acceleration, downstream indexing, and CDC streaming. Its
`DocumentJson` column contains the caller-agnostic, pre-profile, full API resource body.
It is rebuildable projected state, so deleting a cache row means only that projected state
was removed. Treating that physical deletion as domain deletion conflates two different
lifecycles and makes cache maintenance unsafe.

The previous design captured only `dms.DocumentCache`. It therefore required every API
delete to synchronously verify or materialize a cache row before deleting
`dms.Document`, solely so the cache cascade would produce a CDC tombstone. That made API
deletes depend on an optional projection and made cache truncation/rebuild indistinguishable
from mass domain deletion.

Debezium's PostgreSQL and SQL Server connectors support delete events, delete tombstones,
and custom message-key columns:

- [PostgreSQL connector](https://debezium.io/documentation/reference/stable/connectors/postgresql.html)
- [SQL Server connector](https://debezium.io/documentation/reference/stable/connectors/sqlserver.html)

Change Queries remain a separate polling API surface based on `ContentVersion`,
`ContentLastModifiedAt`, and `tracked_changes_*` tables. They are not the Debezium/Kafka
streaming source.

## Consequences

- Enabling relational CDC/Kafka provisions `dms.DocumentCache`, its projector, and CDC
  capture for both `dms.DocumentCache` and `dms.Document` in the same connector.
- Kafka consumers observe caller-agnostic cached API bodies for upserts and authoritative
  `dms.Document` lifecycle deletes for tombstones.
- Cache deletion, truncation, rebuild, and schema reprovisioning do not publish public
  document tombstones. A rebuild publishes only new cache snapshot/create/update upserts
  for documents that still exist.
- API delete follows the ordinary relational and Change Queries ordering. It does not
  acquire a CDC-specific per-document lock, reconstitute a pre-delete document, verify
  cache freshness, or fail because projection is unhealthy.
- The connector uses `DocumentUuid` as the custom key for both captured tables, routes
  both source topics to the same instance document topic, and preserves one-partition
  ordering for a document key.
- PostgreSQL must expose `dms.Document.DocumentUuid` in delete records. Because the custom
  key is not the table primary key, the CDC setup uses `REPLICA IDENTITY FULL` for
  `dms.Document` unless the pinned connector/database combination proves an equally safe
  replica-identity configuration.
- SQL Server CDC must capture `dms.Document.DocumentUuid` and configure it as the custom
  message key.
- Source-aware filtering happens before final topic routing: cache create/update/snapshot
  records become upserts; document deletes become record-level null tombstones; all other
  captured operations are dropped.
- Because both event classes flow through one connector task and are routed with the same
  key, a committed cache upsert that precedes a canonical delete is appended before that
  delete's tombstone for the key. PostgreSQL and SQL Server E2E tests must prove this
  through the routed public topic.
- Create followed by delete before asynchronous projection may legitimately produce only
  a tombstone. Consumers already treat the topic as an upsert/delete state stream and
  must tolerate a delete for a key whose upsert they did not observe.
- Projector and backfill writes remain fenced by the current `dms.Document` row and its
  representation stamp. This prevents lower-version overwrites and prevents projection
  work from recreating cache state after canonical deletion; it is ordinary projector
  correctness, not a special delete-path contract.
- CDC readiness may report initial backfill, projector failures/lag, connector status,
  and source-binding drift. These signals are observational and never change normal API
  routing or mutation behavior.

## Alternatives Considered

### Capture only `dms.DocumentCache`

Rejected. It makes projected-state deletion stand in for domain deletion, requires
synchronous pre-delete cache materialization, lets projector failure block API deletes,
and turns cache truncation/rebuild into public mass deletion.

### Capture only `dms.Document`

Rejected as the complete stream source. `dms.Document` does not contain the JSON payload.
It is intentionally used only for authoritative lifecycle deletes.

### Capture every resource table directly

Rejected. This exposes the physical relational storage model instead of a stable document
contract and requires consumers to understand per-resource tables, child joins,
extensions, descriptors, and reconstitution rules.

### Use Change Queries tables as the Kafka source

Rejected. Change Queries are a polling API compatibility surface for `/deletes`,
`/keyChanges`, and live resource filters, not a complete document-state payload source.

### Add a relational outbox as the primary source

Deferred. An outbox remains appropriate if DMS later needs explicit domain events with a
different taxonomy or payload. The two-table Debezium design is smaller for the v1
document-state stream because it combines the existing materialized payload with the
existing authoritative lifecycle row.

## Follow-up Design Work

DMS-1245 should implement:

- one connector and table include list covering `dms.DocumentCache` and `dms.Document`,
- provider-specific `DocumentUuid` key setup, including PostgreSQL replica identity,
- source-table and operation filtering before topic routing,
- the Kafka key/value/tombstone contract,
- provider E2E proof of same-key ordering through the routed topic,
- local Docker Compose/bootstrap connector registration,
- replacement of the quarantined KafkaMessaging scenarios.

DMS-1246 owns only the reusable `dms.DocumentCache` projection behavior: projector
lifecycle, initial backfill and rebuild, freshness, stale-write fencing, retry,
dead-letter handling, telemetry, and optional cache-backed reads. It does not add a
CDC-specific pre-delete path or provider materialize-then-delete contract.
