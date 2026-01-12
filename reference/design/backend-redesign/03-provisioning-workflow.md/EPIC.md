# Epic: Provisioning Workflow (Create-Only)

## Description

Provide an operational workflow for schema provisioning that matches the redesign:

- CLI can emit deterministic DDL scripts without provisioning.
- CLI can provision an empty database (optionally creating it first) using a single transaction for schema objects + seed data.
- Preflight fails fast on `EffectiveSchemaHash` mismatch (no migrations).
- Provisioning SQL is resilient to partial runs (existence checks everywhere).

Authorization objects remain out of scope.

## Stories

- `00-ddl-emit-command.md` — CLI: `ddl emit` (files + manifests)
- `01-ddl-provision-command.md` — CLI: `ddl provision` (create-only, optional create DB)
- `02-preflight-and-idempotency.md` — Preflight mismatch + rerun safety + diagnostics

