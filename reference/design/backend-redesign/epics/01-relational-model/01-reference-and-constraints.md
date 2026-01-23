---
jira: DMS-930
jira_url: https://edfi.atlassian.net/browse/DMS-930
---

# Story: Bind References/Descriptors + Derive Constraints

## Description

Augment the base derived model with `documentPathsMapping`, `identityJsonPaths`, and `arrayUniquenessConstraints` per:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (unified `DerivedRelationalModelSet` output)

Key rules:
- Document reference objects are represented by one `..._DocumentId` FK column at the owning scope, plus propagated identity natural-key columns for the referenced resource.
- Descriptor strings are represented by one `..._DescriptorId` FK column and reconstituted from `dms.Descriptor`.
- Root natural key unique constraint is derived from `identityJsonPaths`, using propagated identity columns for identity components sourced from references.
- Child uniqueness constraints are derived from `arrayUniquenessConstraints`.

## Acceptance Criteria

- For each reference object in `documentPathsMapping.referenceJsonPaths`, the model creates:
  - a `..._DocumentId` column at the correct table scope,
  - `{RefBaseName}_{IdentityPart}` propagated identity columns at the same scope,
  - a `DocumentReferenceBinding` with correct `IsIdentityComponent` classification and column bindings.
- For each descriptor path, the model creates:
  - a single `..._DescriptorId` column at the correct scope,
  - descriptor binding metadata sufficient for reconstitution and (when applicable) identity classification.
- Root-table natural key UNIQUE constraint matches `identityJsonPaths` semantics.
- Child-table UNIQUE constraints are created per `arrayUniquenessConstraints` with deterministic column ordering.
- Unknown/mismatched mapping paths fail fast during model compilation (no silent omissions).

## Tasks

1. Implement binding of document references to:
   - `..._DocumentId` FK columns,
   - propagated identity columns, and
   - `DocumentReferenceBinding` metadata (including `IsIdentityComponent` and local column bindings).
2. Implement binding of descriptor paths to `..._DescriptorId` columns and descriptor binding metadata.
3. Implement constraint derivation:
   - root natural key unique,
   - child uniqueness from `arrayUniquenessConstraints`.
   - per-reference “all-or-none” CHECK constraints and composite FKs (for propagated identity columns).
4. Add unit tests covering:
   1. references inside nested collections (scope selection),
   2. identity-component classification,
   3. descriptor suppression/reconstitution inputs,
   4. fail-fast on unknown mapping paths.
