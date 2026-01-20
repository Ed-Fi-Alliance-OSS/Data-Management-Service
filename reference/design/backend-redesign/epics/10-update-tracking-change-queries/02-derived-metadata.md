# Story: Serve `_etag`, `_lastModifiedDate`, and `ChangeVersion` from Stored Stamps

## Description

Implement serving of update-tracking metadata per `reference/design/backend-redesign/update-tracking.md`:

- `_etag` is derived from the stored representation stamp (`dms.Document.ContentVersion`).
- `_lastModifiedDate` is served from `dms.Document.ContentLastModifiedAt`.
- `ChangeVersion` is served from `dms.Document.ContentVersion`.

This design performs no dependency enumeration at read time; indirect impacts are materialized as FK cascade updates that trigger normal stamping.

## Acceptance Criteria

- `_etag` changes when the served representation changes (direct content changes or indirect reference-identity changes).
- `_lastModifiedDate` changes when the served representation changes.
- `ChangeVersion` matches the served representation stamp.
- Reads do not perform dependency token expansion.

## Tasks

1. Implement mapping from `dms.Document` stamps to API fields (`_etag`, `_lastModifiedDate`, `ChangeVersion`).
2. Add integration tests covering indirect representation changes (a referenced identity change cascades into a referrer and bumps the referrer’s stamps).
