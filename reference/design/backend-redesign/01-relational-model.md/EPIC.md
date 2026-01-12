# Epic: Derived Relational Model (v1)

## Description

Derive a fully explicit relational model (schemas, tables, columns, constraints, indexes, views, triggers inventory) from the effective `ApiSchema.json` set, following the rules in:

- `reference/design/backend-redesign/data-model.md`
- `reference/design/backend-redesign/flattening-reconstitution.md`
- `reference/design/backend-redesign/extensions.md`

This epic focuses on *model derivation* (not SQL emission yet) and producing a deterministic `relational-model.manifest.json` suitable for snapshot/golden tests.

Authorization objects remain out of scope.

## Stories

- `00-base-schema-traversal.md` — Traverse JSON schema → base tables/columns
- `01-reference-and-constraints.md` — References/descriptors + identity/uniqueness constraints
- `02-naming-and-overrides.md` — Naming rules + `relational.nameOverrides` + truncation/collision errors
- `03-ext-mapping.md` — `_ext` (extensions) relational mapping model
- `04-abstract-union-views.md` — Abstract resource union view model derivation
- `05-relational-model-manifest.md` — Emit `relational-model.manifest.json` (stable)

