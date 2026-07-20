---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Kafka Message and Source-Routing Contract Tests

## Design References

- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Connector transform pipeline](../../../cdc-streaming.md#connector-transform-pipeline)
- [Verification](../../../cdc-streaming.md#verification)

## Outcome

Add fast serialized-record and provider integration tests that pin the v1 public contract
without requiring an API E2E path for every source operation.

## Deliverables

1. Add canonical PostgreSQL and SQL Server Debezium fixtures for every source operation.
2. Exercise classification, tombstone conversion/suppression, key and value shaping,
   JSON expansion, and topic routing.
3. Add realistic nested/reference-link payload fixtures from the shared materializer.
4. Add real-provider delete-key and routed-topic ordering coverage.

## Acceptance Evidence

- Every retained fixture produces exactly the record required by the topic/message ADR.
- Every dropped fixture produces no public record, including automatic extra tombstones.
- Regression tests catch wrapper, quoting, escaped-JSON, timestamp, metadata, internal
  field, and Kafka-null contract violations.
- Provider tests cover canonical deletion without a cache row, cache rebuild cleanup,
  and same-key routed ordering.

## Out of Scope

- Full API-driven E2E scenarios.
- Projector reconciliation/completeness testing.
- Kafka ACL testing.
