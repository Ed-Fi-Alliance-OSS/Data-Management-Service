# Epic: Update Tracking (`_etag/_lastModifiedDate`) + Change Queries (`ChangeVersion`)

## Description

Implement the representation-sensitive update tracking design in:

- `reference/design/backend-redesign/update-tracking.md` (normative)

Deliverables include:
- write-side stamping of `ContentVersion/IdentityVersion` (global monotonic stamps),
- journal emission via triggers on `dms.Document`,
- read-side derivation of `_etag`, `_lastModifiedDate`, and per-item `ChangeVersion`,
- `If-Match` enforcement using derived `_etag`,
- foundations for Change Query selection using journals + `dms.ReferenceEdge`.

Authorization remains out of scope.

## Stories

- `00-token-stamping.md` — Allocate stamps and update token columns (no-op detection)
- `01-journaling-contract.md` — Treat journals as derived artifacts; validate trigger behavior
- `02-derived-metadata.md` — Derive `_etag/_lastModifiedDate/ChangeVersion` on reads
- `03-if-match.md` — Enforce optimistic concurrency using derived `_etag`
- `04-change-query-selection.md` — Implement Change Query candidate selection (journal-driven)
- `05-change-query-api.md` — Implement Change Query endpoints (optional/future-facing)

