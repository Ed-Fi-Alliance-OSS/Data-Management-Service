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

CDC mode requires a stronger `dms.DocumentCache` projector contract than ordinary cache-backed reads:
upsert projection may lag, but deletes must synchronously ensure a cache source row exists before
`dms.Document` is removed so Debezium can publish the tombstone.

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

- This epic depends on the `dms.DocumentCache` implementation epic in
  [`../18-document-cache/EPIC.md`](../18-document-cache/EPIC.md). Connector work can proceed with fixtures, but
  CDC must not be exposed as supported until the relevant `18-document-cache` source guarantees are implemented
  and provider-verified.
- Story 00 consumes DMS-1246 and distinguishes connector-registration prerequisites from completed source
  readiness. Connector work can be developed with fakes or fixtures, but CDC should not be exposed as supported
  until the projector's CDC guarantees are implemented:
  bounded initial backfill, stale-write fencing, synchronous pre-delete materialization, configured-target
  physical source binding, and visible health/lag.
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
- Story 03 owns the one-shot registration workflow. Production-like automation repeats that workflow explicitly
  for every deployment-configured CDC target; runtime target discovery or reconciliation is not part of v1.

## Dependency Matrix with `18-document-cache`

| This story | Depends on `18-document-cache` | Dependency type | Notes |
| --- | --- | --- | --- |
| `17-00-documentcache-cdc-prerequisites.md` | 18-00, 18-01, 18-04, 18-06, 18-07, 18-08, 18-09, 18-10 | Hard | Consumes configuration, explicit targets/source bindings, projector state, bounded backfill status/target, delete source-row materialization, fencing, failure state, health, and provider verification. |
| `17-01-cdc-ddl-support.md` | 18-01, 18-10 | Hard for final verification | CDC setup can start from the existing cache table shape, but final provider proof depends on projector state DDL and delete-source verification. |
| `17-02-connector-template-generation.md` | 18-01, 18-10 | Soft until smoke tests | Templates can be built with fixture records; final delete/tombstone smoke coverage needs provider-verified cache deletes. |
| `17-03-bootstrap-enable-kafka-cdc.md` | 18-00, 18-04, 18-09, 18-10, plus 17-00 | Hard | Bootstrap validates DocumentCache registration prerequisites, registers before backfill traffic, and waits for source plus connector readiness before advertising CDC. |
| `17-04-message-contract-tests.md` | 18-02, 18-06, 18-07, 18-10 | Mixed | Fixture-only tests can start earlier. Source-level delete tests require materialization, fencing, and provider verification. |
| `17-05-e2e-kafka-scenarios.md` | 18-00, 18-03, 18-04, 18-06, 18-07, 18-09, 18-10, plus 17-00 through 17-04 | Hard | API-driven Kafka scenarios need projector, readiness, immediate-delete path, and provider proof. |
| `17-06-ops-docs-runbooks.md` | 18-08, 18-09, 18-11 | Hard for final docs | CDC runbooks must document DocumentCache failure, readiness, recovery, and delete blocking behavior. |

## Scope Guardrails

- Do not capture normalized resource tables directly.
- Do not capture `dms.Document` as the source payload.
- Do not publish shared cross-instance Kafka topics that rely on an instance field in the value.
- Do not publish two independently authorized instance topics from the same physical `dms.DocumentCache`.
- Do not include `DocumentId` in the public Kafka key or value contract.
- Do not add DMS request-path dual writes to Kafka.
- Do not make `-EnableKafkaUI` imply connector registration.
- Do not require Change Queries to be enabled for Kafka CDC.
- Do not discover, add, remove, or replace CDC data-store targets automatically at runtime.
- Do not infer CDC target membership from CMS; use the explicit deployment list.
- Do not let CDC source drift change normal request routing. A zero-loss host may opt into mutation blocking, but
  GETs and other read-only requests must never be blocked by that policy.
- Do not reuse an existing instance topic for a different physical document set without an explicit
  migration/reset decision.
- Do not expose authorization metadata, EdOrg hierarchy arrays, API client identity, or readable-profile-specific
  projections in the Kafka value.

## Completion Criteria

- A provisioned relational DMS instance can opt into CDC and publish create/update/delete changes from
  `dms.DocumentCache` to its instance document topic.
- PostgreSQL and SQL Server implementations both preserve tombstones keyed by `DocumentUuid`.
- CDC-mode deletes cannot complete unless `dms.DocumentCache` can supply the source row whose cascaded delete
  produces the tombstone.
- Projector/backfill retries cannot overwrite a newer cache row or recreate a cache row after a CDC-mode
  delete.
- Published records conform to the v1 topic/message contract from DMS-1245.
- Local setup and E2E flows can register connectors against the selected data store without hard-coded database
  names.
- Production topic prefixes are unique across DMS/CMS deployments sharing Kafka, and deployment automation can
  repeat the one-shot workflow for every explicitly listed CDC target.
- Target-list additions/removals and physical-source migrations are explicit deployment operations with no
  automatic destructive cleanup or topic reuse across different physical document sets.
- Successful CMS refreshes continue to drive normal API routing. For configured CDC targets, confirmed physical
  source drift makes readiness false and requires coordinated deployment; same-source credential or harmless
  connection-setting changes do not count as drift.
- Documentation covers setup, security, monitoring, restart, offset reset, resnapshot, and teardown.
