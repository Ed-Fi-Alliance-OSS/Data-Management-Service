---
status: proposed
date: 2026-07-20
jira: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1240
  - DMS-1089
---

# Decision Record: Kafka Topic and Message Contract for Relational CDC

## Decision

Relational DMS CDC publishes a compacted document-state stream from two complementary
tables captured by one connector: `dms.DocumentCache` supplies document upserts and
`dms.Document` supplies authoritative deletes.

The v1 public Kafka contract is:

- one document topic per DMS instance,
- one topic for all resource types in that instance,
- Kafka key = `DocumentUuid`,
- Kafka value = an envelope containing `dms.DocumentCache` metadata and the expanded
  `DocumentJson` payload,
- delete = Kafka tombstone for the same `DocumentUuid` key, sourced only from a
  `dms.Document` delete.

The stream is an upsert/delete state stream, not an immutable event log and not the
Change Queries API.

## Topic Naming

Recommended topic pattern:

```text
<topic-prefix>.instance.<instance-key>.documents.v1
```

Default local-only topic prefix:

```text
edfi.dms
```

Example:

```text
edfi.dms.instance.data-store-12.documents.v1
```

`<instance-key>` is a deployment-controlled, Kafka-safe, opaque identifier for one DMS
instance. The recommended default is `data-store-<DataStoreId>` when CMS is the
instance registry. Hosted deployments may use a different stable opaque alias, but the
topic name must not include district names, tenant display names, school years, or other
human-readable sensitive identifiers.

`DataStoreId` is scoped to one CMS deployment; it is not globally unique across separate
CMS installations. Therefore, a production `<topic-prefix>` must include a stable,
Kafka-safe, opaque deployment/environment key that is unique among all DMS deployments
sharing the Kafka cluster. For example:

```text
edfi.deployment-a.prod.dms.instance.data-store-12.documents.v1
```

The short `edfi.dms` prefix is allowed only for an isolated local/test broker. Production
startup or connector generation must reject that default unless the host explicitly
asserts that the Kafka cluster is dedicated to one DMS/CMS deployment. Tenant names must
not be used to provide uniqueness; they remain administrative routing scope and are not
part of the public topic contract.

## Topic Shape

Use one compacted document topic per instance rather than topic-per-resource.

Rationale:

- topic-per-instance matches the multitenancy data-segregation guidance,
- Kafka ACLs can grant a consumer access to exactly one instance stream,
- one topic per instance avoids multiplying topics by every Ed-Fi resource type,
- resource type is already present in each value through `projectName` and `resourceName`,
- consumers can still route to resource-specific downstream stores from the envelope.

Recommended topic configuration:

```text
cleanup.policy=compact
```

Hosts may add delete retention or time retention settings for operational replay windows,
but consumers must not depend on this topic preserving every historical change. It
preserves final document state by key, plus tombstones during the Kafka delete retention
window.

## Message Key

The public Kafka key is the lowercase `D`-format `DocumentUuid` string.

Wire contract:

- key converter: Kafka Connect `org.apache.kafka.connect.storage.StringConverter`,
- key bytes: UTF-8 text for the lowercase `D`-format `DocumentUuid`,
- no JSON quoting,
- no Kafka Connect `schema` / `payload` wrapper.

```text
f81d4fae-7dec-11d0-a765-00a0c91e6bf6
```

`DocumentUuid` is the stable public document identity. `DocumentId` is an internal
surrogate and is not part of the public key or value contract.

Connector and database setup must configure `DocumentUuid` as the custom key for both
captured tables. In particular, a `dms.Document` delete must be keyed by `DocumentUuid`,
not by `DocumentId`. PostgreSQL uses `REPLICA IDENTITY FULL` on `dms.Document` so the
non-primary-key `DocumentUuid` is available to the delete event; SQL Server CDC must
capture that column. The connector deployment design owns the exact pinned-version
configuration.

The connector must not rely on deriving the key only from the unwrapped value, because
delete tombstones have a null value. `DocumentUuid` must be available in the Debezium key
path so the tombstone can carry the correct public key.

## Message Value

Wire contract:

- value converter: Kafka Connect `org.apache.kafka.connect.json.JsonConverter`,
- `value.converter.schemas.enable=false`,
- value bytes for creates, updates, and initial snapshots: UTF-8 JSON object,
- no Kafka Connect `{ "schema": ..., "payload": ... }` wrapper,
- no Avro, Protobuf, or Schema Registry subject contract in v1,
- JSON object field order is not contractual.

For creates, updates, and initial snapshots, the value is a JSON object:

```json
{
  "contractVersion": 1,
  "documentUuid": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
  "projectName": "EdFi",
  "resourceName": "Student",
  "resourceVersion": "5.2.0",
  "contentVersion": 123456,
  "etag": "123456-a1b2c3d4.j._.l.i",
  "lastModifiedAt": "2026-07-06T15:30:45.1234567Z",
  "document": {
    "id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
    "_etag": "123456-a1b2c3d4.j._.l.i",
    "_lastModifiedDate": "2026-07-06T15:30:45.1234567Z"
  }
}
```

Field meanings:

| Field | Source | Contract |
| --- | --- | --- |
| `contractVersion` | connector/static transform | Integer contract version for this topic value shape. |
| `documentUuid` | `dms.DocumentCache.DocumentUuid` | Stable public API document id and Kafka key. |
| `projectName` | `dms.DocumentCache.ProjectName` | MetaEd project name, such as `EdFi`. |
| `resourceName` | `dms.DocumentCache.ResourceName` | Resource name, such as `Student`. |
| `resourceVersion` | `dms.DocumentCache.ResourceVersion` | Project/schema resource version copied from `dms.ResourceKey`. |
| `contentVersion` | `dms.DocumentCache.ContentVersion` | Representation version applied by the projector. |
| `etag` | derived from `dms.DocumentCache.ContentVersion` and the Kafka document-state `variantKey` | Opaque DMS API `_etag` value for the published document-state representation. |
| `lastModifiedAt` | `dms.DocumentCache.LastModifiedAt` | Full-resource `_lastModifiedDate` source value, serialized as a UTC RFC 3339 / ISO-8601 string with up to seven fractional second digits and a trailing `Z`. |
| `document` | `dms.DocumentCache.DocumentJson` | Expanded structured full API resource body, not an escaped JSON string. |

The published field names are lower camel case even though the database column names are
Pascal case. Connector transforms should rename columns after Debezium unwrap.

`contractVersion` is a JSON number. `contentVersion` is a JSON number and consumers must
treat it as a signed 64-bit integer. `documentUuid` must match the Kafka key exactly.

The Kafka document-state `variantKey` uses the same five-component shape as API
`_etag` values: `{schemaEpoch}.j._.{linkFlag}.i` for JSON, no readable profile, the
published document's link mode, and identity content coding. The example uses
`a1b2c3d4.j._.l.i` as an illustrative variant key.

`document` is produced from the cached caller-agnostic, pre-profile, full API resource
body. The value-shaping transform injects the stream-bound `_etag` from the envelope
`etag`, so the published document includes top-level `id`, `_etag`, and
`_lastModifiedDate`; is not profile-filtered; and does not include authorization arrays
or EdOrg hierarchy JSON. When the read plan includes link injection, `document` contains
reference `link` subtrees. DMS does not maintain a second link-free Kafka projection.

The envelope values for `documentUuid`, `etag`, and `lastModifiedAt` are authoritative
stream metadata and must match the embedded `id`, `_etag`, and `_lastModifiedDate` values
inside `document`.

The `etag` value is the DMS API `_etag` string for the Kafka document-state variant. It
is not a hex digest and not an HTTP quoted entity-tag wrapper. Consumers must treat it as
an opaque string and use `contentVersion` for stale-write ordering.

## JSON Expansion

`document` must be published as structured JSON.

When Debezium emits `DocumentJson` as an escaped string, the connector uses the Ed-Fi
owned expand-JSON SMT from DMS-1240 after renaming:

```text
sourceFields=document
```

If a future connector/runtime can publish the JSON column as a structured object without
an SMT, it must still publish the same value contract.

## Create and Update Semantics

A non-null value is an upsert for the message key.

Consumers should apply the value only when it is newer than the state they already hold
for that `documentUuid`. `contentVersion` is the recommended idempotency and stale-write
guard. Duplicate messages and replays are allowed.

Kafka ordering is guaranteed only per partition. Because the key is `DocumentUuid`,
ordering is guaranteed per document, not globally across all documents. Consumers must
not assume global `contentVersion` ordering unless a deployment intentionally uses one
partition and documents that operational tradeoff.

## Delete and Tombstone Semantics

Delete is represented by a Kafka tombstone:

```text
key bytes = UTF-8 lowercase DocumentUuid string
value bytes = Kafka null
```

The v1 document-state topic does not publish a separate `deleted=true` envelope and does
not promise a deleted document body. Consumers that maintain resource-specific downstream
stores must retain enough local state from prior upserts to route the tombstone.

Kafka null is distinct from a JSON document containing `null`. The tombstone record has a
null value at the Kafka record level.

This replaces the legacy KafkaMessaging scenario shape that expected `deleted=false` /
`deleted=true` and an `EdFiDoc` body. DMS-1232 should update or replace those scenarios
to assert the relational v1 contract.

The tombstone is derived only from the authoritative `dms.Document` delete event. The
connector drops the accompanying non-null Debezium delete envelope and emits a Kafka
record-level null value with the event's `DocumentUuid` key. It ignores all
`dms.DocumentCache` deletes, including cascades, truncation/rebuild cleanup, and operator
cache maintenance.

API deletion therefore does not depend on cache presence, freshness, projector health,
or synchronous pre-delete reconstitution. A create followed by delete before the
asynchronous projector writes a cache row may produce a tombstone without a preceding
upsert; that is valid state-stream behavior.

Both tables are captured by the same connector task and routed to this topic with the
same key. A cache upsert committed before a canonical document delete must appear before
the tombstone in the key's partition. Provider E2E tests lock down this same-key ordering
through the routed public topic.

## Security and Authorization

Topic-per-instance isolation is the primary Kafka authorization boundary. Consumer ACLs
should grant access at the instance-topic level.

The message value does not include:

- tenant display name,
- API client identity,
- authorization strategy output,
- EdOrg hierarchy arrays,
- readable-profile-specific projections.

The stream is still sensitive student data. Shared topics with instance filtering are not
part of this contract.

## Non-Contractual Shapes

The v1 public topic must not publish:

- Debezium `before` / `after` / `source` / `op` envelopes,
- Kafka Connect `schema` / `payload` wrappers,
- JSON-quoted keys,
- escaped `DocumentJson` strings,
- Avro, Protobuf, or Schema Registry subjects,
- `deleted=true` delete envelopes,
- legacy `EdFiDoc` payloads.

## Alternatives Considered

### Keep `edfi.dms.document`

Rejected for the relational public contract. It is a single shared topic name and does
not express instance isolation. Local test environments may use simpler aliases during
transition, but implementation stories should target the v1 topic-per-instance naming
contract.

### Topic per resource per instance

Rejected. It gives consumers convenient subscription granularity, but it multiplies topic
count by the full resource inventory for every instance and makes provisioning, ACLs, and
connector transforms more complex. The envelope already carries resource metadata.

### Shared topic with `instanceId` in the value

Rejected. It relies on every consumer filtering correctly and raises cross-instance data
leakage risk. This conflicts with the multitenancy analysis.

### Include `DocumentId` in the public value

Rejected. `DocumentId` is an internal storage surrogate. Including it would invite
consumers to depend on a non-API identifier that can vary across backfills,
reprovisioning, and database restores.

### Publish delete envelopes instead of tombstones

Rejected for v1. The document topic is a compacted state stream. Tombstones are the
standard delete representation for compacted topics and keep sink behavior simple.
Future outbox/event topics can carry richer delete events if needed.

## Follow-up Design Work

DMS-1245 should next define the connector deployment model:

- PostgreSQL and SQL Server source connector settings,
- table include list for `dms.DocumentCache` and `dms.Document`,
- key-column and authoritative `dms.Document` delete-key guarantees for `DocumentUuid`,
- transform order for source-operation filtering, unwrap, field renames, static
  `contractVersion`, JSON expansion, key simplification, document-delete-to-tombstone
  conversion, and topic routing,
- snapshot mode and backfill behavior,
- local Docker Compose/bootstrap registration.

The DMS-1246 decision records in [../document-cache/](../document-cache/) define the
projector guarantees this contract depends on for upserts:

- lag and health signals for projection,
- retry and dead-letter behavior,
- rebuild/backfill semantics that publish upserts only and do not create stale
  lower-`contentVersion` messages after newer values for the same `documentUuid`,
- projector fencing so queued work cannot recreate a cache row after canonical
  `dms.Document` deletion.
