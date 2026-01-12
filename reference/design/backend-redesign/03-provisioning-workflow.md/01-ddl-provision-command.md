# Story: CLI Command — `ddl provision` (Create-Only)

## Description

Provide a CLI command that provisions a target database for a given `EffectiveSchemaHash` using the generated DDL.

Key behaviors (per `reference/design/backend-redesign/ddl-generation.md`):
- Create-only semantics (no migrations).
- Optional database creation (pre-step; outside transaction where required).
- Main provisioning runs in a single transaction:
  - schemas/tables/views/sequences/triggers
  - deterministic seeds and schema fingerprint recording
- Robust to partial runs via existence checks.

## Acceptance Criteria

- CLI can provision an empty PostgreSQL database and an empty SQL Server database.
- Provisioning records `dms.EffectiveSchema` and `dms.SchemaComponent` rows for the current effective schema.
- Provisioning is safe to rerun for the same effective schema and completes successfully.
- Provisioning does not emit or create authorization objects.

## Tasks

1. Define CLI connection options for each dialect (connection string + dialect selection).
2. Implement optional “create database if missing” behavior (dialect-specific).
3. Implement script execution against the target DB with a single transaction boundary for the main provisioning step.
4. Add integration tests or a script-first harness hook that provisions a local docker DB (pgsql + mssql) and validates success.

