---
jira: DMS-1315
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add Fresh-Cache Reads with Relational Fallback

## Design References

- **Cache-backed reads and domain lifecycle**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle
- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Configuration and projection target selection**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection

The referenced design sections define cache usability, fallback, response shaping, and direct
fill. This story is only the work package for implementing them.

## Outcome

Add optional DocumentCache use to GET/query body assembly while retaining the existing
relational read path as the correctness path.

## Dependencies

- Depends on 18-00 through 18-04.
- DMS-1190 Story 39 is not a prerequisite for primary-only cache delivery. When derivative
  routing is already present, this story consumes the effective-target contract in
  `../10-update-tracking-change-queries/39-snapshot-read-replica-runtime-routing.md` and
  owns the conditional integration and tests below. If this story lands first, Story 39
  owns that work when derivative routing is added. The two features cannot ship together
  until the integration is complete.

## Implementation Scope

- Add the provider cache-lookup adapter to the relational read pipeline.
- Require lifecycle `Tracking`, a clear cache-ahead latch, and row-level content-version
  equality. `Disabled`, `Resetting`, `Rebuilding`, latch, missing, and stale states all
  use relational fallback.
- Integrate response shaping and authorization with cached and fallback materialization.
- Integrate optional direct fill through the shared materializer and atomic
  cache-write/conditional-acknowledgement component.
- When DMS-1190 Story 39 derivative routing is present, bind cache lookup, lifecycle reads,
  canonical-version comparison, and relational fallback to the request's selected physical
  database. A snapshot or read-replica request uses `DocumentCache` state only from that
  same database when it is eligible; otherwise it bypasses cache acceleration. It never
  reads the parent primary's cache.
- Under that same condition, treat an expected connection-establishment failure during
  cache data-source or connection construction, connection-string parsing, or open as an
  unavailable cache read and fall through to relational acquisition on the same selected
  target. Caller cancellation and unexpected or programming exceptions are not cache
  misses and propagate unchanged.
- Under that same condition, bypass direct fill for snapshot and read-replica targets
  because direct fill writes `dms.DocumentCache` and derivative-eligible requests remain
  read-only.
- Add cache-read and fallback metrics.

## Resolved Cache-Backed Read Scope and Runtime Contract

### Component Boundary

- Add one read-acceleration coordinator around the existing relational GET/query body
  assembly. Implement the coordinator in the relational repository/read-handler layer with
  provider-specific cache-lookup adapters. It must not become a second authorization,
  query-planning, hydration, or cache-writing pipeline.
- The coordinator consumes the 18-01 resolved target context, the 18-02
  `IDocumentCacheMaterializer`-style service, and the 18-03 shared
  cache-write/conditional-acknowledgement service. It does not resolve
  `DocumentCache:Targets`, validate inventory or provider prerequisites, transition
  lifecycle, page durable work, administer recovery, compose Kafka messages, or create a
  second cache writer.
- Cache acceleration applies only to external resource and descriptor GET-by-id and
  GET-many response-body assembly. Internal `StoredDocument` reads, read-modify-write
  flows, mutations, Change Query `/deletes` and `/keyChanges`, `/availableChangeVersions`,
  token info, health/readiness, discovery, OpenAPI, and administrative commands stay on
  their existing relational paths.
- The existing relational read path remains the correctness path. A cache miss, stale row,
  lifecycle fence, disabled configuration, unresolved target, provider prerequisite
  ineligibility, expected cache-adapter acquisition failure, direct-fill failure, or
  direct-fill timeout must not change the public response selected by relational fallback.

### Target and Configuration Gate

- The cache branch is considered only when `ReadAcceleration:Enabled` is true, the request
  is an external read response, and the request's effective data-store target has an exact
  resolved `DocumentCache:Targets` entry for the same normalized tenant key and
  `DataStoreId`.
- The cache lookup adapter must use the same physical database selected for the request.
  With DMS-1190 Story 39 present, the target kind (`Primary`, `Snapshot`, or
  `ReadReplica`) is part of that binding decision. A derivative request uses only cache
  state from that derivative database; it never opens the parent primary connection for
  lifecycle, source-version comparison, cache lookup, or fallback.
- If the cache adapter cannot bind every cache-read operation to the request-selected
  target, bypass cache acceleration for that request and continue relationally on the same
  selected target. Do not reinterpret the request as a process-local projector target or as
  the target from the last successful background projection observation.
- Expected provider acquisition failures while building or opening the cache read
  connection are cache-unavailable outcomes and fall through to relational acquisition on
  the same selected target. Caller cancellation, object-disposal caused by request abort,
  deterministic target/mapping bugs, and unexpected programming/provider exceptions are not
  cache misses and propagate through the normal request failure path.

### Relational Selection, Authorization, and Freshness Boundary

- Authorization, query filtering, descriptor URI resolution, change-version filtering,
  total-count calculation, page ordering, and candidate `DocumentId` selection remain
  relational. Cache rows supply only the body for a candidate that the relational path has
  already selected and authorized for a `200` response.
- GET-by-id first runs the existing target lookup and GET authorization flow. Only after the
  request has a stable authorized `DocumentId`, `DocumentUuid`, resource key, and
  `ContentVersion` does the coordinator attempt a cache lookup. `404`, wrong-resource,
  unsupported authorization, security-configuration, namespace/relationship denial, and
  authorization retry outcomes do not consult the cache.
- GET-many first runs the existing relational page-candidate query, including
  authorization and total-count behavior. The cache lookup receives the selected
  `DocumentId`/`DocumentUuid`/resource-key/`ContentVersion` metadata and must preserve the
  candidate order and total count if it serves the page from cache.
- A fresh cache hit requires one provider-consistent observation that all of the following
  are true for the requested document: lifecycle is `Tracking`,
  `CacheAheadRecoveryRequired` is false, the current canonical row still matches the
  selected resource and expected `DocumentUuid`, and
  `dms.DocumentCache.ContentVersion = dms.Document.ContentVersion`. When the relational
  candidate supplied an expected `ContentVersion`, the current canonical version must still
  equal that expected value as well; otherwise the row is a cache miss and relational
  fallback rechecks the body.
- Missing cache rows, missing source rows after candidate selection, source-version drift,
  stale cache, cache-ahead latch, `Disabled`, `Resetting`, `Rebuilding`, missing or invalid
  lifecycle state, and projection-ineligible target state are distinct internal miss
  outcomes for metrics and diagnostics. They all fall back relationally and do not surface
  as public cache-specific errors.

### Query Page Fallback Rule

- Use an all-or-nothing cache page for v1 GET-many. After relational candidate selection,
  perform one bounded batch cache lookup for the selected page. If every selected candidate
  is fresh, shape the response from cached JSON. If any selected candidate misses or is
  stale, hydrate and materialize the complete selected page through the existing relational
  path rather than mixing cached and relational documents in one response.
- The all-or-nothing page rule is the selected v1 solution because it preserves existing
  page hydration, readable-profile, link-stripping, and retry-boundary behavior without
  adding partial-page merge semantics.
- Empty GET-many pages are successful relational candidate results and do not require a
  cache lookup or direct fill.

### Cached Response Shaping

- Treat `dms.DocumentCache.DocumentJson` as the 18-02 caller-agnostic cache projection:
  full unprofiled JSON with `id` and `_lastModifiedDate`, no served `_etag`, ordinary
  resource links in the fixed stream context, and descriptor no-link shape.
- Never mutate cached JSON in place. The cache lookup adapter returns raw JSON content, and
  the coordinator parses it into a request-local `JsonObject` with `System.Text.Json`
  before injecting served metadata or applying readable-profile projection.
- For cache hits, compose the served `_etag` with the same `IServedEtagComposer` inputs as
  the relational external-response path: current `ContentVersion`, selected
  effective-schema/schema epoch, JSON format, readable profile name when present,
  `ResourceLinks:Enabled`, and the selected response content coding. Do not use
  `DocumentCache.StreamEtag` as the API response `_etag`; it is the fixed stream
  representation validator used by projection/CDC.
- Run response shaping in the same order as relational reads: start from the full cached
  projection, inject the served `_etag`, apply readable-profile projection when present,
  then run the `ResourceLinks:Enabled` stripping pass. Conditional GET
  `If-None-Match` remains in the existing handler after authorization and response
  selection, so cache hits and relational fallback produce identical `200`/`304` behavior.
- The cache hit result returns the same public `GetResult.GetSuccess` or
  `QueryResult.QuerySuccess` shape as relational reads. No public header, status, body
  field, content type, or problem-details shape identifies whether the body came from
  cache.

### Direct Fill

- Direct fill is best effort after a relational fallback has selected a successful external
  response. It must use the 18-02 materializer and 18-03 shared writer with caller purpose
  `DirectFill`; it must not build cache rows from already-shaped API response JSON, update
  `DocumentCache` directly, or acknowledge work through a read-path-specific SQL path.
- For GET-by-id, direct fill attempts the single authorized `DocumentId` that missed or was
  stale. For GET-many, direct fill attempts the selected page's missed or stale
  `DocumentId`s sequentially and stops when the request-scoped `DirectFillTimeout` budget
  is exhausted.
- Direct fill is skipped when read acceleration is disabled, the target is not an exact
  resolved primary target, the lifecycle/target state is ineligible, the request is a
  snapshot or read-replica request, the relational result is not a successful external
  response, or the request cancellation token is already canceled before the fill starts.
- Direct-fill materializer or writer failures are logged and counted with sanitized bounded
  diagnostics, then swallowed by the read coordinator. They must not replace, delay beyond
  the direct-fill budget, or retry the already-computed relational response. Target-fatal
  deterministic invariant failures should also update the same target-health diagnostic
  path used by projection.

### Telemetry and Evidence Boundary

- Emit bounded counters for cache read attempts, hits, all-or-nothing page hits, misses by
  reason, fallback reason, expected adapter-acquisition failure, unexpected exception,
  direct-fill skipped/attempted/succeeded/failed/timed-out, and derivative-target bypass.
  Add duration histograms for cache lookup and direct fill. Labels include provider,
  normalized target key, effective target kind, operation (`getById`, `query`), resource
  kind (`resource`, `descriptor`), and bounded outcome.
  Do not label logs or metrics with `DocumentUuid`, `DocumentId`, `DocumentJson`, query
  parameter values, authorization subjects, namespace values, or profile payload content.
- Unit tests should cover the coordinator decision table, response-shaping order, served
  ETag composition, all-or-nothing query fallback, direct-fill result swallowing, and
  cancellation/exception classification.
- Provider integration tests should cover cache-hit SQL, lifecycle/latch/stale/missing
  miss reasons, source drift between candidate selection and cache lookup, descriptor and
  ordinary-resource paths, profile-shaped responses, `ResourceLinks:Enabled` true and
  false, conditional GET `304`, direct-fill writer invocation, direct-fill timeout, and
  no direct-fill on derivative targets.
- When derivative routing is present, integration tests must use distinguishable primary,
  snapshot, and read-replica databases and prove cache hits, misses, authorization SQL,
  relational fallback, and direct-fill bypass all stay on the selected target.

## Acceptance Evidence

- API and provider integration tests cover the cache states, fallback paths, provider
  prerequisites, and response variants in the referenced design sections.
- When DMS-1190 Story 39 derivative routing is present, integration tests use
  distinguishable primary, snapshot, and read-replica databases and prove cache hits and
  relational fallback stay on the selected target, primary cache JSON is never returned
  for a derivative request, and derivative requests perform no direct fill write. A
  cache-enabled unavailable-snapshot case proves expected cache-adapter acquisition
  failure falls through to relational acquisition on that snapshot with no primary
  fallback; a seam-level test proves caller cancellation from cache acquisition is not
  swallowed as a cache miss. DMS-1190 Story 40 owns the exact Snapshot Not Found `404`
  assertion when relational acquisition also fails.
- Authorization tests cover cached and fallback execution.
- Timeout and concurrency fixtures cover the direct-fill integration boundary.

## Not Assigned to This Story

- Projection scheduling and repair are assigned to 18-04.
- Kafka connector behavior is assigned to E19.
