# Recommended Improvements for Profile Support in the Backend Redesign

## Purpose

This document lists the follow-up planning and documentation changes needed to make profile support implementation-ready for the backend redesign.

## Highest Priority Actions

### 1. Add a Core profile delivery plan and surface it as a hard dependency

Create a Core-owned spike story for creating a plan for the core side of profile support. An outcome of the spike should be to create stories for the plan.
Put the spike in the relational write path epic.

That plan should explicitly cover:

- profile metadata loading and validation
- readable vs writable profile selection
- recursive request shaping and writable validation
- stored-state projection for profiled writes
- `ScopeInstanceAddress` and `CollectionRowAddress` derivation
- creatability analysis
- readable projection after full reconstitution
- typed profile error classification

Update the dependency plan so backend stories that consume Core profile outputs show a hard dependency on this Core work.

At minimum, that dependency should be visible for:

- `reference/design/backend-redesign/epics/07-relational-write-path/01b-profile-write-context.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/01c-current-document-for-profile-projection.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/05b-profile-error-classification.md`
- `reference/design/backend-redesign/epics/08-relational-read-path/01-json-reconstitution.md`
- `reference/design/backend-redesign/epics/DEPENDENCIES.md`

### 2. Specify stable scope and row address derivation

Add a normative derivation algorithm for `ScopeInstanceAddress` and `CollectionRowAddress`.

The derivation should be defined from compiled scope metadata plus JSON data, not described only as a record shape.

The algorithm should show:

- how `JsonScope` is chosen
- how ancestor collections are discovered
- how ancestor collection instances are ordered
- how compiled semantic-identity parts are read in compiled order
- how request-side and stored-side address derivation stay aligned for nested collections and `_ext` scopes

Add fail-fast diagnostics for contract mismatches at runtime.

At minimum, backend should fail deterministically when:

- Core emits an address whose `JsonScope` does not map to a compiled scope
- Core emits an address whose ancestor chain cannot be matched to compiled collection ancestry
- backend cannot line up a Core-emitted visible stored row or scope with the compiled plan shape expected for that resource

### 3. Tighten the hidden-member preservation contract for key unification

The design already states that `HiddenMemberPaths` governs preservation, including key-unified aliases and presence columns.

It should go one step further and define a complete binding-accounting rule for profiled writes.

For every compiled write binding in a profiled scope, the design should classify it as one of:

- visible and writable
- hidden and preserved
- cleared when the scope is visible but absent
- storage-managed and never directly written by the profile merge logic

Document explicitly how `HiddenMemberPaths` applies to:

- canonical storage columns introduced by key unification
- generated or persisted alias columns
- synthetic presence columns
- FK and descriptor bindings derived from hidden members

Add a defensive validation rule for profiled scope execution:

- every compiled binding affected by a profiled scope must be accounted for by the visible surface, `HiddenMemberPaths`, clear-on-visible-absent behavior, or storage-managed handling

### 4. Expand creatability guidance with a multi-level worked example

The creatability rules are strong, but the examples are mostly single-hop cases.

Add at least one three-level worked example that shows top-down creatability decisions across nested scopes.

Recommended shape:

- resource root
- collection item or nested common-type scope
- extension child collection beneath that parent

The example should include a hidden required member at the middle level and show both outcomes:

- an update to existing visible data that remains valid
- an attempted create of new visible data that is rejected because a required member is hidden

## Important Design Clarifications

### 5. Require no-op detection to reuse merge rowset synthesis

The current design already requires no-op detection to apply the same post-merge rules as execution.

Make the implementation guidance explicit:

- guarded no-op comparison should reuse the same merge-ordering and rowset-synthesis logic, or a shared helper built from the same executor-facing metadata
- no independent profile-specific merge implementation should be introduced just for no-op detection

### 6. Cross-reference empty-effective-key compatibility findings to the compilation failure rule

Add a short cross-reference from `reference/design/backend-redesign/design-docs/ods-profile-compatibility-findings.md` to the rule in the profile design that invalid persisted multi-item collection scopes without non-empty semantic identity must fail before runtime merge execution.

This keeps the compatibility memo and the normative runtime contract aligned.

### 7. Clarify extension child table foreign-key chains and delete semantics

The extension design already explains why a root-level extension child table can reuse the root `..._DocumentId` as both the root locator and the immediate parent key.

Add one more level of clarity by documenting:

- the FK chain for root-level extension child collections
- the FK chain for collection-aligned extension child collections
- how delete and cascade behavior works for each case
- how nested extension child collections attach through `ParentCollectionItemId`
