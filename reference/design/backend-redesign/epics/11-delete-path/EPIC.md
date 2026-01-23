---
jira: DMS-1009
jira_url: https://edfi.atlassian.net/browse/DMS-1009
---


# Epic: Delete Path & Conflict Diagnostics

## Description

Implement DELETE by id semantics for the relational primary store, per:

- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (“Delete Path (DELETE by id)”)

Key behaviors:
- Delete via `dms.Document` (cascades to resource tables and identities).
- Rely on FK constraints to prevent deleting referenced documents.
- Map FK violations to conflict responses using deterministic FK naming (and optional model-derived diagnostics).

Authorization remains out of scope.

## Stories

- `DMS-1010` — `00-delete-by-id.md` — Implement delete transaction and cascade expectations
- `DMS-1011` — `01-conflict-mapping.md` — Map FK violations to DMS conflict error shapes
- `DMS-1012` — `02-referencing-diagnostics.md` — Provide consistent “who references me?” diagnostics without a reverse-edge table
- `DMS-1013` — `03-delete-tests.md` — Unit/integration coverage for delete + conflicts (pgsql + mssql)
