---
jira: DMS-1124
---

# Slice 2: Root-Table-Only Profile Merge

## Purpose

Add the first successful profiled merge/persist slice by supporting only resources and requests whose persisted behavior is confined to the root table.

This slice proves the core overlay model in the smallest safe environment:

- visible request values overlay onto current stored root-row values,
- hidden root-row values are preserved,
- visible-absent inlined scopes clear only visible/clearable bindings,
- hidden-governed bindings are preserved,
- key-unification canonical storage and synthetic presence behave correctly, and
- hidden FK/descriptor bindings on the root row are preserved correctly.

This slice is intentionally narrower than "root row plus everything attached to it." It supports only shapes whose profiled runtime behavior can be fully expressed within the root table.

## In Scope

- Profiled writes whose persisted behavior is confined to the root table
- Root-row overlay using current stored values plus visible request values
- Root-hosted inlined-scope visible-present / visible-absent / hidden semantics
- Binding classification on the root table:
  - visible/writable,
  - hidden/preserved,
  - clear-on-visible-absent,
  - storage-managed
- Hidden FK/descriptor preservation on the root table
- Key-unification canonical storage preservation on the root table
- Synthetic presence preservation on the root table
- Profiled `PUT` and `POST`-as-update for supported root-table-only shapes
- Profiled `POST` create-new for supported root-table-only shapes after Slice 1 creatability handling

## Explicitly Out Of Scope

- Any separate-table non-collection scope
- Any separate-table root extension row
- Any collection/common-type/extension collection scope
- Any collection-aligned descendant scope
- Profile guarded no-op

## Supported After This Slice

A profiled write is supported by this slice only when all of the following are true:

- the persisted shape is confined to the root table,
- no profiled request or stored metadata requires separate-table scope handling,
- no profiled request or stored metadata requires collection handling, and
- no slice-fenced unsupported scope family is present in request-side or stored-side metadata.

When these conditions hold, profiled writes may succeed through merge and persist.

## Still Fenced After This Slice

The slice fence remains in place for any profiled write involving:

- separate-table non-collection scopes,
- separate-table `_ext` rows,
- top-level collections,
- nested collections,
- root-level extension child collections,
- collection-aligned extension child collections, or
- profiled guarded no-op.

## Design Constraints

- Root-row updates must overlay onto current stored values rather than rebuild the row from request data alone.
- Every non-storage-managed root-table binding must be classified before DML.
- Visible-absent inlined scopes may clear only bindings classified as clearable.
- Hidden-governed bindings must never be cleared by visible-absent behavior.
- Hidden FK/descriptor bindings must preserve stored values when any governing profile-hidden member requires preservation.
- Key-unification canonical source selection must preserve hidden-governed stored state correctly and continue to follow the normative `key-unification.md` contract, including full `KeyUnificationWritePlan.MembersInOrder` evaluation and fail-closed disagreement handling.
- Synthetic presence flags governed by hidden members must preserve stored values.
- No unsupported non-root shape may silently fall through; those cases remain fenced.

## Supported Shape Definition

This slice should intentionally gate itself to root-table-only shapes.

A profiled write is root-table-only when:

- the write plan's profiled runtime behavior requires changes only to the root table, and
- request-side and stored-side profile metadata do not require any merge decision for:
  - separate-table non-collection scopes,
  - root-level separate-table extension rows,
  - collections, or
  - collection-aligned descendants.

A conservative first implementation is acceptable: if there is doubt whether a shape escapes the root table, keep it fenced.

## Runtime Decision Matrix

### Create-new target, root-table-only shape

- Slice 1 determines final target outcome and root creatability.
- If creatable is false, reject before DML.
- If creatable is true and shape is root-table-only:
  - flatten using `WritableRequestBody`,
  - classify root-table bindings,
  - persist root-row values without hidden overlay because no current stored root row exists,
  - keep unsupported non-root profile families fenced.

### Existing target, root-table-only shape, visible root members present

- Load current root-row values.
- Classify root-table bindings using:
  - hidden member paths,
  - visible-absent clearable paths for inlined scopes,
  - document-reference metadata as needed.
- Overlay visible request values onto current stored root-row values.
- Preserve hidden-governed bindings from current stored root-row values.
- Apply key-unification and synthetic presence adjustment after overlay.
- Persist resulting root-row update.

### Existing target, root-table-only shape, visible-absent inlined scope

- Do not treat the inlined scope as a row delete.
- Clear only bindings classified as clear-on-visible-absent.
- Preserve hidden-governed bindings from current stored state.
- Persist adjusted root-row update.

### Existing target, root-table-only shape, hidden inlined scope

- Preserve stored bindings for the hidden scope.
- No clear behavior is allowed for hidden-governed bindings.
- Persist only changes driven by visible portions of the root table.

### Existing target or create-new target, unsupported non-root shape detected

- Return slice fence from Slice 1 / current-slice shape classifier.
- Do not attempt partial merge or DML.

## Acceptance Criteria

- Profiled root-table-only writes succeed through merge and persist.
- Existing root-row updates preserve hidden root-row values.
- Visible-absent inlined scopes clear only visible/clearable bindings and preserve hidden-governed bindings.
- Hidden FK/descriptor bindings on the root row preserve stored values correctly.
- Key-unification canonical storage on the root row preserves hidden-governed stored state correctly.
- Synthetic presence values governed by hidden members preserve stored state correctly.
- Unsupported non-root profiled shapes remain fenced.

## Tests Required

### Unit tests

- Root-row overlay preserves hidden scalar values
- Visible-absent inlined scope clears only clearable bindings
- Hidden inlined bindings remain preserved
- Hidden FK/descriptor bindings are preserved on matched root-row update
- Root-row key-unification canonical storage is preserved correctly in mixed hidden/visible cases
- Root-row synthetic presence flags preserve hidden-governed stored state
- Unsupported non-root profiled shapes still return slice fence

### Integration tests

- Root-table-only profiled `PUT` with hidden inlined member preservation
- Root-table-only profiled `POST`-as-update with hidden inlined member preservation
- Root-table-only profiled `POST` create-new success after creatability passes
- Root-table-only inlined `ProfileVisibleButAbsentNonCollectionScope`
- Root-table-only `ProfileHiddenInlinedColumnPreservation`
- Root-table-only hidden FK/descriptor preservation case
- Root-table-only key-unification / synthetic presence preservation case
- PostgreSQL and SQL Server parity coverage, or explicit review rationale when this slice introduces no dialect-sensitive behavior beyond previously covered paths

## Reviewer Focus

Reviewers for this slice should focus only on:

- root-row binding classification,
- overlay correctness on the root table,
- visible-absent inlined-scope clear behavior,
- hidden preservation on the root table,
- key-unification and synthetic presence adjustment on the root row, and
- correctness of the root-table-only shape gate.

Reviewers should explicitly ignore:

- separate-table scope behavior,
- collection merge behavior,
- descendant extension behavior, and
- guarded no-op.

## Leaves Behind For Next Slice

The next slice removes the fence for separate-table non-collection profile semantics.

That slice owns:

- separate-table visible-present insert/update,
- separate-table visible-absent delete,
- hidden separate-table preservation,
- root-level separate-table extension row behavior, and
- update-allowed / create-denied behavior for matched vs new separate-table scopes.
