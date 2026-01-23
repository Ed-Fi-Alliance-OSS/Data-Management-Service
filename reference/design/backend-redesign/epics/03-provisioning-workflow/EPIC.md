---
jira: DMS-948
jira_url: https://edfi.atlassian.net/browse/DMS-948
---

# Epic: Provisioning Workflow (Create-Only)

## Description

Provide an operational workflow for schema provisioning that matches the redesign:

- CLI can emit deterministic DDL scripts without provisioning.
- CLI can provision an empty database (optionally creating it first) using a single transaction for schema objects + seed data.
- Preflight fails fast on `EffectiveSchemaHash` mismatch (no migrations).
- Provisioning SQL is resilient to partial runs (existence checks everywhere).

Authorization objects remain out of scope.

## Stories

- `DMS-950` — `00-ddl-emit-command.md` — CLI: `ddl emit` (files + manifests)
- `DMS-951` — `01-ddl-provision-command.md` — CLI: `ddl provision` (create-only, optional create DB)
- `DMS-952` — `02-preflight-and-idempotency.md` — Preflight mismatch + rerun safety + diagnostics
- `DMS-953` — `03-ddl-manifest.md` — Emit `ddl.manifest.json` (normalized hashes + counts)
- `DMS-954` — `04-remove-legacy-schemagenerator.md` — Remove legacy SchemaGenerator toolchain and migrate references
- `DMS-955` — `05-seed-descriptors.md` — Optional: `ddl provision --seed-descriptors` (bootstrap descriptor reference data)
