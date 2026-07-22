---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1232
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Design References

- [Connector transformation](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation)
- [Connector topology and provider setup](../../../cdc-streaming.md#connector-topology-and-provider-setup)
- [Provider source-position barrier](../../../cdc-streaming.md#provider-source-position-barrier)
- [Source-history continuity](../../../cdc-streaming.md#source-history-continuity)
- [Pinned connector runtime](../../../cdc-streaming.md#pinned-connector-runtime)

The linked design sections define connector inputs, generated configuration, image
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
- Integrate binding-derived identity, provider setup, Kafka policy, transform, heartbeat,
  metrics, and source-offset settings owned by the design.
- Add pinned-image loading, rendering, restart, and provider smoke fixtures.

## Acceptance Evidence

- Rendering tests cover every generated and rejected configuration category in the design
  references.
- Pinned-image tests cover transform loading, producer/partition behavior, heartbeat and
  offset visibility, and provider restart integration.
- SQL Server image coverage includes the qualified database/runtime combination identified
  by the integration design.

## Not Assigned to This Story

- Bootstrap command wiring and Connect REST lifecycle are assigned to 19-04.
- Detailed transform behavior and public-record assertions are assigned to 19-03 and
  19-05.
