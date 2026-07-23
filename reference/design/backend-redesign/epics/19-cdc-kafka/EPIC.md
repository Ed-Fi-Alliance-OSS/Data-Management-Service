---
jira: DMS-1309
source_spike: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1089
  - DMS-1279
---

# Epic: Relational CDC/Kafka Streaming

## Design References

- [Configuration, integration, readiness, and operations](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

The linked design documents own the normative connector, topic, message, durability,
readiness, and recovery contracts. This epic partitions implementation work, and its
stories own the executable acceptance evidence for those contracts.

## Outcome

Deliver the relational CDC/Kafka integration, its provider and connector tooling,
verification, local/E2E setup, and operator documentation through the story set below.

## Stories

- `DMS-1319` — `00-documentcache-cdc-prerequisites.md` — Add deployment-owned CDC binding and readiness
- `DMS-1320` — `01-cdc-ddl-support.md` — Emit/provision provider CDC key and database support
- `DMS-1321` — `02-connector-template-generation.md` — Generate PostgreSQL and SQL Server connector templates
- `DMS-1322` — `03-document-state-transform.md` — Add the DMS-specific relational record transform
- `DMS-1323` — `04-bootstrap-enable-kafka-cdc.md` — Add explicit local/bootstrap connector registration
- `DMS-1324` — `05-message-contract-tests.md` — Add message and source-routing contract tests
- `DMS-1325` — `06-e2e-kafka-scenarios.md` — Replace legacy Kafka E2E expectations
- `DMS-1326` — `07-ops-docs-runbooks.md` — Add setup, monitoring, recovery, and security runbooks

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- Every story's scoped implementation, documentation, and acceptance evidence is delivered.
- Pull requests trace test identifiers to the applicable `CDC-INV-*` contract IDs without
  copying design requirements into epic or story text.
- The evidence owned by the stories passes in the supported connector-image, provider,
  broker-backed, contract, E2E, and operational test layers.
- Operator documentation is checked against the shipped tooling and links back to the
  owning design sections for behavior.

Design exclusions and deferrals remain owned by the linked design documents; changing
them requires changing the owner rather than this epic.
