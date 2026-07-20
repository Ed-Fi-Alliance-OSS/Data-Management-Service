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

## Design References

- [Authoritative relational CDC design](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Implement the relational Debezium/Kafka CDC capability defined by the design references,
including provider setup, connector generation and registration, contract verification,
API-driven E2E coverage, and operator guidance. This epic owns connector-side lifecycle
capture; `18-document-cache` owns the reusable projected upsert source.

## Stories

- `TBD` — `00-documentcache-cdc-prerequisites.md` — Wire target-specific registration and readiness prerequisites
- `TBD` — `01-cdc-ddl-support.md` — Emit/provision provider CDC key and database support
- `TBD` — `02-connector-template-generation.md` — Generate PostgreSQL and SQL Server connector templates
- `TBD` — `03-bootstrap-enable-kafka-cdc.md` — Add explicit local/bootstrap connector registration
- `TBD` — `04-message-contract-tests.md` — Add message and source-routing contract tests
- `TBD` — `05-e2e-kafka-scenarios.md` — Replace legacy Kafka E2E expectations
- `TBD` — `06-ops-docs-runbooks.md` — Add setup, monitoring, recovery, and security runbooks

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- Both providers pass database CDC/key smoke tests and real routed-topic ordering tests.
- Generated and published records pass the topic/message contract suite.
- Connector transforms copy the DMS-projected opaque stream ETag and contain no schema,
  link-configuration, or ETag-composition rules.
- Local and E2E setup registers against selected provisioned data stores without
  hard-coded instance values.
- API deletion remains correct when projection is absent or failing.
- Operator documentation covers supported setup, security, observation, recovery,
  migration, and explicit destructive cleanup.

Anything excluded or deferred by the authoritative design is outside this epic unless a
new decision record changes that design.
