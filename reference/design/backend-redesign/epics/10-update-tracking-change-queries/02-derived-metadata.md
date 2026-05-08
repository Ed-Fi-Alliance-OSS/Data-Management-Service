---
jira: DMS-1004
jira_url: https://edfi.atlassian.net/browse/DMS-1004
---

# Story: Serve `_etag`, `_lastModifiedDate`, and `ChangeVersion` from Stored Stamps

## Description

Implement serving of update-tracking metadata per `reference/design/backend-redesign/design-docs/update-tracking.md`:

- `_etag` is derived from the canonical JSON form of the full resource-state document before
  readable-profile projection:
  remove server-generated fields `id`, `link`, `_etag`, and `_lastModifiedDate`, canonicalize
  object properties using ordinal ordering while preserving array order, serialize as minified
  UTF-8, compute `SHA-256`, and encode as base64. Reference `link` objects are response decorations
  and do not affect `_etag`. Readable profile filtering changes the response body but preserves the
  same full-resource `_etag`.
- `_lastModifiedDate` is served from `dms.Document.ContentLastModifiedAt`.
- `ChangeVersion` is established from `dms.Document.ContentVersion` for Change Query responses.

This design performs no dependency enumeration at read time; indirect impacts are materialized as FK cascade updates that trigger normal stamping.

## Acceptance Criteria

- `_etag` changes when the full resource-state representation changes (direct content changes or
  indirect reference-identity changes), but not when only readable profile filtering or response
  decorations such as `link` change the response body.
- A full-resource change to a field hidden by the active readable profile still changes the `_etag`
  returned by profiled reads, proving profiled metadata remains a full-resource validator.
- `_lastModifiedDate` changes when the full resource-state representation changes.
- `ChangeVersion` matches the full resource-state representation stamp when surfaced by Change
  Query responses.
- Reads do not perform dependency token expansion.

## Tasks

1. Implement mapping from `dms.Document` stamps to API metadata semantics:
   `_lastModifiedDate` served from `ContentLastModifiedAt`, `ChangeVersion` sourced from `ContentVersion`,
   and `_etag` recomputed from the full resource-state representation before readable-profile projection.
2. Add integration tests covering indirect representation changes (a referenced identity change cascades into a referrer and bumps the referrer's stamps).
3. Add canonicalization tests proving reference `link` subtrees are ignored by the `_etag` hash:
   the same resource-state document with no `link`, with one or more `link` subtrees, and with those
   `link` subtrees stripped again must produce the same `_etag`, while a real reference identity
   value change still produces a different `_etag`.
4. Add read-side profile metadata tests proving profiled GET/query responses preserve the same
   full-resource `_etag` as unprofiled responses for the same document.
5. Add read-side profile metadata tests proving changes to profile-hidden full-resource fields
   still produce a new `_etag` in profiled GET/query responses.
