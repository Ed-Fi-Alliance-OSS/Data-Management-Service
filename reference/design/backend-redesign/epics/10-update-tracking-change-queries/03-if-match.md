---
jira: DMS-1005
jira_url: https://edfi.atlassian.net/browse/DMS-1005
---

# Story: Enforce `If-Match` Using Stored Representation Stamps

## Description

Implement optimistic concurrency checks using stored representation stamps:

- For update operations that support `If-Match`, compare the client-provided `_etag` to the current `_etag`
  computed from the canonical JSON form of the current served representation, as defined by
  `reference/design/backend-redesign/design-docs/update-tracking.md`.
- No dependency locking is required because indirect impacts are materialized as local updates that bump the same stamp.
- `DMS-984` introduces the internal `ContentVersion` freshness recheck for the shared guarded no-op fast path, and `DMS-1124` reuses that result for profiled no-op outcomes; this story owns HTTP `If-Match` comparison and `412` mapping for both changed-write and stale-no-op outcomes.

## Acceptance Criteria

- When `If-Match` equals the current `_etag`, the changed write proceeds and a guarded no-op may short-circuit after the internal `ContentVersion` freshness recheck succeeds on the shared no-profile or profiled executor path.
- When `If-Match` does not match, the request fails with the appropriate HTTP error semantics (e.g., precondition failed).
- If the shared guarded no-op executor path introduced by `DMS-984` and extended by `DMS-1124` reports that a no-op decision became stale before the guarded short-circuit step and `If-Match` no longer matches the current `_etag`, the request fails rather than returning success based on stale data.
- The check is representation-sensitive and reflects dependency identity changes.

## Tasks

1. Implement a "read current document and compute `_etag` from its served representation" path usable by update handlers prior to write.
2. Implement `If-Match` comparison and stale-no-op handling paths usable by both changed writes and guarded no-op handlers, consuming the internal freshness result produced by the shared `DMS-984` executor path and reused by `DMS-1124`.
3. Add tests for:
   1. match success,
   2. mismatch failure,
   3. mismatch caused by dependency identity change,
   4. stale no-op compare reported by the shared guarded no-op executor path (`DMS-984`, reused by `DMS-1124`) that is rejected by the guarded `If-Match` recheck.
