---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1232
---

# Story: Replace Legacy Kafka E2E Expectations with Relational CDC Scenarios

## Description

Replace or update the quarantined legacy KafkaMessaging E2E coverage so it validates the relational CDC v1
contract.

The legacy expectations used a shared `edfi.dms.document` topic, `deleted=false` / `deleted=true`, and an
`EdFiDoc` body. The relational contract uses an instance document topic, `DocumentUuid` keys, lower-camel
metadata envelopes, expanded `document` payloads, and Kafka tombstones for deletes.

## Acceptance Criteria

- E2E setup opts into relational CDC through the explicit bootstrap flag from Story 03.
- E2E setup registers the connector against the same provisioned E2E database used by the DMS test process.
- Create scenario writes a representative resource through the API and observes a non-null Kafka value:
  - topic follows the v1 instance topic contract,
  - key bytes are the API `id` / `DocumentUuid` as UTF-8 lowercase text,
  - value bytes are a JSON object with no Kafka Connect `schema` / `payload` wrapper,
  - value has `contractVersion = 1`,
  - value has `resourceName`,
  - value has `etag` in the current opaque DMS API `_etag` format,
  - value has expanded `document`.
- Update scenario observes a later value for the same `DocumentUuid` with a higher `contentVersion`.
- Delete scenario observes a Kafka record-level tombstone for the same `DocumentUuid`, not JSON `null`.
- Delete coverage includes a create-then-delete path that does not wait for asynchronous
  projection and proves the authoritative `dms.Document` delete emits a tombstone even
  when no cache row was created.
- Cache maintenance coverage deletes/truncates and rebuilds `dms.DocumentCache` and
  proves no domain tombstones are published.
- PostgreSQL and SQL Server provider E2E coverage proves same-key routed-topic ordering:
  when a cache upsert commits before canonical deletion, the upsert is consumed before
  the `dms.Document` tombstone.
- Scenarios do not assert legacy `EdFiDoc`, `deleted=false`, or `deleted=true` fields.
- Scenarios wait on connector readiness before issuing API writes.
- Scenarios collect connector logs and topic diagnostics when messages are not observed.
- PostgreSQL relational E2E coverage runs for both self-contained and keycloak identity providers if the test
  shard supports both modes.
- SQL Server E2E uses the real connector and routed Kafka topic; fixture-only or
  transform-only coverage is insufficient for the provider ordering guarantee.

## Tasks

1. Locate existing ignored Kafka E2E scenarios or add a replacement relational CDC feature file.
2. Update setup to pass the CDC enablement flag and selected environment file.
3. Add a Kafka test helper that reads from the v1 instance topic and filters by `DocumentUuid` key.
4. Update create/update/delete assertions to the v1 contract.
5. Add focused missing-cache delete, cache-rebuild, and same-key ordering scenarios.
6. Add connector readiness polling before API writes.
7. Add failure diagnostics for connector status, recent connector logs, topic list, and consumed records.
8. Remove old ignore markers only after the relational CDC scenarios pass consistently.

## Out of Scope

- Testing every Ed-Fi resource type.
- Kafka ACL testing.
- Production connector scaling tests.
