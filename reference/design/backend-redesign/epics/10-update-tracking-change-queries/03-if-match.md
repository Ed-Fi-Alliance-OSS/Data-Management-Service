---
jira: DMS-1005
jira_url: https://edfi.atlassian.net/browse/DMS-1005
---

# Story: Enforce `If-Match` Using Stored Representation Stamps

## Description

Implement optimistic concurrency checks using stored representation stamps:

- For update operations that support `If-Match`, compare the client-provided `_etag` to the current `_etag` served from `dms.Document.ContentVersion`.
- No dependency locking is required because indirect impacts are materialized as local updates that bump the same stamp.
- `DMS-984` owns only the internal `ContentVersion` freshness recheck for the guarded no-op fast path; this story owns HTTP `If-Match` comparison and `412` mapping for both changed-write and stale-no-op outcomes.

## Acceptance Criteria

- When `If-Match` equals the current `_etag`, the changed write proceeds and a guarded no-op may short-circuit after the internal `ContentVersion` freshness recheck succeeds.
- When `If-Match` does not match, the request fails with the appropriate HTTP error semantics (e.g., precondition failed).
- If `DMS-984` reports that a no-op decision became stale before the guarded short-circuit step and `If-Match` no longer matches the current `_etag`, the request fails rather than returning success based on stale data.
- The check is representation-sensitive and reflects dependency identity changes.

## Tasks

1. Implement a “read current stored `_etag`” path usable by update handlers prior to write.
2. Implement `If-Match` comparison and stale-no-op handling paths usable by both changed writes and guarded no-op handlers, consuming the internal freshness result produced by `DMS-984`.
3. Add tests for:
   1. match success,
   2. mismatch failure,
   3. mismatch caused by dependency identity change,
   4. stale no-op compare reported by `DMS-984` that is rejected by the guarded `If-Match` recheck.
