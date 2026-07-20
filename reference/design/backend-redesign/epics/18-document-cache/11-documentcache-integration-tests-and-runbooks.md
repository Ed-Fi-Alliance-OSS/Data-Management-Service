---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Design References

- [Authoritative projection and CDC design](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Validate the completed projection feature across providers and publish operator guidance
that links to, rather than restates, the authoritative design.

## Dependencies

- Depends on the remaining E18 stories and informs CDC story 17-06.

## Deliverables

1. Add provider fixtures for capability combinations, projection, fallback, failure,
   restart, fencing, health, and rebuild.
2. Exercise CDC projection-completeness transitions without requiring a Kafka connector.
3. Publish DocumentCache operation/troubleshooting guidance and cross-link CDC connector
   operations separately.

## Acceptance Evidence

- PostgreSQL and SQL Server integration tests cover all completed E18 story outcomes,
  including `StreamEtag` consistency, metadata consistency, lower-version gaps, fair
  retry, and API independence.
- Rebuild tests use ordinary reconciliation and never introduce a separate backfill
  workflow.
- Runbook procedures are checked against implemented configuration, health output, and
  recovery behavior.

## Out of Scope

- Kafka connector setup, ACLs, offsets, and topic management.
- Consumer application guidance.
