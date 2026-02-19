---
jira: TBD
jira_url: TBD
---

# Story: Compile Hydration Read Plans (`SelectByKeysetSql`) for All Tables

## Description

Expand plan compilation from the root-only thin slice to full hydration read-plan coverage for all derived resource tables.

The read-path executor materializes a page keyset (`DocumentId`s) and then executes one `SELECT` per table using compiled `SelectByKeysetSql` statements. This story owns compiling those per-table `SELECT`s with stable ordering.

Design references:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (hydration usage + multi-result execution)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (read plan contract + ordering rules)

## Scope (What This Story Is Talking About)

- Owns compilation of per-table hydration SQL (`TableReadPlan.SelectByKeysetSql`) for all derived resource tables:
  - root tables,
  - child/collection tables (including nested),
  - `_ext` extension tables.
- Does not implement the multi-result execution layer (reader loops, `NextResult()`, etc.); this story only produces compiled SQL + deterministic ordering metadata that layer consumes.

## Acceptance Criteria

- The plan compiler produces a `TableReadPlan` for every table in `TablesInDependencyOrder`.
- Each `SelectByKeysetSql`:
  - joins to a materialized keyset table containing `BIGINT DocumentId`,
  - joins on the table’s root `DocumentId` key column (the first key part) so every table is filtered to the page,
  - emits a stable select-list order consistent with the table model (including any `UnifiedAlias` binding/path columns required for reconstitution and reference identity projection),
  - emits a deterministic `ORDER BY` on the table key columns in key order (parent key parts..., ordinal).
- SQL output is canonicalized and stable for the same selection key (pgsql + mssql).
- Unit tests validate deterministic output and stable ordering behavior.

## Tasks

1. Implement per-table hydration SQL compilation (`SelectByKeysetSql`) for all tables, using the shared SQL writer and deterministic alias naming utilities.
2. Ensure select-list ordering and `ORDER BY` ordering match the reconstitution contract:
   - select-list order derived from the table model’s column ordering,
   - `ORDER BY` derived from the table key columns (parent key parts..., ordinal).
3. Ensure the join predicate to the page keyset is correct for root, child, and `_ext` tables (root `DocumentId` key part).
4. Add unit tests for deterministic output across dialects and input-order permutations.
