---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Kafka Message and Source-Routing Contract Tests

## Design References

- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Connector transformation](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation)
- [Verification](../../../cdc-streaming.md#verification)

The linked design sections are the contract and verification authority. This story builds
the conformance suites and does not duplicate their fixtures or rules in planning text.

## Outcome

Add fast serialized-record, provider, broker-backed, and consumer-conformance tests for
the v1 stream.

## Dependencies

- Depends on the 19-03 transform artifact and the 19-01 provider source setup.
- Broker-backed readiness cases also consume 19-00 and 19-04.
- Representative materialized records are supplied by 18-02.

## Implementation Scope

- Add canonical PostgreSQL and SQL Server source-record fixtures.
- Add transform and serialized-record conformance suites.
- Add provider key/routing/ordering and broker retry/failure scenarios.
- Add record-size and idle-source readiness fixtures against the real connector.
- Add the reference consumer-continuity fixture required by the verification design.

## Acceptance Evidence

- A traceability table maps each test case to the applicable message-ADR or verification
  section.
- Provider and serialized-record suites cover every source and output category in that
  table.
- Broker-backed suites cover the delivery, failure, sizing, and progress cases assigned by
  the verification design.
- Consumer-conformance fixtures cover bootstrap and continuity behavior from the public
  contract.

## Not Assigned to This Story

- Full API-driven scenarios are assigned to 19-06.
- Projector completeness and Kafka ACL provisioning are assigned to E18 and 19-04.
