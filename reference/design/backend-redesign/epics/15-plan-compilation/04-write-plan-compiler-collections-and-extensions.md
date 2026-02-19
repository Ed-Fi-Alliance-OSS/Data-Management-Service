---
jira: TBD
jira_url: TBD
---

# Story: Compile Write Plans for Child/Extension Tables (Replace Semantics + Batching)

## Description

Expand plan compilation from the root-only thin slice to full write-plan coverage for all derived resource tables:

- root table insert/update,
- child/collection tables using replace semantics (`DeleteByParentSql` + bulk insert),
- `_ext` extension tables (root + child), using the same replace semantics.

Design references:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (write plan usage + replace semantics)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (write plans + key-unification rules)
- `reference/design/backend-redesign/design-docs/key-unification.md` (write-time exclusion of unified alias columns)

## Acceptance Criteria

- The plan compiler produces a `TableWritePlan` for every table in `TablesInDependencyOrder`:
  - `InsertSql` for all tables,
  - `UpdateSql` for root tables where applicable,
  - `DeleteByParentSql` for non-root tables (child/collection/_ext).
- `ColumnBindings` ordering is deterministic and matches emitted SQL parameter positions.
- Key unification invariants are enforced:
  - unified-alias columns are never written,
  - canonical storage columns and synthetic presence flags are bound as precomputed where required.
- Bulk insert batching respects dialect constraints (e.g., SQL Server parameter limits).
- Unit tests cover:
  - nested collections,
  - `_ext` tables,
  - key-unification cases (including presence gating),
  - deterministic SQL output (pgsql + mssql).

## Tasks

1. Implement write-plan compilation across all tables in dependency order:
   - generate `InsertSql`, optional `UpdateSql`, and `DeleteByParentSql`.
2. Implement dialect-aware batching metadata used by bulk insert executors.
3. Add unit tests for deterministic output and key-unification invariants on representative models.

