---
jira: DMS-1313
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Implement Monotonic Cache Upsert and Post-Delete Fencing

## Design References

- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md

The referenced design sections define cache-write ordering, concurrency, lifecycle, and
publication implications. This story is only the work package for implementing them.

## Outcome

Implement one provider-equivalent cache writer shared by reconciliation and optional
direct fill.

## Dependencies

- Depends on 18-00, 18-02, E10 representation stamps, and E11 delete behavior.
- Unblocks the 18-04 projector and 18-05 cache-backed read path.

## Implementation Scope

- Add the provider-specific cache DML and transaction adapters.
- Integrate the writer with the materializer result and projection safety state.
- Route projector and direct-fill writes through the shared component.
- Add sanitized outcome metrics and performance coverage.

## Acceptance Evidence

- PostgreSQL and SQL Server concurrency tests cover the writer interleavings and outcomes
  required by the referenced design sections.
- Provider tests cover integration with schema constraints, delete lifecycle, and safety
  state.
- Performance evidence compares the required projector and direct-fill workload modes.

## Not Assigned to This Story

- Difference discovery and recovery orchestration are assigned to 18-04.
- Consumer ordering behavior is assigned to the Kafka contract and E19 verification.
