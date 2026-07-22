---
jira: DMS-1312
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add Reusable Caller-Agnostic Document Materialization

## Design References

- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md

The referenced design sections define the materialized representation and coherence rules.
This story is only the work package for implementing them.

## Outcome

Add the reusable cache-projection materializer used by reconciliation, optional direct
fill, and CDC fixtures.

## Dependencies

- Depends on 18-00 plus the relational read/reconstitution and update-tracking services.
- Unblocks 18-03 through 18-05 and supplies representative records to E19 tests.

## Implementation Scope

- Add the materializer interface, result model, and runtime implementation.
- Reuse compiled read plans, reconstitution, and the shared served-ETag composer.
- Add source-coherence and result-invariant validation at the materializer boundary.
- Add representative materialized-document fixtures for projection and CDC verification.

## Acceptance Evidence

- Unit and provider integration tests cover every materializer state and invariant owned by
  the referenced design sections.
- Concurrency fixtures exercise source changes at the materializer boundary.
- Representation fixtures are shared with the CDC test work rather than redefining the
  public message contract in this story.

## Not Assigned to This Story

- Cache persistence and reconciliation scheduling are assigned to 18-03 and 18-04.
- Kafka envelope shaping is assigned to E19.
