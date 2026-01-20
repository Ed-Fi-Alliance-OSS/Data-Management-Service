# Story: Journaling Contract (Triggers Own Journal Writes)

## Description

Enforce the journaling rules in `reference/design/backend-redesign/update-tracking.md`:

- `dms.DocumentChangeEvent` is a derived artifact.
- Journal rows are emitted by database triggers on `dms.Document` when token columns change.
- Application code must not write journal rows directly (to avoid “forgotten write” and double-entry bugs).

## Acceptance Criteria

- When `dms.Document` is inserted, triggers emit:
  - one `dms.DocumentChangeEvent` row.
- When `ContentVersion` changes, triggers emit a `dms.DocumentChangeEvent` row.
- Identity projection changes must also bump `ContentVersion`, so identity updates are still represented in `dms.DocumentChangeEvent` via the `ContentVersion` change.
- For watermark-only clients, `ChangeVersion` values used for journaling are unique per representation change (see “Token Stamping” story).
- Application code has no direct writes to journal tables in the relational backend path.

## Tasks

1. Ensure provisioning emits the required triggers (pgsql + mssql) per `update-tracking.md`.
2. Add integration smoke tests validating trigger behavior on insert/update token changes.
3. Audit relational backend code to ensure no direct journal writes exist.
