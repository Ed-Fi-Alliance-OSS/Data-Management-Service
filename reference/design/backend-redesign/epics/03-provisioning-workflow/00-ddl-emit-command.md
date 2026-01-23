---
jira: DMS-950
jira_url: https://edfi.atlassian.net/browse/DMS-950
---

# Story: CLI Command — `ddl emit`

## Description

Provide a CLI command that generates deterministic artifacts without connecting to a database:

- `{dialect}.sql` DDL scripts (pgsql + mssql as configured)
- `effective-schema.manifest.json`
- `relational-model.manifest.json`
- optional `ddl.manifest.json` (hashes/counts) for diagnostics

The CLI should accept an explicit list of `ApiSchema.json` inputs (or a fixture file) and an output directory.

## Acceptance Criteria

- CLI generates deterministic outputs for the same inputs (byte-for-byte stable).
- Output filenames and layout match `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`.
- CLI does not require database connectivity for `ddl emit`.
- CLI surfaces actionable errors for invalid inputs (missing schema, invalid overrides, etc.).

## Tasks

1. Add/extend CLI parsing to support `ddl emit` with:
   - explicit `ApiSchema.json` file list,
   - output directory,
   - dialect selection.
2. Implement artifact emission to the standard filenames (`pgsql.sql`, `mssql.sql`, manifests).
3. Add CLI unit tests that validate:
   1. argument validation,
   2. deterministic outputs for a small fixture.
4. Update CLI README/documentation with the new `ddl emit` workflow.
