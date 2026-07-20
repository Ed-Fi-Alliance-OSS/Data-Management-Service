---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Fresh-Cache Read Path with Relational Fallback

## Description

Add optional cache-backed GET/query response assembly using `dms.DocumentCache`.

Cache-backed reads are opportunistic. A missing, stale, disabled, or unhealthy cache must not break normal
GET/query behavior because DMS can always fall back to relational reconstitution.

## Dependencies

- Depends on `18-00-documentcache-configuration-and-mode-boundaries.md` and
  `18-02-document-materializer-service.md`.
- Benefits from `18-03-async-projector-reconciliation-loop.md` but must behave correctly
  when the projector is behind or disabled.
- No hard dependency on `17-cdc-kafka`; this story documents the non-CDC fallback boundary that
  `17-cdc-kafka/06-ops-docs-runbooks.md` must not misstate.

## Acceptance Criteria

- Read acceleration is used only when `ReadAcceleration:Enabled = true`.
- A cache row is usable only when:
  - `DocumentCache.ContentVersion == Document.ContentVersion`,
  - `DocumentCache.LastModifiedAt == Document.ContentLastModifiedAt`.
- Missing or stale cache rows fall back to relational reconstitution.
- Authorization and query candidate selection are evaluated against relational sources before cached JSON is
  used for response-body assembly.
- Readable-profile projection runs after cache retrieval.
- `DataManagement:ResourceLinks:Enabled` stripping runs after cache retrieval and readable-profile projection.
- Cached JSON is not the source of `_etag`; cache-backed and relational-fallback reads compose the same served
  `_etag` from `ContentVersion` and the active request `variantKey`.
- The read path does not enqueue projection work. The reconciliation loop discovers
  missing/stale rows from database state.
- After relational fallback, the read path may directly use the shared guarded cache
  upsert as an optional optimization.
- Metrics distinguish cache hit, miss, stale miss, and relational fallback.
- Tests cover cache hit, miss, stale row, profile projection, link stripping, and disabled read acceleration.

## Tasks

1. Add repository/read-path integration for optional cache lookup.
2. Implement freshness comparison against `dms.Document`.
3. Reuse existing profile projection and link stripping after cache retrieval.
4. Add relational fallback and optional direct guarded fill for misses/stale rows.
5. Add metrics/logging for cache decisions.
6. Add focused GET/query tests for cache-backed and fallback behavior.

## Out of Scope

- Making cache-backed reads mandatory.
- Querying or authorizing directly from `DocumentJson`.
- Kafka CDC behavior.
