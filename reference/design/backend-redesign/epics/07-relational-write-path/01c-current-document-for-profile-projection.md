# Story: Load and Reconstitute the Current Stored Document for Profile Projection

## Description

Implement the write-side current-state loader needed by profile-constrained update/upsert flows.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/overview.md`

This story owns the internal backend capability to:

- load the full current relational state for one existing document using compiled hydration/projection plans,
- reconstitute the full stored JSON document, including references, descriptors, nested collections, and `_ext`, before any readable-profile filtering, and
- hand that current stored document to the profile write-context assembly path so Core can assemble the full `ProfileAppliedWriteContext`, including `VisibleStoredBody`, stored-scope visibility, visible stored collection-row metadata, and hidden-member preservation metadata.

This is distinct from `E08`: public GET/query endpoints, paging, and readable-profile response projection remain read-path work. This story only delivers the write-path prerequisite needed by profiled merge and no-op execution.

## Acceptance Criteria

- For `PUT`, and for `POST` when upsert resolves to an existing document, backend can load the current relational rows for the target `DocumentId` before profile-constrained merge decisions are finalized.
- The write-side current-document load covers:
  - root rows,
  - nested collection/common-type rows,
  - resource-level and collection/common-type `_ext` rows,
  - reference identity projection, and
  - descriptor projection.
- Reconstituted current JSON matches the stored document shape expected by Core's writable-profile projector and does not apply readable-profile filtering.
- The current-state load is sufficient for Core to assemble the full stored-side profile contract required by profiled merge execution, not just `VisibleStoredBody`.
- The write pipeline can reuse the same current-state load for profile projection and downstream merge/no-op comparison instead of issuing a second "load current document" roundtrip.
- Unit or integration tests cover at least one nested + `_ext` fixture in a profiled update/upsert flow.

## Tasks

1. Implement the write-side current-state loader for a single existing document using the compiled hydration SQL and projection plans already selected for the active mapping set.
2. Hydrate root/child/extension tables with deterministic ordering keyed by `DocumentId`, `CollectionItemId`, `ParentCollectionItemId`, and `BaseCollectionItemId` where collection/common-type extension scopes align to a base row.
3. Reconstitute the full stored JSON document, including reference identity values, descriptor values, and `_ext` overlays, without applying readable-profile filtering.
4. Surface the reconstituted current document to the profile write-context assembly path so Core can produce `VisibleStoredBody`, stored-scope visibility, visible stored collection-row metadata, and hidden-member preservation metadata.
5. Add tests proving profiled update/upsert flows can project current stored state without relying on the public read pipeline.
