---
jira: DMS-1005
jira_url: https://edfi.atlassian.net/browse/DMS-1005
---

# Story: Enforce `If-Match` Using Stored Representation Stamps

## Description

Implement optimistic concurrency checks using stored representation stamps for relational document resources and descriptor resources:

- For operations that support `If-Match` (`PUT`, `DELETE`, and `POST` when upsert resolves to an existing
  document), compare the client-provided `If-Match` header value to the current `_etag` computed from the canonical
  JSON form of the current served representation, as defined by
  `reference/design/backend-redesign/design-docs/update-tracking.md`. This applies equally to descriptor resources
  stored through `dms.Descriptor`.
- `If-Match` is optional. When the header is absent, these operations proceed without an HTTP precondition check.
- Header matching is an exact opaque string comparison: the header value must exactly equal the current `_etag` value
  DMS would serve for the resource representation. The implementation must not normalize quotes, parse entity-tag
  lists, or otherwise reinterpret the value for this story.
- When `POST` with `If-Match` resolves to an insert/new document, the request fails with `412` because there is no
  current representation whose `_etag` can satisfy the precondition.
- No dependency locking is required because indirect impacts are materialized as local updates that bump the same stamp.
- `DMS-984` introduces the internal `ContentVersion` freshness recheck for the shared guarded no-op fast path, and
  `DMS-1124` reuses that result for profiled no-op outcomes; this story owns HTTP `If-Match` comparison and `412`
  mapping for changed writes, deletes, `POST` upsert-as-update, and stale-no-op outcomes.

## Acceptance Criteria

- When `If-Match` is absent, `PUT`, `DELETE`, and `POST` upsert-as-update continue through the existing relational
  and descriptor write/delete paths without an HTTP precondition check.
- When `If-Match` exactly equals the current `_etag`, the changed write/delete proceeds and a guarded no-op may
  short-circuit after the internal `ContentVersion` freshness recheck succeeds on the shared no-profile or profiled
  executor path.
- When `If-Match` does not exactly match, the request fails with the appropriate HTTP error semantics (e.g.,
  precondition failed / `412`).
- When `POST` resolves to a new document and the request includes `If-Match`, the request fails with `412`; DMS does
  not ignore the header and does not treat it as an insert precondition success.
- If the shared guarded no-op executor path introduced by `DMS-984` and extended by `DMS-1124` reports that a no-op
  decision became stale before the guarded short-circuit step and `If-Match` no longer matches the current `_etag`,
  the request fails rather than returning success based on stale data.
- The check is representation-sensitive and reflects dependency identity changes.
- Descriptor `PUT`, descriptor `DELETE`, and descriptor `POST` upsert-as-update enforce the same optional exact-match
  `If-Match` semantics as relational document resources.

## Tasks

1. Implement a "read current document and compute `_etag` from its served representation" path usable by `PUT`,
   `DELETE`, and `POST` upsert-as-update handlers prior to write/delete, for both relational document resources and
   descriptor resources.
2. Implement optional exact opaque-string `If-Match` comparison and stale-no-op handling paths usable by changed
   writes, deletes, and guarded no-op handlers, consuming the internal freshness result produced by the shared
   `DMS-984` executor path and reused by `DMS-1124`.
3. Add tests for:
   1. absent `If-Match` proceeds without a precondition check,
   2. exact match success,
   3. exact mismatch failure,
   4. `POST` insert/new-document resolution with `If-Match` fails with `412`,
   5. descriptor `PUT`, descriptor `DELETE`, and descriptor `POST` upsert-as-update use the same semantics,
   6. mismatch caused by dependency identity change,
   7. at least one PostgreSQL and one SQL Server relational integration test proving a cascaded referenced identity
      change changes the dependent `_etag` and causes stale `If-Match` to return `412`,
   8. existing `If-Match` E2E scenarios switched to the relational backend without changing the scenario coverage, and
   9. stale no-op compare reported by the shared guarded no-op executor path (`DMS-984`, reused by `DMS-1124`) that
      is rejected by the guarded `If-Match` recheck.
