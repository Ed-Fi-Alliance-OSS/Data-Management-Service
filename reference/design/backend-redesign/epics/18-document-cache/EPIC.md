---
jira: DMS-1308
source_spike: DMS-1246
related:
  - DMS-1245
---

# Epic: `dms.DocumentCache` Projection

## Design References

- [Configuration, integration, readiness, and operations](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Relational data model](../../design-docs/data-model.md)

The linked design documents own the normative projection, schema, recovery, and streaming
contracts. This epic partitions implementation work, and its stories own the executable
acceptance evidence for those contracts.

## Outcome

Deliver the optional document-projection capability and its supporting schema, runtime,
verification, administrative utility, and operator documentation through the story set
below.

## Stories

- `DMS-1310` — `00-documentcache-schema-and-provider-ddl.md` — Finalize schema and provider DDL
- `DMS-1311` — `01-documentcache-configuration-and-target-selection.md` — Add configuration and target selection
- `DMS-1312` — `02-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `DMS-1313` — `03-monotonic-cache-upsert-and-delete-fencing.md` — Implement monotonic cache upsert and post-delete fencing
- `DMS-1314` — `04-async-projector-reconciliation-loop.md` — Add the asynchronous reconciliation loop
- `DMS-1315` — `05-cache-backed-read-path.md` — Add fresh-cache reads with relational fallback
- `DMS-1316` — `06-documentcache-health-readiness-and-telemetry.md` — Add projection health and telemetry
- `DMS-1317` — `07-documentcache-integration-tests-and-runbooks.md` — Add provider integration coverage and runbooks
- `DMS-1318` — `08-representation-restamp-utility.md` — Add the out-of-band representation-restamp utility

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- Every story's scoped implementation, documentation, and acceptance evidence is delivered.
- Pull requests trace test identifiers to the applicable `CDC-INV-*` contract IDs without
  copying design requirements into epic or story text.
- The evidence owned by the stories passes in the supported provider, concurrency,
  integration, performance, and operational test layers.
- Operator documentation is checked against the shipped commands and status surfaces and
  links back to the owning design sections for behavior.

Design exclusions and deferrals remain owned by the linked design documents; changing
them requires changing the owner rather than this epic.
