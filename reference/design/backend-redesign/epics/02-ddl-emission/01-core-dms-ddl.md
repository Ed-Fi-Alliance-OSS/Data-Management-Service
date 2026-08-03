---
jira: DMS-937
jira_url: https://edfi.atlassian.net/browse/DMS-937
---

# Story: Emit Core `dms.*` DDL (Including Update-Tracking Triggers)

> **Superseded by the `dms.Document` / `dms.ReferentialIdentity` removal:** the emitted core-object list.
> `dms.Document`, `dms.ReferentialIdentity` and `dms.DocumentCache` are no longer emitted; the core DDL
> emits `dms.Descriptor`, `dms.EffectiveSchema`, `dms.ResourceKey`, `dms.SchemaComponent`, three
> sequences, `GetMaxChangeVersion` (plus `throw_error` on PostgreSQL) and the SQL Server `BigIntTable`
> type. Retained as a historical work record. See [`docs/RELATIONAL-BACKEND.md` §4](../../../../../docs/RELATIONAL-BACKEND.md#4-debugging-the-writeread-paths-and-update-tracking-stored-stamps).

## Description

Generate deterministic DDL for all required core objects in schema `dms`, per:

- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/update-tracking.md`
- `reference/design/backend-redesign/design-docs/ddl-generation.md`

Includes tables, constraints, indexes, sequences, and journaling triggers.

## Acceptance Criteria

- Generated DDL includes (at minimum) the v1 inventory from `ddl-generation.md`:
  - `dms.ResourceKey`, `dms.Document`, `dms.ReferentialIdentity`, `dms.Descriptor`
  - optional projection table: `dms.DocumentCache`
  - `dms.EffectiveSchema`, `dms.SchemaComponent`
  - `dms.ChangeVersionSequence`, `dms.DocumentChangeEvent`
  - required journaling triggers/functions on `dms.Document`
- All identifiers are quoted per dialect.
- No authorization tables/views (`auth.*`, `dms.DocumentSubject`, etc.) are emitted.
- DDL output for small fixtures is snapshot-testable and deterministic.

## Tasks

1. Implement DDL emission for each required `dms.*` table/sequence/index, using the dialect writer.
2. Implement update-tracking trigger emission per `reference/design/backend-redesign/design-docs/update-tracking.md` (PG and MSSQL variants).
3. Ensure deterministic ordering of statements (phased ordering per `ddl-generation.md`).
4. Add snapshot tests that validate core DDL output for a small fixture (both dialects).
