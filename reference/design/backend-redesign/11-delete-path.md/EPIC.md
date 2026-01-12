# Epic: Delete Path & Conflict Diagnostics

## Description

Implement DELETE by id semantics for the relational primary store, per:

- `reference/design/backend-redesign/transactions-and-concurrency.md` (“Delete Path (DELETE by id)”)

Key behaviors:
- Delete via `dms.Document` (cascades to resource tables and identities).
- Rely on FK constraints to prevent deleting referenced documents.
- Map FK violations to conflict responses, using deterministic FK naming and `dms.ReferenceEdge` diagnostics.

Authorization remains out of scope.

## Stories

- `00-delete-by-id.md` — Implement delete transaction and cascade expectations
- `01-conflict-mapping.md` — Map FK violations to DMS conflict error shapes
- `02-referenceedge-diagnostics.md` — Provide consistent “who references me?” diagnostics
- `03-delete-tests.md` — Unit/integration coverage for delete + conflicts (pgsql + mssql)

