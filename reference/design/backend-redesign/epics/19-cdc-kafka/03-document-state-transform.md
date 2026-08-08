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

## Clarifying Questions and Answers

### Questions 1

1. What exact discriminator should `DocumentState` use to recognize a Debezium native heartbeat record before applying the "missing source metadata fails" rule? The transform config has no `topic.prefix`, so tasking needs to know whether detection is by `__debezium-heartbeat.<topic-prefix>` topic pattern, Connect schema/name, provider adapter shape, or another fixed Debezium invariant.
2. Under the pinned Debezium 3.6 / Kafka Connect 4.3 image, are Debezium native heartbeat records guaranteed to carry a non-null value after SMT processing? If a native heartbeat has a null value, should `DocumentState` preserve it as a progress-topic tombstone, fail it, or synthesize a non-null progress acknowledgement despite the "preserve value schema/value" rule?
3. What exact PostgreSQL `DocumentCache.LastModifiedAt` Connect schema and Java value shape should the provider adapter accept for `timestamp with time zone` under the pinned image, including logical name, UTC/fractional representation, and invalid-shape tests?
4. What exact PostgreSQL and SQL Server `DocumentUuid` key schema/value shapes should the provider adapters accept under the pinned image, and should otherwise parseable UUID text be rejected when it arrives through a non-pinned Connect schema or logical type?
5. Because 19-03 implements and tests `DocumentState` in `Ed-Fi-Kafka-Connect` while the E18 materialized-document fixtures are specified under the DMS repository, what is the intended cross-repository fixture consumption mechanism that avoids copying or redefining cache-row JSON and expected public document values?
6. For deterministic retained-record failures, should `DocumentState` expose a stable reason-code enum that tests assert, or should story-owned tests assert only exception type plus bounded metadata categories while leaving exact reason strings internal?
7. Which 19-03 fixture/test assets, if any, are intended to become reusable by 19-05 message-contract tests, versus being private plugin-unit builders with 19-05 owning separate serialized and broker-backed fixtures?

### Answers 1

1. Recognize a Debezium native heartbeat before source-metadata validation by the raw source record topic: `record.topic()` must start with `__debezium-heartbeat.` and have a non-empty suffix. Do not add `topic.prefix` to the `DocumentState` config. Connector templates and live validation must keep Debezium's default heartbeat topic prefix by requiring `topic.heartbeat.prefix=__debezium-heartbeat` and rejecting a non-empty `topic.heartbeat.name` or any conflicting heartbeat prefix. Any non-heartbeat record with missing source metadata still fails.
2. The pinned Debezium 3.6 heartbeat implementation normally emits a non-null heartbeat value struct containing `ts_ms`. `DocumentState` should not depend on that for correctness: if a native heartbeat reaches the transform with a null value or null value schema, still replace the key with `cdc-progress`, route it to `progress.topic`, and preserve the null value/schema. Do not synthesize a value and do not fail solely because the heartbeat value is null; the progress topic is only a transport acknowledgement boundary.
3. The PostgreSQL adapter should accept `DocumentCache.LastModifiedAt` only as the pinned Debezium `TIMESTAMPTZ` shape: Kafka Connect `STRING` schema with semantic name `io.debezium.time.ZonedTimestamp` and a Java `String` value representing a UTC/GMT timestamp, for example `2026-07-06T15:30:45.123456Z`. Parse it as UTC, truncate fractional seconds without rounding, and emit `yyyy-MM-ddTHH:mm:ssZ`. Invalid-shape tests should reject a missing semantic name, `IsoTimestamp`, numeric timestamp schemas, non-string Java values, malformed text, non-UTC offsets, and special/unbounded timestamp text.
4. For public document keys, require a schema-backed `Struct` key with a required `DocumentUuid` field. PostgreSQL accepts only a `STRING` field with semantic name `io.debezium.data.Uuid` and Java `String` UUID text. SQL Server accepts only the pinned `uniqueidentifier` key shape captured by provider fixtures: a required Kafka Connect `STRING` field with Java `String` UUID text and no alternate logical type unless the pinned fixture proves one. In both providers, parse to UUID and emit lower-case `D` format. Reject raw string keys, schemaless maps, Java UUID objects, bytes, missing fields, invalid UUIDs, and otherwise parseable UUID text arriving through a non-pinned schema or logical type.
5. Use the shared language-neutral DMS fixtures under `src/dms/backend/Fixtures/document-cache/materialized-documents/<case-name>/` as the source of truth. `Ed-Fi-Kafka-Connect` tests should receive that fixture root through a test-only environment variable or Gradle/system property supplied by local setup and CI from a DMS checkout; they should not vendor or copy the JSON. The transform tests may derive provider raw `SourceRecord` fixtures from `expected-cache-row.json` and compare the emitted public `document` body against `expected-public-cdc-document.json`. The full Kafka envelope expectation should be composed in the transform test from `expected-cache-row.json` plus that expected public document body. Those expected cache/public JSON files remain owned by the DMS fixture directory.
6. Expose a stable closed set of transform failure reason codes, implemented as an enum or equivalent constants, and include the reason code with bounded metadata in deterministic `DataException` diagnostics. Story-owned tests should assert exception type, reason code, and bounded metadata categories such as provider, source table, operation, and source topic. They should not assert full prose messages.
7. The reusable assets from 19-03 are the published `DocumentState` transform artifact, its qualified connector image evidence, and any neutral fixture-root reader needed to load the shared DMS JSON fixtures. Keep 19-03's low-level Java `SourceRecord` builders, malformed-record builders, classification matrices, and unit-only provider adapters private to the plugin tests. 19-05 should own separate serialized-record, broker-backed progress acknowledgement, partitioner, record-size, and consumer-conformance fixtures, deriving document bodies from the shared DMS JSON rather than reusing private 19-03 unit builders.

### Questions 2

1. For public document upserts and tombstones, should `DocumentState` preserve, clear, or fail on any Kafka Connect headers and source-record timestamp values? Progress records explicitly preserve headers, but the public document contract and security exclusions do not state whether connector/internal headers or source timestamps are consumer-visible.
2. For retained `DocumentCache` upserts, which non-key cache-row fields must be validated against exact pinned Connect schemas/logical names rather than only Java value type and semantic content? In particular, should otherwise parseable `DocumentJson`, `ContentVersion`, `ProjectName`, `ResourceName`, `ResourceVersion`, or `StreamEtag` values be rejected when their Connect schema differs from the provider fixture shape?
3. For a retained `dms.Document` delete, is the Debezium key the sole authority for the public tombstone key, or should the transform also validate any available `before.DocumentUuid` value against that key and fail a mismatch as a malformed retained record?
4. What exact test-only environment variable or Gradle/system property name should `Ed-Fi-Kafka-Connect` use to locate the shared DMS materialized-document fixture root, and should tests fail, skip, or fall back to a conventional sibling checkout path when that root is absent?
5. Should transform failures expose reason codes and bounded metadata through a custom `DataException` subtype or structured accessor API for tests, or is including the stable reason code and metadata categories in the exception message sufficient?

### Answers 2

1. For public document upserts and tombstones, `DocumentState` should strip Kafka Connect headers and clear the Connect record timestamp by emitting public records with an empty header set and a null record timestamp. Do not fail solely because the raw Debezium input carried headers or a source-record timestamp; treat them as connector metadata and ignore them for public output. This keeps the public topic contract limited to the v1 key, value/tombstone, partitioning, and documented envelope fields, and prevents Debezium/internal metadata or source timing from becoming consumer-visible contract surface. Consumers must continue to use `contentVersion` for state ordering and `lastModifiedAt` for the DMS document timestamp; Kafka record timestamps remain outside the public contract and may be assigned by the broker/producer according to normal Kafka policy. Tasking should add transform tests proving that source headers and timestamps are stripped for both a public upsert and a public tombstone, while progress records keep the already specified preserve-header/value behavior.
2. Validate every retained `DocumentCache` required field against the pinned provider fixture schema as well as semantic content. `DocumentJson` must be a schema-backed Kafka Connect `STRING` with the pinned provider shape: PostgreSQL `jsonb` uses Debezium's JSON logical string shape (`io.debezium.data.Json`), and SQL Server `nvarchar(max)` uses the pinned string shape with the configured unavailable-value marker check. `ContentVersion` must be the pinned `INT64`/Java `Long` shape. `ProjectName`, `ResourceName`, `ResourceVersion`, and `StreamEtag` must be the pinned schema-backed string shapes for their provider. Reject schemaless values, alternate logical names, numeric/string coercions, bytes, maps, Java objects, and otherwise parseable values arriving through a non-pinned schema. Provider fixtures should own the exact optionality and schema-parameter assertions so a Debezium/provider upgrade changes tests before the transform accepts a new shape.
3. The Debezium key remains the sole authority for the emitted public tombstone key. Do not derive the tombstone key from the delete value, and do not require `before.DocumentUuid` because delete values may be null or provider-limited. If the pinned provider delete value includes an available `before.DocumentUuid`, validate it through the same provider UUID adapter and fail the retained record when it does not equal the normalized key. If `before` is absent, null, or lacks `DocumentUuid` in an otherwise valid pinned delete shape, emit the tombstone from the key.
4. Use Gradle property `edfiDmsMaterializedDocumentFixtureRoot`, pointing at the DMS `src/dms/backend/Fixtures/document-cache/materialized-documents` directory. Tests that require the shared materialized-document fixtures should fail fast when the property is missing, the path does not exist, or required fixture files are missing. Do not use an environment-variable fallback, skip, vendor fallback JSON, or silently search a conventional sibling checkout path; local setup and CI should pass the path explicitly.
5. Implement a small custom `DataException` subtype for deterministic `DocumentState` transformation failures, with structured accessors for the stable reason-code enum and bounded metadata map. The exception message should include the same reason code and sanitized metadata for logs, but tests should assert the subtype, accessor values, and ordinary `DataException` assignability rather than parsing prose. Configuration failures should continue to use `ConfigException`.
