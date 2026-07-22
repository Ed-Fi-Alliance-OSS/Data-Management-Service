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

- Depends on 19-00 through 19-05, including the published 19-03 transform, the completed
  projection path needed for upserts, and 18-08 for byte-changing restamp coverage.

## Deliverables

1. Refine DMS-1232's legacy message expectations before implementation: v1 upserts use
   the DMS-1245 envelope and deletes use Kafka-null tombstones, not
   `deleted=false`/`deleted=true` records carrying `EdFiDoc`.
2. Opt E2E setup into CDC, persist its local JSON binding record, and wait for
   deployment-owned combined target readiness before observed writes. Treat the lack of
   admitted test writes as the maintenance gate and require the fresh startup/restart audit
   and post-audit publication barrier before opening the test write phase.
3. Add a consumer helper that selects the instance topic and filters by document key,
   with `max.partition.fetch.bytes` and `fetch.max.bytes` set to at least the binding's
   `maxRecordBytes`.
4. Cover API create, update, and delete plus focused missing-cache delete, cache rebuild,
   same-key ordering, a safe equal-version same-topic correction, and a byte-changing
   correction performed through 18-08's out-of-band restamp utility.
5. Capture connector status/logs, topics, and consumed records on timeout.
6. Remove legacy ignore markers only after consistent relational scenario results.
7. Exercise combined readiness from an otherwise idle database and retain diagnostics for
   the captured provider barrier, heartbeat/capture progress, and committed connector
   source offset without exposing raw physical identifiers.

## Acceptance Evidence

- PostgreSQL scenarios exercise supported self-contained and Keycloak modes where their
  shards permit.
- PostgreSQL and SQL Server use real connectors and public routed topics; the SQL Server
  provider matrix includes SQL Server 2025 with the pinned Debezium 3.6 image and current
  `nvarchar(max)` `DocumentJson` schema.
- Consumed records conform to the topic/message ADR and never assert legacy fields.
- Consumed upserts carry the exact DMS-projected stream ETag in `document._etag` and have
  no top-level envelope `etag`; ordinary resource scenarios prove API link disabling does
  not change the link-bearing stream variant.
- Provider scenarios prove the deletion, cache-maintenance, and ordering cases required
  by the authoritative verification section.
- A corrective-rebuild scenario proves the later equal-`contentVersion` record replaces
  prior consumer state in the same topic without a new binding generation or offset reset,
  and that every changed public representation has a different `StreamEtag`.
- A byte-changing correction fixture that would otherwise retain its ETag uses 18-08's
  utility and proves the corrected record reaches the same topic with a higher
  `contentVersion` and different `document._etag`, without resetting offsets or creating a
  binding generation.
- Both providers prove setup does not pass on connector status or lag alone: a barrier is
  captured after the fresh post-drain zero audit, an internal heartbeat advances the idle
  source, and readiness passes only after the committed connector source offset reaches the
  barrier and before observed writes are admitted.
- Setup/teardown evidence proves normal restart retains binding state and destructive
  teardown removes governed artifacts before the binding record.

## Out of Scope

- Exhaustive resource coverage.
- A broader Kafka ACL matrix; focused broker-backed own-topic access and cross-instance
  denial coverage is owned by 19-04.
- Connector scaling tests.
