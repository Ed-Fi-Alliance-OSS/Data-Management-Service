---
jira: DMS-1326
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/cdc-streaming.md
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced documents own the architecture, contracts, constraints, and deferrals. This
story documents the shipped implementation and must link to those owners rather than
restate them.

## Outcome

Publish verified operator guidance for the implemented relational CDC capability.

## Dependencies

- Depends on 18-07 and the completed E19 setup, status, and lifecycle tooling.

## Implementation Scope

- Document local opt-in, production-like prerequisites, setup, observation, and
  troubleshooting for both providers.
- Document the shipped topic, connector, consumer, binding-state, security, retention,
  sizing, and telemetry operations.
- Document only the implemented restart, recovery, containment, source-replacement, and
  destructive-retirement commands.
- Cross-link E18 projection/restamp guidance and the design-owned deferred workflows.
- Add documentation checks against command help, templates, status output, and test
  fixtures.

## Acceptance Evidence

- Runbook commands are exercised against the supported PostgreSQL and SQL Server workflows.
- Documentation tests detect drift from the shipped configuration, status, and lifecycle
  surfaces.
- Every behavioral, security, recovery, or compatibility statement links to its owning
  design section instead of reproducing its normative algorithm or value table.
- Destructive procedures are verified against the implemented guarded operations.

## Not Assigned to This Story

- Cloud-provider-specific instructions and consumer product implementation guidance are
  separate work.
- Design changes must be made in the owning documents, not in the runbook.
