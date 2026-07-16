---
jira: DMS-1004
jira_url: https://edfi.atlassian.net/browse/DMS-1004
---

# Story: Compose `_etag` and Serve `_lastModifiedDate` / `ChangeVersion` from Stored Stamps

## Description

Implement serving of update-tracking metadata per `reference/design/backend-redesign/design-docs/update-tracking.md`:

- `_etag` is composed from `dms.Document.ContentVersion` plus the served representation's
  `variantKey`. `ContentVersion` tracks resource-state changes; `variantKey` distinguishes
  byte-affecting representation selectors such as format, readable profile, link mode, and
  content coding. DMS does not hash or read back the document body to build `_etag`.
- `_lastModifiedDate` is served from `dms.Document.ContentLastModifiedAt`.
- `ChangeVersion` is established from `dms.Document.ContentVersion` for Change Query responses.

This design performs no dependency enumeration at read time; indirect impacts are materialized as FK cascade updates that trigger normal stamping.

## Acceptance Criteria

- `_etag` changes when the full resource-state representation changes (direct content changes or
  indirect reference-identity changes) because `ContentVersion` changes.
- `_etag` also changes when the served representation selector changes, such as readable profile,
  resource-link mode, or content coding, because `variantKey` changes.
- A full-resource change to a field hidden by the active readable profile still changes the `_etag`
  returned by profiled reads because the stored `ContentVersion` changes.
- `_lastModifiedDate` changes when the full resource-state representation changes.
- `ChangeVersion` matches the full resource-state representation stamp when surfaced by Change
  Query responses.
- Reads do not perform dependency token expansion or document-body hashing for `_etag`.

## Tasks

1. Implement mapping from `dms.Document` stamps to API metadata semantics:
   `_lastModifiedDate` served from `ContentLastModifiedAt`, `ChangeVersion` sourced from `ContentVersion`,
   and `_etag` composed from `ContentVersion` plus the active `variantKey`.
2. Add integration tests covering indirect representation changes (a referenced identity change cascades into a referrer and bumps the referrer's stamps).
3. Add tests proving `_etag` composition does not hash or inspect the document body and uses the
   expected `variantKey` segments for format, readable profile, link mode, and content coding.
4. Add read-side profile metadata tests proving profiled GET/query responses compose the expected
   profile-specific `_etag` from the same `ContentVersion`.
5. Add read-side profile metadata tests proving changes to profile-hidden full-resource fields
   still produce a new `_etag` in profiled GET/query responses.
