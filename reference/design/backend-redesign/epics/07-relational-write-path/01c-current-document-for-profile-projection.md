# Story: Load and Reconstitute the Current Stored Document for Profile Projection

## Description

Implement the write-side current-state loader needed by profile-constrained update/upsert flows.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/overview.md`

Dependency note: this story is hard-blocked on `reference/design/backend-redesign/epics/07-relational-write-path/01a-core-profile-delivery-plan.md`, which plans the Core-owned stored-state projection and address-derivation work it hands off to.

This story owns the internal backend capability to:

- load the full current relational state for one existing document using compiled hydration/projection plans,
- reconstitute the full stored JSON document, including references, descriptors, nested collections, and `_ext`, before any readable-profile filtering, and
- hand that current stored document to the profile write-context assembly path so Core can assemble the full `ProfileAppliedWriteContext`, including `VisibleStoredBody`, `StoredScopeStates`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths` metadata.

This is distinct from `E08`: public GET/query endpoints, paging, and readable-profile response projection remain read-path work. This story only delivers the write-path prerequisite needed by profiled merge and no-op execution.

When this story adds profiled fixtures, it should reuse the shared scenario names from `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md`, especially the nested and `_ext` variants already carried under the collection-preservation scenarios.

## Acceptance Criteria

- For `PUT`, and for `POST` when upsert resolves to an existing document, backend can load the current relational rows for the target `DocumentId` before profile-constrained merge decisions are finalized.
- The write-side current-document load covers:
  - root rows,
  - nested collection/common-type rows,
  - resource-level and collection/common-type `_ext` rows,
  - reference identity projection, and
  - descriptor projection.
- Reconstituted current JSON matches the stored document shape expected by Core's writable-profile projector and does not apply readable-profile filtering.
- The reconstituted current JSON preserves the collection ancestry and `_ext` placement Core needs to derive stored-side `ScopeInstanceAddress` and `CollectionRowAddress` values against compiled scope metadata for nested and extension-aligned scopes.
- The current-state load is sufficient for Core to assemble the full stored-side profile contract required by profiled merge execution, not just `VisibleStoredBody`; specifically, it supports `StoredScopeStates` with `VisiblePresent` / `VisibleAbsent` / `Hidden`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths`.
- The loaded current rows preserve the stored values needed for hidden-member overlay on matched profiled rows/scopes, including canonical key-unification storage columns, synthetic presence flags, and hidden reference/descriptor FK bindings, so backend can account for every non-storage-managed compiled binding before DML.
- The write pipeline can reuse the same current-state load for profile projection and downstream merge/no-op comparison instead of issuing a second "load current document" roundtrip.
- Unit or integration tests cover at least one nested + `_ext` fixture in a profiled update/upsert flow, reusing a nested or `_ext` variant from `ProfileVisibleRowUpdateWithHiddenRowPreservation` or `ProfileHiddenExtensionChildCollectionPreservation`, and prove the stored-side projection can derive addresses aligned to compiled scope metadata.

## Tasks

1. Implement the write-side current-state loader for a single existing document using the compiled hydration SQL and projection plans already selected for the active mapping set.
2. Hydrate root/child/extension tables with deterministic ordering keyed by `DocumentId`, `CollectionItemId`, `ParentCollectionItemId`, and `BaseCollectionItemId` where collection/common-type extension scopes align to a base row.
3. Reconstitute the full stored JSON document, including reference identity values, descriptor values, and `_ext` overlays, without applying readable-profile filtering.
4. Surface the reconstituted current document plus the compiled-scope metadata or adapter Core uses for address derivation to the profile write-context assembly path so Core can produce `VisibleStoredBody`, `StoredScopeStates`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths`, while preserving current canonical storage, synthetic presence, and hidden FK/descriptor values needed for downstream binding-accounting.
5. Add tests proving profiled update/upsert flows can project current stored state without relying on the public read pipeline, reusing shared nested or `_ext` scenario names where applicable and verifying stored-side address derivation stays aligned with compiled scope metadata.
