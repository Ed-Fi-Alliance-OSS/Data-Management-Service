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

## Clarifying Questions and Answers

### Questions 1

1. When a cache lookup adapter has bound and opened the selected target but the lookup command fails before a fresh/stale/miss outcome is available, such as command timeout, transient provider failure, or result-shape read failure, should 18-05 fall back relationally as cache-unavailable, propagate the request failure, or classify only a narrower subset as fallback?
2. If `DocumentCache.DocumentJson` for an otherwise fresh row is invalid JSON, is not a JSON object, contains `_etag`, or violates the cached-document invariants needed for response shaping, should the coordinator treat it as a cache miss with relational fallback, a deterministic target/projection invariant failure, or a public request failure?
3. For read-path direct fill, is lifecycle `Rebuilding` eligible because the 18-03 writer permits projector/direct-fill writes there, or should 18-05 attempt direct fill only while lifecycle is `Tracking`?
4. After an expected cache-adapter acquisition or selected-target bind failure that falls back relationally on a primary target, should direct fill be skipped as part of the same cache-unavailable outcome, or may the direct-fill materializer/writer still run when their target context is otherwise eligible?
5. For GET-many, if the batch cache lookup is bypassed for a page-level reason such as lifecycle fence, latch, target/prerequisite ineligibility, or adapter acquisition failure rather than per-document missing/stale rows, should direct fill attempt any selected page documents, or is direct fill limited to document IDs that had an attempted lookup and returned missing/stale/source-drift outcomes?
6. Under DMS-1190 Story 39, does a configured parent `DocumentCache:Targets` entry enable cache-read eligibility checks against the selected snapshot/read-replica database, or must 18-01/18-05 expose distinct per-effective-target-kind cache eligibility observations before derivative cache hits are taskable?
7. What concrete bound defines the "one bounded batch cache lookup" for GET-many: the already-validated API page size, an existing global maximum page size, or a new DocumentCache read-acceleration cap that forces relational fallback when exceeded?

### Answers 1

1. Use a narrower fallback classification. Provider-classified cache-read availability failures after bind/open, such as command timeout, transient connection loss, or equivalent transient provider failure while executing or reading the lookup, are cache-unavailable outcomes and fall back relationally. Result-shape failures such as missing columns, wrong column types, duplicate rows for one document, or impossible source/cache tuples are deterministic cache-adapter or target-inventory invariant failures: do not serve the cached body, do not count them as ordinary misses, fall back relationally for the current response, and emit the unexpected/invariant diagnostic path. Caller cancellation, request-abort disposal, and unclassified programming exceptions still propagate unchanged.
2. Treat invalid `DocumentCache.DocumentJson`, a non-object value, a stored `_etag`, or cached-document invariant violations as deterministic target/projection invariant failures, not cache misses and not public cache-specific errors. The coordinator must not serve or mutate that cache row; it should fall back relationally for the current authorized response, record bounded target-fatal diagnostics/metrics through the projection health path, and leave repair to projection, rebuild, scrub, or operator action.
3. `Rebuilding` is direct-fill eligible when the target is an exact resolved primary target, provider prerequisites are satisfied, and the cache-ahead latch is clear. Cache reads still require `Tracking`, so `Rebuilding` never serves a cache hit, but 18-05 may run best-effort direct fill after successful relational fallback because 18-03 explicitly permits projector/direct-fill writes in `Rebuilding`. Implement 18-05 with separate cache-read lifecycle eligibility and direct-fill write eligibility checks.
4. Skip direct fill after an expected cache-adapter acquisition failure or selected-target bind failure in the same request. That outcome means the read coordinator could not establish the cache path for the selected target, so it should preserve the relational response, count direct fill as skipped for cache-unavailable or bind-failed state, and avoid invoking the materializer/writer from that degraded request.
5. For `Tracking`, direct fill is limited to document IDs whose attempted cache lookup produced document-level missing, stale, or source-drift outcomes. Page-level bypasses for `Disabled`, `Resetting`, a set latch, target/prerequisite ineligibility, adapter acquisition failure, or bind failure do not direct-fill the selected page. The one exception is `Rebuilding`: because it is cache-read-ineligible but direct-fill-write-eligible, GET-by-id may attempt the authorized document and GET-many may attempt the selected page documents sequentially within `DirectFillTimeout`.
6. A configured parent `DocumentCache:Targets` entry is the configuration gate; do not add separate configured entries for snapshot or read-replica targets. Under Story 39, 18-05 must still bind every cache-read operation to the request's selected effective target kind and physical database. Use target-scoped cache-read contexts keyed by selected effective target kind and physical database so derivative reads never reuse the primary's lifecycle, prerequisite, cache row, canonical version, or fallback connection. If that exact selected-target context is unavailable, bypass cache acceleration for the derivative request.
7. The bounded GET-many cache lookup is bounded by the already-selected candidate page after public paging validation. Its size is at most the request's validated `limit` or cursor `pageSize`, or the configured `MaximumPageSize` default when omitted, and therefore at most `AppSettings:MaximumPageSize`. Empty and zero-size pages perform no cache lookup or direct fill. Do not add a separate DocumentCache read-acceleration cap or a new fallback path for over-cap pages; an oversized selected page would be a paging validation or planner invariant bug, not a runtime cache policy decision.

### Questions 2

1. What cancellation-token contract should 18-05 use for cache lookup, relational fallback, and direct fill: extend GET/query request or repository seams to carry the request cancellation token into those operations, or classify cancellation only when the cache adapter, materializer, or writer independently throws cancellation from an existing seam?
2. If request cancellation occurs after relational fallback has computed a successful external response but while best-effort direct fill is still running, should the read coordinator swallow that as a direct-fill aborted/skipped outcome and return the computed response when possible, or should the cancellation propagate as the request outcome?
3. Should 18-05 own a candidate-selection refactor that exposes authorized GET-many page metadata without body hydration for both ordinary resources and descriptors, including total count, selected order, selected-boundary metadata, `DocumentId`, `DocumentUuid`, resource key, `ContentVersion`, and last-modified metadata, or should any part of that refactor be split into a prerequisite story?
4. For GET-many fallback after an all-or-nothing cache miss, if some originally selected candidate documents are deleted or drift before relational fallback hydration completes, should direct fill be attempted only for documents that survive fallback materialization, or for every originally selected miss/stale/source-drift candidate whose source metadata can still be read?
5. Is cache acceleration for GET-many in 18-05 limited to currently implemented traditional paging until cursor page execution exists, despite the bounded-lookup rule naming cursor `pageSize`, or must this story also establish and test the cache candidate-page contract for cursor paging?

### Answers 2

1. Extend the GET-by-id, GET-many, cache-lookup, materializer, and writer seams needed by 18-05 so the request cancellation token is passed explicitly through cache lookup, relational fallback, and direct fill. Do not rely on incidental adapter cancellation. Cache lookup and relational fallback cancellation before a response is selected propagates as request cancellation, not as a cache miss. Direct fill should use a linked token that combines the request token with `DirectFillTimeout`, so timeout remains a direct-fill timeout outcome and caller cancellation remains distinguishable in diagnostics.
2. Swallow cancellation observed only from the optional direct-fill phase after relational fallback has already computed a successful external response. Record the fill as skipped, aborted, or timed out according to the token that fired, and return the computed relational response when the HTTP pipeline can still send it. If the request token is already canceled before direct fill starts, skip direct fill. Cancellation before cache lookup or relational fallback has selected the response still propagates through the normal request cancellation path.
3. 18-05 owns the minimal candidate-selection refactor needed for cache-backed GET-many. Expose one authorized page-candidate result for ordinary resources and descriptors before body hydration, carrying total count, selected order, selected-boundary metadata when the active paging mode has one, `DocumentId`, `DocumentUuid`, resource key, `ContentVersion`, and content last-modified metadata. Cache acceleration, relational fallback hydration, and direct-fill selection consume that same result. Do not split this into a prerequisite story.
4. Direct fill after GET-many fallback should use the intersection of the original document-level miss/stale/source-drift cache outcomes and the documents that survive successful relational fallback materialization for the public response. Do not direct-fill a candidate that was deleted, drifted out of the fallback result, failed fallback hydration, or is no longer part of the served response, even if its source metadata can still be read separately. For surviving rows, the materializer and shared writer still re-read current source state and apply their normal monotonic predicates.
5. 18-05 cache acceleration for GET-many is limited to the paging modes implemented when the story lands, which currently means traditional paging. Shape the candidate-page seam so cursor execution can later supply the same ordered candidate metadata and selected-boundary value, but 18-05 does not implement cursor execution or cursor-specific cache tests. Until cursor page execution provides that contract, cursor GET-many requests, if present, bypass cache acceleration and use the relational cursor path. The `cursor pageSize` in the bounded-lookup rule applies only after the cursor execution story is present and integrated with this coordinator.
