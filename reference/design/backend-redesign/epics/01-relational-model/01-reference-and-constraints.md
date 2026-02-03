---
jira: DMS-930
jira_url: https://edfi.atlassian.net/browse/DMS-930
---

# Story: Bind References + Derive Constraints

## Description

Augment the base derived model with `documentPathsMapping`, `identityJsonPaths`, and `arrayUniquenessConstraints` per:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (unified `DerivedRelationalModelSet` output)

Key rules:
- Document reference objects are represented by one `..._DocumentId` FK column at the owning scope, plus propagated
  identity natural-key columns for the referenced resource.
- Root natural key unique constraint is derived from `identityJsonPaths`, using the `..._DocumentId` FK column for
  identity components sourced from references.
- Child uniqueness constraints are derived from `arrayUniquenessConstraints`.

Descriptor binding (`*_DescriptorId` columns + descriptor edge metadata) is handled in the base traversal pass
(`DMS-929`).

### Implicit contracts enforced by DMS-930

These rules are consistent with the `flattening-reconstitution.md` semantics, but they are currently enforced by
implementation code rather than spelled out in the design docs:

- Identity-component document references must be required.
  - If a document reference participates in `identityJsonPaths`, the mapping entry must have `isRequired=true`.
  - Enforced in `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/ExtractInputsStep.cs` when
    extracting documentPathsMapping (the "Identity references must be required" guard).
  - Rationale: identityJsonPaths represent required natural-key parts; optional references would allow null identity
    components and undermine uniqueness guarantees.
- Array-uniqueness paths that resolve to reference identity fields bind to the reference FK column.
  - When an `arrayUniquenessConstraints` path matches a reference identity path, the derived UNIQUE constraint uses
    the reference `..._DocumentId` column rather than the propagated identity columns.
  - Enforced in
    `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/ArrayUniquenessConstraintRelationalModelSetPass.cs`
    (`ResolveArrayUniquenessColumn` uses `DocumentReferenceBinding.FkColumn` for identity-path matches).
  - Rationale: the FK `..._DocumentId` is the stable key for the referenced document; using it preserves determinism
    and aligns with the root-identity rule above.

## Integration (ordered passes)

- Per-resource: derive reference columns and constraints for a single resource using its `documentPathsMapping`,
  `identityJsonPaths`, and `arrayUniquenessConstraints`.
- Set-level (`DMS-1033`): run as a whole-schema pass after base tables/columns (and after extension tables exist, if extension sites participate). This pass is allowed to scan/consult other resources/projects to:
  - validate document-reference targets exist in the effective schema set,
  - determine target identity projection contracts (including abstract targets),
  - and infer descriptor identity parts inside reference objects when needed.

## Acceptance Criteria

- For each reference object in `documentPathsMapping.referenceJsonPaths`, the model creates:
  - a `..._DocumentId` column at the correct table scope,
  - `{RefBaseName}_{IdentityPart}` propagated identity columns at the same scope,
  - a `DocumentReferenceBinding` with correct `IsIdentityComponent` classification and column bindings.
- Root-table natural key UNIQUE constraint matches `identityJsonPaths` semantics.
  - For identity components sourced from references, the UNIQUE constraint uses the `..._DocumentId` column.
- Child-table UNIQUE constraints are created per `arrayUniquenessConstraints` with deterministic column ordering.
- Unknown/mismatched mapping paths fail fast during model compilation (no silent omissions).

## Tasks

1. Implement binding of document references to:
   - `..._DocumentId` FK columns,
   - propagated identity columns, and
   - `DocumentReferenceBinding` metadata (including `IsIdentityComponent` and local column bindings).
2. Implement constraint derivation:
   - root natural key unique,
   - child uniqueness from `arrayUniquenessConstraints`.
   - per-reference “all-or-none” CHECK constraints and composite FKs (for propagated identity columns).
3. Add unit tests covering:
   1. references inside nested collections (scope selection),
   2. identity-component classification,
   3. fail-fast on unknown mapping paths.
4. Wire this derivation into the `DMS-1033` set-level builder as a whole-schema pass over all resources.
