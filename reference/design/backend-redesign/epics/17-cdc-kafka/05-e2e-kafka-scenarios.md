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
  - key is the API `id` / `DocumentUuid`,
  - value has `contractVersion = 1`,
  - value has `resourceName`,
  - value has expanded `document`.
- Update scenario observes a later value for the same `DocumentUuid` with a higher `contentVersion`.
- Delete scenario observes a tombstone for the same `DocumentUuid`.
- Scenarios do not assert legacy `EdFiDoc`, `deleted=false`, or `deleted=true` fields.
- Scenarios wait on connector readiness before issuing API writes.
- Scenarios collect connector logs and topic diagnostics when messages are not observed.
- PostgreSQL relational E2E coverage runs for both self-contained and keycloak identity providers if the test
  shard supports both modes.
- SQL Server E2E coverage is added only if the broader E2E infrastructure supports SQL Server CDC reliably;
  otherwise SQL Server CDC coverage remains in integration/smoke tests and is documented as such.

## Tasks

1. Locate existing ignored Kafka E2E scenarios or add a replacement relational CDC feature file.
2. Update setup to pass the CDC enablement flag and selected environment file.
3. Add a Kafka test helper that reads from the v1 instance topic and filters by `DocumentUuid` key.
4. Update create/update/delete assertions to the v1 contract.
5. Add connector readiness polling before API writes.
6. Add failure diagnostics for connector status, recent connector logs, topic list, and consumed records.
7. Remove old ignore markers only after the relational CDC scenarios pass consistently.

## Out of Scope

- Testing every Ed-Fi resource type.
- Kafka ACL testing.
- Production connector scaling tests.
