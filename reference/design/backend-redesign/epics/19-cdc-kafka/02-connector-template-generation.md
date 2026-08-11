---
jira: DMS-1321
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1232
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Design References

- **Connector transformation**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation
- **Connector topology and provider setup**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup
- **Provider source-position barrier**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity
- **Pinned connector runtime**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#pinned-connector-runtime

The referenced design sections define connector inputs, generated configuration, image
qualification, and lifecycle constraints. This story is only the work package for
implementing them.

## Outcome

Generate and validate provider connector configurations for the deployment binding and
the published `DocumentState` transform.

## Dependencies

- Depends on 19-01 for provider setup and 19-03 for the published transform artifact.
- Template code and rendering tests may proceed before the transform image is available.

## Implementation Scope

- Add typed connector-template inputs and validation.
- Add PostgreSQL and SQL Server configuration renderers.
- Render exact include lists for `Document`, `DocumentCache`, and heartbeat sources and
  reject any connector configuration that includes `DocumentProjectionWork`.
- Integrate binding-derived identity, provider setup, Kafka policy, transform, heartbeat,
  metrics, and source-offset settings owned by the design.
- Generate and validate the exact key converter and Debezium delete-tombstone settings
  required by the public document and progress key contracts:
  `key.converter=org.apache.kafka.connect.storage.StringConverter` and
  `tombstones.on.delete=false`.
- Generate and validate the exact value-converter settings required for public document
  state publication:
  `value.converter=org.edfi.kafka.connect.converters.DocumentStateJsonConverter`,
  `value.converter.schemas.enable=false`, and
  `value.converter.decimal.format=NUMERIC`.
- Emit and validate the fixed Debezium topic naming settings required by `DocumentState`
  native-heartbeat classification for PostgreSQL and SQL Server:
  `topic.delimiter=.`,
  `topic.naming.strategy=io.debezium.schema.SchemaTopicNamingStrategy`,
  `topic.heartbeat.prefix=__debezium-heartbeat`, and an unset or empty
  `topic.heartbeat.name`.
- Add pinned-image loading, rendering, restart, and provider smoke fixtures.

## Acceptance Evidence

- Rendering tests cover every generated and rejected configuration category in the design
  references.
- Rendering and live-validation tests require the exact `StringConverter` key-converter
  path and `tombstones.on.delete=false` for PostgreSQL and SQL Server connector templates,
  and reject missing, duplicate, or conflicting values.
- Rendering and live-validation tests require the exact `DocumentStateJsonConverter`
  value-converter path and the `schemas.enable=false` and `decimal.format=NUMERIC`
  delegate settings, and reject missing, duplicate, or conflicting converter properties.
- Rendering and live-validation tests reject missing or conflicting `topic.delimiter`,
  `topic.naming.strategy`, or `topic.heartbeat.prefix` values and reject any non-empty
  `topic.heartbeat.name`.
- Live connector validation confirms the work table is absent from effective capture.
- Pinned-image tests cover transform loading, producer/partition behavior, heartbeat and
  offset visibility, and provider restart integration.
- SQL Server image coverage includes the qualified database/runtime combination identified
  by the integration design.

## Not Assigned to This Story

- Bootstrap command wiring and Connect REST lifecycle are assigned to 19-04.
- Detailed transform behavior and public-record assertions are assigned to 19-03 and
  19-05.
