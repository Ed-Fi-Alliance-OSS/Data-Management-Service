---
jira: DMS-1105
jira_url: https://edfi.atlassian.net/browse/DMS-1105
---

# Story: Load and Reconstitute the Current Stored Document for Profile Projection

## Description

Implement the write-side current-state loader needed by profile-constrained update/upsert flows.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/overview.md`

Dependency note: this story is hard-blocked on the following Core profile stories produced by the delivery plan spike (`01a-core-profile-delivery-plan.md`):
- `DMS-984` (`03-persist-and-batch.md`) — provides the rebased executor/session write path and the existing current-state load that this story extends,
- C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — provides the shared compiled-scope adapter this story hands off to Core, and
- C6 (`01a-c6-stored-state-projection-and-hidden-member-paths.md`) — the Core-owned stored-state projector that consumes the current document loaded by this story.

In the rebased `DMS-984` branch, `RelationalWriteCurrentState` already supplies row/metadata state for no-profile executor behavior, but it does not yet return the fully reconstituted stored JSON document needed by Core's writable-profile projector. This story extends that existing current-state load rather than introducing a separate profile-only lookup path.

In the rebased `DMS-984` branch, relational repository orchestration temporarily fences any write carrying `BackendProfileWriteContext` with:

- `UnknownFailure`
- HTTP `500`
- `profile-aware relational writes pending DMS-1123/DMS-1105/DMS-1124`

This story does not remove that temporary fence by itself. It supplies the reconstituted current-document hand-off consumed later by `DMS-1124` once `DMS-1123`, `DMS-1105`, and `DMS-1124` are all in place.

This story owns the internal backend capability to:

- load the full current relational state for one existing document using compiled hydration/projection plans,
- reconstitute the full stored JSON document, including references, descriptors, nested collections, and `_ext`, before any readable-profile filtering, and
- hand that current stored document plus the selected mapping-set-scoped compiled-scope catalog or equivalent adapter to the profile write-context assembly path so Core can assemble the full `ProfileAppliedWriteContext`, including `VisibleStoredBody`, `StoredScopeStates`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths` metadata.

This is distinct from `E08`: public GET/query endpoints, paging, and readable-profile response projection remain read-path work. This story only delivers the write-path prerequisite needed by profiled merge and no-op execution.

When this story adds profiled fixtures, it should reuse the shared scenario names from `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md`, especially the nested and `_ext` variants already carried under the collection-preservation scenarios.

## Acceptance Criteria

- For `PUT`, and for `POST` when upsert resolves to an existing document after the executor's final in-session target resolution, backend can load the current relational rows for the target `DocumentId` before profile-constrained merge decisions are finalized.
- The write-side current-document load covers:
  - root rows,
  - nested collection/common-type rows,
  - resource-level and collection/common-type `_ext` rows,
  - reference identity projection, and
  - descriptor projection.
- Reconstituted current JSON matches the stored document shape expected by Core's writable-profile projector, preserves `DMS-991` reference identity values and descriptor values, and does not apply readable-profile filtering from `DMS-1113`.
- The reconstituted current JSON preserves the collection ancestry and `_ext` placement Core needs to derive stored-side `ScopeInstanceAddress` and `CollectionRowAddress` values against the selected compiled-scope catalog or equivalent adapter for nested and extension-aligned scopes.
- The write-path profile projection hand-off includes the same selected compiled-scope catalog or equivalent adapter used for request-side derivation; Core does not need to inspect hydration or write-plan types directly.
- The current-state load is sufficient for Core to assemble the full stored-side profile contract required by profiled merge execution, not just `VisibleStoredBody`; specifically, it supports `StoredScopeStates` with `VisiblePresent` / `VisibleAbsent` / `Hidden`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths`.
- The loaded current rows preserve the stored values needed for hidden-member overlay on matched profiled rows/scopes, including canonical key-unification storage columns, synthetic presence flags, and hidden reference/descriptor FK bindings, so backend can account for every non-storage-managed compiled binding before DML.
- The write pipeline can reuse the same current-state load for profile projection and downstream merge/no-op comparison instead of issuing a second "load current document" roundtrip.
- One current-state load produces both the existing executor-facing row/metadata state and the reconstituted stored JSON document needed by Core; the profiled write path does not issue a separate profile-only load.
- Completing this story alone does not route profiled relational writes through the executor or change the temporary `UnknownFailure` / `500` fence behavior; fence removal remains owned by `DMS-1124`.
- Unit or integration tests cover at least one nested + `_ext` fixture in a profiled update/upsert flow, reusing a nested or `_ext` variant from `ProfileVisibleRowUpdateWithHiddenRowPreservation` or `ProfileHiddenExtensionChildCollectionPreservation`, and prove the stored-side projection can derive addresses aligned to the shared compiled-scope adapter.

## Tasks

1. Extend the existing write-side current-state loader used by the `DMS-984` executor for a single existing document so it can also reconstitute and return the full stored JSON document alongside the executor-facing row/metadata state.
2. Hydrate root/child/extension tables with deterministic ordering keyed by `DocumentId`, `CollectionItemId`, `ParentCollectionItemId`, and `BaseCollectionItemId` where collection/common-type extension scopes align to a base row.
3. Reconstitute the full stored JSON document, including reference identity values, descriptor values, and `_ext` overlays, without applying readable-profile filtering.
4. Surface the reconstituted current document plus the selected compiled-scope catalog or equivalent adapter Core uses for address derivation and canonical member-path vocabulary to the profile write-context assembly path after the executor's final in-session target resolution so Core can produce `VisibleStoredBody`, `StoredScopeStates`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths`, while preserving current canonical storage, synthetic presence, and hidden FK/descriptor values needed for downstream binding-accounting.
5. Ensure downstream profiled merge/no-op work in `DMS-1124` can reuse this same current-state load without a second current-document roundtrip.
6. Add tests proving profiled update/upsert flows can project current stored state without relying on the public read pipeline, reusing shared nested or `_ext` scenario names where applicable and verifying stored-side address derivation stays aligned with the shared compiled-scope adapter.
