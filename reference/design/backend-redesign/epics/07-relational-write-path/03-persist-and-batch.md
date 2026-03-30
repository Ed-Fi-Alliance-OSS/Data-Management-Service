---
jira: DMS-984
jira_url: https://edfi.atlassian.net/browse/DMS-984
---

# Story: Persist Row Buffers with Stable-Identity Merge Semantics (Batching, Limits, Transactions)

## Description

Persist flattened row buffers to the database in a single transaction:

Dependency note: `reference/design/backend-redesign/epics/DEPENDENCIES.md` is the canonical dependency map, and this story is on the `E15-S04b` / `DMS-1108` critical path. Runtime merge execution here consumes the retrofitted stable-identity collection merge-plan contract from `reference/design/backend-redesign/epics/15-plan-compilation/04b-stable-collection-merge-plans.md`; it must not be implemented against the older delete-by-parent / `Ordinal`-based collection plan shape. Under the DMS-983 split, this story also waits on `DMS-1123` / `02b-profile-applied-request-flattening.md` so profiled `WritableRequestBody` selection stays out of the persist/no-op executor itself.

- For `PUT`, and for `POST` when upsert resolves to an existing document, compare the current persisted rowset to the post-merge rowset the executor would actually write and skip DML when they are identical.
- Guarded no-op comparison must reuse the same merge-ordering and post-merge rowset-synthesis logic as the real executor, either directly or through a shared helper built from the same executor-facing merge metadata; do not introduce a compare-only profile merge implementation.
- Insert/update `dms.Document` and resource root rows when a change exists, but reject profiled creates when Core marks the root resource instance non-creatable.
- For non-collection scopes (root-adjacent, nested/common-type, and extension scopes), use normal visible-present / visible-absent semantics:
  - use `StoredScopeStates` keyed by compiled scope identity to distinguish visible-present, visible-absent, and hidden,
  - insert only when the scope is newly present, stored in a separate table, and Core marked that create-of-new-visible-data case as creatable,
  - update matched visible rows/scopes by overlaying visible request/resolved values onto current stored row values using compiled bindings plus `HiddenMemberPaths`,
  - delete only when the scope is visible and intentionally absent and stored in a separate table,
  - when the visible-but-absent scope is inlined into parent storage, clear only the visible compiled bindings for that scope and preserve bindings governed by `HiddenMemberPaths`, and
  - preserve hidden scopes and hidden inlined columns/member values using `HiddenMemberPaths` metadata when the writable profile excludes them, and
  - classify every affected compiled binding as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed before DML; fail deterministically if any binding cannot be accounted for.
- For collection/common-type/extension collection tables, use stable-identity merge semantics:
  - determine the visible stored rows for the scope instance from Core-projected `VisibleStoredCollectionRows` keyed by compiled `JsonScope` and stable parent address,
  - match visible stored rows to request candidates by compiled semantic identity,
  - update matched rows in place using the same compiled-binding overlay model,
  - delete only omitted visible rows,
  - insert only new visible rows marked creatable by Core in `VisibleRequestCollectionItems`, and
  - preserve hidden rows and hidden columns/member values using contract metadata rather than inference from projected JSON alone or backend-owned profile evaluation.
  - runtime execution consumes the non-empty compiled semantic identity guaranteed by `E15-S04b` / `DMS-1108`, where runtime/write-plan compilation already opted into the strict relational-model pipeline from `DMS-1103`; it does not derive a fallback match key when that upstream prerequisite is missing.
- Recompute `Ordinal` using the deterministic post-merge sibling-order rule defined in the design docs.
- Respect dialect parameter limits and implement batching to avoid N+1 patterns.
- Guard the no-op fast path by revalidating the observed `ContentVersion` before returning success; public `If-Match` header semantics remain owned by `DMS-1005`.

## Shared Profile Scenario Baseline

The runtime executor story and downstream test-migration stories should reuse the same compact scenario names when describing profile coverage:

`reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md` carries the compact feature-by-scenario matrix that maps these names to shared fixture and parity coverage. This story remains the source of truth for what each scenario means at runtime.

- `NoProfileWriteBehavior` — control case with no writable profile; proves the write path still behaves as the normal full-surface upsert/update path.
- `FullSurfaceCollectionReorder` — no-profile/full-surface collection reorder; matched rows keep stable `CollectionItemId` values while `Ordinal` changes.
- `ProfileVisibleRowUpdateWithHiddenRowPreservation` — profiled visible-row update/merge; hidden rows and hidden members are preserved. This scenario family includes no-previously-visible, interleaved update-plus-insert, nested collection, root-level extension child-collection, and collection-aligned extension child-collection variants.
- `ProfileVisibleRowDeleteWithHiddenRowPreservation` — profiled visible-row delete; hidden rows survive untouched. This scenario family includes the delete-all-visible-while-hidden-rows-remain case.
- `ProfileVisibleButAbsentNonCollectionScope` — profiled non-collection scope is visible but intentionally absent; separate-table scopes delete, while inlined scopes clear only visible bindings.
- `ProfileHiddenInlinedColumnPreservation` — matched visible scope preserves hidden parent/root-row values through compiled-binding overlay, including key-unified canonical storage, synthetic presence flags, and hidden FK/descriptor bindings when those bindings are driven by hidden members.
- `ProfileRootCreateRejectedWhenNonCreatable` — profiled `POST` create is rejected before insert DML when Core marks the root non-creatable.
- `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` — profiled new visible scope/item is rejected when Core marks it non-creatable; pair this with an update-allowed/create-denied case so existing visible data remains updatable, including a three-level chain where a hidden required middle-level member blocks both a new visible parent and its descendant extension child create.
- `ProfileHiddenExtensionRowPreservation` — hidden `_ext` rows and hidden extension columns on matched visible `_ext` rows are preserved.
- `ProfileHiddenExtensionChildCollectionPreservation` — hidden extension child collections at root-level or collection-aligned scopes are preserved while visible data merges normally.
- `ProfileUnchangedWriteGuardedNoOp` — unchanged profiled write short-circuits as a guarded no-op without changing persisted state or update-tracking metadata.

## Acceptance Criteria

- POST/PUT runs in a single transaction and either commits all changed rows or rolls back fully on failure.
- `PUT` and POST-as-update short-circuit as successful no-ops when the comparable stored/writable rowset is unchanged, including the `ProfileUnchangedWriteGuardedNoOp` scenario.
- No-op detection piggybacks on the existing current-state load and does not require a dedicated “did anything change?” roundtrip.
- Guarded no-op comparison reuses the same merge-ordering and post-merge rowset-synthesis logic as execution, either by invoking the same helper or a shared helper built from the same executor-facing metadata; runtime does not maintain a separate profile-specific compare-only merge path.
- Before returning a no-op result, the executor revalidates that the observed `ContentVersion` is still current; stale compares are surfaced to the outer concurrency layer instead of returning success on stale state.
- Collection/common-type rows preserve existing stable identity for matched rows and reserve new `CollectionItemId` values only for unmatched inserts.
- No-profile collection writes retain stable-identity merge behavior, and `FullSurfaceCollectionReorder` proves an ordinal-only reorder updates matched rows in place instead of falling back to delete+insert.
- Before profiled DML proceeds, runtime classifies every affected non-storage-managed compiled binding as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed, and treats any unaccounted binding as a deterministic profile-runtime failure.
- Profile-scoped non-collection decisions consume Core-projected `StoredScopeStates`, and profile-scoped collection merges consume Core-projected `VisibleStoredCollectionRows` keyed by compiled scope identity; runtime execution does not evaluate writable-profile predicates in backend or infer hidden-vs-absent from `VisibleStoredBody` alone.
- Profile-scoped collection/common-type/extension collection merges start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously.
- `ProfileVisibleRowUpdateWithHiddenRowPreservation` covers scope instances with no previously visible rows, visible updates plus inserts with hidden rows interleaved, nested collection scopes, root-level extension child collections, and collection-aligned extension child collections.
- `ProfileVisibleRowDeleteWithHiddenRowPreservation` covers delete-all-visible cases where hidden rows remain.
- Matched collection rows, matched visible non-collection rows/scopes, and matched visible extension rows preserve hidden values through compiled-binding overlay from current stored rows plus `HiddenMemberPaths`, including canonical key-unification storage columns, synthetic presence flags, and hidden FK/descriptor bindings derived from hidden members.
- Matched visible scopes/items update successfully even when the same writable profile would reject creation of a brand-new visible scope/item because required members are hidden by the profile.
- Hidden collection rows, hidden non-collection scopes, hidden inlined parent/root-row values, and hidden extension data are preserved under writable profiles.
- `ProfileHiddenExtensionRowPreservation` and `ProfileHiddenExtensionChildCollectionPreservation` follow the same preservation/merge rules as base data.
- `ProfileVisibleButAbsentNonCollectionScope` deletes separate-table rows or clears only the bindings classified as visible-and-clearable for inlined parent/root-row scopes according to the compiled mapping; bindings still governed by hidden preserved member paths are not cleared, and hidden scopes are not treated as deletes.
- `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` fail deterministically as profile-based policy/validation errors before insert DML commits.
- `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` includes a three-level chain where an existing visible middle-level parent still allows descendant update/create, while a new visible middle-level parent is rejected because a required middle-level member is hidden and therefore blocks descendant extension-child creation in the same request.
- Collection merge execution assumes `DMS-1108` already selected the strict relational-model/runtime compilation path and therefore rejected any persisted multi-item collection scope that lacks a non-empty compiled semantic identity from the allowed upstream schema sources: scope-resolved `arrayUniquenessConstraints` for non-reference-backed scopes, or exactly one qualifying scope-local `documentPathsMapping.referenceJsonPaths` binding set for reference-backed scopes.
- Bulk operations avoid N+1 insert/update patterns.
- Implementation works on both PostgreSQL and SQL Server with appropriate batching/parameterization behavior.

## Authorization Batching Consideration

Authorization is out of scope for this story, but the transaction and batching structure should be designed to allow authorization check statements to be prepended within the same roundtrip. For POST, auth checks are batched into the roundtrip that creates the `dms.Document` row; for PUT, auth checks and current-state loading run in the roundtrip that precedes the guarded no-op / persist step. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" (POST roundtrip #3, PUT roundtrip #3).

## Tasks

1. Implement rowset comparison for existing-document update flows by reusing the same stable-identity merge and post-merge ordering logic as the real executor, or a shared helper built from the same executor-facing merge metadata.
2. Implement a guarded no-op fast path that revalidates the observed `ContentVersion` before short-circuiting and returns a stale-compare outcome to the outer concurrency layer when freshness is lost.
3. Implement a write executor that applies the compiled `ResourceWritePlan` table-by-table in dependency order when a change exists, including pre-DML failure for non-creatable profiled root-resource creates and the rule that existing visible roots remain on the update path.
4. Implement profile-aware non-collection scope handling for separate-table 1:1/extension scopes and inlined parent-row common-type/root-column data using `ProfileAppliedWriteContext.StoredScopeStates` plus `HiddenMemberPaths`, including compiled-binding overlay for matched visible scopes, clear-only-visible-bindings behavior for visible-absent inlined scopes, the distinction between create-of-new-visible-data and update-of-existing-visible-data, and deterministic binding-accounting validation for key-unified/presence/FK/descriptor bindings.
5. Implement stable-identity collection/common-type merge execution using `ProfileAppliedWriteContext.VisibleStoredCollectionRows` and `ProfileAppliedWriteRequest.VisibleRequestCollectionItems`, including matched-row update via compiled-binding overlay, visible-row delete, hidden-member preservation, batched `CollectionItemId` reservation for inserts, and the rule that only unmatched visible items consult `Creatable` without backend-owned profile predicate evaluation.
6. Implement deterministic post-merge `Ordinal` recomputation aligned to the no-op comparison path.
7. Implement bulk insert batching with dialect-specific limits and strategies.
8. Add integration tests that cover the shared profile scenario baseline above:
   - `NoProfileWriteBehavior`, including one changed resource with nested collections and one `FullSurfaceCollectionReorder` case that proves matched rows keep stable identity while `Ordinal` changes,
   - `ProfileVisibleRowUpdateWithHiddenRowPreservation`, including no-previously-visible, interleaved update-plus-insert, nested collection, root-level extension child-collection, and collection-aligned extension child-collection ordering variants,
   - `ProfileVisibleRowDeleteWithHiddenRowPreservation`, including a delete-all-visible-while-hidden-rows-remain case,
   - `ProfileVisibleButAbsentNonCollectionScope` plus `ProfileHiddenInlinedColumnPreservation`, including key-unified canonical storage, synthetic presence, and hidden FK/descriptor coverage,
   - `ProfileHiddenExtensionRowPreservation` plus `ProfileHiddenExtensionChildCollectionPreservation`,
   - `ProfileRootCreateRejectedWhenNonCreatable` plus `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, including an update-allowed/create-denied pairing and the three-level parent-create-denied/child-denied chain, and
   - `ProfileUnchangedWriteGuardedNoOp` for unchanged PUT / POST-as-update requests with no DML-visible state or update-tracking changes (pgsql + mssql where available).
