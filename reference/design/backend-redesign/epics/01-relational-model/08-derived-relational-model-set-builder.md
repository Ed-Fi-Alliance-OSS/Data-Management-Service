---
jira: DMS-1033
jira_url: https://edfi.atlassian.net/browse/DMS-1033
---

# Story: Build `DerivedRelationalModelSet` from the Effective Schema Set

## Description

Implement the set-level orchestration that derives a single dialect-aware `DerivedRelationalModelSet` from the configured effective `ApiSchema.json` set (core + extensions), per:

- `reference/design/backend-redesign/epics/00-effective-schema-hash/00-schema-loader.md` (explicit multi-file schema set input contract)
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (unified `DerivedRelationalModelSet` shape + ordering rules)
- `reference/design/backend-redesign/design-docs/data-model.md` (project schema normalization + collision rules)
- `reference/design/backend-redesign/design-docs/extensions.md` (`_ext` project-key resolution rules)

This story introduces the missing “multi-document glue” between:
- the effective schema set (projects + resources) loaded/normalized by E00, and
- the per-resource derivation steps in this epic (DMS-929/930/931/932/933/942/945).

Key responsibilities:
- Establish canonical project ordering and validate cross-project invariants (e.g., physical schema name uniqueness).
- Invoke the per-resource pipeline for every concrete resource in every project, producing a complete inventory across core + extension projects.
- Provide shared set-level lookup services needed during derivation:
  - `_ext` project-key resolution to a configured project,
  - descriptor/document-reference target validation against the effective schema set,
  - abstract resource metadata lookup for polymorphic identity artifacts.

## Acceptance Criteria

- Given a fixture with multiple `ApiSchema.json` inputs (core + at least one extension), the derived `DerivedRelationalModelSet` is stable across:
  - input file ordering,
  - JSON whitespace/property ordering within files (assuming canonicalization rules from E00),
  - and dictionary iteration order in C#.
- Projects appear exactly once in `ProjectSchemasInEndpointOrder`, sorted by `ProjectEndpointName` ordinal.
- Fail fast when project-schema normalization would create ambiguous physical schemas:
  - two projects normalize to the same physical schema name (per `data-model.md`), or
  - two projects collide on `ProjectEndpointName` after normalization/validation rules defined in E00.
- All concrete resources in the effective schema set contribute a `ConcreteResourceModel`, with `ConcreteResourcesInNameOrder` sorted ordinal by `(project_name, resource_name)` (per `compiled-mapping-set.md`).
- `_ext` project keys discovered by traversal must resolve to a configured project:
  - first match on `ProjectEndpointName` (case-insensitive),
  - fallback match on `ProjectName` (case-insensitive),
  - otherwise fail fast with an actionable error that includes the owning JSON scope and the unknown key.
- `documentPathsMapping` targets (descriptors and document references) are validated against the effective schema set:
  - missing `(project_name, resource_name)` targets fail fast during model derivation.

## Tasks

1. Define a set-level input contract (library-first) that consumes:
   - the normalized multi-project schema set produced by E00,
   - the deterministic schema component list and resource key seed summary (`EffectiveSchemaInfo`),
   - the target SQL dialect.
2. Implement a `DerivedRelationalModelSetBuilder` that:
   - constructs `ProjectSchemasInEndpointOrder` (including physical schema name normalization + collision validation),
   - iterates all concrete resources and invokes the per-resource derivation pipeline steps from this epic,
   - aggregates results into a single `DerivedRelationalModelSet` per `compiled-mapping-set.md`.
3. Add a set-level project/resource registry used by derivation steps for:
   - `_ext` project-key resolution/validation,
   - cross-project target validation for descriptor/document-reference bindings,
   - abstract resource metadata lookup.
4. Add unit tests using a small multi-project fixture (core + extension) that assert:
   1. determinism across input ordering,
   2. unknown `_ext` key failure,
   3. physical schema name collision failure,
   4. missing descriptor/reference target failure.
