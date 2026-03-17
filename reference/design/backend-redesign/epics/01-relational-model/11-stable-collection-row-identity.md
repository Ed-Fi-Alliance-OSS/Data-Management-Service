# Story: Derive Stable Collection Row Identity and Parent-Scope Keys

## Description

Retrofit the derived relational model so persisted collection/common-type scopes use a stable internal row identity instead of parent-scope ordinal keys.

This aligns completed epic `01` work to the profile-compatible design in:

- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/extensions.md`
- `reference/design/backend-redesign/design-docs/profiles.md`

The current derived model still treats collection tables as keyed by root document id plus ancestor ordinals plus `Ordinal`. That supports replace semantics, but it does not support:

- profile-scoped collection merges that preserve hidden stored rows,
- stable attachment of nested descendants to matched collection rows across updates, or
- scope-aligned extension tables keyed to the stable identity of the base row they extend.

This story updates the derived model to the new collection-key strategy:

- top-level collection/common-type tables get a stable `CollectionItemId`,
- nested collections additionally get `ParentCollectionItemId`,
- every collection table still carries the root `..._DocumentId`,
- `Ordinal` remains sibling-order state rather than physical row identity, and
- extension/common-type scope tables under collections align to the owning base `CollectionItemId`.

## Acceptance Criteria

### Collection/common-type table shape

- Persisted collection/common-type tables in the derived model expose:
  - `CollectionItemId` as the physical row identity,
  - root `..._DocumentId` on every collection table,
  - `ParentCollectionItemId` on nested collection tables, and
  - `Ordinal` as sibling-order metadata rather than as part of the primary key.
- Top-level collection/common-type tables no longer use `(RootDocumentId, ..., Ordinal)` as the modeled primary key.
- Nested collection/common-type tables no longer use ancestor ordinals as the modeled parent locator.

### Constraint and locator semantics

- The derived constraint inventory models:
  - primary key on `CollectionItemId`,
  - sibling-order uniqueness on `(ParentScope, Ordinal)`,
  - semantic collection identity uniqueness separate from the PK, and
  - nested parent/root consistency via `(ParentCollectionItemId, RootDocumentId)` foreign keys.
- Collection/common-type extension scope tables align to the owning base row identity:
  - root-scope extension rows remain keyed by `DocumentId`,
  - collection/common-type extension scope rows are keyed by the base row `CollectionItemId`,
  - extension child collections use the same stable-identity strategy as core collections.

### Downstream model contract

- The derived model exposes enough deterministic metadata for downstream consumers to:
  - attach nested child rows by stable parent row identity,
  - preserve matched row identity across write-time merges,
  - distinguish physical row identity from sibling ordering, and
  - derive read/write plans without reconstructing parent-ordinal keys.
- Persisted multi-item collection scopes fail validation/compilation when they do not compile a non-empty semantic identity.

### Verification

- Relational-model manifest output and authoritative goldens are updated to reflect the stable-identity shape.
- Unit tests cover top-level collections, nested collections, and collection-aligned extension scopes.

## Tasks

1. Update collection/common-type key derivation in the relational-model builder to model stable `CollectionItemId` and `ParentCollectionItemId`.
2. Update extension table derivation so collection/common-type extension scopes align to base-row stable identity instead of ancestor ordinals.
3. Derive the new PK/UK/FK inventories for stable collection identity, sibling ordering, and parent/root consistency.
4. Expose stable-identity metadata needed by downstream DDL, plan-compilation, and read-path consumers.
5. Add or align validation so persisted multi-item collection scopes fail when they do not compile a non-empty semantic identity.
6. Update unit tests, manifests, and authoritative goldens for representative nested-collection and `_ext` fixtures.
