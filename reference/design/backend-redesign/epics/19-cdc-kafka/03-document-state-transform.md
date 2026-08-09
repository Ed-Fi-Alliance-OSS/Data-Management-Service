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
records, progress routing, runtime compatibility, and the schema-backed public upsert path
used for exact decimal serialization. This story is only the work package for implementing
them.

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
- Emit public upserts through the schema-backed Connect value path required by the topic and
  message contract so valid `DocumentJson` decimal numbers serialize as exact unquoted JSON
  numbers with the pinned `JsonConverter`.
- Preserve the public transform shape: projection work has no transform record type.
  Provider fixtures fail closed if an unexpected `DocumentProjectionWork` record reaches
  the transform.
- Package the transform in the qualified Ed-Fi Kafka Connect image.
- Retain regression coverage for the existing generic transform.

## Implementation Contract

- Implement `org.edfi.kafka.connect.transforms.DocumentState` in
  `Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect` against the source mapping, message shape,
  progress routing, diagnostics, and runtime compatibility owned by the design references.
- Keep the completed generic `ExpandJson` transform unchanged and keep relational
  `DocumentState` behavior in one Ed-Fi-owned SMT instead of a stock SMT chain.
- Keep transform-owned provider `SourceRecord` builders and malformed-record builders
  private to plugin tests. Later DMS stories consume the published artifact, qualified
  image evidence, and shared DMS fixture files through their own integration tests.

## Story-Owned Test Scope

- Add fast JUnit tests for pure classification, key normalization, output shaping,
  timestamp normalization, routing, and failure reasons.
- Add provider raw-record fixtures for PostgreSQL and SQL Server that match the pinned
  Debezium 3.6 image. Fixtures are minimal `SourceRecord` builders locked to the pinned
  Debezium field and schema shapes, not connector-template assertions.
- Cover every source and operation category owned by the ADRs, including recognized
  dropped operations, `DocumentProjectionWork` fail-closed behavior, unexpected source
  failure, malformed retained records, and both heartbeat key shapes required by readiness.
- Cover representative public upserts from the E18 materialized-document fixture set:
  ordinary link-bearing resource, descriptor/no-link stream context, and one nested or
  extension case. Derive the raw Debezium fixtures from those files; do not copy or
  redefine the cache-row JSON or expected public document body.
- Tests that use shared materialized-document fixtures receive the explicit Gradle
  property `edfiDmsMaterializedDocumentFixtureRoot` pointing at
  `src/dms/backend/Fixtures/document-cache/materialized-documents`; they fail fast when
  it is missing, invalid, or incomplete.
- Cover invalid `DocumentJson`, non-object JSON, pre-existing `_etag`, missing or
  mismatched `DocumentUuid`, invalid `ContentVersion`, SQL Server unavailable marker,
  non-UTC timestamp, fractional-second truncation, timestamp/document mismatch, and
  unsupported temporal logical types.
- Cover exact decimal serialization by asserting the bytes produced by the pinned
  `JsonConverter` with `schemas.enable=false` and `decimal.format=NUMERIC`, including the
  guard that schemaless `BigDecimal` output is not a valid implementation path.
- Add plugin-loading and smoke transformation tests in the qualified Ed-Fi Kafka Connect
  image. Broader serialized-record, broker-backed progress acknowledgement, partitioner,
  record-size, and consumer-conformance evidence remains assigned to 19-05 and 19-06.
- Keep regression tests for `ExpandJson$Value` running in the same plugin build so this
  story proves the new DMS-specific transform did not amend or replace the completed
  generic transform.

## Acceptance Evidence

- JUnit provider fixtures cover every source-operation class and output category defined by
  the source and message ADRs for both PostgreSQL and SQL Server source shapes.
- JUnit fixtures prove a schema-backed heartbeat key and a Debezium heartbeat with a null
  source key both produce the fixed Kafka Connect string progress key, with no source-key
  pass-through.
- Invalid-record and provider-temporal fixtures cover the design-owned failure rules.
- Decimal evidence proves the schema-backed public-upsert path preserves valid
  `DocumentJson` decimals as JSON numbers without `Double`, `Float`, string conversion,
  Schema Registry, Avro, Protobuf, or a custom public converter.
- Plugin-loading tests pass on the qualified connector runtime.
- Regression tests cover the unchanged generic transform artifact.

## Not Assigned to This Story

- Connector generation/registration and API-driven E2E scenarios are assigned to other
  E19 stories.
- DMS materialization is assigned to E18.
