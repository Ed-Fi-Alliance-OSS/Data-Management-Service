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

Implement relational DMS Debezium/Kafka streaming using the CDC decisions from DMS-1245:

- [`0001-document-cache-cdc-source.md`](../../design-docs/cdc/0001-document-cache-cdc-source.md)
- [`0002-kafka-topic-and-message-contract.md`](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [`0003-debezium-connector-deployment.md`](../../design-docs/cdc/0003-debezium-connector-deployment.md)

The epic delivers a compacted document-state stream sourced from `dms.DocumentCache`, with one document topic
per DMS instance, Kafka keys based on `DocumentUuid`, lower-camel envelope values, expanded structured
`document` payloads, and Kafka tombstones for deletes.

This epic does not implement a domain-event outbox and does not use polling Change Queries as the Kafka
source. Change Queries remain an API compatibility feature, while Debezium/Kafka CDC is a database-log-backed
streaming feature.

## Stories

- `TBD` — `00-documentcache-cdc-prerequisites.md` — Wire CDC enablement to `dms.DocumentCache` projector guarantees
- `TBD` — `01-cdc-ddl-support.md` — Emit/provision CDC key and database setup support for `dms.DocumentCache`
- `TBD` — `02-connector-template-generation.md` — Generate PostgreSQL and SQL Server connector templates
- `TBD` — `03-bootstrap-enable-kafka-cdc.md` — Add explicit local/bootstrap connector registration
- `TBD` — `04-message-contract-tests.md` — Add Kafka message contract tests
- `TBD` — `05-e2e-kafka-scenarios.md` — Replace legacy Kafka E2E expectations with relational CDC scenarios
- `TBD` — `06-ops-docs-runbooks.md` — Add CDC setup, monitoring, recovery, and security runbooks

## Cross-Story Dependency Notes

- Story 00 consumes DMS-1246 and is the CDC readiness gate. The connector work can be developed with fakes or
  fixtures, but CDC should not be exposed as supported until the projector's CDC guarantees are implemented.
- Story 01 provides engine-specific database support that Story 02 connector templates consume, especially
  delete tombstone keys based on `DocumentUuid`.
- Story 02 owns connector shape and transform order. Story 03 should register generated or parameterized
  templates rather than carrying a separate hard-coded connector design.
- Story 04 can begin with captured fixture records and then graduate to real Kafka Connect smoke coverage once
  Story 02 is available.
- Story 05 depends on Stories 00-04 for a supported local CDC path. It should retire DMS-1232's legacy
  `deleted=true` / `EdFiDoc` expectations.
- Story 06 can draft docs in parallel but should not publish production guidance until Stories 00-03 settle the
  actual command and connector surfaces.

## Scope Guardrails

- Do not capture normalized resource tables directly.
- Do not capture `dms.Document` as the source payload.
- Do not publish shared cross-instance Kafka topics that rely on an instance field in the value.
- Do not include `DocumentId` in the public Kafka key or value contract.
- Do not add DMS request-path dual writes to Kafka.
- Do not make `-EnableKafkaUI` imply connector registration.
- Do not require Change Queries to be enabled for Kafka CDC.
- Do not expose authorization metadata, EdOrg hierarchy arrays, API client identity, or readable-profile-specific
  projections in the Kafka value.

## Completion Criteria

- A provisioned relational DMS instance can opt into CDC and publish create/update/delete changes from
  `dms.DocumentCache` to its instance document topic.
- PostgreSQL and SQL Server implementations both preserve tombstones keyed by `DocumentUuid`.
- Published records conform to the v1 topic/message contract from DMS-1245.
- Local setup and E2E flows can register connectors against the selected data store without hard-coded database
  names.
- Documentation covers setup, security, monitoring, restart, offset reset, resnapshot, and teardown.
