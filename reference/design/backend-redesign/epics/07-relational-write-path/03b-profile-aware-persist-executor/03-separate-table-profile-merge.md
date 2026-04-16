---
jira: DMS-1124
---

# Slice 3: Separate-Table Profile Merge

## Purpose

Add profile-aware non-collection separate-table semantics once root-table-only overlay and preservation behavior already exist.

This slice expands successful profiled writes from "root table only" to include:

- separate-table 1:1/common-type scopes,
- separate-table root-level extension rows, and
- matched existing scope update vs new visible scope create decisions.

Collections remain fenced so this slice stays focused on non-collection scope behavior.

## In Scope

- Separate-table non-collection scopes driven by `StoredScopeStates` and `RequestScopeStates`
- Visible-present insert/update decisions for separate-table scopes
- Visible-absent delete decisions for separate-table scopes
- Hidden separate-table scope preservation
- Root-level separate-table `_ext` row preservation and update behavior
- Create-of-new-visible-data vs update-of-existing-visible-data decisions for separate-table scopes
- Existing-visible-update / create-denied behavior for non-collection scopes

## Explicitly Out Of Scope

- Top-level collections
- Nested collections
- Root-level extension child collections
- Collection-aligned extension child collections
- Profile guarded no-op

## Supported After This Slice

- Profiled writes whose runtime behavior is confined to:
  - the root table,
  - root-hosted inlined scopes, and
  - separate-table non-collection scopes
  may succeed through merge and persist.
- Root-level separate-table extension rows follow the same visible-present / hidden / visible-absent semantics as other separate-table scopes.
- New visible separate-table scope creation consults `Creatable`.
- Matched existing visible separate-table scope updates remain allowed even when the same profile would reject creation of a brand-new visible scope.

## Still Fenced After This Slice

The slice fence remains in place for any profiled write involving:

- top-level collections,
- nested collections,
- root-level extension child collections,
- collection-aligned extension child collections, or
- profiled guarded no-op.

## Design Constraints

- Non-collection separate-table decisions must come from request/stored scope metadata, not inferred buffer presence alone.
- Matched separate-table overlay reuses the Slice 2 binding-classification and binding-accounting model for non-storage-managed bindings.
- Hidden separate-table scopes are preserved untouched.
- Visible-absent separate-table scopes delete only when the scope is visible and intentionally absent.
- New visible scope creation consults `Creatable`; matched existing visible scope update does not.
- Root-level separate-table extension rows must follow the same preservation and create/update rules as base separate-table scopes.

## Runtime Decision Matrix

### Visible-present request scope, no stored visible scope

- If `Creatable` is false, reject before insert DML.
- If `Creatable` is true, insert the new separate-table scope row.

### Visible-present request scope, matched stored visible scope

- Overlay visible request values onto current stored row values.
- Preserve hidden-governed bindings from current stored row values.
- Update the existing separate-table scope row in place.

### Visible-absent request scope, matched stored visible scope

- Delete the separate-table row only when the scope is visible and intentionally absent.
- Do not treat hidden stored data as visible-absent delete.

### Hidden stored scope

- Preserve the current separate-table row unchanged.
- Do not insert, update, or delete based on request omission.

## Acceptance Criteria

- Separate-table visible-present insert/update behavior is correct under profile semantics.
- Separate-table visible-absent delete behavior is correct under profile semantics.
- Hidden separate-table scopes are preserved.
- Root-level separate-table `_ext` rows and hidden extension columns are preserved under the same rules as base separate-table data.
- Existing visible scope update remains allowed even when creating a brand-new visible scope at the same logical profile shape would be rejected.
- Runtime decisions consume `StoredScopeStates` and `RequestScopeStates`; they do not infer hidden-vs-absent from `VisibleStoredBody` or buffer presence alone.
- Matched separate-table updates preserve hidden-governed values through the inherited Slice 2 binding-accounting model for affected non-storage-managed bindings.

## Tests Required

### Unit tests

- Separate-table visible-present insert when stored scope absent and `Creatable=true`
- Separate-table create rejection when stored scope absent and `Creatable=false`
- Separate-table matched update preserves hidden-governed values
- Separate-table visible-absent delete
- Hidden separate-table scope preservation
- Root-level separate-table extension row preservation

### Integration tests

- Separate-table `ProfileVisibleButAbsentNonCollectionScope`
- `ProfileHiddenExtensionRowPreservation`
- Existing-visible-update / create-denied non-collection pair
- One root-level separate-table extension update case with hidden-member preservation
- PostgreSQL and SQL Server parity coverage, or explicit review rationale when this slice introduces no dialect-sensitive behavior beyond previously covered paths

## Reviewer Focus

Reviewers for this slice should focus only on:

- separate-table request/stored scope-state decisions,
- create-vs-update behavior for separate-table scopes,
- hidden separate-table preservation,
- root-level separate-table extension row behavior, and
- correct use of scope metadata rather than inferred presence.

Reviewers should explicitly ignore:

- top-level collection behavior,
- nested/extension child collections, and
- guarded no-op.

## Leaves Behind For Next Slice

The next slice removes the fence for top-level collection merge only.

That slice owns:

- visible stored/request collection matching,
- hidden top-level collection row preservation,
- visible-row delete/insert semantics for top-level collections, and
- top-level ordinal recomputation.
