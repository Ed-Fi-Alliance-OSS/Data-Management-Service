---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add the Asynchronous DocumentCache Reconciliation Loop

## Design References

- [Freshness and reconciliation](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation)
- [Bounded in-process execution policy](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#bounded-in-process-execution-policy)
- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)

The linked design sections define discovery, reconciliation, scheduling, failure, and
recovery behavior. This story is only the work package for implementing them.

## Outcome

Implement the hosted per-data-store reconciliation service and its administrative
projection-recovery entry point.

## Dependencies

- Depends on 18-00 through 18-03.
- Supplies projection state to 18-05, 18-06, and E19.

## Implementation Scope

- Add the target supervisor and isolated worker scopes.
- Add provider-specific incremental discovery and full-audit query adapters.
- Integrate materialization, the shared cache writer, failure handling, and administrative
  recovery.
- Add scheduling, bounded-execution, cancellation, and sanitized telemetry support.

## Acceptance Evidence

- Provider, multi-data-store, concurrency, query-plan, and scheduling tests cover the
  reconciliation states and transitions in the referenced design sections.
- Recovery tests exercise the implemented administrative entry point and its transactional
  integration.
- Configuration tests cover the execution settings owned by the integration design.

## Not Assigned to This Story

- Connector and combined CDC status are assigned to E19.
- Health endpoint shaping is assigned to 18-06.
