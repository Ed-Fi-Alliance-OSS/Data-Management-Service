---
jira: TBD
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

The linked design documents are the acceptance authority. This epic only partitions the
implementation work and does not restate connector, topic, message, durability,
readiness, or recovery contracts.

## Outcome

Deliver the relational CDC/Kafka integration, its provider and connector tooling,
verification, local/E2E setup, and operator documentation through the story set below.

## Stories

- `TBD` — `00-documentcache-cdc-prerequisites.md` — Add deployment-owned CDC binding and readiness
- `TBD` — `01-cdc-ddl-support.md` — Emit/provision provider CDC key and database support
- `TBD` — `02-connector-template-generation.md` — Generate PostgreSQL and SQL Server connector templates
- `TBD` — `03-document-state-transform.md` — Add the DMS-specific relational record transform
- `TBD` — `04-bootstrap-enable-kafka-cdc.md` — Add explicit local/bootstrap connector registration
- `TBD` — `05-message-contract-tests.md` — Add message and source-routing contract tests
- `TBD` — `06-e2e-kafka-scenarios.md` — Replace legacy Kafka E2E expectations
- `TBD` — `07-ops-docs-runbooks.md` — Add setup, monitoring, recovery, and security runbooks

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- Every story's scoped implementation and documentation is delivered.
- Pull requests trace tests to the applicable design sections without copying their
  requirements into epic or story acceptance text.
- The connector-image, provider, broker-backed, contract, E2E, and operational
  verification required by the owning design documents passes in the supported lanes.
- Operator documentation is checked against the shipped tooling and links back to the
  owning design sections for behavior.

Design exclusions and deferrals remain owned by the linked design documents; changing
them requires changing the owner rather than this epic.
