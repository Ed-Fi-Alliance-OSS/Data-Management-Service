---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1240
---

# Story: Add the Relational `DocumentState` Kafka Connect Transform

## Design References

- [Connector transformation](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Pinned connector runtime](../../../cdc-streaming.md#pinned-connector-runtime)
- [Completed generic expand-JSON transform](../../design-docs/expandjsonsmt-replacement.md)

The linked design sections define source classification, record transformation, public
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
- Package the transform in the qualified Ed-Fi Kafka Connect image.
- Retain regression coverage for the existing generic transform.

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
