---
jira: DMS-1001
jira_url: https://edfi.atlassian.net/browse/DMS-1001
---


# Epic: Update Tracking (`_etag/_lastModifiedDate`) + Change Queries (`ChangeVersion`)

## Description

Implement the representation-sensitive update tracking design in:

- `reference/design/backend-redesign/design-docs/update-tracking.md` (normative)
- `reference/design/backend-redesign/design-docs/change-queries.md` (normative)

Deliverables include:
- write-side stamping of `ContentVersion/IdentityVersion` (global monotonic stamps),
- journal emission via triggers,
- serving `_etag`, `_lastModifiedDate`, and per-item `ChangeVersion` from stored stamps,
- ensuring successful no-op updates leave stored stamps and journal rows unchanged,
- `If-Match` enforcement using stored representation stamps,
- ChangeQueries feature does not introduce any breaking changes to its API interface
- Ideally, being able to support the feature without requiring DB snapshots

## Stories

- `DMS-1002` — `00-token-stamping.md` — Allocate stamps and update token columns only for representation changes
- `DMS-1003` — `01-journaling-contract.md` — Treat journals as derived artifacts; validate trigger behavior
- `DMS-1004` — `02-derived-metadata.md` — Serve `_etag/_lastModifiedDate/ChangeVersion` from stored stamps
- `DMS-1005` — `03-if-match.md` — Enforce optimistic concurrency using stored `_etag`
- `DMS-1006` — `04-change-query-selection.md` — Implement Change Query candidate selection (journal + verify)
- `DMS-1007` — `05-change-query-api.md` — Implement Change Query endpoints (optional/future-facing)
- `DMS-1008` — `06-descriptor-stamping.md` — Ensure descriptor writes stamp/journal correctly (triggers on `dms.Descriptor`)
- `DMS-1168` — `07-get-max-change-version-function.md` — Emit `dms.GetMaxChangeVersion()` function for `/availableChangeVersions`
- `DMS-1169` — `08-remove-document-change-event.md` — Remove `dms.DocumentChangeEvent`; superseded by the per-resource `tracked_changes_*` tables and the `ContentVersion` mirror
