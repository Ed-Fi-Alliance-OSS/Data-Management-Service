---
jira: DMS-928
jira_url: https://edfi.atlassian.net/browse/DMS-928
---

# Epic: Derived Relational Model

## Description

Derive a fully explicit relational model (schemas, tables, columns, constraints, indexes, views, triggers inventory) from the effective `ApiSchema.json` set, following the rules in:

- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (unified `DerivedRelationalModelSet` shape)
- `reference/design/backend-redesign/design-docs/extensions.md`

This epic focuses on *model derivation* (not SQL emission yet). The primary in-memory deliverable is `DerivedRelationalModelSet` (see `compiled-mapping-set.md`), which is then:
- consumed by DDL emission (E02) to generate SQL, and
- consumed by plan compilation (E15) to generate dialect-specific CRUD plans (used by runtime and optional packs).

This epic also produces a deterministic `relational-model.manifest.json` suitable for snapshot/golden tests.

Authorization objects remain out of scope.

## Stories

- `DMS-929` — `00-base-schema-traversal.md` — Traverse JSON schema → base tables/columns
- `DMS-930` — `01-reference-and-constraints.md` — References/descriptors + identity/uniqueness constraints
- `DMS-931` — `02-naming-and-overrides.md` — Naming rules + `relational.nameOverrides` + truncation/collision errors
- `DMS-932` — `03-ext-mapping.md` — `_ext` (extensions) relational mapping model
- `DMS-933` — `04-abstract-union-views.md` — Abstract identity tables + union view model derivation
- `DMS-934` — `05-relational-model-manifest.md` — Emit `relational-model.manifest.json` (stable)
- `DMS-942` — `06-descriptor-resource-mapping.md` — Map descriptor resources to `dms.Descriptor` (no per-descriptor tables)
- `DMS-945` — `07-index-and-trigger-inventory.md` — Derive deterministic indexes + triggers inventory (DDL intent)
- `DMS-1033` — `08-derived-relational-model-set-builder.md` — Build `DerivedRelationalModelSet` from the effective schema set
