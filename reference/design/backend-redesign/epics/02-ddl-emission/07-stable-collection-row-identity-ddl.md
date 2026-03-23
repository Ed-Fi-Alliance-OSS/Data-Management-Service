---
jira: DMS-1107
jira_url: https://edfi.atlassian.net/browse/DMS-1107
---

# Story: Emit Stable Collection Row Identity DDL (Sequence, PKs, FKs, Constraints)

## Description

Retrofit DDL emission to match the stable collection-row identity model introduced for profile-compatible writes.

This story applies the relational-model changes from `DMS-1103` to emitted PostgreSQL and SQL Server DDL per:

- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/extensions.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/profiles.md`

This story depends on `DMS-1103`'s relational-model contract split:

- DDL consumes the stable-identity and compiled semantic-identity metadata produced by the shared/default relational-model pipeline when that metadata exists, but
- DDL does not own or reintroduce global semantic-identity fail-fast for generic or hand-authored fixtures, and
- strict validation remains scoped to supported-model/runtime-boundary callers such as `DMS-1108`.

The new DDL contract must support:

- stable internal child-row identity via `CollectionItemId`,
- nested child attachment via `ParentCollectionItemId`,
- sibling ordering via `Ordinal` uniqueness rather than PK identity,
- collection/common-type extension alignment to base-row identity, and
- extension child-table FK chains that follow the immediate parent scope without introducing redundant root-cascade paths, and
- later write-path merge semantics that update matched rows in place instead of delete/reinsert.

## Acceptance Criteria

### Core DMS objects

- DDL emits `dms.CollectionItemIdSequence` for both PostgreSQL and SQL Server.
- Statement ordering ensures the sequence exists before any table defaults that depend on it.

### Relational-model boundary

- DDL consumes the permissive/shared relational-model output from `DMS-1103` rather than assuming `CreateDefault()` is globally strict.
- DDL emits compiled stable-identity and semantic-identity constraints when the upstream relational model exposes that metadata.
- Missing semantic identity in a generic or otherwise out-of-bound fixture is not, by itself, a universal DDL compilation failure.
- Any fail-fast requirement for missing semantic identity is scoped to callers that deliberately opt into the strict/runtime boundary rather than to DDL emission itself.

### Project/resource/extension tables

- Top-level collection/common-type tables emit:
  - `CollectionItemId` with the correct engine-specific default sourced from `dms.CollectionItemIdSequence`,
  - root `..._DocumentId`,
  - `Ordinal`,
  - primary key on `CollectionItemId`, and
  - unique constraints/indexes for sibling ordering and compiled semantic identity, where the relational-model metadata may come from scope-resolved `arrayUniquenessConstraints` or, for reference-backed scopes, exactly one qualifying scope-local `documentPathsMapping.referenceJsonPaths` binding set.
- Nested collection/common-type tables emit:
  - `CollectionItemId`,
  - `ParentCollectionItemId`,
  - root `..._DocumentId`,
  - `Ordinal`,
  - primary key on `CollectionItemId`, and
  - foreign keys/uniques that enforce `(ParentCollectionItemId, RootDocumentId)` parent/root consistency.
- Collection/common-type extension scope tables align to the owning base row identity instead of ancestor ordinals.
- Root-level extension child collections emit:
  - `CollectionItemId`,
  - root `..._DocumentId`,
  - `Ordinal`,
  - a parent-chain FK from that same root `..._DocumentId` column to the extension root row, and
  - delete semantics where base-root deletion reaches the child through the extension root rather than through a second independent root-cascade edge.
- Collection/common-type-aligned extension child collections emit:
  - `CollectionItemId`,
  - `BaseCollectionItemId`,
  - root `..._DocumentId`,
  - `Ordinal`, and
  - FKs/uniques that enforce attachment to the aligned extension scope/base collection chain.
- Nested extension child collections emit:
  - `CollectionItemId`,
  - `ParentCollectionItemId`,
  - root `..._DocumentId`,
  - `Ordinal`, and
  - parent/root consistency FKs to the immediate parent extension child row.

### Parity and determinism

- PostgreSQL and SQL Server DDL remain semantically equivalent for the stable-identity design.
- Canonical SQL output, fixtures, and deterministic manifests are updated for the new shape.
- DB-apply smoke coverage validates the emitted PK/UK/FK/sequence behavior for representative collection and extension tables.

## Tasks

1. Emit `dms.CollectionItemIdSequence` in core DMS DDL with deterministic ordering and engine-appropriate defaults.
2. Update project/resource table emission for top-level and nested collection/common-type tables to use stable row identity columns and constraints.
3. Update extension-table emission for collection/common-type scopes so extension rows key to the owning base `CollectionItemId`, and ensure aligned extension scope rows can act as the immediate FK parent for descendant extension child collections.
4. Update FK-supporting indexes and deterministic ordering rules for the new constraint inventory.
5. Emit the root-level, collection-aligned, and nested extension child-table FK/cascade chains so delete propagation follows the immediate parent scope without redundant root-cascade edges.
6. Refresh snapshot/golden fixtures and DB-apply smoke coverage for nested collections, root-level `_ext` child collections, and collection-aligned `_ext` scopes.
7. Keep DDL compilation on the permissive/shared relational-model pipeline from `DMS-1103`; this story consumes compiled semantic-identity metadata when present, but it does not move semantic-identity fail-fast out of the strict/runtime boundary owned by `DMS-1108`.
