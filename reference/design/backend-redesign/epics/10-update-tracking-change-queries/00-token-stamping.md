# Story: Emit Stamping Triggers for `dms.Document` (Content + Identity Stamps)

## Description

Implement write-side stamping per `reference/design/backend-redesign/update-tracking.md` using database triggers:

- On any representation change (root/child/extension table writes, including FK-cascade updates to propagated identity columns), bump:
  - `dms.Document.ContentVersion` and `dms.Document.ContentLastModifiedAt`.
- On any identity projection change, also bump:
  - `dms.Document.IdentityVersion` and `dms.Document.IdentityLastModifiedAt`,
  - and treat it as a representation change (also bump content stamps).

No-op detection is best-effort and optional; correctness requires only that stamps change when the served representation changes.

## Acceptance Criteria

- Representation changes update `ContentVersion` and `ContentLastModifiedAt`.
- Identity projection changes update `IdentityVersion` and `IdentityLastModifiedAt` and also update `ContentVersion`.
- FK-cascade updates to propagated identity columns cause the same stamping behavior as direct writes.

## Tasks

1. Emit per-dialect stamping triggers/functions as part of DDL generation (pgsql + mssql).
2. Ensure triggers cover:
   - root tables,
   - child tables,
   - `_ext` tables, and
   - FK-cascade updates to propagated identity columns.
3. Add unit/integration tests for:
   1. content-only changes,
   2. identity projection changes,
   3. indirect reference-identity changes via cascades.
