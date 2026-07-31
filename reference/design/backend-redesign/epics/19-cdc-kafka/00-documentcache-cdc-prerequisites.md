---
jira: DMS-1319
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1246
---

# Story: Add Deployment-Owned CDC Binding and Readiness

## Design References

- **Projection health and deployment-owned CDC readiness**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness
- **V1 readiness scope**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope
- **Provider source-position barrier**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity
- **Deployment-owned physical source binding**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding

The referenced design sections define binding, readiness, continuity, and lifecycle behavior.
This story is only the work package for implementing them.

## Outcome

Add deployment-owned CDC state and status services that combine DMS projection
operational-health and caught-up observations with provider, Kafka, and connector
observations.

## Dependencies

- Consumes target and projection observations from 18-01 and 18-06.
- Integrated readiness scenarios consume the atomic projection path from 18-03 and durable
  queue processing from 18-04.
- Consumes provider artifacts from 19-01 and connector configuration/offset shapes from
  19-02.
- Supplies state and status behavior to 19-04.

## Implementation Scope

- Add CDC target input and validation models.
- Add binding and incident state abstractions plus the local state-store implementation.
- Add the shared deterministic CDC artifact-name helper with the binding model. Given the
  deployment key, opaque instance key, generation, provider, and binding record, it returns
  the provider artifact names consumed by 19-01 and the connector/topic names consumed by
  19-02/19-04. It must not derive names from tenant display names, connection strings,
  server names, database names, or connector JSON.
- Add guarded binding lifecycle operations used by bootstrap and teardown.
- Add provider source-position and source-history adapters.
- Add per-target and aggregate status evaluation with sanitized diagnostics and telemetry.
- Consume lifecycle/latch/process eligibility as projection operational health and indexed
  queue absence as projection caught-up status. Do not consume scan recency, exact-zero
  relationship counts, or process-local completeness cursors.
- Compose initial CDC admission from caught-up status, the provider heartbeat barrier, and
  a second caught-up observation for the same source while canonical write admission is
  closed. After admission, queue growth does not revoke CDC admission or normal API
  routing.

## Acceptance Evidence

- State-store and lifecycle tests cover the binding and incident transitions in the
  referenced design sections.
- Artifact-name helper tests cover deterministic output, provider-specific identifier
  limits/sanitization, generation isolation, and the complete name inventory consumed by
  19-01, 19-02, and 19-04.
- PostgreSQL and SQL Server adapter tests cover position, continuity, and failure
  classifications.
- Status tests cover the complete design-owned readiness input matrix and aggregation.
- Status tests distinguish projection operational failure, projection backlog, enqueue
  failure, connector failure, continuity failure, and ordinary canonical API health.
- API integration tests preserve the separation between deployment status and DMS request
  routing.

## Not Assigned to This Story

- DMS projection implementation is assigned to E18.
- Provider object provisioning, connector rendering, and Connect REST orchestration are
  assigned to 19-01, 19-02, and 19-04.
