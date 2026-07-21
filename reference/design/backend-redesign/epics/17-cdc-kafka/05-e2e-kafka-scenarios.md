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
- [Verification](../../../cdc-streaming.md#verification)

## Outcome

Replace the quarantined legacy KafkaMessaging scenarios with API-driven relational CDC
coverage against the actual provisioned data store and routed public topic.

## Dependencies

- Depends on 17-00 through 17-04, including the published 17-02a transform, and the
  completed projection path needed for upserts.

## Deliverables

1. Refine DMS-1232's legacy message expectations before implementation: v1 upserts use
   the DMS-1245 envelope and deletes use Kafka-null tombstones, not
   `deleted=false`/`deleted=true` records carrying `EdFiDoc`.
2. Opt E2E setup into CDC, persist its local JSON binding record, and wait for
   deployment-owned combined target readiness before observed writes.
3. Add a consumer helper that selects the instance topic and filters by document key.
4. Cover API create, update, and delete plus focused missing-cache delete, cache rebuild,
   and same-key ordering scenarios.
5. Capture connector status/logs, topics, and consumed records on timeout.
6. Remove legacy ignore markers only after consistent relational scenario results.

## Acceptance Evidence

- PostgreSQL scenarios exercise supported self-contained and Keycloak modes where their
  shards permit.
- PostgreSQL and SQL Server use real connectors and public routed topics.
- Consumed records conform to the topic/message ADR and never assert legacy fields.
- Consumed upserts carry the exact DMS-projected stream ETag in `document._etag` and have
  no top-level envelope `etag`; ordinary resource scenarios prove API link disabling does
  not change the link-bearing stream variant.
- Provider scenarios prove the deletion, cache-maintenance, and ordering cases required
  by the authoritative verification section.
- Setup/teardown evidence proves normal restart retains binding state and destructive
  teardown removes governed artifacts before the binding record.

## Out of Scope

- Exhaustive resource coverage.
- Kafka ACL or connector scaling tests.
