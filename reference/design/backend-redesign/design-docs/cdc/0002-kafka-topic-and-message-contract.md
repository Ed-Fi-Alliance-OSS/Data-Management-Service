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

Relational DMS CDC publishes one compacted document-state topic per DMS instance. It is
an upsert/delete state stream, not an immutable event log and not the Change Queries API.
The source decision is recorded in
[0001-relational-cdc-projector-and-sources.md](0001-relational-cdc-projector-and-sources.md);
deployment and transform details are in
[Relational CDC and Document Projection](../../../cdc-streaming.md).

The v1 public contract is:

- one topic for all resource types in one instance,
- Kafka key = lowercase `D`-format `DocumentUuid`,
- non-null value = lower-camel metadata envelope plus expanded `DocumentJson`,
- delete = Kafka record-level null tombstone for the same key.

## Topic

```text
<topic-prefix>.instance.<instance-key>.documents.v1
```

`<instance-key>` is a stable, deployment-controlled, Kafka-safe opaque identifier. The
recommended CMS-backed default is `data-store-<DataStoreId>`. It must not contain
district names, tenant display names, school years, or other human-readable sensitive
identifiers.

`DataStoreId` is not globally unique across CMS deployments. A production
`<topic-prefix>` therefore includes a stable opaque deployment/environment key unique
among every DMS deployment sharing the Kafka cluster, for example:

```text
edfi.deployment-a.prod.dms.instance.data-store-12.documents.v1
```

The short `edfi.dms` prefix is allowed only on an isolated local/test broker or one
explicitly dedicated to a single DMS/CMS deployment. Production validation rejects that
default otherwise. Tenant names are not a uniqueness mechanism.

The topic uses:

```text
cleanup.policy=compact
```

Hosts may add delete or time retention for operational replay windows, but consumers do
not depend on complete history. Tombstones remain observable only according to Kafka's
configured delete retention.

Topic-per-instance supplies the Kafka authorization boundary, bounds topic count, and
keeps resource routing in message metadata. Shared cross-instance topics and
topic-per-resource are not the v1 contract.

## Key

The public key is UTF-8 lowercase `DocumentUuid` text with no JSON quoting or Kafka
Connect schema wrapper:

```text
f81d4fae-7dec-11d0-a765-00a0c91e6bf6
```

The connector uses:

```text
org.apache.kafka.connect.storage.StringConverter
```

`DocumentUuid` is the stable public document identity. Internal `DocumentId` is not part
of the public key or value. The key originates in the Debezium key path for both captured
tables; it cannot be derived only from an unwrapped value because delete values are null.

## Upsert Value

The connector uses
`org.apache.kafka.connect.json.JsonConverter` with
`value.converter.schemas.enable=false`. Creates, updates, and snapshots produce a UTF-8
JSON object without a Kafka Connect `schema` / `payload` wrapper. Field order is not
contractual; Avro, Protobuf, and Schema Registry subjects are outside v1.

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

| Field | Contract |
| --- | --- |
| `contractVersion` | JSON number `1` for this value shape |
| `documentUuid` | Stable public API document id; exactly matches the Kafka key |
| `projectName` | MetaEd project name from the cache row |
| `resourceName` | Resource name from the cache row |
| `resourceVersion` | Project/schema resource version copied from `dms.ResourceKey` |
| `contentVersion` | Signed 64-bit representation version used for idempotency and stale-write ordering |
| `etag` | Opaque DMS API `_etag` for the published stream representation |
| `lastModifiedAt` | UTC RFC 3339/ISO-8601 timestamp with up to seven fractional digits and trailing `Z` |
| `document` | Expanded structured full API resource body, never an escaped JSON string |

Database Pascal-case columns are renamed to lower camel case. `contractVersion` and
`contentVersion` are JSON numbers.

The stream `variantKey` uses the API five-component shape
`{schemaEpoch}.j._.{linkFlag}.i`: JSON, no readable profile, the published document's link
mode, and identity content coding. `etag` is composed from `contentVersion` and that
variant key. It is not a digest, an HTTP quoted entity-tag, or a value read from a cache
`Etag` column. Consumers treat it as opaque and use `contentVersion` for ordering.

`document` is caller-agnostic, pre-profile full resource JSON. It includes `id`, the
stream `_etag`, and `_lastModifiedDate`. When the compiled read plan injects reference
links, it contains those `link` subtrees; DMS does not maintain another link-free Kafka
projection. It excludes authorization arrays, EdOrg hierarchy JSON, API client identity,
and readable-profile-specific output.

Envelope `documentUuid`, `etag`, and `lastModifiedAt` exactly match embedded `id`,
`_etag`, and `_lastModifiedDate`. If Debezium exposes `DocumentJson` as a string, the
DMS-1240 Ed-Fi expand-JSON SMT runs with `sourceFields=document`; another implementation
may replace that SMT only while preserving the identical structured contract.

A non-null value is an upsert. Duplicates, replays, and snapshots are allowed. Consumers
apply a record only when its `contentVersion` is newer than the state they retain for the
key. Kafka ordering is per partition and therefore per keyed document, not global
`contentVersion` order.

## Delete

A delete is:

```text
key bytes = UTF-8 lowercase DocumentUuid
value bytes = Kafka null
```

Kafka null is not a JSON document containing `null`. V1 publishes no `deleted=true`
envelope and promises no deleted document body. Resource-specific consumers retain
enough prior local state to route a tombstone.

The authoritative `dms.Document` delete produces the tombstone. Cache deletion,
cascade, truncation, rebuild, and cleanup produce no public record. A canonical delete
may therefore produce a tombstone without a preceding upsert. When a cache upsert commits
before canonical deletion, both records use the same key and connector task so the
upsert precedes the tombstone in that key's routed partition.

## Security and Exclusions

Consumer ACLs grant access at the instance-topic level. The stream contains sensitive
student data even though it excludes tenant display name, API client identity,
authorization results, EdOrg hierarchy arrays, and readable-profile projections.

The public topic never exposes:

- Debezium `before`, `after`, `source`, or `op` envelopes,
- Kafka Connect `schema` / `payload` wrappers,
- JSON-quoted keys or escaped `DocumentJson`,
- internal `DocumentId` or operational `ComputedAt`,
- Avro, Protobuf, or Schema Registry subjects,
- legacy `EdFiDoc` or `deleted=true` shapes.

## Consequences

- Consumers can reconstruct current instance document state but not complete history.
- One ACL protects one instance while resource metadata supports downstream routing.
- Public identity and per-document ordering survive canonical deletion because the key
  is independent of the value.
- DMS-1232 KafkaMessaging coverage must replace the shared-topic,
  `deleted=false`/`deleted=true`, and `EdFiDoc` expectations.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Shared `edfi.dms.document` topic | Rejected: it does not express instance isolation. |
| Topic per resource per instance | Rejected: it multiplies topics, ACLs, provisioning, and routing transforms while the envelope already identifies the resource. |
| Shared topic with `instanceId` in the value | Rejected: consumer filtering is not a security boundary and tombstones have no value. |
| Include `DocumentId` | Rejected: it is an internal surrogate with no public contract role. |
| Publish delete envelopes | Rejected: compacted state streams use Kafka tombstones and no deleted body is guaranteed. |
