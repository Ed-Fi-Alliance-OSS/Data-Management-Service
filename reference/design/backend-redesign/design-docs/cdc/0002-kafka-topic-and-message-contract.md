---
status: proposed
date: 2026-07-20
jira: DMS-1245
related:
  - DMS-1246
  - DMS-1232
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
<topic-prefix>.instance.<instance-key>-g<generation>.documents.v1
```

`<instance-key>` is a stable, deployment-controlled, Kafka-safe opaque identifier unique
within the topic prefix. `data-store-<DataStoreId>` is the recommended CMS-backed default
only when that ID is unique across the deployment; otherwise deployment automation
assigns another opaque key for the `(tenant key, DataStoreId)` target. It must not contain
district names, tenant display names, school years, or other human-readable sensitive
identifiers. `<generation>` is the positive integer from the deployment-owned immutable
binding record. It starts at `1` and advances whenever a different physical source or a
new topic/consumer state namespace is required. It is an administrative namespace, not a
projector generation or a public per-record ordering field.

`DataStoreId` is not globally unique across CMS deployments. A production
`<topic-prefix>` therefore includes a stable opaque deployment/environment key unique
among every DMS deployment sharing the Kafka cluster, for example:

```text
edfi.deployment-a.prod.dms.instance.data-store-12-g1.documents.v1
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
| `contractVersion` | JSON number `1`; identifies the v1 envelope and immutable stream-representation contract |
| `documentUuid` | Stable public API document id; exactly matches the Kafka key |
| `projectName` | MetaEd project name from the cache row |
| `resourceName` | Resource name from the cache row |
| `resourceVersion` | Project/schema resource version copied from `dms.ResourceKey` |
| `contentVersion` | Signed 64-bit representation version used for idempotency and stale-write ordering |
| `lastModifiedAt` | UTC RFC 3339/ISO-8601 timestamp with up to seven fractional digits and trailing `Z` |
| `document` | Expanded structured full API resource body, never an escaped JSON string |

Database Pascal-case columns are renamed to lower camel case. `contractVersion` and
`contentVersion` are JSON numbers.

The physical temporal representation is not part of the public contract. One Ed-Fi-owned
`DocumentState` SMT consumes each raw Debezium record and produces the complete public
upsert, public tombstone, or drop result; the connector does not compose that contract
from an independent generic JSON expander and a predicate-heavy stock-SMT chain. In
particular, SQL Server stores `dms.DocumentCache.LastModifiedAt` as `datetime2(7)`. The
pinned Debezium connector's `adaptive` time-precision mode exposes that column as an `INT64`
`io.debezium.time.NanoTimestamp`, not a JSON string. The required Ed-Fi `DocumentState`
SMT owns its conversion to `lastModifiedAt`: it interprets the Debezium logical
type as nanoseconds since the Unix epoch in UTC, preserves SQL Server's 100-nanosecond
precision without rounding, and emits the RFC 3339/ISO-8601 string required above with
at most seven fractional digits and a trailing `Z`. A raw numeric `lastModifiedAt` or a
plain field rename is non-conforming. The transform also verifies that the emitted value
exactly matches `document._lastModifiedDate`; a mismatch fails the record rather than
publishing inconsistent metadata.

The stream `variantKey` uses the API five-component shape
`{schemaEpoch}.j._.{linkFlag}.i`: JSON, no readable profile, the published document's link
mode, and identity content coding. Ordinary resource documents are the link-bearing
cache intermediate and use `l` regardless of the API `ResourceLinks:Enabled` setting;
descriptors use the backend's descriptor representation context and use `n`.

DMS computes `StreamEtag` while materializing `dms.DocumentCache` by calling the same
served-ETag composer as the API with that fixed stream context. Kafka Connect copies the
opaque value to `document._etag`; it does not derive `schemaEpoch`, interpret link
configuration, or implement DMS ETag encoding. `document._etag` is not a digest or an
HTTP quoted entity-tag. Consumers preserve it as opaque and use `contentVersion` for
ordering.

`document` is caller-agnostic, pre-profile full resource JSON. It includes `id`, the
stream `_etag`, and `_lastModifiedDate`. When the compiled read plan injects reference
links, it contains those `link` subtrees; DMS does not maintain another link-free Kafka
projection. It excludes authorization arrays, EdOrg hierarchy JSON, API client identity,
and readable-profile-specific output.

Envelope `documentUuid` and `lastModifiedAt` exactly match embedded `id` and
`_lastModifiedDate`. The source cache row carries the DMS-computed `StreamEtag`, which is
published only as `document._etag`; the envelope does not duplicate it. If Debezium
exposes `DocumentJson` as a string, the `DocumentState` SMT parses it directly, injects
the ETag, and emits the structured `document` value. Invalid JSON or inconsistent
embedded metadata fails transformation rather than publishing a partial record.

A non-null value is an upsert. Duplicates, replays, and snapshots are allowed. Consumers
apply a record only when its `contentVersion` is newer than the state they retain for the
key. Kafka ordering is per partition and therefore per keyed document, not global
`contentVersion` order.

Consequently, a lower `contentVersion` is never an in-place correction for a higher value
already observed on the topic. If projection health detects
`DocumentCache.ContentVersion > Document.ContentVersion` and that higher cache value may
have been published, recovery requires a new binding generation, topic, consumer state
namespace, and snapshot. Internal-only projections whose rows cannot have been observed
downstream may instead delete the incompatible cache row and rebuild it from canonical
state.

## V1 Stream-Representation Immutability

The `documents.v1` topic deliberately has no projection-generation or
stream-representation-generation field. Within v1, the following are an immutable
contract:

- the served-ETag composition algorithm for a fixed set of inputs,
- the stream selector tuple `{schemaEpoch}.j._.{linkFlag}.i`, including the resource and
  descriptor link-context rules,
- publication of the DMS-computed value only as `document._etag`, and
- `contentVersion` as the consumer's sole stale-write ordering value within the topic.

An implementation refactor is compatible only when conformance fixtures prove that the
same `ContentVersion`, `EffectiveSchemaHash`, document kind/link context, format, profile,
and content-coding inputs produce exactly the same `StreamEtag`. Normal changes to those
inputs, such as a canonical document update that advances `ContentVersion`, remain v1
behavior; changing how unchanged inputs are interpreted is not. An
`EffectiveSchemaHash` change is also a different input rather than an ETag-algorithm
change, but schema reprovisioning must discard incompatible cache rows and follow the
CDC design's explicit new-binding-generation and new-topic rules when retained consumer
state could otherwise observe reused document keys and versions.

A DMS change that would produce a different `StreamEtag` for the same inputs, change the
fixed stream selectors, or otherwise change the published document representation for
unchanged canonical document/schema state must not ship under `documents.v1`. This
includes an output-changing bug fix. The change requires a new topic contract such as
`documents.v2`, a matching `contractVersion`, complete cache reprojection under the new
contract, and a fresh connector snapshot/republication into the new topic.

Rewriting same-`contentVersion` rows in the existing v1 topic is not an upgrade mechanism:
conforming live consumers may discard those records as not newer. DMS must not advance
canonical `ContentVersion` merely to force delivery of a projection-contract change.
Because one `DocumentCache` row stores one `DocumentJson` and one `StreamEtag`, v1 does
not support concurrent live publication of two stream-representation contracts from that
row. A contract-version transition is a coordinated cutover; overlap requires a separate
projection design and decision record.

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
- internal `DocumentId`, the source-only `StreamEtag` column name, or operational
  `ComputedAt`,
- Avro, Protobuf, or Schema Registry subjects,
- legacy `EdFiDoc` or `deleted=true` shapes.

## Consequences

- Consumers can reconstruct current instance document state but not complete history.
- A published cache-ahead invariant cannot be repaired by sending the lower canonical
  version to the same topic; source rollback/reset recovery uses a new topic generation.
- One ACL protects one instance while resource metadata supports downstream routing.
- Public identity and per-document ordering survive canonical deletion because the key
  is independent of the value.
- Consumers can preserve the exact DMS stream-representation ETag without access to
  `EffectiveSchemaHash` or duplicating DMS representation rules.
- Publication requires the Ed-Fi `DocumentState` SMT. It atomically owns source/operation
  classification, filtering, JSON parsing, key and envelope shaping, timestamp
  normalization, nested ETag injection, tombstone synthesis, and topic routing; a
  stock-only or generic-expand-plus-stock transform chain is not the v1 design.
- `StreamEtag` is not a reusable ETag for a differently profiled, link-shaped, formatted,
  or content-coded HTTP response; an HTTP server composes its own validator for the
  representation it serves.
- The v1 stream representation and ETag composer are immutable for unchanged inputs;
  output-changing fixes require a new binding generation, complete reprojection, and
  publication to a new versioned topic rather than same-version replacement in
  `documents.v1`.
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
| Require consumers to compose `_etag` from `contentVersion` | Rejected: the public record does not otherwise carry the in-force `EffectiveSchemaHash`, and duplicating DMS representation rules would not guarantee the same validator. |
| Compose `_etag` in Kafka Connect | Rejected: schema and representation selection belong to DMS; the connector treats the projected `StreamEtag` as opaque and only copies it into the public shape. |
| Add a projection/stream generation to v1 | Rejected for v1: it would add database, public-contract, consumer-ordering, and mixed-generation fencing semantics. V1 instead freezes the representation contract and uses a new versioned topic plus full snapshot for an output-changing upgrade. |
| Republish a changed ETag at the same `contentVersion` in `documents.v1` | Rejected: conforming consumers may discard it, and replay could not order old and new derivations without another public generation value. |
| Rely on Debezium's default SQL Server temporal serialization | Rejected: `datetime2(7)` is an `INT64` `NanoTimestamp` in `adaptive` mode, which violates the string contract. |
| Require SQL Server `time.precision.mode=isostring` in the currently pinned image | Rejected for v1: the pinned Debezium 2.7 connector supports `adaptive` and `connect`, but not `isostring`; the required Ed-Fi `DocumentState` SMT performs the lossless conversion. |
