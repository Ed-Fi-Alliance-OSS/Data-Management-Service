---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration and Target Selection

## Design References

- [Configuration and projection target selection](../../../cdc-streaming.md#configuration-and-projection-target-selection)
- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)

The linked design sections define target selection, validation, and lifecycle behavior.
This story is only the work package for implementing them.

## Outcome

Implement the DMS configuration and target-resolution layer used by the projection
workers, cache reads, and health reporting.

## Dependencies

- May proceed alongside 18-00.
- Supplies target contexts to E18 stories 18-04 through 18-06 and target observations to
  E19.

## Implementation Scope

- Add strongly typed configuration binding and validation.
- Add target normalization, resolution, refresh, and replacement lifecycle services.
- Add provider-prerequisite validation at the target boundary.
- Add supported appsettings examples that link to the authoritative design.
- Add target-scoped diagnostics needed by health reporting.

## Acceptance Evidence

- Configuration and lifecycle tests cover the states and transitions enumerated by the
  referenced design sections.
- Provider integration tests cover target prerequisite validation and isolation.
- Appsettings and diagnostics are verified against the implementation and defer behavioral
  explanation to the design owner.

## Not Assigned to This Story

- Projector scheduling, cache reads, and health aggregation are assigned to later E18
  stories.
- Durable CDC binding and connector lifecycle are assigned to E19.
