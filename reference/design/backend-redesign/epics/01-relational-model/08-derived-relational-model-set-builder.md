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

Dialect note: “dialect-aware” includes deterministic identifier-length handling and any other engine-specific defaults
that affect the SQL-free derived model inventories (e.g., collision detection and manifests). These “dialect rules”
(identifier length limits, shortening algorithm, type defaults) must be implemented once as a reusable component and
shared between E01 derivation (this epic) and E02 SQL emission, rather than duplicated in both layers.

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

Note (current code): `ExtractInputsStep` performs project-wide descriptor-path inference on every per-resource run (`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/ExtractInputsStep.cs:108`, `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/ExtractInputsStep.cs:135`). This needs to move into the `DMS-1033` set-level pass context so the inferred descriptor-path map can be computed once (per project/effective schema set) and reused across per-resource steps.

Clarification: the per-resource derivation pipeline in this epic builds a model for **one resource at a time** (selected by resource endpoint name). This story’s builder is responsible for looping over *all* resources across *all* configured `ApiSchema.json` inputs (core + extensions) to produce the complete `DerivedRelationalModelSet`.

Implementation note (ordered passes): implement this story as an ordered set-level pipeline of **passes**, where each pass performs a single iterative scan over the full effective schema set (projects/resources) and is allowed to consult other resources/projects as needed. Cross-resource derivation logic should live with the pass/story that needs it (e.g., reference/descriptor inference in DMS-930, abstract hierarchy discovery in DMS-933), rather than being forced into a single shared registry for performance. Determinism is still required: every pass must iterate projects/resources in canonical ordinal order and must not depend on dictionary iteration order.

### Proposed pass ordering (current)

This list is intentionally “high level”; exact pass boundaries can be adjusted as the implementations land:

1. **Base traversal + descriptor binding pass** (DMS-929): derive root/collection tables + scalar columns and bind descriptor edges while discovering `_ext` sites.
2. **Descriptor resource mapping pass** (DMS-942): detect/validate descriptor resources and bind descriptor storage/resource mapping metadata.
3. **Extension pass** (DMS-932): derive extension tables for discovered `_ext` sites (including project-key resolution).
4. **Reference binding pass** (DMS-930): bind document references into tables by adding FK/identity columns and emitting `DocumentReferenceBinding` metadata.
5. **Abstract artifact pass** (DMS-933): derive abstract identity tables and abstract union views by scanning hierarchy members. This runs after reference binding because abstract identity/view derivation may depend on reference-bound identity columns on concrete roots, and before reference constraint derivation so FK targets for abstract references can resolve to identity tables.
6. **Root identity constraint pass** (DMS-930): derive unique constraints for root identity columns.
7. **Reference constraint pass** (DMS-930): derive FK and all-or-none constraints using the bound reference metadata and abstract identity tables.
8. **Array uniqueness constraint pass** (DMS-930): derive array uniqueness constraints after all table/constraint prerequisites exist.
9. **Constraint dialect hashing pass**: apply dialect-specific deterministic constraint hashing before identifier shortening.
10. **Dialect identifier shortening pass** (DMS-931): apply identifier shortening and whole-set collision validation.
11. **Canonical ordering pass**: normalize final model inventories into deterministic output ordering.

`DMS-934` (manifest emission) should serialize the final `DerivedRelationalModelSet` inventories and should not re-derive anything independently.

## Acceptance Criteria

- Given a fixture with multiple `ApiSchema.json` inputs (core + at least one extension), the derived `DerivedRelationalModelSet` is stable across:
  - input file ordering,
  - JSON whitespace/property ordering within files (assuming canonicalization rules from E00),
  - and dictionary iteration order in C#.
- Projects appear exactly once in `ProjectSchemasInEndpointOrder`, sorted by `ProjectEndpointName` ordinal.
- Fail fast when project-schema normalization would create ambiguous physical schemas:
  - two projects normalize to the same physical schema name (per `data-model.md`), or
  - two projects collide on `ProjectEndpointName` after normalization/validation rules defined in E00.
- All concrete resources in the effective schema set that are **not** `isResourceExtension: true` contribute a `ConcreteResourceModel` (including descriptor resources), with `ConcreteResourcesInNameOrder` sorted ordinal by `(project_name, resource_name)` (per `compiled-mapping-set.md`). Resource-extension schemas (`isResourceExtension: true`) are excluded because they are mapped as `_ext` extension tables attached to their owning base resource per `extensions.md`.
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
   - the target SQL dialect and shared dialect rules (identifier limits/shortening/type defaults).
2. Implement a `DerivedRelationalModelSetBuilder` that:
   - constructs `ProjectSchemasInEndpointOrder` (including physical schema name normalization + collision validation),
   - defines a stable pass hook point (e.g., `IRelationalModelSetPass` + a shared builder context) so other stories can register set-level passes,
   - configures and executes ordered full-schema passes (each pass scans all projects/resources),
   - aggregates results into a single `DerivedRelationalModelSet` per `compiled-mapping-set.md`.
3. Provide pass implementations direct access to the full effective schema set (core + extensions) so they can:
   - resolve `_ext` project keys,
   - validate descriptor/document-reference targets,
   - and discover abstract resource hierarchies.
4. Add unit tests using a small multi-project fixture (core + extension) that assert:
   1. determinism across input ordering,
   2. unknown `_ext` key failure,
   3. physical schema name collision failure,
   4. missing descriptor/reference target failure.
