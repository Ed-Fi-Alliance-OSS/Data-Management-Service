# Epic: Authorization (Placeholder / Deferred)

## Description

Authorization for the relational primary store is intentionally deferred and is not part of the current backend redesign implementation scope.

This epic is a placeholder for future work to finalize and implement the authorization design currently documented (incompletely) in:

- `reference/design/backend-redesign/auth.md`

Until this epic is actively scheduled:
- the DDL generator MUST NOT emit any authorization objects (`auth.*`, `dms.DocumentSubject`, etc.), and
- runtime query/write paths MUST NOT depend on authorization-specific storage.

## Stories

- `00-auth-placeholder.md` — Placeholder story capturing scope and follow-ups

