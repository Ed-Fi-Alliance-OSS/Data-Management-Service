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

## Handoff from DMS-930

DMS-930 intentionally stopped at provisional naming so the reference/constraint story could land. Current behavior:

- Provisional naming: `RelationalNameConventions` is used by `ProjectSchemaNormalizer`, `DeriveTableScopesAndKeysStep`, `DeriveColumnsAndBindDescriptorEdgesStep`, `ReferenceBindingRelationalModelSetPass`, `ConstraintDerivationRelationalModelSetPass`, and `AbstractIdentityTableDerivationRelationalModelSetPass` to assemble schema/table/column/constraint names directly from resource and JSONPath segments (PascalCase + singularization). Constraint names are emitted as `FK_{Table}_{ColumnSuffix}`, `UX_{Table}_{Col1}_{Col2}_...`, and `CK_{Table}_{FkColumn}_AllOrNone`. These identifiers are not shortened and can exceed dialect limits (PostgreSQL 63 bytes, SQL Server 128 chars).
- Placeholder overrides: `ExtractInputsStep.ExtractReferenceNameOverrides` validates `resourceSchema.relational.nameOverrides` JSONPaths and only accepts document reference object paths. Valid overrides only affect reference base names used in `ReferenceBindingRelationalModelSetPass`; all non-reference keys fail fast with `Only document reference object paths are supported until DMS-931.`
- Placeholder shortening: `RelationalModelSetBuilderContext.ValidateIdentifierShorteningCollisions` + `IdentifierCollisionDetector` call `ISqlDialectRules.ShortenIdentifier` to detect collisions, but derived identifiers are not rewritten. The model still carries the original long names.

DMS-931 responsibilities (naming handoff):

- Apply the full naming rules and override semantics (including `rootTableNameOverride` and non-reference `relational.nameOverrides`) across schema/table/column/constraint/index/trigger/view names in the derived model.
- Implement dialect shortening in the model itself (rewrite identifiers, then validate collisions after rewriting), reusing or replacing `IdentifierCollisionDetector`.
- Ensure collision detection runs on the rewritten identifiers and produces actionable diagnostics.

## Integration (ordered passes)

- Shared services: naming/override/shortening helpers are used by all per-resource derivation code when producing physical identifiers.
- Set-level (`DMS-1033`): include a whole-schema pass that validates override keys, applies any required post-processing (e.g., dialect shortening), and performs collision detection across the complete derived inventory (tables/columns/constraints/indexes/triggers/views).

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
6. Wire whole-set collision detection (and any dialect-shortening validation) into the `DMS-1033` ordered-pass builder.
