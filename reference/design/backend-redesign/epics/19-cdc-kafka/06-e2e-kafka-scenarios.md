---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1232
---

# Story: Replace Legacy Kafka E2E Expectations

## Design References

- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Local bootstrap and CI](../../../cdc-streaming.md#local-bootstrap-and-ci)
- [Contract-to-evidence traceability](../../../cdc-streaming.md#contract-to-evidence-traceability)
- [Source-history continuity](../../../cdc-streaming.md#source-history-continuity)

The linked design sections define the supported E2E workflow and observable stream. This
story is only the work package for implementing the scenarios.

## Outcome

Replace the quarantined legacy KafkaMessaging scenarios with API-driven relational CDC
coverage.

## Dependencies

- Depends on 19-00 through 19-05 and the completed E18 upsert projection path.

## Implementation Scope

- Update DMS-1232 fixtures and assertions to consume the relational public contract.
- Integrate E2E setup/teardown with the explicit bootstrap CDC workflow.
- Add topic-consumer helpers and failure diagnostics.
- Add the API mutation, cache lifecycle, ordering, restart, and source-history scenarios
  assigned to this story by the design traceability table.
- Remove legacy ignore markers after the relational lanes are stable.

## Acceptance Evidence

- PostgreSQL and SQL Server lanes use real provider capture, connectors, routed topics,
  and API traffic.
- Story-owned traceability maps each E2E test identifier to the `CDC-INV-*` contract ID it
  proves.
- Setup, restart, teardown, and failure artifacts are retained by the test harness for
  diagnosis.

## Not Assigned to This Story

- Exhaustive resource coverage, connector scaling, and the broader ACL matrix are outside
  this E2E work package.
