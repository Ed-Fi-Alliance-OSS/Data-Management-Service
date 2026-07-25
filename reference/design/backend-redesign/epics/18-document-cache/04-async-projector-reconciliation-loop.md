---
jira: DMS-1314
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add the Asynchronous DocumentCache Reconciliation Loop

## Design References

- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Bounded in-process execution policy**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#bounded-in-process-execution-policy
- **Baseline, rebuild, deactivation, and scrub**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#baseline-rebuild-deactivation-and-scrub
- **Cache-ahead invariant recovery**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-ahead-invariant-recovery
- **Projection health and deployment-owned CDC readiness**: reference/design/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness

The referenced design sections define discovery, reconciliation, scheduling, failure, and
recovery behavior. This story is only the work package for implementing them.

## Outcome

Implement the hosted per-data-store durable-work service and serialized administrative
projection lifecycle/recovery entry points.

## Dependencies

- Depends on 18-00 through 18-03.
- Supplies projection state to 18-05, 18-06, and E19.

## Implementation Scope

- Add the target supervisor and isolated worker scopes.
- Add provider-specific fair oldest-work paging with bounded page memory, poison-item
  traversal, cursor wraparound, duplicate-replica safety, restart from durable work, and
  long-outage backlog recovery.
- Integrate materialization, the shared cache writer, failure handling, and administrative
  recovery.
- Own the database-scoped session mutex and execute the 18-01 command boundaries for
  offline activation/deactivation, plus `Resetting` orchestration for online cache
  rebuild, offline internal-only cache-ahead recovery, and explicit integrity scrub.
- Keep the operation-specific clear boundaries distinct: online rebuild preserves pending
  work and clears only cache while enqueueing continues; offline deactivation and
  internal-only cache-ahead recovery clear cache and work under their required writer
  fences. Reject the simple activation/deactivation toggle for active or historical CDC
  binding/consumer state, and reject internal-only recovery when downstream publication
  is possible or uncertain.
- Implement bounded projected-state clearing and windowed/backpressured baseline/rebuild
  seeding through a captured `DocumentId` boundary. Retry pages invalidated by concurrent
  delete, conditionally repair mismatched existing work inside the current page without a
  second scrub/mutex handoff, and persist no baseline cursor in v1.
- Add scheduling, target backoff, cancellation, and sanitized bounded failure diagnostics.

## Acceptance Evidence

- Provider, multi-data-store, concurrency, query-plan, and scheduling tests cover the
  reconciliation states and transitions in the referenced design sections.
- Recovery tests exercise the implemented administrative entry point and its transactional
  integration.
- Provider tests cover administrative exclusion, session loss, `Resetting` crash/retry,
  poison work exhausting seed capacity, bounded backlog amplification, restart from the
  beginning, and no source scan on ordinary restart.
- Recovery tests prove online rebuild does not discard pending work, offline
  activation/deactivation enforce their writer fences and downstream-history eligibility,
  internal-only cache-ahead recovery clears stale work so a pre-recovery higher
  requirement cannot later be acknowledged, and possibly published state remains latched
  and intact.
- Configuration tests cover the execution settings owned by the integration design.

## Not Assigned to This Story

- Connector and combined CDC status are assigned to E19.
- Health endpoint shaping is assigned to 18-06.
