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

## Acceptance Criteria

- The plan compiler produces a `TableReadPlan` for every table in `TablesInDependencyOrder`.
- Each `SelectByKeysetSql`:
  - joins to a materialized keyset table containing `DocumentId`,
  - emits a stable select-list order consistent with the table model,
  - emits a deterministic `ORDER BY` on the table key (parent key parts..., ordinal).
- SQL output is canonicalized and stable for the same selection key (pgsql + mssql).
- Unit tests validate deterministic output and stable ordering behavior.

## Tasks

1. Implement per-table hydration SQL compilation (`SelectByKeysetSql`) for all tables.
2. Ensure select-list ordering and `ORDER BY` ordering match the reconstitution contract.
3. Add unit tests for deterministic output across dialects and input-order permutations.

