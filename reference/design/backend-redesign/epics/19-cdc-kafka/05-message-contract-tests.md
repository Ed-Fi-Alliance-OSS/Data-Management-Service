---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Kafka Message and Source-Routing Contract Tests

## Design References

- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Connector transformation](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation)
- [Contract-to-evidence traceability](../../../cdc-streaming.md#contract-to-evidence-traceability)

The linked design sections own the normative contracts. This story owns the executable
conformance scenarios and pass evidence without duplicating the contract text.

## Outcome

Add fast serialized-record, provider, broker-backed, and consumer-conformance tests for
the v1 stream.

## Dependencies

- Depends on the 19-03 transform artifact and the 19-01 provider source setup.
- Broker-backed readiness cases also consume 19-00 and 19-04.
- Representative materialized records are supplied by 18-02.

## Implementation Scope

- Add canonical PostgreSQL and SQL Server source-record fixtures.
- Add transform and serialized-record conformance suites, including the exact fixed UTF-8
  progress-key bytes produced by `StringConverter`.
- Add provider key/routing/ordering and broker retry/failure scenarios.
- Add record-size and idle-source readiness fixtures against the real connector.
- Add the reference consumer-continuity fixture assigned to this story by the design
  traceability table.

## Acceptance Evidence

- Story-owned traceability maps each test identifier to the applicable `CDC-INV-*`
  contract ID.
- Provider and serialized-record suites cover every source and output category assigned to
  this story by the design traceability table.
- Progress-key suites cover structured and null source keys and prove that the resulting
  non-null string key publishes successfully to the compacted progress topic.
- Broker-backed suites cover the delivery, failure, sizing, and progress evidence assigned
  to this story, including committed source-offset advancement only after the keyed
  progress record is acknowledged.
- Consumer-conformance fixtures cover bootstrap and continuity behavior from the public
  contract.

## Not Assigned to This Story

- Full API-driven scenarios are assigned to 19-06.
- Projector completeness and Kafka ACL provisioning are assigned to E18 and 19-04.
