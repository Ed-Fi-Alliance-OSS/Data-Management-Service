---
jira: DMS-1316
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Design References

- **Projection health and deployment-owned CDC readiness**: reference/design/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness
- **Security, telemetry, and operations**: reference/design/cdc-streaming.md#security-telemetry-and-operations

The referenced design sections define the projection status model, observations, and privacy
rules. This story is only the work package for implementing them.

## Outcome

Expose separate per-data-store projection operational-health and caught-up status plus
sanitized queue/lifecycle telemetry without coupling normal API routing to projection.

## Dependencies

- Depends on 18-00, 18-01, 18-04, and 18-05.
- Supplies the DMS-owned projection observations consumed by E19 status.

## Implementation Scope

- Add the projection status model and current-source observation adapter.
- Compose operational health from process eligibility, durable lifecycle `Tracking`, and
  a clear cache-ahead latch. Compose caught-up from operational health plus an indexed
  queue-empty observation in the same provider-consistent statement.
- Report lifecycle, queue presence, oldest work, worker/concurrency/backoff state,
  bounded backlog estimates, cache-ahead state, and bounded per-document failure
  diagnostics. Prohibit unbounded document labels and routine exact backlog counts.
- Keep enqueue failures distinct from processing failures: enqueue failure rejects its
  canonical transaction; processing failure leaves durable work.
- Treat `Resetting`, `Rebuilding`, `Disabled`, missing/disabled enqueue triggers, and a
  set latch as projection non-operational while leaving canonical API health/routing
  independent.
- Add health/status serialization, structured logs, and metrics.
- Keep connector aggregation outside the DMS projection surface.

## Acceptance Evidence

- Status-model tests cover every projection state and transition defined by the referenced
  design sections.
- Provider and API integration tests cover observation behavior, target isolation, and
  sanitization.
- Polling tests verify the health surface independently from background work execution.
- Scale tests prove health/caught-up/oldest-work polling performs no source/cache scan and
  remains independent of total document cardinality.

## Not Assigned to This Story

- Durable connector binding, connector status, and deployment aggregation are assigned to
  E19.
- External dashboards are deployment work.
