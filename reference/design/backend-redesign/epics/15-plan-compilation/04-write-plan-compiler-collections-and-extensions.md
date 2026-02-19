---
jira: DMS-1045
jira_url: https://edfi.atlassian.net/browse/DMS-1045
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

## Scope (What This Story Is Talking About)

- Owns compilation of `ResourceWritePlan` / `TableWritePlan` for resources stored as relational tables (non-descriptor resources).
- Owns the SQL + binding metadata required by replace semantics:
  - delete existing child/extension rows for a parent key, then
  - bulk insert the current payload rows.
- Does not implement the runtime flattener/executor (owned by the write-path stories); this story only produces compiled SQL + deterministic binding metadata those executors consume.

## Acceptance Criteria

### Write-plan coverage

- The plan compiler produces a `TableWritePlan` for every table in `TablesInDependencyOrder`:
  - `InsertSql` for all tables,
  - `UpdateSql` for root tables where applicable,
  - `DeleteByParentSql` for non-root tables (child/collection/_ext).
- `DeleteByParentSql` predicates are correct and scope-aligned:
  - deletes by the *parent key prefix* (root `DocumentId` plus any parent ordinals),
  - does not include the child row’s own `Ordinal` key column (deletes the whole collection/scope for that parent).

### Deterministic bindings (no SQL parsing)

- `ColumnBindings` ordering is deterministic and matches emitted SQL parameter positions.
- `ColumnBindings` and `WriteValueSource` coverage is sufficient for executors to materialize rows without SQL parsing:
  - key columns: `DocumentId`, `ParentKeyPart(i)`, `Ordinal`,
  - scalar values: `Scalar(relativeJsonPath, scalarType)`,
  - references: `DocumentReference(binding)` and `DescriptorReference(...)`,
  - key-unification-derived values: `Precomputed`.

### Key unification invariants

- Key unification invariants are enforced:
  - unified-alias columns are never written,
  - canonical storage columns and synthetic presence flags are bound as precomputed where required.
- Fail fast: the plan compiler rejects any table plan that:
  - attempts to write a `UnifiedAlias` column, or
  - leaves a `WriteValueSource.Precomputed` binding without exactly one corresponding `KeyUnificationWritePlan` producer.

### Batching

- Bulk insert batching respects dialect constraints (e.g., SQL Server parameter limits).
- Compiled plans carry deterministic per-table batching metadata (e.g., `MaxRowsPerBatch`) derived from:
  - dialect limits, and
  - the table’s bound/writable column count (`ColumnBindings.Count`).

### Testing

- Unit tests cover:
  - nested collections,
  - `_ext` tables,
  - key-unification cases (including presence gating),
  - deterministic SQL output (pgsql + mssql).
- When fixture-based artifacts are emitted, `mappingset.manifest.json` includes stable, normalized SQL hashes and binding-order metadata for:
  - `InsertSql` (all tables),
  - `UpdateSql` (root when applicable),
  - `DeleteByParentSql` (all non-root tables),
  enabling golden comparisons per `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`.

## Tasks

1. Implement write-plan compilation across all tables in dependency order:
   - generate `InsertSql`, optional `UpdateSql`, and `DeleteByParentSql`,
   - emit deterministic `ColumnBindings` that match SQL parameter order.
2. Implement `DeleteByParentSql` compilation for all non-root tables using the parent key prefix semantics (`DocumentId` + parent ordinals).
3. Implement dialect-aware batching metadata (e.g., SQL Server ~2100 parameter limit) and store it in `TableWritePlan` for bulk insert executors.
4. Enforce key-unification compile-time invariants (exclude `UnifiedAlias`, require complete `Precomputed` coverage by `KeyUnificationWritePlan`).
5. Add unit tests for deterministic output and key-unification invariants on representative models (pgsql + mssql).
6. Add (or extend) small fixtures that cover collections + `_ext` + key unification and validate write-plan output via `mappingset.manifest.json` golden comparisons (pgsql + mssql).
