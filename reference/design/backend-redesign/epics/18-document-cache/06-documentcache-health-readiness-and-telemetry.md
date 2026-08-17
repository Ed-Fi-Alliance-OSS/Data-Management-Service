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
  refresh inventory, refresh enqueue-trigger validation, refresh SQL Server prerequisites,
  start workers, wait for work to drain, run direct fill, read cache rows, inspect Kafka,
  inspect provider CDC artifacts, or calculate deployment readiness during a status
  request. It observes the current configured target state only.
- Active administrative command and last-ended command diagnostics are process-local
  observations from the 18-04 observation store. 18-06 reports only command observations
  known to the current DMS process/replica and does not add durable command-phase
  persistence or cross-replica sharing. Durable lifecycle states such as `Resetting` and
  `Rebuilding` remain authoritative durable state, but they do not imply a resumable active
  command name or phase.
- The status service reports every explicit `DocumentCache:Targets` entry. Unconfigured
  databases are omitted, even when they are loaded in CMS or are serving API traffic. An
  authorized request with an empty `DocumentCache:Targets` list returns a successful
  contract response with `contractVersion: 1`, `observedAt`, and `targets: []`.

### Status Surface and JSON Contract

- Add a dedicated read-only DocumentCache projection status endpoint under the existing
  health module at `GET /health/document-cache`. Do not make projection state part of the
  existing `/health` pass/fail result, and do not register an `IHealthCheck` whose
  unhealthy result can remove a normal API replica from service.
- Require authorization for `GET /health/document-cache` because the operator-grade payload
  includes target keys, `DocumentId` diagnostics, opaque physical-source fingerprints,
  effective settings, lifecycle/admin state, queue state, and sanitized diagnostic
  messages. Keep ordinary `/health` anonymous and minimal for load balancers, container
  health, and existing operational probes.
- Use the existing OAuth/JWT validation path, not a new authorization system. Configure
  `DataManagement:DocumentCache:Status:RequiredRole`, with
  `dms-document-cache-operator` as the recommended value, and authorize a valid bearer
  token by checking the configured `JwtAuthentication:RoleClaimType` claim type for that
  role value. Implement this as a dedicated status-endpoint role check or policy; do not
  reuse the normal API `JwtAuthentication:ClientRole` gate unless that code path is
  refactored to accept an explicit required role and to honor
  `JwtAuthentication:RoleClaimType` end to end.
- A missing or invalid token returns `401`, and a valid token without the configured role
  returns `403`. Treat a missing, empty, whitespace-only, or invalid
  `DataManagement:DocumentCache:Status:RequiredRole` as fail-closed by leaving
  `GET /health/document-cache` unmapped so callers receive the ordinary `404` fallback.
  Apply the same behavior in development, test, and production; tests that exercise the
  endpoint must configure an explicit valid role. Do not fail application startup, map the
  endpoint only to return `403`, silently omit fields to make the endpoint anonymous, or
  use Ed-Fi claim sets, resource claims, education organization IDs, namespace prefixes,
  data store IDs, CMS admin permissions, database tables, or a new policy store as the v1
  operator contract.
- The endpoint returns a successful HTTP response when the status request itself can be
  evaluated and serialized. Target-level `nonOperational`, `notCaughtUp`, or `unknown`
  states are data in the response body, not endpoint failures. Use ordinary request
  failures only for inability to run the status surface itself, such as cancellation or an
  unexpected serialization/programming failure.
- Use stable `System.Text.Json` DTOs with lower-camel property names, lower-camel enum
  strings, top-level `contractVersion: 1`, and top-level `observedAt`. Do not expose
  numeric enum values. Reuse the 18-01 nested target-key shape:
  `"targetKey": { "tenantKey": "", "dataStoreId": 1 }`. No public status field or reason
  remains implementation-local once serialized by this endpoint.
- Set top-level `observedAt` to the UTC time when the status service captures the immutable
  registry/runtime snapshot for the request, immediately before bounded-parallel target
  evaluation starts. Each target entry carries `processObservedAt` for the process-local
  registry/runtime observation and `durableObservedAt` for the provider current-source
  statement when that statement succeeds.
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
- Keep stable target objects and component objects present in the JSON contract. Use `null`
  for unavailable scalar values such as `durableObservedAt`, oldest-work timestamp,
  oldest-work age, diagnostic message, active command, and last-ended diagnostic. Use empty
  arrays for bounded diagnostic collections when there are no entries. Use explicit
  lower-camel enum values for component availability and success: `operationalHealth.reason`
  and `caughtUp.reason` are `none` on successful `operational` or `caughtUp` states and
  otherwise carry the selected fixed reason; skipped or failed queue observation uses
  `queueSummary.presence: "unknown"`. Always include `backlogEstimate` as an object; v1
  emits `kind: "unavailable"` and `value: null`.
- Serialize timestamps as UTC ISO 8601 strings with a `Z` offset using `System.Text.Json`
  `DateTimeOffset` values normalized to UTC. Serialize public JSON durations, ages, and
  timeout/settings values as numeric seconds with fractional values allowed, using property
  names that include the unit, such as `oldestWorkAgeSeconds`, `pollIntervalSeconds`,
  `failureBackoffSeconds`, `directFillTimeoutSeconds`, `statusObservationTimeoutSeconds`,
  and `endpointTimeoutSeconds`. Do not use .NET `TimeSpan` strings or ISO 8601 duration
  strings in the status contract.
- Include status tuning values in `effectiveSettings`, including the per-target
  status-observation timeout and top-level endpoint timeout. Omit
  `Status:RequiredRole` entirely from public `effectiveSettings`; it is authorization
  configuration, not projection behavior.
- E19 consumes this per-target output as an input. This story does not add
  `canRegisterConnector`, expected-source comparison, source-drift retention,
  binding-generation fields, connector status, Kafka lag, or a deployment aggregate.

### Provider Current-Source Observation

- Add one provider adapter per relational provider for current projection status
  observation. It should be separate from the 18-04 work pager because polling status is
  not queue processing and must not advance cursors or apply poison traversal behavior.
- Evaluate targets with bounded parallelism capped by the effective
  `Projector:MaxConcurrentTargets`. Add a per-target status-observation timeout setting
  defaulting to five seconds and a top-level endpoint budget defaulting to 30 seconds. The
  endpoint budget starts when the status service captures the request snapshot; each
  target's per-target timeout starts when its bounded-parallel evaluation work item begins
  and is linked to the endpoint budget and caller cancellation.
- A target status-observation timeout serializes that target's required current-source
  facts as `unknown` with reason `statusObservationTimeout` and sanitized diagnostics. A
  provider-observation exception or status statement failure serializes those facts as
  `unknown` with reason `providerObservationFailed` and sanitized diagnostics. Peer targets
  continue and the endpoint still returns a successful response when serialization itself
  succeeds. If the endpoint budget expires before some targets start or finish, serialize
  those targets as `unknown` with reason `statusEndpointTimeout`; caller cancellation still
  follows the normal request-cancellation path.
- Skip the provider current-source statement unless process eligibility is otherwise
  satisfied for the current target generation: resolved target, compatible provider
  metadata, valid inventory and enqueue-trigger observation, satisfied provider
  prerequisites, and a current running/unfaulted runtime observation with no active
  target-level backoff. For targets already known non-operational or unknown from 18-01 or
  18-04 process facts, serialize current durable-state, queue, oldest-work, and
  `durableObservedAt` fields as unavailable and use the process-eligibility reason for
  `operationalHealth` and `caughtUp`. Do not reuse an earlier durable success to fill those
  fields. Previously captured 18-01 validation observations may still appear in their own
  component fields with their observation time, but they are not the 18-06 current-source
  observation.
- The adapter executes one read-only, provider-consistent statement per target to observe
  the durable status facts used for caught-up:
  - `dms.DocumentCacheState(StateId = 1).ProjectionLifecycleState`;
  - `dms.DocumentCacheState(StateId = 1).CacheAheadRecoveryRequired`;
  - whether any row exists in `dms.DocumentProjectionWork`; and
  - the oldest work row's `FirstEnqueuedAt` and provider-computed age when work exists.
- The same statement must decide the caught-up predicate. Do not combine a lifecycle read
  from one command with queue emptiness from another command. A statement failure returns
  `unknown`; do not reuse a previous successful caught-up or lifecycle observation.
- `durableObservedAt` is the provider/database UTC timestamp returned by the same statement
  that reads lifecycle, latch, queue presence, and oldest work; it is `null` when durable
  observation is skipped or fails. Queue age values are computed by the provider statement
  relative to that durable observation time.
- Oldest-work observation uses `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`
  with `ORDER BY FirstEnqueuedAt, DocumentId` and a single-row limit. Queue presence uses
  indexed existence. The status path never scans `dms.Document`, never scans
  `dms.DocumentCache`, and never runs a source/cache/work relationship scan.
- Routine exact backlog counts are out of scope. V1 reports backlog estimates as
  unavailable for both PostgreSQL and SQL Server while still exposing indexed queue
  presence and oldest-work timestamp/age. Exact counts and cheap/catalog-estimated values
  belong only to explicit operator diagnostics or later work outside normal health polling.
- A missing or invalid `DocumentCacheState` row is a known non-operational durable-state
  observation and makes caught-up `notCaughtUp`. An unreadable state row or failed status
  statement makes caught-up `unknown`. A missing, disabled, invalid, or unreadable enqueue
  trigger comes from the 18-01 inventory observation and is known non-operational.

### Composition Rules

- Process eligibility is composed from the current 18-01 target observation and the
  current-generation 18-04 runtime observation. A resolved target must have satisfied
  inventory, satisfied enqueue-trigger validation, provider-compatible metadata,
  satisfied provider prerequisites, and a current running/unfaulted execution context.
- Keep `operationalHealth.reason` and `caughtUp.reason` as single fixed-enum values
  selected by deterministic precedence. Additional simultaneous facts belong in bounded
  diagnostics arrays and component fields, not in multi-reason status values.
- Use this precedence for operational health: `unresolvedTarget`, provider-metadata
  reasons `providerMetadataMissing` then `providerMetadataUnknown`, `providerMismatch`,
  `connectionInputMissing`, `physicalSourceFingerprintFailure`,
  `effectiveSchemaCompatibilityFailure`, `resourceKeyCompatibilityFailure`,
  `inventoryInvalid`, `enqueueTriggerUnavailable`, SQL Server prerequisite reasons
  `sqlServerPrerequisiteFailed` then `unsupportedPrerequisiteIncident`, `targetRemoved`,
  `targetReplaced`, `runtimeNotObserved`, `runtimeCancelled`, `runtimeFaulted`,
  `targetBackoff`, then `statusObservationTimeout` as `unknown` when process eligibility
  otherwise passed but the per-target status-observation timeout expired, then
  `providerObservationFailed` as `unknown` when process eligibility otherwise passed but
  durable facts could not be read, then durable-state reasons `stateMissingOrInvalid`,
  `lifecycleDisabled`, `lifecycleResetting`, `lifecycleRebuilding`, and
  `cacheAheadRecoveryRequired`. Map 18-01 lifecycle-observation failures that prevent
  target eligibility to `stateMissingOrInvalid` when they are known missing or invalid
  state observations, and to `providerObservationFailed` when the state row is unreadable
  or no authoritative durable fact was observed.
- `Disabled`, `Resetting`, `Rebuilding`, and a set cache-ahead latch are known
  `nonOperational` durable states. Queue presence does not affect operational health.
- Represent a current-generation target with no 18-04 runtime health snapshot as a
  not-yet-observed runtime, not as idle. Add fixed execution-state value `notObserved`;
  set `operationalHealth.status` and `caughtUp.status` to `unknown` with reason
  `runtimeNotObserved`. Idle between polls requires an actual current-generation runtime
  snapshot such as `idle` or `waitingForPoll`, with current generation and no cancellation,
  fault, or target backoff; that state does not by itself make the target non-operational
  and allows the durable status statement to run.
- Waiting for the global concurrency gate, being idle between polls, and document-scoped
  poison/backoff diagnostics do not by themselves make the target non-operational. A
  target-level provider backoff that prevents ordinary work until a retry time is reported
  in execution state and makes operational health `nonOperational` until the retry window
  has elapsed.
- Caught-up status is `caughtUp` only when process eligibility is true and the same
  provider statement observes lifecycle `Tracking`, a clear cache-ahead latch, and no
  durable work row. If process eligibility is false for a known reason, caught-up is
  `notCaughtUp` with that reason. If current durable facts cannot be observed, caught-up is
  `unknown`. `caughtUp.reason` uses the same process/durable precedence while the target is
  not operational or unknown; when operational durable facts are readable, queue presence
  uses `queueNotEmpty`, and an empty queue uses no failure reason.
- Target-fatal diagnostics raised outside the projector loop, such as invalid cached JSON
  from direct fill/cache read or deterministic writer invariant failures, are reported as
  bounded target diagnostics and metrics, but they do not directly change
  `operationalHealth.status`. They affect operational health only when the
  current-generation runtime observation has actually faulted, cancelled, or put the target
  into target-level provider backoff.
- Last-ended target diagnostics and active administrative command observations are reported
  for operator context, but only current-generation target observations contribute to
  operational health and caught-up status. Public `activeCommand` and last-ended command
  fields include only observations tied to the current target generation; do not add
  `isCurrentGeneration` or `currentTargetGeneration` fields to expose retained non-current
  command observations in v1.

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
  source identifiers, raw JSON target keys, or unbounded resource names as metric labels.
  Metrics use a deterministic bounded surrogate target label. Keep the full nested
  `targetKey` only in status JSON and sanitized logs where the status contract already
  exposes it.
- Pin the target surrogate as `t1_` plus the first 24 lowercase hex characters of
  `SHA-256(UTF8("document-cache-target-v1\0" + normalizedTenantKey + "\0" + dataStoreId decimal))`.
  Use that value in the metric `target` label and never use the raw JSON target key as a
  metric label.
- Extend the existing DocumentCache projection meter rather than creating an unrelated
  observability namespace. Add and test status instruments
  `edfi.dms.document_cache.status.observations`,
  `edfi.dms.document_cache.status.provider_observation.duration`,
  `edfi.dms.document_cache.status.oldest_work.age`, and
  `edfi.dms.document_cache.enqueue.failures`. Provider-observation duration and
  oldest-work age histograms use seconds with unit `s`; counters use unit `{observation}`
  or `{failure}` as appropriate.
- Metric labels are limited to bounded values such as provider, target surrogate,
  lifecycle, operational-health status, caught-up status, bounded reason/category, command,
  phase, canonical operation, resource kind (`resource` or `descriptor`), and enqueue
  failure category. Pin structured-log event names and property keys for status
  observations and enqueue failures; numeric `EventId` allocation may follow repository
  conventions and is not an external contract.
- Keep enqueue-failure telemetry at the canonical write/provider-exception boundary, not
  in the projector loop. A classified enqueue failure records a distinct enqueue-failure
  log/metric and rejects the complete canonical transaction; it does not create a
  processing-failure diagnostic or durable work row. Ordinary `Disabled` lifecycle writes
  are successful no-work observations and are not enqueue failures. Missing, disabled,
  invalid, or unreadable enqueue-trigger inventory observed by 18-01 remains
  non-operational status data; it is not an enqueue-failure event unless a canonical write
  actually raises a provider exception and rolls back because of that enqueue artifact.
- Use fixed public enqueue-failure categories: `stateMissingOrInvalid` for missing,
  unreadable, or invalid `DocumentCacheState`; `enqueueTriggerUnavailable` for
  canonical-write failures caused by missing, disabled, invalid, or privilege-broken
  enqueue artifacts; `workPersistenceFailed` for failed work-table insert or advance while
  lifecycle is enqueue-enabled; `providerTimeout`; `providerUnavailable`; and
  `unclassifiedProviderFailure`. Do not include SQLSTATE, SQL Server error number,
  lifecycle value, `DocumentId`, `DocumentUuid`, resource names, request bodies, subjects,
  connection strings, or physical identifiers in enqueue-failure metric labels or
  structured-log properties.
- Emit canonical write-path enqueue-failure logs and metrics for any classified enqueue
  exception that rolls back a canonical write, even when the affected data store is not
  configured in `DocumentCache:Targets` or the local projection target is unresolved,
  removed, or replaced. Compute the `t1_` target surrogate from the canonical request's
  normalized tenant key and `dataStoreId`, not from the current configured target list. If
  the write fails before a logical target key can be determined, use the bounded literal
  target label `unknown`. The status endpoint reports enqueue-failure diagnostics under a
  target only when they can be associated with a current configured target.

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

## Clarifying Questions and Answers

### Questions 1

1. Should 18-06 own a process-local bounded observation sink for recent classified enqueue failures that canonical write paths publish to and `GET /health/document-cache` reads, or are enqueue-failure diagnostics in status JSON intended to come only from logs, metrics, or existing durable provider state?
2. When a target's per-target status-observation timeout expires while the top-level endpoint budget remains, should `operationalHealth.reason` and `caughtUp.reason` use existing `providerObservationFailed`, a distinct `statusObservationTimeout`, or another fixed reason value?
3. If the 18-04 projection observation store does not already expose current-generation execution state, target-level backoff, active and last-ended administrative command observations, and target-fatal diagnostics as a typed immutable snapshot, does 18-06 own extending that store or must 18-04 be amended before 18-06 tasking?
4. Does 18-06 own checking in a canonical v1 status-response contract fixture or JSON schema that pins every component property name, enum string, null field, and empty-array behavior for E19 and the follow-on CLI, or should implementation tasks derive the exact DTO shape from the narrative bullets?

### Answers 1

1. 18-06 should own a process-local bounded enqueue-failure observation sink. Canonical write/provider-exception handling publishes classified enqueue failures to that sink and emits the matching log/metric from the same boundary. `GET /health/document-cache` reads the sink for current configured targets only. Do not derive status diagnostics from logs or metrics, and do not add durable enqueue-failure storage in v1; restart or replica changes may lose these recent process-local diagnostics.
2. Add a distinct fixed reason value `statusObservationTimeout`. When process eligibility otherwise passed but the per-target status-observation timeout expires, set both `operationalHealth.status` and `caughtUp.status` to `unknown` with reason `statusObservationTimeout`, set durable fields and queue presence to unavailable/unknown, and do not reuse earlier durable observations. Keep `providerObservationFailed` for provider exceptions or failed status statements, and keep `statusEndpointTimeout` for the top-level endpoint budget.
3. 18-04 must expose the typed immutable current-generation projection snapshot before 18-06 tasking depends on it. If it is missing, amend 18-04 rather than adding a parallel observation store in 18-06. The snapshot should cover execution state, target-level backoff, current-generation active and last-ended administrative command observations, and bounded target-fatal diagnostics; 18-06 only composes and serializes those observations.
4. 18-06 owns checked-in canonical v1 status-response JSON contract fixtures and serialization tests. Include at least an empty-targets payload and a representative populated target payload that pins property names, lower-camel enum strings, UTC timestamps, numeric seconds, null scalars, empty arrays, unavailable backlog shape, deterministic target-key shape, and stable component objects. E19 and the follow-on CLI should consume that fixture contract rather than deriving DTO shape from narrative bullets.

### Questions 2

1. What exact configuration keys should 18-06 add for the per-target status-observation timeout and top-level endpoint timeout, and should invalid or nonpositive values fail startup validation, fall back to defaults, or leave `GET /health/document-cache` unmapped?
2. What public JSON shape and retention policy should the process-local enqueue-failure observation sink use in the v1 status response: a bounded recent-event array under target diagnostics, an aggregate-by-category component, or both?
3. Does 18-06 own converting all DocumentCache metric target labels introduced by earlier E18 stories to the `t1_` target surrogate, or only the new status and enqueue-failure instruments added by this story?

### Answers 2

1. Add `DataManagement:DocumentCache:Status:StatusObservationTimeout` with a default of five seconds and `DataManagement:DocumentCache:Status:EndpointTimeout` with a default of 30 seconds. They are independent positive `TimeSpan` options under the same `Status` section as `RequiredRole`; missing timeout keys use the defaults. An omitted `Status` section therefore leaves `RequiredRole` missing and the endpoint unmapped, but does not make the timeout values malformed. Explicitly malformed timeout values, zero or negative timeout values, an explicitly null or malformed `Status` section, or values too large for `CancelAfter` are malformed `DocumentCache` configuration and must fail startup validation. The fail-closed unmapped-endpoint behavior is only for missing, empty, whitespace-only, or invalid `Status:RequiredRole`, not for invalid timeout values.
2. Use both, in one stable `enqueueFailures` component on each target. The component contains `recentEvents`, a bounded latest-event array, and `byCategory`, an aggregate computed only from the retained process-local events. Each event includes `observedAt`, fixed lower-camel `category`, lower-camel `canonicalOperation`, lower-camel `resourceKind`, and a sanitized bounded `message`; omit `DocumentId`, `DocumentUuid`, resource names, request bodies, subjects, SQLSTATE, SQL Server error numbers, connection strings, lifecycle values, and physical identifiers. Retain the latest `Projector:PageSize` enqueue-failure events per normalized target key in memory, ordered oldest-to-newest within the retained window, and track an `evictedCount` for events dropped from that per-target buffer. The status endpoint serializes this component only for current configured targets whose normalized target key matches retained events; empty state is `recentEvents: []`, `byCategory: []`, and `evictedCount: 0`. Counts are retained-window process observations, not durable or lifetime totals.
3. 18-06 owns converting every DocumentCache metric target label introduced by E18 to the `t1_` target surrogate, not only the new status and enqueue-failure instruments. Add one shared surrogate-label helper, use the metric label name `target`, emit `unknown` when a logical target key cannot be determined, and update existing projection, writer, read-acceleration, administrative, status, and enqueue-failure metric tests to prove no metric uses raw target keys, tenant display names, physical identifiers, or document identifiers. This does not change the public JSON `targetKey` shape or sanitized structured-log context where the status contract already exposes the target key.
