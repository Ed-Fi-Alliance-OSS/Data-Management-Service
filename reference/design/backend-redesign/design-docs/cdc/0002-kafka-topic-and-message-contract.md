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

The topic's partition count is fixed when the topic is created, and the connector uses the
same pinned key-based partitioner for the binding's lifetime. Neither may change in place:
otherwise the same key could move to another partition and lose the per-key offset
ordering used for corrective republishes. A deployment that needs a different partition
count or partitioner creates a new binding generation and topic.

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
pinned Debezium connector's `adaptive` time-precision mode exposes that column as an `INT64`
`io.debezium.time.NanoTimestamp`, not a JSON string. The required Ed-Fi `DocumentState`
SMT owns its conversion to `lastModifiedAt`: it interprets the Debezium logical
type as nanoseconds since the Unix epoch in UTC and deliberately truncates any fractional
second to the same whole-second UTC representation already emitted by DMS materializers.
`LastModifiedAt` retains its provider precision in the cache row, but subsecond precision is
not part of the public stream contract and is not used for freshness or ordering. A raw
numeric `lastModifiedAt`, fractional public timestamp, rounding into the next second, or
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
are not independently frozen. A refactor or bug fix that changes `DocumentJson` or
`StreamEtag` for an unchanged canonical `ContentVersion` is compatible when the new value
conforms more accurately to the existing v1 semantics and does not change the key,
required fields or their types, document contract, or delete behavior.

Operators repair a compatible projection defect in the existing topic:

1. Report the CDC target not ready and stop every old cache writer, including projector
   loops and optional direct fill.
2. Deploy the corrected materializer/composer while keeping old cache writers stopped.
3. Clear `dms.DocumentCache` with the provider-supported rebuild operation. Cache
   deletes/truncation remain non-domain operations and produce no public tombstones.
4. Start only corrected projector writers and run full reconciliation to an exact zero
   finishing audit. The existing connector captures the rebuilt cache inserts and
   publishes corrective upserts with their unchanged `contentVersion` values at later
   offsets.
5. Wait for connector catch-up through a post-audit source position, recheck projection
   readiness, and then restore combined CDC readiness.

The repair does not advance canonical `ContentVersion`, reset connector offsets, create a
new topic, or require a new binding generation. Ordinary reconciliation still treats an
equal-version cache row as fresh and does not rewrite it; the explicit clear-and-rebuild
operation is what produces the corrective inserts. Consumers apply those later
equal-version records according to the ordering rule above. Old cache writers must remain
stopped throughout the rebuild so obsolete and corrected materializers cannot
alternate outputs for one version.

Changing the partition count or pinned partitioner is not necessarily a message-shape
change, but it still creates a new binding generation, topic, and consumer state namespace
because offsets from different partitions cannot order equal-version records.

An incompatible public-contract change requires a new topic contract such as `documents.v2`, a
matching `contractVersion`, a new binding generation/topic, complete reprojection, and
consumer bootstrap in the new state namespace. Incompatible changes include changing key
encoding, removing or changing the JSON type of a required field, changing delete
semantics, or intentionally replacing the documented v1 document semantics rather than
correcting their implementation. Schema reprovisioning likewise uses a new topic when
retained consumer state could otherwise observe reused document keys and versions under
an incompatible schema.

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
upsert precedes the tombstone in that key's routed partition.

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

- Consumers can reconstruct current instance document state but not complete history.
- Raw delivery may contain duplicates or lower-version replays; the consumer ordering rule,
  not record arrival alone, keeps applied non-null upsert state monotonic.
- At-least-once replay may temporarily place an older upsert after a tombstone; the stream
  converges again when the replayed tombstone arrives and the connector catches up.
- Consumers may temporarily retain an older monotonic projection that committed after a
  newer canonical source version but before that newer version was projected.
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
- A compatible projection or opaque-ETag defect is repaired by clearing and rebuilding
  the cache into the existing topic; the later Kafka offset replaces an equal
  `contentVersion`. Incompatible public-contract changes still require a new versioned
  topic.
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
| Add a projection/stream generation to v1 | Rejected for v1: Kafka's per-key partition offset orders equal-`contentVersion` corrective republishes without another database column or public field. |
| Require strictly newer `contentVersion` for every replacement | Rejected: it makes a conforming projection or opaque-ETag correction require a new topic even though Kafka already orders the later record for the same key. |
| Republish a corrected ETag at the same `contentVersion` in `documents.v1` | Accepted for a compatible repair: clear and rebuild the cache after stopping old cache writers, and let the later Kafka offset win. |
| Rely on Debezium's default SQL Server temporal serialization | Rejected: `datetime2(7)` is an `INT64` `NanoTimestamp` in `adaptive` mode, which violates the string contract. |
| Require SQL Server `time.precision.mode=isostring` in the currently pinned image | Rejected for v1: the pinned Debezium 2.7 connector supports `adaptive` and `connect`, but not `isostring`; the required Ed-Fi `DocumentState` SMT normalizes the adaptive temporal value to the existing DMS whole-second representation. |
