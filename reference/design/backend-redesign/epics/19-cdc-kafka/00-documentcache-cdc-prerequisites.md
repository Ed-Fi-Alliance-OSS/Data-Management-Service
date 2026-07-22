---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Add Deployment-Owned CDC Binding and Readiness

## Design References

- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [V1 readiness scope](../../../cdc-streaming.md#v1-readiness-scope)
- [Provider source-position barrier](../../../cdc-streaming.md#provider-source-position-barrier)
- [Source-history continuity](../../../cdc-streaming.md#source-history-continuity)
- [Deployment-owned physical source binding](../../../cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)

The linked design sections define binding, readiness, continuity, and lifecycle behavior.
This story is only the work package for implementing them.

## Outcome

Add deployment-owned CDC state and status services that combine DMS projection
observations with provider, Kafka, and connector observations.

## Dependencies

- Consumes target and projection observations from 18-01 and 18-06.
- Consumes provider artifacts from 19-01 and connector configuration/offset shapes from
  19-02.
- Supplies state and status behavior to 19-04.

## Implementation Scope

- Add CDC target input and validation models.
- Add binding and incident state abstractions plus the local state-store implementation.
- Add guarded binding lifecycle operations used by bootstrap and teardown.
- Add provider source-position and source-history adapters.
- Add per-target and aggregate status evaluation with sanitized diagnostics and telemetry.

## Acceptance Evidence

- State-store and lifecycle tests cover the binding and incident transitions in the
  referenced design sections.
- PostgreSQL and SQL Server adapter tests cover position, continuity, and failure
  classifications.
- Status tests cover the complete design-owned readiness input matrix and aggregation.
- API integration tests preserve the separation between deployment status and DMS request
  routing.

## Not Assigned to This Story

- DMS projection implementation is assigned to E18.
- Provider object provisioning, connector rendering, and Connect REST orchestration are
  assigned to 19-01, 19-02, and 19-04.
