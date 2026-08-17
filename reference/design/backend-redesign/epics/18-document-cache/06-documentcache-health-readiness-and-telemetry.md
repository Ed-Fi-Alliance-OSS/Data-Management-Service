---
jira: DMS-1316
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Design References

- **Projection health and deployment-owned CDC readiness**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness
- **Security, telemetry, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations

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

## Resolved Health/Status and Telemetry Scope

### Component Boundary

- Add one DMS-owned projection status composition service. It consumes the 18-01 target
  registry snapshot, the 18-04 projection observation store, and the provider
  current-source observation adapter described below. The exact type names may follow local
  conventions, but the boundary is one status pipeline, not separate endpoint, CLI, and
  E19 models with duplicate classifiers.
- Do not serialize the existing internal 18-01 diagnostic snapshot directly. It is
  intentionally an internal domain snapshot. This story owns the stable JSON-facing status
  DTOs and maps internal registry/projection observations into that contract.
- The status service does not resolve targets, refresh CMS, execute lifecycle commands,
  start workers, wait for work to drain, run direct fill, read cache rows, inspect Kafka,
  inspect provider CDC artifacts, or calculate deployment readiness. It observes the
  current configured target state only.
- The status service reports every explicit `DocumentCache:Targets` entry. Unconfigured
  databases are omitted, even when they are loaded in CMS or are serving API traffic.

### Status Surface and JSON Contract

- Add a dedicated read-only DocumentCache projection status endpoint under the existing
  health module at `GET /health/document-cache`. Do not make projection state part of the
  existing `/health` pass/fail result, and do not register an `IHealthCheck` whose
  unhealthy result can remove a normal API replica from service.
- The endpoint returns a successful HTTP response when the status request itself can be
  evaluated and serialized. Target-level `nonOperational`, `notCaughtUp`, or `unknown`
  states are data in the response body, not endpoint failures. Use ordinary request
  failures only for inability to run the status surface itself, such as cancellation or an
  unexpected serialization/programming failure.
- Use stable `System.Text.Json` DTOs with lower-camel property names and lower-camel enum
  strings. Do not expose numeric enum values. Reuse the 18-01 nested target-key shape:
  `"targetKey": { "tenantKey": "", "dataStoreId": 1 }`.
- Sort target entries deterministically by normalized tenant key and `DataStoreId` so
  tests, operators, and E19 consumers can compare output without relying on dictionary
  order.
- Model operational health and caught-up status as separate tri-state fields:
  - `operationalHealth.status`: `operational`, `nonOperational`, or `unknown`.
  - `caughtUp.status`: `caughtUp`, `notCaughtUp`, or `unknown`.
  Each field also carries a bounded reason/category string from a fixed enum and a
  sanitized diagnostic message when useful.
- Target output includes, when available: resolution and eligibility state, generation,
  provider, opaque physical-source fingerprint, inventory and enqueue-trigger validation
  statuses, SQL Server prerequisite statuses, lifecycle, cache-ahead latch state,
  operational health, caught-up status, queue summary, execution state, active
  administrative command, last ended target diagnostic, bounded document diagnostics,
  bounded target diagnostics, and effective queue/read/admin settings.
- E19 consumes this per-target output as an input. This story does not add
  `canRegisterConnector`, expected-source comparison, source-drift retention,
  binding-generation fields, connector status, Kafka lag, or a deployment aggregate.

### Provider Current-Source Observation

- Add one provider adapter per relational provider for current projection status
  observation. It should be separate from the 18-04 work pager because polling status is
  not queue processing and must not advance cursors or apply poison traversal behavior.
- The adapter executes one read-only, provider-consistent statement per target to observe
  the durable status facts used for caught-up:
  - `dms.DocumentCacheState(StateId = 1).ProjectionLifecycleState`;
  - `dms.DocumentCacheState(StateId = 1).CacheAheadRecoveryRequired`;
  - whether any row exists in `dms.DocumentProjectionWork`; and
  - the oldest work row's `FirstEnqueuedAt` and provider-computed age when work exists.
- The same statement must decide the caught-up predicate. Do not combine a lifecycle read
  from one command with queue emptiness from another command. A statement failure returns
  `unknown`; do not reuse a previous successful caught-up or lifecycle observation.
- Oldest-work observation uses `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`
  with `ORDER BY FirstEnqueuedAt, DocumentId` and a single-row limit. Queue presence uses
  indexed existence. The status path never scans `dms.Document`, never scans
  `dms.DocumentCache`, and never runs a source/cache/work relationship scan.
- Routine exact backlog counts are out of scope. If a provider can supply a cheap bounded
  or catalog-estimated backlog value, expose it with an explicit estimate kind; otherwise
  report backlog estimate as unavailable. Exact counts belong only to explicit operator
  diagnostics outside normal health polling.
- A missing or invalid `DocumentCacheState` row is a known non-operational durable-state
  observation and makes caught-up `notCaughtUp`. An unreadable state row or failed status
  statement makes caught-up `unknown`. A missing, disabled, invalid, or unreadable enqueue
  trigger comes from the 18-01 inventory observation and is known non-operational.

### Composition Rules

- Process eligibility is composed from the current 18-01 target observation and the
  current-generation 18-04 runtime observation. A resolved target must have satisfied
  inventory, satisfied enqueue-trigger validation, provider-compatible metadata,
  satisfied provider prerequisites, and a current running/unfaulted execution context.
- If a target is configured but unresolved, provider-mismatched, inventory-invalid,
  prerequisite-ineligible, removed, replaced, cancelled, or faulted, operational health is
  `nonOperational` with the corresponding fixed reason. If the adapter or status service
  cannot determine a required fact, operational health is `unknown`.
- `Disabled`, `Resetting`, `Rebuilding`, and a set cache-ahead latch are known
  `nonOperational` durable states. Queue presence does not affect operational health.
- Waiting for the global concurrency gate, being idle between polls, and document-scoped
  poison/backoff diagnostics do not by themselves make the target non-operational. A
  target-level provider backoff that prevents ordinary work until a retry time is reported
  in execution state and makes operational health `nonOperational` until the retry window
  has elapsed.
- Caught-up status is `caughtUp` only when process eligibility is true and the same
  provider statement observes lifecycle `Tracking`, a clear cache-ahead latch, and no
  durable work row. If process eligibility is false for a known reason, caught-up is
  `notCaughtUp` with that reason. If current durable facts cannot be observed, caught-up is
  `unknown`.
- Last-ended target diagnostics and active administrative command observations are
  reported for operator context, but only current-generation target observations
  contribute to operational health and caught-up status.

### Diagnostics, Privacy, and Telemetry

- Sanitize every diagnostic string through the existing logging/diagnostic sanitizer and
  cap string length. Cap target diagnostics, administrative phase diagnostics, poison
  traversal diagnostics, and document-scoped failure diagnostics by the effective projector
  page size.
- Bounded document-scoped diagnostics may include `DocumentId`, category, observed time,
  and next retry time. Do not include `DocumentUuid`, document JSON, request bodies,
  authorization subjects, connection strings, tenant display names, physical server or
  database names, query parameter values, profile payloads, or unsanitized provider error
  text.
- Never use document identifiers, tenant display names, connection strings, raw physical
  source identifiers, or unbounded resource names as metric labels. Status and metrics may
  label by provider, sanitized normalized target key, lifecycle, operational-health status,
  caught-up status, bounded reason/category, command, and phase.
- Extend the existing DocumentCache projection meter rather than creating an unrelated
  observability namespace. Add status-observation counters and provider-observation
  duration histograms; record queue-present/caught-up/operational states as bounded labels
  on observations and oldest-work age as a histogram when work exists.
- Keep enqueue-failure telemetry at the canonical write/provider-exception boundary, not
  in the projector loop. A classified enqueue failure records a distinct enqueue-failure
  log/metric and rejects the complete canonical transaction; it does not create a
  processing-failure diagnostic or durable work row. Provider-specific SQLSTATE or SQL
  Server error numbers are implementation details, not the public status contract.

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
