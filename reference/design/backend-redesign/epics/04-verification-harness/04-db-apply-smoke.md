---
jira: DMS-961
jira_url: https://edfi.atlassian.net/browse/DMS-961
---

# Story: DB-Apply Smoke Tests (Docker Compose; PGSQL + MSSQL)

## Description

Implement DB-apply smoke tests that:

1. start a fresh PostgreSQL/SQL Server using docker compose,
2. apply generated DDL to an empty database,
3. apply the same DDL a second time (idempotency),
4. introspect the provisioned schema into a stable manifest,
5. run a minimal journaling trigger smoke check (insert/update `dms.Document` emits journal rows).

Testcontainers is explicitly not allowed; use docker compose.

## Acceptance Criteria

- A script-first workflow exists (PowerShell and/or bash) to run DB-apply for both engines.
- Applying the same DDL twice succeeds for both engines.
- Introspection manifest includes (at minimum):
  - schemas, tables, columns/types/nullability,
  - PK/UK/FK constraints,
  - indexes,
  - views,
  - triggers,
  - `dms.EffectiveSchema` / `dms.SchemaComponent` / `dms.ResourceKey` rows.
- Journaling trigger smoke check passes:
  - inserting into `dms.Document` emits rows in `dms.DocumentChangeEvent`.
  - updating multiple `dms.Document` rows in one statement emits one `dms.DocumentChangeEvent` row per updated document and uses distinct `ChangeVersion` values (watermark-only compatibility).

## Tasks

1. Add docker compose configurations (or profiles) for pgsql and mssql suitable for tests.
2. Implement scripts to:
   1. start DB,
   2. apply DDL (pgsql: `psql`; mssql: `sqlcmd`),
   3. apply again,
   4. run trigger smoke SQL.
3. Implement engine-specific introspection queries and emit `provisioned-schema.{dialect}.manifest.json`.
4. Add optional NUnit wrappers (category `DbApply`) that invoke the scripts and fail with captured logs on error.
