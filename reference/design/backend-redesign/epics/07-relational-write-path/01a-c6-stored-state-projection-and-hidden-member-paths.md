---
jira: TBD
---

# Story: Stored-State Projection + HiddenMemberPaths Computation

## Description

Implement the Core-owned stored-state projector callback: given the current stored JSON and the compiled-scope adapter, produce `VisibleStoredBody`, `StoredScopeStates`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths`, and assemble the complete `ProfileAppliedWriteContext`.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibilities #8, #10, #11, #14
- `reference/design/backend-redesign/design-docs/profiles.md` §"Minimum Core Write Contract"
- `reference/design/backend-redesign/design-docs/profiles.md` §"Hidden-Member Preservation Execution Model"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on:
- C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — adapter for stored-side address derivation
- C3 (`01a-c3-request-visibility-and-writable-shaping.md`) — uses the same visibility classification rules
- C5 (`01a-c5-assemble-profile-applied-write-request.md`) — `ProfileAppliedWriteRequest` is included in the context

**Core responsibility coverage:**
- #8 (stored-state projection for writes)
- #10 (visibility signaling — stored side)
- #11 (collection visibility details)
- #14 (extension profile semantics — stored side)

This story produces the `ProfileAppliedWriteContext` that backend consumes in `E07-S01b` (DMS-1103) and that the write-side current-document loader in `E07-S01c` (DMS-1105) hands off to.

### HiddenMemberPaths Vocabulary Split

Core emits `HiddenMemberPaths` in the canonical scope-relative vocabulary published by the adapter's `CanonicalScopeRelativeMemberPaths`. Backend resolves those canonical paths to physical bindings through its own compiled write metadata (`TableWritePlan`, `CollectionMergePlan`, `KeyUnificationWritePlan`).

## Acceptance Criteria

### Stored-State Projection

- `VisibleStoredBody` is produced by applying the same writable-profile rules used for `WritableRequestBody` to the full current stored JSON.
- `StoredScopeStates` entries are emitted for every compiled non-collection scope with:
  - `Address` — `ScopeInstanceAddress` derived from stored JSON using the C1 adapter,
  - `Visibility` — `VisiblePresent`, `VisibleAbsent`, or `Hidden`, and
  - `HiddenMemberPaths` — canonical scope-relative paths of members hidden by the writable profile.
- `VisibleStoredCollectionRows` entries are emitted for every visible persisted collection row with:
  - `Address` — `CollectionRowAddress` derived from stored JSON, identifying the row by compiled semantic identity, and
  - `HiddenMemberPaths` — canonical scope-relative paths of hidden members for that row.
- `VisibleStoredCollectionRows` serves a dual purpose: it provides `HiddenMemberPaths` for matched rows AND it enables backend's delete signaling. A visible stored row present in `VisibleStoredCollectionRows` but absent from the request's `VisibleRequestCollectionItems` (by `CollectionRowAddress`) signals backend to delete that row. Hidden rows are never surfaced in `VisibleStoredCollectionRows` and are unconditionally preserved.

### HiddenMemberPaths

- `HiddenMemberPaths` are emitted only in the canonical vocabulary from `CanonicalScopeRelativeMemberPaths`.
- `HiddenMemberPaths` cover hidden scalar columns, hidden inlined common-type members, hidden reference members, and hidden extension members.
- For any profiled matched row/scope, `HiddenMemberPaths` plus compiled write-plan metadata must let backend classify every non-storage-managed binding as: visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed.

### Context Assembly

- `ProfileAppliedWriteContext` is assembled with:
  - `Request` — `ProfileAppliedWriteRequest` from C5,
  - `VisibleStoredBody`,
  - `StoredScopeStates`, and
  - `VisibleStoredCollectionRows`.

### No-Profile Passthrough

When no writable profile applies, C6 is not invoked. No `ProfileAppliedWriteContext` is assembled. Backend uses its existing non-profiled write path unchanged. See "No-Profile Passthrough Path" in the delivery plan.

### Extension Semantics

- Extension scopes (`_ext` at root and within collection/common-type elements) follow the same stored-state projection rules as base data.
- Extension scope `HiddenMemberPaths` are emitted using the canonical vocabulary.

### Testing

- Visible, absent, and hidden non-collection scopes produce correct `StoredScopeState` entries.
- `HiddenMemberPaths` are correct for hidden scalars, hidden references, and hidden extension members.
- Nested collection rows produce correct `VisibleStoredCollectionRow` entries with semantic identity.
- `HiddenMemberPaths` use the canonical vocabulary from the adapter.
- At least one matched-row overlay case verifying `HiddenMemberPaths` sufficiency for binding accounting.
- A visible stored collection row with no matching request item is correctly included in `VisibleStoredCollectionRows`, enabling backend's delete-vs-preserve decision: backend deletes visible stored rows that have no matching request item, while hidden rows (not in `VisibleStoredCollectionRows`) are preserved.
- Extension scope stored-state projection follows base-data rules.

## Tasks

1. Implement stored-side visibility classification using C3's shared visibility classification primitive (see C3 §"Shared Visibility Classification Primitive"). When C5 has already classified stored scopes for the existence lookup, C6 must extend those intermediate results (adding `HiddenMemberPaths`) rather than reclassifying from scratch. This is required by profiles.md §"Creatability Decision Model": creatability must be evaluated against the same visible stored state as the write contract. Reclassifying independently risks divergence.
2. Implement `HiddenMemberPaths` computation: for each hidden member in a visible scope/row, emit the canonical scope-relative path from the adapter vocabulary.
3. Produce `VisibleStoredBody`, `StoredScopeStates`, and `VisibleStoredCollectionRows`.
4. Assemble `ProfileAppliedWriteContext(Request, VisibleStoredBody, StoredScopeStates, VisibleStoredCollectionRows)`.
5. Add tests covering visible/absent/hidden scopes, `HiddenMemberPaths` for scalars/references/extensions, nested collection rows, and canonical vocabulary usage.
