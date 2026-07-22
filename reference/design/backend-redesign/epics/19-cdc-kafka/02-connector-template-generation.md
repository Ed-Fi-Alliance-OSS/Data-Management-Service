---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1232
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Design References

- [Connector transform pipeline](../../../cdc-streaming.md#connector-transform-pipeline)
- [Connector topology and provider setup](../../../cdc-streaming.md#connector-topology-and-provider-setup)
- [Provider source-position barrier](../../../cdc-streaming.md#provider-source-position-barrier)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Generate parameterized provider connector configurations that implement the authoritative
source routing and serialized public contract using the separately published
`DocumentState` transform.

## Dependencies

- Depends on 19-01 for provider CDC setup and 19-03 for a runnable published transform.
  Template authoring and rendering tests may proceed in parallel with 19-03, but no
  connector is registered until the pinned image contains that class.

## Deliverables

1. Define inputs from the immutable deployment binding for provider/source fingerprint,
   target and generation identity, connector/topic names, fixed topic partition count,
   `partitionerAlgorithm`, and `contractVersion`. Accept the positive signed 32-bit
   `maxRecordBytes` ceiling and optional larger `producerBufferBytes` from mutable
   deployment-owned operational policy alongside credentials, replication/capture
   identity, and snapshot behavior.
2. Generate one PostgreSQL or SQL Server connector configuration per DMS instance and
   immutable binding, without hard-coded instance values. Scope each connector to exactly
   one instance database and reject SQL Server configurations that select multiple
   databases.
   Configure `message.key.columns` to use `DocumentUuid` for both captured tables without
   requiring it to be the `DocumentCache` primary key or a cache index.
3. Configure SQL Server with `time.precision.mode=isostring` and
   `unavailable.value.placeholder=__debezium_unavailable_value` explicitly. Make the
   Ed-Fi `DocumentState` SMT validate `datetime2(7)`
   `io.debezium.time.IsoTimestamp` strings, deliberately truncate fractional seconds
   without rounding to the existing DMS whole-second UTC string, and fail a retained
   upsert whose required `DocumentJson` carries the unavailable marker.
4. Configure the `DocumentState` SMT delivered by 19-03 as the complete boundary from a raw
   Debezium record to a final public upsert, final public tombstone, internal progress
   record, or dropped record. Do not add an independent generic expand-JSON SMT or split
   the contract across stock predicate, unwrap, rename, and routing chains.
5. Configure only the transform's `provider`, `target.topic`, and `progress.topic` values.
   Generate `progress.topic` exactly as `target.topic + ".cdc-progress"`; do not expose it
   as an independent operator input, and reject any different value. Keep the DMS source
   table/column and v1 public-field mapping fixed in the versioned transform rather than
   generating a mapping DSL.
6. Keep `EffectiveSchemaHash`, link-option interpretation, and DMS ETag composition out
   of connector transforms.
7. Validate all version-specific properties and transform classes against the
   `edfialliance/ed-fi-kafka-connect` image built from
   `quay.io/debezium/connect:3.6.0.Final@sha256:6f3fe6407bae8f2a7714b9fc174d545d52d81044b4f4add1565854f020943d47`.
   Require deployment to select the published Ed-Fi image by immutable digest.
8. Accept only the binding token `partitionerAlgorithm: "kafka-murmur2-v1"` for v1 and
   map it to a compatible producer implementation in the pinned connector image. The
   token means `(KafkaMurmur2(serializedKeyBytes) & 0x7fffffff) % partitionCount`; it is
   not a Java class or library version. Reject a missing/unknown token and any independent
   partitioner class or configuration that conflicts with the token-defined behavior.
9. Emit the fixed source-producer overrides `enable.idempotence=true`, `acks=all`,
   `retries=2147483647`, and `max.in.flight.requests.per.connection=5` using the Kafka
   Connect `producer.override.*` connector properties. Do not expose them as template
   inputs. Reject duplicate properties or any attempted override that conflicts with
   these values.
10. Emit the fixed top-level connector setting `errors.tolerance=none`; do not rely on
    its Kafka Connect default or expose it as a template input. Reject a duplicate,
    missing, or conflicting value. Do not configure tolerant skipping or a dead-letter
    queue for malformed retained records.
11. Emit `producer.override.max.request.size=<maxRecordBytes>`,
    `producer.override.buffer.memory=<producerBufferBytes>`, and the fixed
    `producer.override.compression.type=none`. Default `producerBufferBytes` to the greater
    of `33554432` and `maxRecordBytes`, permit an explicit larger operational value, and
    reject a smaller one. Do not infer the record ceiling from DMS's request body limit.
    Reject a missing, duplicate, or conflicting property and document the additional Kafka
    Connect worker heap headroom required beyond `buffer.memory`.
12. Include `dms.CdcHeartbeat` beside the two document tables and emit a positive
    `heartbeat.interval.ms` with a generated, provider-qualified
    `heartbeat.action.query` that atomically increments the singleton. Default the
    interval to 5,000 ms, permit an explicit positive operational override within the
    readiness timeout, and require SQL Server `poll.interval.ms` to be no greater than
    it. These timing values are not binding fields. Reject missing, duplicate, free-form,
    or conflicting heartbeat properties.
13. Emit and live-validate `statistics.metrics.enabled=true` for both providers so the
    Debezium 3.6 P50/P95/P99 `MilliSecondsBehindSource` telemetry remains available.

## Acceptance Evidence

- Rendering tests cover representative providers and reject invalid production topic
  prefixes, incomplete binding inputs, nonpositive or changed partition counts, a missing,
  unknown, or changed `partitionerAlgorithm`, or generated identities that differ from
  the binding record.
- Pinned-image tests use fixed serialized `DocumentUuid` key/partition fixtures across
  representative partition counts to prove the generated producer maps
  `kafka-murmur2-v1` exactly. An image may change its implementation but not those results;
  changing the algorithm token or partition count requires a new binding generation/topic.
- Template tests prove the configured class, source includes, key columns, converters,
  tombstone suppression, transform properties, public target topic, and derived progress
  topic match the binding and 19-03 contract.
- Provider template/smoke tests prove the non-indexed cache UUID column produces the same
  public key bytes as the canonical document-delete source.
- SQL Server rendering tests require `time.precision.mode=isostring` and the exact
  unavailable-value marker; pinned-image smoke coverage proves the published transform
  converts a realistic retained record and fails a required marker value.
- Pinned-image SQL Server integration coverage includes SQL Server 2025.
- SQL Server rendering tests prove that each connector selects exactly one instance
  database and one binding topic, and reject attempted multi-database consolidation.
- Rendering tests require the exact ordering-safe source-producer overrides and reject
  idempotence disabled, acknowledgements other than `all`, fewer retries, more than five
  in-flight requests, and duplicate/conflicting producer properties.
- Rendering tests require exactly one explicit `errors.tolerance=none` and reject an
  omitted, duplicate, or conflicting value such as `all`.
- Rendering tests require the exact operational `max.request.size`, explicit
  `buffer.memory` no smaller than it, and fixed no-compression override; prove the
  `33554432` minimum/default and a larger explicit buffer; reject
  nonpositive/out-of-range size values and every duplicate/conflicting producer-size
  property; and prove a changed `maxRecordBytes` rerenders the same binding generation and
  topic.
- Rendering tests require the emitted heartbeat table include, positive interval, exact
  provider action query, and valid SQL Server poll relationship. Pinned-image smoke tests
  prove heartbeat-table and Debezium heartbeat records are produced and acknowledged in
  the derived progress topic, never emitted to the public topic, and make their source
  offsets committable only after every earlier source record completes. The committed
  offsets remain observable through the connector-offset REST endpoint with the provider
  fields required by readiness.
- Rendering and live-configuration tests require
  `statistics.metrics.enabled=true`; image smoke tests expose P50/P95/P99 source-lag
  attributes on the Kafka Connect 4.3.0 runtime.
- A pinned-image smoke test proves the configured transform class loads; detailed
  transform behavior remains owned by 19-03 and the shared contract suite in 19-05.

## Out of Scope

- Bootstrap command wiring.
- Full API-driven E2E scenarios.
- Production credential provisioning.
- Multi-database SQL Server connectors and source-aware topic routing.
