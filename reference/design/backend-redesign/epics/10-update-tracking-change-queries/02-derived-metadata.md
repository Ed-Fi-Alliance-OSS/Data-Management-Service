---
jira: DMS-1004
jira_url: https://edfi.atlassian.net/browse/DMS-1004
---

# Story: Serve `_etag`, `_lastModifiedDate`, and `ChangeVersion` from Stored Stamps

## Description

Implement serving of update-tracking metadata per `reference/design/backend-redesign/design-docs/update-tracking.md`:

- `_etag` is derived from the canonical JSON form of the served response document:
  remove `id`, `_etag`, and `_lastModifiedDate`, canonicalize object properties using ordinal ordering
  while preserving array order, serialize as minified UTF-8, compute `SHA-256`, and encode as base64.
- `_lastModifiedDate` is served from `dms.Document.ContentLastModifiedAt`.
- `ChangeVersion` is established from `dms.Document.ContentVersion` for Change Query responses.

This design performs no dependency enumeration at read time; indirect impacts are materialized as FK cascade updates that trigger normal stamping.

## Acceptance Criteria

- `_etag` changes when the served representation changes (direct content changes or indirect reference-identity changes).
- `_lastModifiedDate` changes when the served representation changes.
- `ChangeVersion` matches the served representation stamp when surfaced by Change Query responses.
- Reads do not perform dependency token expansion.

## Tasks

1. Implement mapping from `dms.Document` stamps to API metadata semantics:
   `_lastModifiedDate` served from `ContentLastModifiedAt`, `ChangeVersion` sourced from `ContentVersion`,
   and `_etag` recomputed from the served representation.
2. Add integration tests covering indirect representation changes (a referenced identity change cascades into a referrer and bumps the referrer's stamps).
