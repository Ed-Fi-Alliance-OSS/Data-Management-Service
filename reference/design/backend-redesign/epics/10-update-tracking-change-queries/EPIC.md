# Epic: Update Tracking (`_etag/_lastModifiedDate`) + Change Queries (`ChangeVersion`)

## Description

Implement the representation-sensitive update tracking design in:

- `reference/design/backend-redesign/update-tracking.md` (normative)

Deliverables include:
- write-side stamping of `ContentVersion/IdentityVersion` (global monotonic stamps),
- journal emission via triggers on `dms.Document`,
- serving `_etag`, `_lastModifiedDate`, and per-item `ChangeVersion` from stored stamps on `dms.Document`,
- `If-Match` enforcement using stored representation stamps,
- foundations for Change Query selection using `dms.DocumentChangeEvent` (“journal + verify”).

Authorization remains out of scope.

## Stories

- `00-token-stamping.md` — Allocate stamps and update token columns (no-op detection)
- `01-journaling-contract.md` — Treat journals as derived artifacts; validate trigger behavior
- `02-derived-metadata.md` — Serve `_etag/_lastModifiedDate/ChangeVersion` from stored stamps
- `03-if-match.md` — Enforce optimistic concurrency using stored `_etag`
- `04-change-query-selection.md` — Implement Change Query candidate selection (journal + verify)
- `05-change-query-api.md` — Implement Change Query endpoints (optional/future-facing)
- `06-descriptor-stamping.md` — Ensure descriptor writes stamp/journal correctly (triggers on `dms.Descriptor`)
