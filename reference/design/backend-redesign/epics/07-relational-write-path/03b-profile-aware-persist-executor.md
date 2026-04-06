---
jira: DMS-1124
jira_url: https://edfi.atlassian.net/browse/DMS-1124
---

# Story: Apply Profile-Aware Merge, Hidden-Data Preservation, and Creatability in the Persist Executor

## Description

Extend the `DMS-984` persist executor foundation to support profile-constrained writes:

Dependency note: `reference/design/backend-redesign/epics/DEPENDENCIES.md` is the canonical dependency map. This follow-on is hard-blocked on:

- `DMS-984` / `03-persist-and-batch.md` — provides the request-scoped transaction boundary, shared stable-identity merge/no-op executor infrastructure, batching, and no-profile runtime behavior,
- `DMS-1106` / `01b-profile-write-context.md` — provides the validated Core/backend profile write contract and address validation,
- `DMS-1105` / `01c-current-document-for-profile-projection.md` — provides the write-side current-document load and reconstitution needed for profiled update/upsert flows, and
- `DMS-1123` / `02b-profile-applied-request-flattening.md` — keeps `WritableRequestBody` source selection at the orchestration boundary instead of reopening it inside the persist/no-op executor.

This story must extend the executor/no-op path introduced in `DMS-984`; it must not fork a separate profile-only persist path or rebuild collection merge behavior against a different metadata shape.

In the rebased `DMS-984` branch, relational repository orchestration currently short-circuits any write carrying `BackendProfileWriteContext` with:

- `UnknownFailure`
- HTTP `500`
- `profile-aware relational writes pending DMS-1123/DMS-1105/DMS-1124`

This story owns removing that temporary fence once `DMS-1123` and `DMS-1105` are in place and routing valid profiled relational writes through the shared executor/no-op path.

The hand-off is:

- `DMS-1123` supplies request-body source selection so profiled flattening uses `WritableRequestBody`,
- `DMS-1105` supplies the reconstituted current stored document plus current-state load needed for profiled existing-document flows, and
- `DMS-1124` consumes both within the shared `DMS-984` executor/no-op path without reopening body-source selection or creating a separate profile-only pipeline.

- For profiled `PUT`, and for profiled `POST` when upsert resolves to an existing document, compare the current persisted rowset to the profile-applied post-merge rowset the executor would actually write and skip DML when they are identical.
- Guarded no-op comparison must reuse the same merge-ordering and post-merge rowset-synthesis logic as the profiled executor path; do not introduce a compare-only profile merge implementation.
- Profile-scoped guarded no-op decisions remain provisional until the executor revalidates the observed `ContentVersion`; stale compares hand off to the same outer concurrency layer used by the shared `DMS-984` executor path.
- Reject profiled root creates before insert DML when Core marks the root resource instance non-creatable; existing visible roots remain on the normal update path.
- For non-collection scopes (root-adjacent, nested/common-type, and extension scopes), use profile-aware visible-present / visible-absent / hidden semantics:
  - consume `StoredScopeStates` keyed by compiled scope identity rather than inferring hidden-vs-absent from `VisibleStoredBody` alone,
  - insert only when the scope is newly visible-present, stored in a separate table, and Core marked that create-of-new-visible-data case as creatable,
  - update matched visible rows/scopes by overlaying visible request/resolved values onto current stored row values using compiled bindings plus `HiddenMemberPaths`,
  - delete only when the scope is visible and intentionally absent and stored in a separate table,
  - when the visible-but-absent scope is inlined into parent storage, clear only the visible compiled bindings for that scope and preserve bindings governed by `HiddenMemberPaths`, and
  - classify every affected compiled binding as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed before DML; fail deterministically if any binding cannot be accounted for.
- For collection/common-type/extension collection tables, use profile-aware stable-identity merge semantics:
  - determine the visible stored rows for the scope instance from Core-projected `VisibleStoredCollectionRows` keyed by compiled `JsonScope` and stable parent address,
  - match visible stored rows to request candidates by compiled semantic identity,
  - update matched rows in place using the same compiled-binding overlay model,
  - delete only omitted visible rows,
  - insert only new visible rows marked creatable by Core in `VisibleRequestCollectionItems`, and
  - preserve hidden rows and hidden columns/member values using contract metadata rather than inference from projected JSON alone or backend-owned profile evaluation.
- Recompute `Ordinal` using the deterministic profile-scoped sibling-order rule defined in the design docs: start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously.
- Preserve hidden `_ext` rows and hidden extension columns under the same rules as base data.

## Shared Profile Scenario Baseline

The profiled runtime executor follow-on and downstream test-migration stories should reuse the same compact scenario names when describing profile coverage:

`reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md` carries the compact feature-by-scenario matrix that maps these names to shared fixture and parity coverage. This story is the source of truth for the profiled runtime scenarios.

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

- Profile-scoped non-collection decisions consume Core-projected `StoredScopeStates`, and profile-scoped collection merges consume Core-projected `VisibleStoredCollectionRows` keyed by compiled scope identity; runtime execution does not evaluate writable-profile predicates in backend or infer hidden-vs-absent from `VisibleStoredBody` alone.
- Valid profiled relational `POST` create, `PUT`, and POST-as-update requests no longer fail with the temporary `UnknownFailure` / `500` fence message and instead enter the shared `DMS-984` executor/no-op path.
- Before profiled DML proceeds, runtime classifies every affected non-storage-managed compiled binding as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed, and treats any unaccounted binding as a deterministic profile-runtime failure.
- `PUT` and POST-as-update short-circuit as successful no-ops when the comparable profile-applied stored/writable rowset is unchanged, including the `ProfileUnchangedWriteGuardedNoOp` scenario.
- Guarded no-op comparison reuses the same merge-ordering and post-merge rowset-synthesis logic as execution, either by invoking the same helper or a shared helper built from the same executor-facing metadata; runtime does not maintain a separate profile-specific compare-only merge path.
- Profile-scoped guarded no-op decisions remain provisional until the executor revalidates that the observed `ContentVersion` is still current; stale compares are surfaced to the shared outer concurrency layer instead of returning success on stale state.
- `ProfileVisibleRowUpdateWithHiddenRowPreservation` covers scope instances with no previously visible rows, visible updates plus inserts with hidden rows interleaved, nested collection scopes, root-level extension child collections, and collection-aligned extension child collections.
- `ProfileVisibleRowDeleteWithHiddenRowPreservation` covers delete-all-visible cases where hidden rows remain.
- Matched collection rows, matched visible non-collection rows/scopes, and matched visible extension rows preserve hidden values through compiled-binding overlay from current stored rows plus `HiddenMemberPaths`, including canonical key-unification storage columns, synthetic presence flags, and hidden FK/descriptor bindings derived from hidden members.
- Matched visible scopes/items update successfully even when the same writable profile would reject creation of a brand-new visible scope/item because required members are hidden by the profile.
- Hidden collection rows, hidden non-collection scopes, hidden inlined parent/root-row values, and hidden extension data are preserved under writable profiles.
- `ProfileHiddenExtensionRowPreservation` and `ProfileHiddenExtensionChildCollectionPreservation` follow the same preservation/merge rules as base data.
- `ProfileVisibleButAbsentNonCollectionScope` deletes separate-table rows or clears only the bindings classified as visible-and-clearable for inlined parent/root-row scopes according to the compiled mapping; bindings still governed by hidden preserved member paths are not cleared, and hidden scopes are not treated as deletes.
- `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` fail deterministically as profile-based policy/validation errors before insert DML commits.
- Profile root-creatability enforcement follows the executor's final in-session POST target outcome: create-only checks run only when POST resolves to create-new, while POST resolving to an existing document flows onto the profiled update path.
- `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` includes a three-level chain where an existing visible middle-level parent still allows descendant update/create, while a new visible middle-level parent is rejected because a required middle-level member is hidden and therefore blocks descendant extension-child creation in the same request.
- Profile-scoped collection/common-type/extension collection merges start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously.
- Implementation works on both PostgreSQL and SQL Server with appropriate batching/parameterization behavior.

## Tasks

1. Remove the temporary repository fence for valid profiled relational writes once `DMS-1123` body-source selection and `DMS-1105` current-document reconstitution are available, and route those requests into the shared `DMS-984` executor/no-op path.
2. Extend the `DMS-984` executor/no-op path to consume `ProfileAppliedWriteRequest` / `ProfileAppliedWriteContext` without forking a separate profile-only persist pipeline.
3. Implement profile-aware non-collection scope handling for separate-table 1:1/extension scopes and inlined parent-row common-type/root-column data using `ProfileAppliedWriteContext.StoredScopeStates` plus `HiddenMemberPaths`, including compiled-binding overlay for matched visible scopes, clear-only-visible-bindings behavior for visible-absent inlined scopes, the distinction between create-of-new-visible-data and update-of-existing-visible-data, and deterministic binding-accounting validation for key-unified/presence/FK/descriptor bindings.
4. Implement stable-identity collection/common-type merge execution using `ProfileAppliedWriteContext.VisibleStoredCollectionRows` and `ProfileAppliedWriteRequest.VisibleRequestCollectionItems`, including matched-row update via compiled-binding overlay, visible-row delete, hidden-member preservation, batched `CollectionItemId` reservation for inserts, and the rule that only unmatched visible items consult `Creatable` without backend-owned profile predicate evaluation.
5. Extend the shared no-op comparison path to support profile-applied rowset synthesis, including `ProfileUnchangedWriteGuardedNoOp`, the same `ContentVersion` freshness recheck, and stale-compare handoff to the outer concurrency layer, without introducing a profile-only compare implementation.
6. Move profile root-creatability enforcement to the executor's final in-session POST target outcome so create-new and POST-as-update are handled by the correct profiled path.
7. Add integration tests that cover the shared profiled runtime baseline above and replace temporary fence-focused assertions with positive runtime assertions for profiled executor entry:
   - `ProfileVisibleRowUpdateWithHiddenRowPreservation`, including no-previously-visible, interleaved update-plus-insert, nested collection, root-level extension child-collection, and collection-aligned extension child-collection ordering variants,
   - `ProfileVisibleRowDeleteWithHiddenRowPreservation`, including a delete-all-visible-while-hidden-rows-remain case,
   - `ProfileVisibleButAbsentNonCollectionScope` plus `ProfileHiddenInlinedColumnPreservation`, including key-unified canonical storage, synthetic presence, and hidden FK/descriptor coverage,
   - `ProfileHiddenExtensionRowPreservation` plus `ProfileHiddenExtensionChildCollectionPreservation`,
   - `ProfileRootCreateRejectedWhenNonCreatable` plus `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, including an update-allowed/create-denied pairing, correct POST create-vs-update target handling, and the three-level parent-create-denied/child-denied chain, and
   - `ProfileUnchangedWriteGuardedNoOp` for unchanged PUT / POST-as-update requests with no DML-visible state or update-tracking changes (pgsql + mssql where available).
