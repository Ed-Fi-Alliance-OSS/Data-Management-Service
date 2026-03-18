---
jira: DMS-984
jira_url: https://edfi.atlassian.net/browse/DMS-984
---

# Story: Persist Row Buffers with Stable-Identity Merge Semantics (Batching, Limits, Transactions)

## Description

Persist flattened row buffers to the database in a single transaction:

- For `PUT`, and for `POST` when upsert resolves to an existing document, compare the current persisted rowset to the post-merge rowset the executor would actually write and skip DML when they are identical.
- Insert/update `dms.Document` and resource root rows when a change exists, but reject profiled creates when Core marks the root resource instance non-creatable.
- For non-collection scopes (root-adjacent, nested/common-type, and extension scopes), use normal visible-present / visible-absent semantics:
  - use `StoredScopeStates` keyed by compiled scope identity to distinguish visible-present, visible-absent, and hidden,
  - insert only when the scope is newly present, stored in a separate table, and Core marked that create-of-new-visible-data case as creatable,
  - update matched visible rows/scopes by overlaying visible request/resolved values onto current stored row values using compiled bindings plus `HiddenMemberPaths`,
  - delete only when the scope is visible and intentionally absent and stored in a separate table,
  - when the visible-but-absent scope is inlined into parent storage, clear only the visible compiled bindings for that scope and preserve bindings governed by `HiddenMemberPaths`, and
  - preserve hidden scopes and hidden inlined columns/member values using `HiddenMemberPaths` metadata when the writable profile excludes them.
- For collection/common-type/extension collection tables, use stable-identity merge semantics:
  - determine the visible stored rows for the scope instance from Core-projected `VisibleStoredCollectionRows` keyed by compiled `JsonScope` and stable parent address,
  - match visible stored rows to request candidates by compiled semantic identity,
  - update matched rows in place using the same compiled-binding overlay model,
  - delete only omitted visible rows,
  - insert only new visible rows marked creatable by Core in `VisibleRequestCollectionItems`, and
  - preserve hidden rows and hidden columns/member values using contract metadata rather than inference from projected JSON alone or backend-owned profile evaluation.
  - runtime execution consumes the non-empty compiled semantic identity emitted by E01/E15; it does not derive a fallback match key when that prerequisite is missing.
- Recompute `Ordinal` using the deterministic post-merge sibling-order rule defined in the design docs.
- Respect dialect parameter limits and implement batching to avoid N+1 patterns.
- Guard the no-op fast path by revalidating the observed `ContentVersion` before returning success; public `If-Match` header semantics remain owned by `DMS-1005`.

## Acceptance Criteria

- POST/PUT runs in a single transaction and either commits all changed rows or rolls back fully on failure.
- `PUT` and POST-as-update short-circuit as successful no-ops when the comparable stored/writable rowset is unchanged.
- No-op detection piggybacks on the existing current-state load and does not require a dedicated “did anything change?” roundtrip.
- Before returning a no-op result, the executor revalidates that the observed `ContentVersion` is still current; stale compares are surfaced to the outer concurrency layer instead of returning success on stale state.
- Collection/common-type rows preserve existing stable identity for matched rows and reserve new `CollectionItemId` values only for unmatched inserts.
- Profile-scoped non-collection decisions consume Core-projected `StoredScopeStates`, and profile-scoped collection merges consume Core-projected `VisibleStoredCollectionRows` keyed by compiled scope identity; runtime execution does not evaluate writable-profile predicates in backend or infer hidden-vs-absent from `VisibleStoredBody` alone.
- Profile-scoped collection/common-type/extension collection merges preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance, and renumber `Ordinal` contiguously.
- Matched collection rows, matched visible non-collection rows/scopes, and matched visible extension rows preserve hidden values through compiled-binding overlay from current stored rows plus `HiddenMemberPaths`.
- Matched visible scopes/items update successfully even when the same writable profile would reject creation of a brand-new visible scope/item because required members are hidden by the profile.
- Hidden collection rows, hidden non-collection scopes, hidden inlined parent/root-row values, and hidden extension data are preserved under writable profiles.
- Hidden `_ext` rows, collection-aligned extension rows, and extension child collections follow the same preservation/merge rules as base data.
- Visible-but-absent non-collection scopes delete separate-table rows or clear only the visible compiled bindings for inlined parent/root-row scopes according to the compiled mapping; hidden scopes are not treated as deletes.
- Profile-scoped `POST` create and new visible scopes/items that Core marks non-creatable fail deterministically as profile-based policy/validation errors before insert DML commits.
- Collection merge execution assumes upstream validation/compilation already rejected any persisted multi-item collection scope that lacks a non-empty compiled semantic identity from `arrayUniquenessConstraints`.
- Bulk operations avoid N+1 insert/update patterns.
- Implementation works on both PostgreSQL and SQL Server with appropriate batching/parameterization behavior.

## Authorization Batching Consideration

Authorization is out of scope for this story, but the transaction and batching structure should be designed to allow authorization check statements to be prepended within the same roundtrip. For POST, auth checks are batched into the roundtrip that creates the `dms.Document` row; for PUT, auth checks and current-state loading run in the roundtrip that precedes the guarded no-op / persist step. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" (POST roundtrip #3, PUT roundtrip #3).

## Tasks

1. Implement rowset comparison for existing-document update flows using the same stable-identity merge and post-merge ordering rules as the real executor.
2. Implement a guarded no-op fast path that revalidates the observed `ContentVersion` before short-circuiting and returns a stale-compare outcome to the outer concurrency layer when freshness is lost.
3. Implement a write executor that applies the compiled `ResourceWritePlan` table-by-table in dependency order when a change exists, including pre-DML failure for non-creatable profiled root-resource creates and the rule that existing visible roots remain on the update path.
4. Implement profile-aware non-collection scope handling for separate-table 1:1/extension scopes and inlined parent-row common-type/root-column data using `ProfileAppliedWriteContext.StoredScopeStates` plus `HiddenMemberPaths`, including compiled-binding overlay for matched visible scopes, clear-only-visible-bindings behavior for visible-absent inlined scopes, and the distinction between create-of-new-visible-data and update-of-existing-visible-data.
5. Implement stable-identity collection/common-type merge execution using `ProfileAppliedWriteContext.VisibleStoredCollectionRows` and `ProfileAppliedWriteRequest.VisibleRequestCollectionItems`, including matched-row update via compiled-binding overlay, visible-row delete, hidden-member preservation, batched `CollectionItemId` reservation for inserts, and the rule that only unmatched visible items consult `Creatable` without backend-owned profile predicate evaluation.
6. Implement deterministic post-merge `Ordinal` recomputation aligned to the no-op comparison path.
7. Implement bulk insert batching with dialect-specific limits and strategies.
8. Add integration tests that:
   - write a changed resource with nested collections and verify row counts/keys after commit,
   - exercise a profile-scoped update that preserves hidden stored data while updating visible rows, and
   - exercise a profile-scoped collection merge with hidden rows interleaved between visible siblings and assert deterministic hidden-gap ordering after `Ordinal` renumbering,
   - exercise profile-scoped non-collection handling for one separate-table scope and one inlined scope, including hidden-vs-visible-absent behavior and clear-only-visible-bindings behavior for the inlined scope,
   - exercise hidden inlined parent/root-row value preservation on a matched visible scope,
   - exercise hidden extension-column preservation on a matched visible `_ext` row,
   - exercise a profiled update/no-op scenario with hidden `_ext` rows or extension child collections and assert they are preserved under the same merge rules as base data,
   - prove a matched visible scope/item update succeeds even when the same profile would mark a brand-new visible scope/item as non-creatable because required members are hidden, and
   - reject a profiled create or visible-scope/item insert when Core marks it non-creatable, and
   - issue unchanged PUT / POST-as-update requests and verify no DML-visible state or update-tracking metadata changes (pgsql + mssql where available).
