---
jira: DMS-1124
---

# Slice 4: Top-Level Collection Merge

## Purpose

Support profiled collection merge for collections directly under the root scope without yet taking on nested collection ancestry or extension-child collection complexity.

This slice adds:

- matching visible stored rows to visible request items by compiled semantic identity,
- hidden top-level collection row preservation,
- visible-row delete/insert/update behavior, and
- top-level profile-scoped ordinal recomputation.

## In Scope

- Top-level collection/common-type/extension collection scopes directly under root
- Visible stored collection row matching using `VisibleStoredCollectionRows`
- Visible request item matching using `VisibleRequestCollectionItems`
- Hidden row preservation for top-level collections
- Matched-row update via compiled-binding overlay
- Delete of omitted visible rows only
- Insert of unmatched visible items only when `Creatable=true`
- Top-level ordinal recomputation for supported shapes

## Explicitly Out Of Scope

- Nested collections
- Root-level extension child collections
- Collection-aligned extension child collections
- Profile guarded no-op

## Supported After This Slice

- Profiled writes may succeed when their collection behavior is confined to top-level collections under root and all earlier slices already support the involved non-collection shapes.
- Top-level visible-row update/delete/insert behavior is supported with hidden-row preservation.
- Top-level collection reorder behavior follows the profile-scoped sibling-order rule for supported shapes.

## Still Fenced After This Slice

The slice fence remains in place for any profiled write involving:

- nested collections,
- root-level extension child collections,
- collection-aligned extension child collections, or
- profiled guarded no-op.

## Design Constraints

- Current-row partitioning into visible vs hidden must be driven by emitted stored-row metadata, not inferred from current DB rows alone.
- Matched top-level collection-row overlay reuses the Slice 2 binding-classification and binding-accounting model for non-storage-managed bindings.
- Duplicate visible request candidates for the same semantic identity must fail deterministically before DML.
- Reverse coverage checks must fail deterministically if visible stored-row metadata cannot be matched to current DB rows.
- Request-side coverage checks must fail deterministically if flattened visible request candidates and `VisibleRequestCollectionItems` do not cover each other one-for-one for the same top-level scope instance and semantic identity.
- Hidden top-level collection rows must remain untouched when visible data merges around them.

## Runtime Decision Matrix

### Visible stored row matches visible request candidate

- Overlay visible request values onto current stored row values.
- Preserve hidden-governed member values from current stored row values.
- Update the matched row in place.

### Visible stored row omitted by visible request set

- Delete the visible row.
- Preserve hidden rows under the same parent scope instance.

### Visible request item has no matched visible stored row

- If `Creatable` is false, reject before insert DML.
- If `Creatable` is true, insert the new visible row.

### Hidden current row in the same top-level collection scope

- Preserve the row untouched.
- Keep the row in ordinal recomputation according to the hidden-gap preservation rule.

## Acceptance Criteria

- Top-level profiled collection merge consumes `VisibleStoredCollectionRows` and `VisibleRequestCollectionItems` rather than backend-owned visibility inference.
- Matched top-level collection rows update in place using the inherited Slice 2 binding-accounting model for affected non-storage-managed bindings.
- Omitted visible rows delete without deleting hidden rows.
- Unmatched visible items insert only when `Creatable=true`.
- Unmatched visible items reject deterministically when `Creatable=false`, while matched existing visible items remain updatable.
- Hidden top-level collection rows are preserved.
- Top-level visible request candidates and `VisibleRequestCollectionItems` are validated one-for-one before DML.
- Matched top-level collection rows preserve hidden FK/descriptor bindings, canonical key-unification storage, and synthetic presence values when those bindings are driven by hidden members.
- Top-level ordinal recomputation preserves hidden-row gaps and renumbers contiguously after merge.

## Tests Required

### Unit tests

- Match visible stored row to visible request item by compiled semantic identity
- Duplicate visible request candidate rejection
- Request-side visible candidate coverage rejection when a flattened visible request candidate has no matching `VisibleRequestCollectionItem`
- Request-side orphan or mismatched `VisibleRequestCollectionItem` rejection
- Reverse stored-row coverage rejection
- Hidden row preservation during top-level update
- Top-level matched update preserves hidden FK/descriptor bindings driven by hidden members
- Top-level matched update preserves canonical key-unification storage and synthetic presence values in mixed hidden/visible cases
- Top-level non-creatable insert rejection with matched visible row update still allowed
- Delete-all-visible while hidden rows remain
- No-previously-visible top-level insert case
- Top-level ordinal recomputation with hidden interleaving

### Integration tests

- Top-level `ProfileVisibleRowUpdateWithHiddenRowPreservation`
- Top-level `ProfileVisibleRowDeleteWithHiddenRowPreservation`
- Top-level `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`
- Top-level update-allowed/create-denied pair
- Delete-all-visible-while-hidden-rows-remain case
- No-previously-visible top-level variant
- One top-level collection hidden-binding preservation case covering FK/descriptor or key-unification/synthetic-presence behavior
- PostgreSQL and SQL Server parity coverage, or explicit review rationale when this slice introduces no dialect-sensitive behavior beyond previously covered paths

## Reviewer Focus

Reviewers for this slice should focus only on:

- top-level visible stored/request row matching,
- hidden-row preservation,
- duplicate and reverse-coverage behavior, and
- top-level ordinal recomputation.

Reviewers should explicitly ignore:

- nested collection ancestry,
- extension child collections, and
- guarded no-op.

## Leaves Behind For Next Slice

The next slice removes the fence for:

- nested collections,
- root-level extension child collections, and
- collection-aligned extension child collections.

That slice owns ancestor-address matching, descendant preservation, and nested second-pass behavior.
