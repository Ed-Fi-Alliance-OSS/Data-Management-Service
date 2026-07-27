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

- Depends on 18-00 through 18-03, including the lifecycle command/result contracts and
  preflight classifications from 18-01.
- Supplies projection state to 18-05, 18-06, and E19.

## Implementation Scope

- Add the target supervisor and isolated worker scopes.
- Add provider-specific fair oldest-work paging with bounded page memory, poison-item
  traversal, cursor wraparound, duplicate-replica safety, restart from durable work, and
  long-outage backlog recovery.
- Integrate materialization, the shared cache writer, failure handling, and administrative
  recovery.
- Own one shared database-scoped session-mutex adapter with the ADR's exact PostgreSQL and
  SQL Server identities and execute the 18-01 command contracts for guarded new-empty
  `Disabled -> Tracking` and offline activation/deactivation. Guarded new-empty execution
  owns the one-time writer-blocking `dms.Document` lock, exclusive state-row lock,
  clear-latch and empty canonical/cache/work checks, SQL Server prerequisite validation
  immediately before activation, and racing-insert safety. Also own `Resetting`
  orchestration for online cache rebuild and offline internal-only cache-ahead recovery,
  plus explicit integrity scrub. Scrub preflight admits only lifecycle `Tracking` with a
  clear cache-ahead latch and rejects any other lifecycle or a latch already set before its
  O(N) scan or mutation. Scrub may set the latch for current cache-ahead state but never
  clears it. All administrative entry points, including 18-08, consume this adapter rather
  than deriving a lock from logical target or connection metadata. The adapter executes
  every coordinator-issued administrative database mutation on its mutex-owning physical
  session across separate short transactions, and never transparently reconnects under
  presumed ownership.
- Keep the operation-specific clear boundaries distinct. Before online rebuild enters
  `Resetting`, atomically require lifecycle `Tracking` or `Rebuilding` and a clear
  cache-ahead latch; a set latch rejects the command without changing lifecycle, cache,
  work, or latch state and directs the operator to cache-ahead recovery or containment.
  With the guard satisfied, online rebuild preserves pending work and clears only cache
  while enqueueing continues. Offline deactivation and internal-only cache-ahead recovery
  clear cache and work under their required writer fences. Reject the simple
  activation/deactivation toggle for active or historical CDC binding/consumer state,
  and reject internal-only recovery when downstream publication is possible or uncertain.
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
- Provider tests cover administrative exclusion through different aliases of the same
  physical database, concurrent administration of different databases on one server or
  cluster, SQL Server's common `public` lock scope across eligible caller principals,
  and session loss that releases the mutex to a replacement while preventing the former
  coordinator from beginning or committing lifecycle, clear, seed, scrub, or restamp DML
  after replacement acquisition. They also cover `Resetting` crash/retry, poison work
  exhausting seed capacity, bounded backlog amplification, restart from the beginning,
  and no source scan on ordinary restart.
- PostgreSQL and SQL Server recovery tests prove online rebuild does not discard pending
  work and rejects a set cache-ahead latch with lifecycle, cache, work, and latch state
  unchanged; guarded new-empty activation rejects nonempty databases and safely resolves
  both outcomes of a racing insert; offline activation/deactivation enforce their writer
  fences and downstream-history eligibility; internal-only cache-ahead recovery clears
  stale work so a pre-recovery higher requirement cannot later be acknowledged; and
  possibly published state remains latched and intact.
- SQL Server activation tests prove an unsatisfied provider prerequisite leaves lifecycle,
  cache, and work state unchanged, and that retry succeeds after correction.
- PostgreSQL and SQL Server scrub tests prove only clear-latch `Tracking` is admitted;
  `Disabled`, `Resetting`, `Rebuilding`, and a pre-existing set latch reject before the
  relationship scan with lifecycle, cache, work, and latch state unchanged. An admitted
  scrub may set the latch after detecting current cache-ahead state but never clears it.
- Configuration tests cover the execution settings owned by the integration design.

## Not Assigned to This Story

- Connector and combined CDC status are assigned to E19.
- Health endpoint shaping is assigned to 18-06.
