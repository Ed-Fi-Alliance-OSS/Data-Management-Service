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

The v1 topic uses exactly:

```text
cleanup.policy=compact
```

It also uses an explicit per-topic tombstone-retention override of at least:

```text
delete.retention.ms=604800000
```

That value is seven days. It is a fixed v1 minimum rather than an immutable binding field;
a host may retain tombstones longer, but bootstrap and live-configuration validation reject
a missing per-topic override or a configured value below the minimum. Inheriting the broker
default is not sufficient even when its current value is at least seven days. V1 does not
permit adding `delete` to `cleanup.policy`. The compact-only policy retains the latest live
upsert for every key. Kafka may eventually remove a tombstone and its superseded records,
leaving that deleted key absent from a fresh reconstruction.

[Kafka defines `delete.retention.ms`](https://kafka.apache.org/40/configuration/topic-level-configs/#topicconfigs_delete.retention.ms)
as the bound within which an offset-zero scan must finish to guarantee a valid final-state
snapshot. V1 therefore makes topic-only reconstruction conditional on this consumer
bootstrap contract:

1. Start from the earliest available offset of every topic partition and capture an
   end-offset barrier for every partition in the bootstrap operation.
2. Apply records through every captured barrier to durable consumer state within 24 hours
   of beginning the first partition scan.
3. Continue incrementally from the next offsets only after that bootstrap state is durable.
4. If the consumer cannot prove completion within 24 hours, do not advertise the state as
   valid; discard it and restart the complete scan.

The 24-hour consumer deadline leaves at least six days of retention margin for cleaner
scheduling, stalls, and recovery. A conforming consumer must capacity-test the largest
retained topic log it claims to support, including dirty/uncompacted records, partition
skew, maximum-sized records, consumer-state writes, and concurrent mutation traffic. It
must scale before production use so the complete scan satisfies the deadline. Live-key
count alone is not sufficient capacity evidence. DMS deployment automation can validate
the topic configuration and expose the contract values, but it cannot certify the runtime
behavior of an independently operated consumer.

A deployment that requires segment time/size deletion by adding `delete` to
`cleanup.policy` must first define and implement a separate authoritative bootstrap source
for current state and weaken the topic-only reconstruction guarantee in a new contract
version. An operational replay window alone is not such a bootstrap source.

The topic's partition count is fixed when the topic is created. The immutable binding also
stores `partitionerAlgorithm: "kafka-murmur2-v1"`. For non-null serialized key bytes `K`
and partition count `N`, this named behavior token selects partition
`(KafkaMurmur2(K) & 0x7fffffff) % N`, byte-for-byte matching Kafka's Java-client Murmur2
key partitioning. The token deliberately does not persist a Java class, client version, or
implementation detail. Connector generation maps it to a compatible implementation in
the pinned image, and fixed key/partition fixtures validate the mapping.

V1 accepts only `kafka-murmur2-v1`. The token and partition count are immutable for the
binding's lifetime; otherwise the same key could move to another partition and lose the
per-key offset ordering used for corrective republishes. A deployment that needs a
different partition count or algorithm creates a new token, binding generation, and
topic. Replacing an implementation while preserving every result defined by the token
does not change the binding.

Topic-per-instance supplies the Kafka authorization boundary, bounds topic count, and
keeps resource routing in message metadata. Shared cross-instance topics and
topic-per-resource are not the v1 contract.

## Record Size

Each deployment target has one positive signed 32-bit `maxRecordBytes` operational
ceiling. It is the maximum byte budget for a one-record produce request under the pinned
v1 key/value converters and producer, including the lowercase UUID key, final UTF-8 public
value, Kafka record-batch framing, and produce-request framing. It is deliberately not an
immutable binding field and does not claim to describe the largest schema-valid DMS
document across configurable schemas and extensions. The ordinary HTTP request-body limit
is not a substitute because materialization can inject links and the public envelope adds
metadata.

The pinned producer is the authoritative per-record enforcement boundary. After the real
transform and converters serialize a record, its `max.request.size` check rejects an
over-budget record locally before broker publication. With the required
`errors.tolerance=none`, that rejection fails the connector task, emits no partial public
record, and keeps combined readiness false until an operator raises the policy or changes
the source projection. V1 pins producer compression to `none`, so acceptance never depends
on a document's compression ratio. Tombstones use the same ceiling and are necessarily
smaller than the largest accepted non-null upsert.

The operational value drives every relevant Kafka limit rather than relying on defaults:

- the connector sets `producer.override.max.request.size=<maxRecordBytes>`, an explicit
  `producer.override.buffer.memory=<producerBufferBytes>` where `producerBufferBytes` is at
  least `maxRecordBytes`, and
  `producer.override.compression.type=none`; bootstrap validates that the Kafka Connect
  worker has additional heap headroom because `buffer.memory` is not a hard total-memory
  bound. `producerBufferBytes` defaults to the greater of `33554432` and
  `maxRecordBytes`; an operator may configure a larger value for throughput;
- the topic sets `max.message.bytes=<maxRecordBytes>`;
- bootstrap verifies that the broker request and replication path accepts that budget;
  for a self-managed broker this includes `socket.request.max.bytes`,
  `message.max.bytes` or the effective topic override, `replica.fetch.max.bytes`, and
  `replica.fetch.response.max.bytes`; and
- consumers configure both `max.partition.fetch.bytes` and `fetch.max.bytes` to at least
  `maxRecordBytes` and provision enough receive/deserialization memory for one such
  record.

See Kafka's authoritative
[producer](https://kafka.apache.org/40/configuration/producer-configs/),
[topic](https://kafka.apache.org/40/configuration/topic-level-configs/),
[broker](https://kafka.apache.org/40/configuration/broker-configs/), and
[consumer](https://kafka.apache.org/40/configuration/consumer-configs/) configuration
references for those byte-limit semantics.

Bootstrap fails before connector registration when it cannot verify this alignment.
Consumer conformance is a public deployment requirement because the producer cannot
validate independently operated consumers. A pinned-runtime boundary test sends
representative materialized envelopes immediately below and above a configured ceiling
through the real transform, converters, partitioner, and producer. It proves enforcement
and governed-limit alignment, not that one fixture is the largest valid record.

An increase is a coordinated in-place operational change, not a new topic generation.
Deployment automation first marks the target not ready and confirms consumer fetch and
deserialization capacity, then raises broker/replica and topic limits, then raises
producer `buffer.memory` to at least the new ceiling and `max.request.size` last and
restarts or resumes the connector. It validates every effective value before restoring
readiness. This ordering prevents the producer from publishing a newly accepted size
before the downstream path can carry it.
Partition count, partitioner behavior, keying, ordering, topic namespace, and public schema
remain unchanged.

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
For `dms.DocumentCache`, `DocumentUuid` is a non-indexed custom message-key column rather
than the relational primary key. Database validation triggers enforce that it equals the
canonical UUID for the row's compact `DocumentId` primary/foreign key.

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
  "lastModifiedAt": "2026-07-06T15:30:45Z",
  "document": {
    "id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
    "_etag": "123456-a1b2c3d4.j._.l.i",
    "_lastModifiedDate": "2026-07-06T15:30:45Z"
  }
}
```

| Field | Contract |
| --- | --- |
| `contractVersion` | JSON number `1`; identifies the v1 envelope and compatibility contract |
| `documentUuid` | Stable public API document id; exactly matches the Kafka key |
| `projectName` | MetaEd project name from the cache row |
| `resourceName` | Resource name from the cache row |
| `resourceVersion` | Project/schema resource version copied from `dms.ResourceKey` |
| `contentVersion` | Signed 64-bit representation version used for canonical-state idempotency and stale-write ordering; equal versions are ordered by Kafka partition offset |
| `lastModifiedAt` | UTC whole-second timestamp in the existing DMS `yyyy-MM-ddTHH:mm:ssZ` representation |
| `document` | Expanded structured full API resource body, never an escaped JSON string |

Database Pascal-case columns are renamed to lower camel case. `contractVersion` and
`contentVersion` are JSON numbers.

The physical temporal representation is not part of the public contract. One Ed-Fi-owned
`DocumentState` SMT consumes each raw Debezium record and produces the complete public
upsert, public tombstone, or drop result; the connector does not compose that contract
from an independent generic JSON expander and a predicate-heavy stock-SMT chain. In
particular, SQL Server stores `dms.DocumentCache.LastModifiedAt` as `datetime2(7)`. The
pinned Debezium 3.6 connector's explicit `isostring` time-precision mode exposes that
column as an ISO-8601 `STRING` with the `io.debezium.time.IsoTimestamp` logical type. The
required Ed-Fi `DocumentState` SMT owns its conversion to `lastModifiedAt`: it parses and
validates the UTC value and deliberately truncates any fractional second to the same
whole-second UTC representation already emitted by DMS materializers. `LastModifiedAt`
retains its provider precision in the cache row and raw connector record, but subsecond
precision is not part of the public stream contract and is not used for freshness or
ordering. A non-UTC or fractional public timestamp, raw numeric value, rounding into the
next second, or plain field rename is non-conforming. The transform also verifies that the
emitted value exactly matches `document._lastModifiedDate`; a mismatch fails the record
rather than publishing inconsistent metadata.

For SQL Server `nvarchar(max)` source data, including `DocumentJson`, the connector pins
Debezium 3.6's unavailable-value marker. A required retained value carrying that marker is
a transformation failure; it is never interpreted as JSON `null` or emitted in the public
record.

The stream `variantKey` uses the API five-component shape
`{schemaEpoch}.j._.{linkFlag}.i`: JSON, no readable profile, the published document's link
mode, and identity content coding. Ordinary resource documents are the link-bearing
cache intermediate and use `l` regardless of the API `ResourceLinks:Enabled` setting;
descriptors use the backend's descriptor representation context and use `n`.

DMS computes `StreamEtag` while materializing `dms.DocumentCache` by calling the same
served-ETag composer as the API with that fixed stream context. Kafka Connect copies the
opaque value to `document._etag`; it does not derive `schemaEpoch`, interpret link
configuration, or implement DMS ETag encoding. `document._etag` is not a digest or an
HTTP quoted entity-tag. Consumers preserve it as opaque; `contentVersion` orders canonical
states and Kafka partition offset orders equal-version projections.

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

A non-null value is an upsert. Duplicates, replays, snapshots, and explicit corrective
republishes are allowed. For one key, consumers apply non-null records against retained
non-null upsert state as follows:

- a higher `contentVersion` replaces retained state,
- a lower `contentVersion` is stale and is ignored, and
- an equal `contentVersion` replaces retained state when it has the later Kafka partition
  offset.

The topic's fixed partition count keeps one key in one partition, so Kafka supplies the
equal-version tie-breaker without another payload field. A consumer that processes a
partition serially may replace on every equal-version record. A consumer that applies
records concurrently or persists independent materialized state retains the last applied
partition and offset with the document. Exact duplicates remain harmless.

Kafka ordering is per partition and therefore per keyed document, not global
`contentVersion` order. `contentVersion` remains the canonical-state ordering value;
partition offset orders multiple projections of that same canonical state.

Database cache transitions and consumer-applied non-null upserts are monotonic, and the
stream is eventually convergent rather than linearizable to the canonical source at each
cache commit. Raw Kafka delivery is at-least-once: duplicates and replays are allowed,
including a lower-version replay after a higher version, and consumers apply the ordering
rule above rather than treating delivery order alone as canonical-state order. Independently,
a projector may coherently materialize version 10, complete its final optimistic source
check, a canonical writer may commit version 11, and the version-10 cache upsert may then
commit and publish before reconciliation publishes version 11. The database upsert never
lets version 10 replace an already cached version 11. A consumer that has not yet observed
version 11 may temporarily retain version 10; this is ordinary projection lag, not a
contract violation. V1 consciously accepts that lag so optional projection does not take a
write-conflicting source-row lock that can delay canonical writers.

Consequently, a lower `contentVersion` is never an in-place correction for a higher value
already observed on the topic. If projection health detects
`DocumentCache.ContentVersion > Document.ContentVersion` and that higher cache value may
have been published, recovery requires a new binding generation, topic, consumer state
namespace, and snapshot. Internal-only projections whose rows cannot have been observed
downstream may instead delete the incompatible cache row and rebuild it from canonical
state.

## V1 Compatibility and Corrective Republishes

The `documents.v1` topic deliberately has no projection-generation or
stream-representation-generation field. The Kafka partition offset already orders
multiple projections of the same canonical `contentVersion`; adding another public
generation would duplicate that ordering mechanism.

Within v1, the compatibility contract fixes:

- the Kafka key encoding, fixed topic partitioning strategy, and delete-as-tombstone
  semantics,
- the envelope field names, JSON types, and required metadata relationships,
- the caller-agnostic, pre-profile document semantics and resource/descriptor link
  contexts, and
- publication of the DMS-computed opaque value only as `document._etag`.

The exact opaque `StreamEtag` bytes and implementation details of document materialization
are not independently frozen. A refactor or bug fix may change `DocumentJson` or
`StreamEtag` while remaining compatible when the new value conforms more accurately to the
existing v1 semantics and does not change the key, required fields or their types,
document contract, or delete behavior. Compatibility does not relax strong-validator
semantics: if corrected public bytes differ, the corrected `StreamEtag` must differ too.
Kafka partition offsets order equal-version projections but do not make one ETag valid for
two byte-different representations.

The consumer contract permits a later equal-version record to replace an earlier record at
the later partition offset. V1 does not, however, implement a production workflow that
replaces an admitted database's exact CDC baseline. A future equal-version correction must
prove that every public byte change also changes `StreamEtag`, stop all obsolete cache
writers, rebuild without emitting domain tombstones, and satisfy the fence, audit, and
publication-barrier requirements in the authoritative design. That workflow is deferred
until deployment automation owns a cross-replica/external-writer fence and transaction
drain.

For a byte-changing case that would otherwise reuse a strong ETag, the out-of-band
representation-restamp utility may run only while the selected data store is explicitly
offline and all DMS replicas and external writers have been stopped outside the utility.
It assigns fresh `ContentVersion` values so ordinary projection and streaming can publish
higher-version state eventually. It does not certify another exact CDC baseline. Its
requirements are defined in
[Relational CDC and Document Projection](../../../cdc-streaming.md#offline-byte-changing-representation-correction).

Changing the partition count or `partitionerAlgorithm` token is not necessarily a
message-shape change, but it still creates a new binding generation, topic, and consumer
state namespace because offsets from different partitions cannot order equal-version
records.

An incompatible public-contract change requires a new topic contract such as
`documents.v2`, a matching `contractVersion`, a new binding generation/topic, complete
reprojection, and consumer bootstrap in the new state namespace. V1 does not implement
that cutover after first-write admission. A future workflow must fence every old connector,
old-contract cache writer, DMS replica, and external writer before clearing the shared cache
and keep that fence through reprojection, snapshot, consumer bootstrap, and publication
verification. Incompatible changes include changing key encoding, removing or changing the
JSON type of a required field, changing delete semantics, or intentionally replacing the
document semantics rather than correcting their implementation.

Because one `DocumentCache` row stores one `DocumentJson` and one `StreamEtag`, it cannot
supply two incompatible contracts concurrently. A zero-gap overlap between contract
versions requires separate versioned projection state and another design decision.

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
upsert precedes the tombstone in that key's routed partition. This ordering promise also
requires the connector's source producer to enable idempotence, acknowledge from all
in-sync replicas, retain retries, and allow no more than five in-flight requests per
connection. The deployment design pins those settings, rejects conflicting overrides,
and verifies the retry path rather than relying on Kafka or image defaults.

At-least-once connector replay can place a previously emitted upsert after an already
observed tombstone and temporarily restore that document. The null tombstone carries no
`contentVersion`, so the non-null ordering rule cannot prevent this delete-boundary replay.
When the connector continues through the same source sequence, the replayed tombstone
deletes the document again. V1 promises eventual convergence after connector catch-up, not
monotonic consumer-applied state across a tombstone. It does not add a versioned delete
envelope, permanent per-key consumer watermark, or exactly-once delivery requirement.

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

- Conforming consumers can reconstruct current instance document state, but not complete
  history, by completing the offset-zero scan within the 24-hour bootstrap deadline.
- That reconstruction guarantee depends on v1's compact-only topic, its explicit seven-day
  minimum tombstone retention, and consumer bootstrap conformance. Segment time/size
  deletion requires a separately defined authoritative bootstrap source and a new contract.
- Retaining tombstones for at least seven days increases retained bytes and cleaner work;
  operators monitor cleaner health and earliest-to-end scan volume as bootstrap capacity.
- Raw delivery may contain duplicates or lower-version replays; the consumer ordering rule,
  not record arrival alone, keeps applied non-null upsert state monotonic.
- At-least-once replay may temporarily place an older upsert after a tombstone; the stream
  converges again when the replayed tombstone arrives and the connector catches up.
- Consumers may temporarily retain an older monotonic projection that committed after a
  newer canonical source version but before that newer version was projected.
- A published cache-ahead invariant durably latches cache use/readiness and cannot be
  repaired by later source equality or by sending the lower canonical version to the same
  topic; source rollback/reset recovery uses a new topic generation.
- One ACL protects one instance while resource metadata supports downstream routing.
- Each binding records the stable `kafka-murmur2-v1` behavior token rather than a Java
  class/version; its fixed key-to-partition mapping and partition count preserve the
  per-key offset ordering contract.
- Each deployment target enforces one mutable maximum-record ceiling. Producer memory and
  request limits, the topic, brokers/replicas, and consumers must all accept it; a
  downstream-first increase reuses the binding and topic.
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
- Consumers replace an equal-`contentVersion` record at the later Kafka offset. Producing a
  baseline-replacing correction or incompatible-contract cutover after first-write
  admission is deferred. An offline byte-changing repair may use the restamp utility and
  publish higher versions eventually without claiming another exact baseline.
- DMS-1232 KafkaMessaging coverage must replace the shared-topic,
  `deleted=false`/`deleted=true`, and `EdFiDoc` expectations.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Shared `edfi.dms.document` topic | Rejected: it does not express instance isolation. |
| Topic per resource per instance | Rejected: it multiplies topics, ACLs, provisioning, and routing transforms while the envelope already identifies the resource. |
| Shared topic with `instanceId` in the value | Rejected: consumer filtering is not a security boundary and tombstones have no value. |
| `cleanup.policy=compact,delete` | Rejected for v1: time/size deletion can remove the sole latest upsert for an unchanged live document, so the topic can no longer bootstrap current state. |
| Inherit the broker's `delete.retention.ms` | Rejected: a broker-default change can silently shorten the valid bootstrap window; every public topic carries and validates its own override. |
| Include `DocumentId` | Rejected: it is an internal surrogate with no public contract role. |
| Publish delete envelopes | Rejected: compacted state streams use Kafka tombstones and no deleted body is guaranteed. |
| Require consumers to compose `_etag` from `contentVersion` | Rejected: the public record does not otherwise carry the in-force `EffectiveSchemaHash`, and duplicating DMS representation rules would not guarantee the same validator. |
| Compose `_etag` in Kafka Connect | Rejected: schema and representation selection belong to DMS; the connector treats the projected `StreamEtag` as opaque and only copies it into the public shape. |
| Add a projection/stream generation to v1 | Rejected for v1: Kafka's per-key partition offset orders equal-`contentVersion` corrective republishes without another database column or public field. |
| Establish a universal maximum from one materializer fixture | Rejected: configurable schemas and extensions make that claim unprovable; representative fixtures instead test the enforced operational boundary. |
| Rotate the topic when only `maxRecordBytes` increases | Rejected: record capacity does not change keying, partitioning, ordering, topic identity, or the public message contract. |
| Require strictly newer `contentVersion` for every replacement | Rejected as a universal rule: Kafka already orders equal-version records whose changed bytes have a different corrected strong ETag. A newer version is required when corrected bytes would otherwise reuse the ETag. |
| Republish a corrected ETag at the same `contentVersion` in `documents.v1` | The consumer ordering rule is accepted, but a production baseline-replacing producer workflow is deferred until deployment owns the required writer fence and drain. |
| Add an out-of-band representation-restamp utility | Accepted only for an explicitly offline data store; it advances existing content stamps and publishes higher versions eventually without certifying another exact CDC baseline. |
| Rely on Debezium's default SQL Server temporal serialization | Rejected: `datetime2(7)` is an `INT64` `NanoTimestamp` in `adaptive` mode, which violates the string contract. |
| Require SQL Server `time.precision.mode=isostring` in the pinned Debezium 3.6 image | Accepted for v1: it preserves the source precision in an unambiguous UTC `IsoTimestamp` and removes signed-nanosecond parsing; the required Ed-Fi `DocumentState` SMT still validates and truncates it to the existing DMS whole-second representation. |
