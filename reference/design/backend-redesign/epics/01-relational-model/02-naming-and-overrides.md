---
jira: DMS-931
jira_url: https://edfi.atlassian.net/browse/DMS-931
---

# Story: Apply Naming Rules + `relational.nameOverrides`

## Description

Implement deterministic physical naming per `reference/design/backend-redesign/design-docs/data-model.md` and override semantics per `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Project schema name normalization from `ProjectEndpointName`.
- Table/column naming (PascalCase, stable suffixes like `_DocumentId` / `_DescriptorId`).
- Deterministic singularization for collection segments (with override escape hatch).
- Identifier length handling (truncate + hash suffix per dialect).
- `relational.nameOverrides` restricted JSONPath grammar + fail-fast validation.
- Collision detection after normalization/truncation/overrides.

This story is part of building the unified `DerivedRelationalModelSet` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`) so DDL emission and plan compilation consume the same physical names.

## Acceptance Criteria

- Given the same effective schema, physical names are deterministic and stable across runs.
- `relational.nameOverrides` keys must match derived elements; unknown keys fail fast.
- Overrides cannot create naming collisions; collisions fail fast with actionable diagnostics.
- Identifier shortening uses deterministic SHA-256-based suffixing and respects:
  - PostgreSQL 63-byte identifier limit,
  - SQL Server 128-character identifier limit.
- Tests include a “naming-stress” fixture exercising:
  - long names,
  - overrides,
  - reserved-word cases,
  - collision detection.

## Tasks

1. Implement schema/table/column naming services following the naming rules in `reference/design/backend-redesign/design-docs/data-model.md`.
2. Implement restricted JSONPath parsing/validation for `nameOverrides`.
3. Implement deterministic singularization and override application for collection naming.
4. Implement identifier shortening and post-shortening collision detection.
5. Add unit tests for:
   1. valid overrides,
   2. unknown override keys,
   3. collisions,
   4. length-limit shortening determinism.
