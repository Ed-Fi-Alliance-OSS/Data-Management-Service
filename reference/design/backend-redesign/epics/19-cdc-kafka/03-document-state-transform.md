---
jira: DMS-1322
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1240
---

# Story: Add the Relational `DocumentState` Kafka Connect Transform

## Design References

- **Connector transformation**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Pinned connector runtime**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#pinned-connector-runtime
- **Completed generic expand-JSON transform**: reference/design/backend-redesign/design-docs/expandjsonsmt-replacement.md

The referenced design sections define source classification, record transformation, public
records, progress routing, and runtime compatibility. This story is only the work package
for implementing them.

## Outcome

Implement and publish the DMS-specific `DocumentState` transform in the Ed-Fi Kafka
Connect plugin without changing the completed generic transform.

## Dependencies

- Depends on the DMS-1245 design decisions.
- Supplies the runnable transform artifact to 19-02 and 19-05.

## Implementation Scope

- Add the transform class and its small typed configuration surface.
- Add provider-record adapters, routing, validation, serialization, and fixed non-null
  progress-key normalization for every retained heartbeat shape.
- Preserve the public transform shape: projection work has no transform record type.
  Provider fixtures fail closed if an unexpected `DocumentProjectionWork` record reaches
  the transform.
- Package the transform in the qualified Ed-Fi Kafka Connect image.
- Retain regression coverage for the existing generic transform.

## Resolved Transform Scope and Runtime Contract

### Component Boundary

- Implement `org.edfi.kafka.connect.transforms.DocumentState` in
  `Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect` as one Kafka Connect SMT that operates on
  raw schema-backed Debezium `SourceRecord` instances.
- Keep the completed generic `ExpandJson` transform unchanged. The relational connector
  does not configure `ExpandJson`, stock unwrap, stock routing, or a predicate chain to
  compose the public record. `DocumentState` owns source classification, key shaping,
  JSON parsing, timestamp normalization, `_etag` injection, tombstone synthesis, and
  routing in one invocation.
- Use a small internal model: provider source adapter, classified source operation,
  validated document key, retained cache row, and output record kind. Do not add a
  table/field mapping language or operator-configurable source names.
- Keep transform-owned tests in the Kafka Connect plugin repository. DMS repository work
  in later stories consumes the published artifact through connector templates, shared
  fixtures, provider capture, and broker-backed contract tests.

### Configuration

- The transform has exactly the configuration values named by the topic/message ADR:
  `provider`, `target.topic`, and `progress.topic`.
- `provider` accepts only `postgresql` and `sqlserver`, using ordinal, lower-case tokens.
  A missing, empty, mixed-case, unknown, or whitespace-padded value fails configuration.
- `target.topic` must be a non-empty string and is the public instance document topic.
  The transform does not validate the complete topic naming policy; that remains in
  connector-template, bootstrap, and live-configuration validation.
- `progress.topic` must exactly equal `target.topic + ".cdc-progress"`. It is present in
  the SMT config only so the transform can route records. Templates generate it, and the
  transform rejects any other value instead of treating it as an operator-controlled
  route.
- Do not add configuration for contract version, source table names, source columns,
  JSON field names, timestamp formats, unavailable-value markers, routing predicates,
  document topic prefixes, or error tolerance.

### Source Classification

- Inspect the original Debezium source table and operation before discarding or rewriting
  the envelope. For relational records, the provider adapter resolves only the pinned
  Debezium 3.6 source metadata shape for schema `dms` and tables `DocumentCache`,
  `Document`, and `CdcHeartbeat`.
- A raw `dms.DocumentProjectionWork` record is always an unexpected source and fails the
  transform. Capture and connector configuration must exclude it; the transform must not
  silently drop it or advance offsets for it.
- Any other unexpected relational source table fails the transform. Missing source
  metadata, missing operation metadata, or an unknown Debezium operation code also fails
  the transform.
- Recognized operation handling is:
  - `dms.DocumentCache` create, update, and snapshot/read produce public upserts.
  - `dms.DocumentCache` delete and truncate are dropped.
  - `dms.Document` delete produces one public tombstone.
  - `dms.Document` create, update, snapshot/read, and truncate are dropped.
  - `dms.CdcHeartbeat` create, update, snapshot/read, and delete route to the progress
    topic.
  - Debezium heartbeat records route to the progress topic.
- Dropped operations return `null` only after source and operation are recognized as an
  excluded case. They do not validate `DocumentUuid`, `DocumentJson`, temporal fields, or
  unavailable markers because they are not retained records.
- Heartbeats are retained progress records, never dropped. The transform replaces the key
  before routing and must not apply public document validation to them.
- If an automatic Debezium delete tombstone reaches the transform, fail it as malformed
  input. Connector templates still own `tombstones.on.delete=false`.

### Public Key and Progress Key

- For public upserts and tombstones, require a non-null schema-backed source key with a
  `DocumentUuid` field. Parse it as a UUID, emit lower-case `D`-format text, and replace
  the key schema with `Schema.STRING_SCHEMA`.
- Reject a missing key, missing `DocumentUuid`, invalid UUID text, or a value that cannot
  be parsed through the provider adapter. Do not derive the public key from the unwrapped
  value because delete values are valid with a null record value.
- For cache upserts, also validate that the row's `DocumentUuid` equals the normalized
  public key. A mismatch fails transformation.
- For progress records, ignore the source key shape and always emit
  `Schema.STRING_SCHEMA` with the fixed key value `cdc-progress`. This covers
  schema-backed heartbeat keys, scalar keys, and Debezium heartbeat records whose source
  key is null.
- Do not add provider, instance, generation, source-partition, or heartbeat sequence
  fields to the progress key. The progress topic is already binding-scoped.

### Public Upsert Value

- For retained cache records, unwrap the Debezium `after` row and require:
  `DocumentUuid`, `ProjectName`, `ResourceName`, `ResourceVersion`, `ContentVersion`,
  `StreamEtag`, `LastModifiedAt`, and `DocumentJson`.
- Ignore `DocumentId`, `ComputedAt`, Debezium `before`, Debezium `source`, Debezium `ts_*`,
  and other internal fields. They must not appear in the public value.
- Parse `DocumentJson` directly into a structured JSON object using the plugin's JSON
  facilities. Reject null, non-string/unparseable provider values, invalid JSON,
  non-object JSON, and the SQL Server unavailable-value marker
  `__debezium_unavailable_value`.
- Treat a pre-existing `_etag` field in `DocumentJson` as an invariant failure. The E18
  materializer stores the value separately as `StreamEtag`, and this transform injects
  that opaque value into the public `document._etag`.
- Normalize `ContentVersion` to a signed 64-bit JSON number. Reject missing, null,
  non-integral, or out-of-range values.
- Normalize `LastModifiedAt` to the existing whole-second UTC
  `yyyy-MM-ddTHH:mm:ssZ` text before constructing the public value:
  - the SQL Server adapter accepts only the pinned Debezium 3.6 `isostring`
    `io.debezium.time.IsoTimestamp` string shape for `datetime2(7)`;
  - fractional seconds are truncated, never rounded;
  - non-UTC values, raw numeric/nanosecond temporal values, and unsupported logical types
    fail transformation; and
  - the PostgreSQL adapter supports only the timestamp shape emitted by the pinned image
    and locked by provider fixtures.
- Build a schemaless lower-camel map value so `JsonConverter` with
  `value.converter.schemas.enable=false` emits plain JSON without a `schema`/`payload`
  wrapper. Use stable insertion order for deterministic fixture output, but do not make
  field order part of the public contract.
- The value contains exactly the public envelope fields from the ADR:
  `contractVersion`, `documentUuid`, `projectName`, `resourceName`, `resourceVersion`,
  `contentVersion`, `lastModifiedAt`, and `document`.
- Validate before returning the record:
  - envelope `documentUuid` equals the emitted key;
  - `document.id` equals the emitted key;
  - `document._lastModifiedDate` equals normalized `lastModifiedAt`;
  - `document._etag` equals the source `StreamEtag`; and
  - no internal source fields are present in the output map.

### Tombstones, Progress Records, and Routing

- For a retained `dms.Document` delete, emit a record-level Kafka tombstone by setting
  `valueSchema` and `value` to null. Do not publish a delete envelope, a JSON `null`
  value, or any document body.
- Route public upserts and tombstones to `target.topic`. Set the Kafka partition to null
  so the configured producer partitioner applies the binding's key partitioning behavior.
  Preserve source partition/source offset through the Kafka Connect record copy path.
- Route retained `dms.CdcHeartbeat` and Debezium heartbeat records to `progress.topic`.
  Replace only the key, key schema, and topic. Preserve their value schema/value and
  headers so they remain a transport acknowledgement boundary for the original source
  position.
- Returning `null` is valid only for explicitly excluded operations from a recognized
  source. A malformed retained source record throws a transformation failure.

### Failure, Diagnostics, and Runtime Compatibility

- Use Kafka Connect `ConfigException` for invalid transform configuration and
  `DataException` for deterministic retained-record transformation failures. Include only
  bounded metadata such as provider, source table, operation, source topic, and reason code.
  Do not log `DocumentJson`, full public values, credentials, tenant names, or unbounded
  document metadata.
- Rely on connector-level `errors.tolerance=none`; the transform implements no secondary
  failure publication path.
- Support only the raw record shapes emitted by the pinned Debezium 3.6 / Kafka Connect
  4.3 image. If a provider or Debezium upgrade changes keys, source metadata, temporal
  logical types, delete shapes, or unavailable-value behavior, fixtures and image
  qualification must be updated before accepting the new shape.
- Keep the implementation Java 17 and Kafka Connect 4.3 compatible. Reuse the plugin's
  existing JSON dependencies and add no JSON-expansion dependency.

### Story-Owned Test Scope

- Add fast JUnit tests for pure classification, key normalization, output shaping,
  timestamp normalization, routing, and failure reasons.
- Add provider raw-record fixtures for PostgreSQL and SQL Server that match the pinned
  Debezium 3.6 image. Fixtures are minimal `SourceRecord` builders locked to the pinned
  Debezium field and schema shapes, not connector-template assertions.
- Cover every source and operation class listed above, including recognized dropped
  operations, `DocumentProjectionWork` fail-closed behavior, unexpected source failure,
  malformed retained records, and both heartbeat key shapes required by readiness.
- Cover representative public upserts from the E18 materialized-document fixture set:
  ordinary link-bearing resource, descriptor/no-link stream context, and one nested or
  extension case. Derive the raw Debezium fixtures from those files; do not copy or
  redefine the cache-row JSON or expected public document body.
- Cover invalid `DocumentJson`, non-object JSON, pre-existing `_etag`, missing or
  mismatched `DocumentUuid`, invalid `ContentVersion`, SQL Server unavailable marker,
  non-UTC timestamp, fractional-second truncation, timestamp/document mismatch, and
  unsupported temporal logical types.
- Add plugin-loading and smoke transformation tests in the qualified Ed-Fi Kafka Connect
  image. Broader serialized-record, broker-backed progress acknowledgement, partitioner,
  record-size, and consumer-conformance evidence remains assigned to 19-05 and 19-06.
- Keep regression tests for `ExpandJson$Value` running in the same plugin build so this
  story proves the new DMS-specific transform did not amend or replace the completed
  generic transform.

## Acceptance Evidence

- JUnit provider fixtures cover every source-operation class and output category defined by
  the source and message ADRs.
- JUnit fixtures prove a schema-backed heartbeat key and a Debezium heartbeat with a null
  source key both produce the fixed Kafka Connect string progress key, with no source-key
  pass-through.
- Invalid-record and provider-temporal fixtures cover the design-owned failure rules.
- Plugin-loading tests pass on the qualified connector runtime.
- Regression tests cover the unchanged generic transform artifact.

## Not Assigned to This Story

- Connector generation/registration and API-driven E2E scenarios are assigned to other
  E19 stories.
- DMS materialization is assigned to E18.
