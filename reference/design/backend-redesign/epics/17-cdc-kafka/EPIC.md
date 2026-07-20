---
jira: TBD
source_spike: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1240
  - DMS-1089
---

# Epic: Relational CDC/Kafka Streaming

## Description

Implement relational DMS Debezium/Kafka streaming using the DMS-1245 decisions:

- [`0001-relational-cdc-sources.md`](../../design-docs/cdc/0001-relational-cdc-sources.md)
- [`0002-kafka-topic-and-message-contract.md`](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [`0003-debezium-connector-deployment.md`](../../design-docs/cdc/0003-debezium-connector-deployment.md)

One connector captures two complementary sources:

- `dms.DocumentCache` create/update/snapshot → document upsert,
- `dms.Document` delete → Kafka tombstone,
- `dms.DocumentCache` delete/truncate and all other `dms.Document` operations → ignored.

The epic delivers one compacted document-state topic per DMS instance, `DocumentUuid`
keys, lower-camel envelope values, expanded `document` payloads, and authoritative
tombstones. Cache lifecycle is explicitly separate from domain lifecycle.

This epic does not implement a domain-event outbox or use polling Change Queries as the
Kafka source.

## Stories

- `TBD` — `00-documentcache-cdc-prerequisites.md` — Wire CDC enablement to two-table source guarantees
- `TBD` — `01-cdc-ddl-support.md` — Emit/provision two-table CDC key and database support
- `TBD` — `02-connector-template-generation.md` — Generate PostgreSQL and SQL Server connector templates
- `TBD` — `03-bootstrap-enable-kafka-cdc.md` — Add explicit local/bootstrap connector registration
- `TBD` — `04-message-contract-tests.md` — Add Kafka message and source-routing contract tests
- `TBD` — `05-e2e-kafka-scenarios.md` — Replace legacy Kafka E2E expectations with relational CDC scenarios
- `TBD` — `06-ops-docs-runbooks.md` — Add CDC setup, monitoring, recovery, and security runbooks

## Cross-Story Dependency Notes

- E18 supplies the asynchronous DocumentCache reconciliation loop, fencing, bounded
  in-memory retry, and mismatch-derived health. It does not own delete capture.
- Story 00 combines projection prerequisites with E17-owned two-table key/filter/source
  binding prerequisites.
- Story 01 supplies PostgreSQL publication/replica identity and SQL Server capture setup
  consumed by Story 02.
- Story 02 owns source-operation classification, cache value shaping,
  document-delete-to-tombstone conversion, duplicate tombstone suppression, key
  simplification, and routed-topic publication.
- Story 03 registers the connector before reconciliation/test traffic and waits for zero
  projection mismatches plus connector/source-position readiness.
- Story 04 begins with fixtures and adds real-provider routed-topic key/order coverage.
- Story 05 depends on Stories 00-04 and replaces DMS-1232's legacy `deleted=true` /
  `EdFiDoc` expectations.
- Story 06 documents the implemented provider setup and recovery behavior.

## Dependency Matrix with `18-document-cache`

| This story | Depends on `18-document-cache` | Dependency type | Notes |
| --- | --- | --- | --- |
| `17-00-documentcache-cdc-prerequisites.md` | 18-00, 18-03, 18-07, 18-09 | Hard for upsert readiness | Consumes configuration, reconciliation, fencing, and exact completeness health. Core E02 supplies source/cache DDL; E17 owns lifecycle capture. |
| `17-01-cdc-ddl-support.md` | — | No E18 dependency | Core E02 supplies `dms.DocumentCache`; two-table CDC setup is owned here. |
| `17-02-connector-template-generation.md` | — | No E18 dependency until upsert smoke tests | Templates can be built with fixtures. |
| `17-03-bootstrap-enable-kafka-cdc.md` | 18-00, 18-03, 18-09, plus 17-00 | Hard | Waits for zero projection mismatches and connector/source-position catch-up. |
| `17-04-message-contract-tests.md` | 18-02 | Soft | Uses realistic cache payloads; delete/filter/order tests are E17-owned. |
| `17-05-e2e-kafka-scenarios.md` | 18-00, 18-03, 18-09, plus 17-00 through 17-04 | Hard for complete upsert coverage | Missing-cache delete remains independently testable. |
| `17-06-ops-docs-runbooks.md` | 18-03, 18-09, 18-11 | Hard for final docs | Consumes mismatch health, bounded retry, and recovery guidance. |

## Scope Guardrails

- Do not capture normalized resource tables directly.
- Do not use `dms.Document` as a payload source; only its delete lifecycle matters.
- Do not interpret `dms.DocumentCache` deletion as domain deletion.
- Do not publish shared cross-instance topics that rely on an instance field in the value.
- Do not publish separate authorized topics for aliases of one physical document set.
- Do not include `DocumentId` in the public key or value.
- Do not add request-path Kafka dual writes or cache pre-delete materialization.
- Do not make API routing or mutation availability depend on CDC readiness.
- Do not make `-EnableKafkaUI` imply connector registration.
- Do not require Change Queries for Kafka CDC.
- Do not discover, add, remove, or replace CDC targets automatically at runtime.
- Do not reuse an instance topic for a different physical document set without explicit
  migration/reset.

## Completion Criteria

- A provisioned relational DMS instance can opt into one connector that captures exactly
  `dms.DocumentCache` and `dms.Document` and publishes the v1 instance topic.
- Both providers configure `DocumentUuid` keys and emit authoritative document tombstones.
- The connector publishes cache create/update/snapshot as upserts, document deletes as
  exactly one tombstone, and drops every other captured operation.
- Cache failure or absence never blocks API deletion; cache truncation/rebuild emits no
  domain tombstones.
- PostgreSQL and SQL Server provider E2E coverage proves same-key ordering through the
  routed topic.
- Published records conform to the DMS-1245 topic/message contract.
- Local setup and E2E flows register connectors against selected data stores without
  hard-coded database names.
- Target/source migrations and destructive cleanup remain explicit operations.
- Documentation covers setup, security, monitoring, restart, offset reset, resnapshot,
  cache rebuild, and teardown.
