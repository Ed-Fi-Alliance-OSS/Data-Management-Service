---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Enforce Projector Stale-Write and Post-Delete Fencing

## Design References

- [Freshness and reconciliation](../../../cdc-streaming.md#freshness-and-reconciliation)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Implement one provider-equivalent guarded cache write shared by reconciliation and
optional direct fill.

## Dependencies

- Depends on canonical `dms.Document` representation stamps and is required by 18-03.

## Deliverables

1. Define the guarded cache write contract.
2. Implement PostgreSQL and SQL Server guarded upserts.
3. Route every projector/direct-fill write through the shared guard.
4. Report guarded no-ops as observable stale skips.

## Acceptance Evidence

- Provider integration tests cover out-of-order candidates, concurrent source update,
  deletion racing materialization, duplicate loops, and parity.
- Tests prove payload timestamps do not become a second guard input.
- Telemetry distinguishes stale skips from unexpected database failures.

## Out of Scope

- A distributed lock manager.
- Kafka consumer stale-message handling.
