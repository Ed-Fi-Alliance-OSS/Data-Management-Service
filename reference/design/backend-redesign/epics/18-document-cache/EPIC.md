---
jira: TBD
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

The linked design documents are the acceptance authority. This epic only partitions the
implementation work and does not restate projection, schema, recovery, or streaming
contracts.

## Outcome

Deliver the optional document-projection capability and its supporting schema, runtime,
verification, administrative utility, and operator documentation through the story set
below.

## Stories

- `TBD` — `00-documentcache-schema-and-provider-ddl.md` — Finalize schema and provider DDL
- `TBD` — `01-documentcache-configuration-and-target-selection.md` — Add configuration and target selection
- `TBD` — `02-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `TBD` — `03-monotonic-cache-upsert-and-delete-fencing.md` — Implement monotonic cache upsert and post-delete fencing
- `TBD` — `04-async-projector-reconciliation-loop.md` — Add the asynchronous reconciliation loop
- `TBD` — `05-cache-backed-read-path.md` — Add fresh-cache reads with relational fallback
- `TBD` — `06-documentcache-health-readiness-and-telemetry.md` — Add projection health and telemetry
- `TBD` — `07-documentcache-integration-tests-and-runbooks.md` — Add provider integration coverage and runbooks
- `TBD` — `08-representation-restamp-utility.md` — Add the out-of-band representation-restamp utility

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- Every story's scoped implementation and documentation is delivered.
- Pull requests trace tests to the applicable design sections without copying their
  requirements into epic or story acceptance text.
- The provider, concurrency, integration, and operational verification required by the
  owning design documents passes in the supported test lanes.
- Operator documentation is checked against the shipped commands and status surfaces and
  links back to the owning design sections for behavior.

Design exclusions and deferrals remain owned by the linked design documents; changing
them requires changing the owner rather than this epic.
