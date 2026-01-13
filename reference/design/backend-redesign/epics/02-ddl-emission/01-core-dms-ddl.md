# Story: Emit Core `dms.*` DDL (Including Update-Tracking Triggers)

## Description

Generate deterministic DDL for all required core objects in schema `dms`, per:

- `reference/design/backend-redesign/data-model.md`
- `reference/design/backend-redesign/update-tracking.md`
- `reference/design/backend-redesign/ddl-generation.md`

Includes tables, constraints, indexes, sequences, and journaling triggers.

## Acceptance Criteria

- Generated DDL includes (at minimum) the v1 inventory from `ddl-generation.md`:
  - `dms.ResourceKey`, `dms.Document`, `dms.IdentityLock`, `dms.ReferentialIdentity`, `dms.Descriptor`, `dms.ReferenceEdge`
  - optional projection table: `dms.DocumentCache`
  - `dms.EffectiveSchema`, `dms.SchemaComponent`
  - `dms.ChangeVersionSequence`, `dms.DocumentChangeEvent`, `dms.IdentityChangeEvent`
  - required journaling triggers/functions on `dms.Document`
- All identifiers are quoted per dialect.
- No authorization tables/views (`auth.*`, `dms.DocumentSubject`, etc.) are emitted.
- DDL output for small fixtures is snapshot-testable and deterministic.

## Tasks

1. Implement DDL emission for each required `dms.*` table/sequence/index, using the dialect writer.
2. Implement update-tracking trigger emission per `reference/design/backend-redesign/update-tracking.md` (PG and MSSQL variants).
3. Ensure deterministic ordering of statements (phased ordering per `ddl-generation.md`).
4. Add snapshot tests that validate core DDL output for a small fixture (both dialects).
