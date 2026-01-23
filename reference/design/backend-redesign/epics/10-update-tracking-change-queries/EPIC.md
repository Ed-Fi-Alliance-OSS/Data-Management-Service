---
jira: DMS-1001
jira_url: https://edfi.atlassian.net/browse/DMS-1001
---


# Epic: Update Tracking (`_etag/_lastModifiedDate`) + Change Queries (`ChangeVersion`)

## Description

Implement the representation-sensitive update tracking design in:

- `reference/design/backend-redesign/design-docs/update-tracking.md` (normative)

Deliverables include:
- write-side stamping of `ContentVersion/IdentityVersion` (global monotonic stamps),
- journal emission via triggers on `dms.Document`,
- serving `_etag`, `_lastModifiedDate`, and per-item `ChangeVersion` from stored stamps on `dms.Document`,
- `If-Match` enforcement using stored representation stamps,
- foundations for Change Query selection using `dms.DocumentChangeEvent` (“journal + verify”).

Authorization remains out of scope.

## Stories

- `DMS-1002` — `00-token-stamping.md` — Allocate stamps and update token columns (no-op detection)
- `DMS-1003` — `01-journaling-contract.md` — Treat journals as derived artifacts; validate trigger behavior
- `DMS-1004` — `02-derived-metadata.md` — Serve `_etag/_lastModifiedDate/ChangeVersion` from stored stamps
- `DMS-1005` — `03-if-match.md` — Enforce optimistic concurrency using stored `_etag`
- `DMS-1006` — `04-change-query-selection.md` — Implement Change Query candidate selection (journal + verify)
- `DMS-1007` — `05-change-query-api.md` — Implement Change Query endpoints (optional/future-facing)
- `DMS-1008` — `06-descriptor-stamping.md` — Ensure descriptor writes stamp/journal correctly (triggers on `dms.Descriptor`)
