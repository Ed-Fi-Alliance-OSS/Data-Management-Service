---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Design References

- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [Security, telemetry, and operations](../../../cdc-streaming.md#security-telemetry-and-operations)

The linked design sections define the projection status model, observations, and privacy
rules. This story is only the work package for implementing them.

## Outcome

Expose per-data-store projection status and sanitized telemetry for the implemented
DocumentCache runtime.

## Dependencies

- Depends on 18-00, 18-01, 18-04, and 18-05.
- Supplies the DMS-owned projection observations consumed by E19 status.

## Implementation Scope

- Add the projection status model and current-source observation adapter.
- Integrate audit, scheduling, target, prerequisite, and safety-state observations.
- Add health/status serialization, structured logs, and metrics.
- Keep connector aggregation outside the DMS projection surface.

## Acceptance Evidence

- Status-model tests cover every projection state and transition defined by the referenced
  design sections.
- Provider and API integration tests cover observation behavior, target isolation, and
  sanitization.
- Polling tests verify the health surface independently from background work execution.

## Not Assigned to This Story

- Durable connector binding, connector status, and deployment aggregation are assigned to
  E19.
- External dashboards are deployment work.
