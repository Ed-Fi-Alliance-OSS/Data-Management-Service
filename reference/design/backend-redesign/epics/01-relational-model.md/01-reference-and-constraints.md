# Story: Bind References/Descriptors + Derive Constraints

## Description

Augment the base derived model with `documentPathsMapping`, `identityJsonPaths`, and `arrayUniquenessConstraints` per:

- `reference/design/backend-redesign/flattening-reconstitution.md`
- `reference/design/backend-redesign/data-model.md`

Key rules:
- Document reference objects are represented by one `..._DocumentId` FK column at the owning scope (not duplicated natural-key columns).
- Descriptor strings are represented by one `..._DescriptorId` FK column and reconstituted from `dms.Descriptor`.
- Root natural key unique constraint is derived from `identityJsonPaths`, using `..._DocumentId` columns for identity components sourced from references.
- Child uniqueness constraints are derived from `arrayUniquenessConstraints`.

## Acceptance Criteria

- For each reference object in `documentPathsMapping.referenceJsonPaths`, the model creates:
  - a single `..._DocumentId` column at the correct table scope,
  - a `DocumentReferenceEdgeSource` with correct `IsIdentityComponent` classification.
- For each descriptor path, the model creates:
  - a single `..._DescriptorId` column at the correct scope,
  - a `DescriptorEdgeSource` with `IsIdentityComponent` when applicable.
- Root-table natural key UNIQUE constraint matches `identityJsonPaths` semantics.
- Child-table UNIQUE constraints are created per `arrayUniquenessConstraints` with deterministic column ordering.
- Unknown/mismatched mapping paths fail fast during model compilation (no silent omissions).

## Tasks

1. Implement binding of document references to FK columns and edge-source metadata (including `IsIdentityComponent`).
2. Implement binding of descriptor paths to `..._DescriptorId` columns and edge-source metadata.
3. Implement constraint derivation:
   - root natural key unique,
   - child uniqueness from `arrayUniquenessConstraints`.
4. Add unit tests covering:
   1. references inside nested collections (scope selection),
   2. identity-component classification,
   3. descriptor suppression/reconstitution inputs,
   4. fail-fast on unknown mapping paths.

