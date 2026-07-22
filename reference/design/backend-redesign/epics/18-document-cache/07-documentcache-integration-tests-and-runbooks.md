---
jira: DMS-1317
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/cdc-streaming.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced design documents define behavior and operator constraints. This story adds
cross-feature evidence and implementation-specific guidance without restating them.

## Outcome

Validate the completed E18 capability across providers and publish DocumentCache operator
guidance.

## Dependencies

- Depends on 18-00 through 18-06 and informs E19 operator documentation.

## Implementation Scope

- Add cross-story PostgreSQL and SQL Server fixtures for the completed projection feature.
- Add restart, failure, recovery, rebuild, and mixed-target scenarios.
- Publish operation and troubleshooting guidance for the shipped commands, configuration,
  status, and telemetry.
- Cross-link E19 procedures where connector or downstream state becomes relevant.

## Acceptance Evidence

- The provider integration matrix covers every E18 `CDC-INV-*` contract assignment not
  already proven in a narrower story suite.
- Runbook steps are exercised against the implemented commands and status output.
- Runbooks link to the owning design sections for contracts, recovery constraints, and
  deferrals instead of copying them.

## Not Assigned to This Story

- Kafka infrastructure, connector, and consumer operation are assigned to E19.
