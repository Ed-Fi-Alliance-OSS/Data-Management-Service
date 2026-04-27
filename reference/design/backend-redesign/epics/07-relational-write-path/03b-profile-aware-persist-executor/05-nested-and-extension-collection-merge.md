---
jira: DMS-1124
---

# Slice 5: Nested And Extension Collection Merge

## Purpose

Remove the remaining collection-shape fences by adding:

- nested collections,
- root-level extension child collections, and
- collection-aligned extension child collections, and
- nested extension child collections under extension-child parents.

This slice finishes the profile-aware collection merge surface that the earlier slices deliberately deferred so top-level collection behavior could be reviewed separately.

## In Scope

- Nested collection merge under collection ancestors
- Root-level extension child collection merge
- Collection-aligned extension child collection merge
- Nested extension child collection merge under extension-child ancestors
- Ancestor-address matching for nested collection instances
- Hidden descendant preservation under matched or hidden parents
- Nested second-pass behavior where stored scope-state handling interacts with collection ancestry
- Ordinal recomputation for supported nested and extension-child collection shapes

## Explicitly Out Of Scope

- Profile guarded no-op
- Any hardening explicitly left to `DMS-1132` or later follow-ons

## Supported After This Slice

- Profiled writes may succeed for nested collection shapes and extension child collection shapes, including nested extension-child chains, when all earlier slices already support the involved root/separate-table behavior.
- Nested visible-row update/delete/insert behavior is supported.
- Root-level, collection-aligned, and nested extension child collections follow the same merge/preservation rules as base collection data.
- Hidden descendants are preserved under both matched-update and hidden-parent cases for supported shapes.

## Still Fenced After This Slice

Only profiled guarded no-op remains intentionally fenced if Slice 6 has not yet landed.

## Design Constraints

- Parent/child attachment must be driven by compiled address derivation and ancestor context, not by request ordinals.
- Matched nested and extension-child collection-row overlay reuses the Slice 2 binding-classification and binding-accounting model for non-storage-managed bindings.
- Request-side coverage checks from Slice 4 must extend to nested and extension-child collection scopes using stable parent address plus ancestor context.
- Reverse stored-row coverage checks from Slice 4 must extend to nested and extension-child `VisibleStoredCollectionRows` within the correct ancestor context before DML.
- Hidden descendants must survive both:
  - visible parent-row merge, and
  - hidden parent-row preservation.
- Nested second-pass logic must not delete collection-descendant scopes incorrectly when flattened buffers omit them.
- Extension child collections must follow the same visibility and preservation rules as base collection data.
- Stored descriptor URI canonicalization in nested and extension-child scopes must reuse the request-cycle resolution cache keyed by `(descriptor resource, URI)` only — no ancestor-context, scope-path, or column-path narrowing. The Slice 4 runtime fail-closed throw shape (URI absent from the cache, scalar-part matching is ambiguous or absent, and current-row count diverges from stored-row count) carries over unchanged into nested and extension-child scopes; this slice introduces no new fence shape.

## Runtime Decision Matrix

### Nested visible stored row matches nested visible request candidate

- Match by compiled semantic identity within the stable parent address identified by ancestor context.
- Overlay visible request values onto current stored row values.
- Preserve hidden-governed member values from current stored row values.
- Update the matched nested row in place.

### Nested visible stored row omitted by visible request set

- Delete the visible nested row.
- Preserve hidden sibling rows and hidden descendants.

### Nested visible request row has no visible stored match

- If `Creatable` is false, reject before insert DML.
- If `Creatable` is true, insert the new visible nested row in the correct parent scope instance.

### Hidden parent row or hidden descendant scope

- Preserve descendant rows/scopes under the hidden parent unchanged.
- Do not treat hidden descendant scopes as visible-absent deletes.

## Acceptance Criteria

- Nested collection rows attach to the correct parent scope instance by ancestor-address context.
- Root-level extension child collections, collection-aligned extension child collections, and nested extension child collections merge under the same preservation rules as base collection data.
- Hidden descendants are preserved under matched and hidden parents.
- Nested and extension-child `VisibleStoredCollectionRows` remain covered one-for-one by real current DB rows within the correct ancestor context before DML.
- Nested and extension-child visible request candidates remain covered one-for-one by `VisibleRequestCollectionItems` within the correct ancestor context before DML.
- Unmatched nested and extension-child visible items reject deterministically when `Creatable=false`, while matched existing visible items remain updatable.
- Matched nested and extension-child rows preserve hidden FK/descriptor bindings, canonical key-unification storage, and synthetic presence values when those bindings are driven by hidden members.
- Nested second-pass handling does not delete scopes incorrectly under collection ancestry.
- Ordinal recomputation for supported nested and extension-child shapes is deterministic and consistent with the profile-scoped sibling-order rule.

## Tests Required

### Unit tests

- Nested current-row matching by ancestor context and semantic identity
- Wrong-parent / ancestor mismatch protection
- Nested or extension-child reverse stored-row coverage rejection
- Nested request-side visible candidate coverage rejection
- Nested or extension-child orphan or mismatched `VisibleRequestCollectionItem` rejection
- Hidden descendant preservation under matched parent update
- Hidden descendant preservation under hidden parent preservation
- Nested matched update preserves hidden FK/descriptor bindings driven by hidden members
- Nested or extension-child matched update preserves canonical key-unification storage and synthetic presence values in mixed hidden/visible cases
- Nested non-creatable insert rejection with matched visible nested row update allowed
- Extension-child non-creatable insert rejection with existing visible parent update allowed
- Nested second-pass delete protection
- Root-level extension child collection matching and preservation
- Collection-aligned extension child collection matching and preservation
- Nested extension child collection matching and preservation
- Nested or extension-child stored descriptor URI canonicalization via request-cycle cache hit when the URI was published only at a different scope path

### Integration tests

- Nested `ProfileVisibleRowUpdateWithHiddenRowPreservation`
- Nested `ProfileVisibleRowDeleteWithHiddenRowPreservation`
- Root-level extension child collection variant
- Collection-aligned extension child collection variant
- Nested extension child collection variant
- `ProfileHiddenExtensionChildCollectionPreservation`
- Nested or extension-child `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`
- Three-level update-allowed/create-denied chain variant
- One nested delete-all-visible-while-hidden-rows-remain variant
- One nested or extension-child hidden-binding preservation case covering FK/descriptor or key-unification/synthetic-presence behavior
- PostgreSQL and SQL Server parity coverage, or explicit review rationale when this slice introduces no dialect-sensitive behavior beyond previously covered paths

## Reviewer Focus

Reviewers for this slice should focus only on:

- ancestor-address matching,
- descendant preservation,
- nested second-pass behavior, and
- extension child collection parity with base collection rules.

Reviewers should explicitly ignore:

- root-table-only overlay details,
- separate-table non-collection behavior, and
- guarded no-op.

## Leaves Behind For Next Slice

The next slice removes the guarded no-op fence for all supported profiled shapes.

That slice owns:

- comparable rowset synthesis for profiled writes,
- `ContentVersion` freshness recheck,
- stale compare behavior, and
- no-op success semantics for `PUT` and `POST`-as-update.
