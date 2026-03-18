# Story: Integrate the Core/Backend Profile Write Contract

## Description

Implement the backend-side orchestration for profile-constrained writes described in:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/overview.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`

Dependency note: this story is hard-blocked on `reference/design/backend-redesign/epics/07-relational-write-path/01a-core-profile-delivery-plan.md`, which plans the Core-owned outputs it consumes, including readable-vs-writable selection, stored-state projection, address derivation, creatability analysis, and typed profile failures.

This story introduces the request-scoped Core/backend profile hand-off needed before profiled create and stable-identity merge execution:

- Core may supply a `ProfileAppliedWriteRequest` containing `WritableRequestBody`, root-resource creatability, request-scope visibility, and visible request collection-item metadata,
- for `POST` requests that would create a new document, backend consults a Core-owned root-creatability decision before inserting `dms.Document` or root rows,
- for update/upsert-to-existing flows, backend accepts the separately loaded current stored document plus the selected mapping-set-scoped compiled-scope catalog or equivalent adapter and invokes a Core-owned projector to obtain `ProfileAppliedWriteContext`, including `VisibleStoredBody`, stored-scope visibility, visible stored collection-row metadata, and hidden-member preservation metadata required by the merge executor's compiled-binding overlay behavior.

Backend must not re-evaluate profile member filters or collection-item value predicates itself, and assumes Core has already rejected invalid writable profiles that hide compiled semantic-identity members required for persisted collection merges.

Coverage in this story should reuse the shared scenario names from `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md` where applicable so contract tests line up with runtime and parity fixtures.

## Acceptance Criteria

### Request/context orchestration

- When no writable profile applies, the backend write pipeline behaves as it does today and treats all stored scopes as visible.
- When a writable profile applies:
  - backend consumes `WritableRequestBody` for flattening,
  - for a `POST` that would create a new document, backend can determine whether the root resource instance is creatable before persistence starts,
  - backend receives structured request-side scope/item visibility metadata rather than inferring behavior from filtered request JSON,
  - for update/upsert-to-existing flows, the contract accepts the current stored document produced by the separate write-side load/reconstitution step,
  - backend invokes the Core-owned projector to obtain `ProfileAppliedWriteContext` without reinterpreting profile rules itself.
  - request-side and stored-side scope/row addresses are consumed exactly as Core derived them from the shared compiled-scope adapter plus JSON data; backend does not derive alternate ordinal-based address shapes.

### Concrete contract coverage

- The backend-facing contract is semantically equivalent to:
  - `ProfileAppliedWriteRequest(WritableRequestBody, RootResourceCreatable, RequestScopeStates, VisibleRequestCollectionItems)`, and
  - `ProfileAppliedWriteContext(Request, VisibleStoredBody, StoredScopeStates, VisibleStoredCollectionRows)`.
- The contract is sufficient to distinguish:
  - visible-and-present scopes,
  - visible-but-absent scopes, and
  - hidden scopes.
- The contract covers:
  - root-adjacent 1:1 scopes,
  - nested/common-type scopes,
  - collection scopes,
  - resource-level `_ext` scopes, and
  - collection/common-type extension scopes.
- The contract exposes creatability decisions for:
  - a new resource instance,
  - a new visible non-collection scope, and
  - a new visible collection/common-type/extension item or scope.
- Backend supplies Core with a mapping-set-scoped immutable compiled-scope catalog or equivalent adapter for address derivation and canonical member-path vocabulary; Core does not consume raw backend plan types directly.
- The contract distinguishes create-of-new-visible-data from update-of-existing-visible-data:
  - `RootResourceCreatable` applies only when the write would create a new document/root row,
  - `RequestScopeState.Creatable` applies only when `Visibility=VisiblePresent` and no visible stored scope exists at that address, and
  - `VisibleRequestCollectionItem.Creatable` applies only when no visible stored row matches by compiled semantic identity.
- Existing visible scopes/rows remain updatable even when `Creatable=false`; hidden stored data does not convert a create attempt into an update.
- The contract is rich enough to drive top-down creatability across a three-level chain (existing root -> middle collection/common-type scope -> descendant extension child collection) so existing visible parent data stays on the update path while a new visible parent with a hidden required member is rejected and blocks descendant creation.
- Every scope/item entry carries the compiled `JsonScope` plus a stable scope-instance/row address derived from compiled collection ancestry and compiled semantic-identity member order instead of request ordinals.
- The emitted addresses follow the normative derivation algorithm in `reference/design/backend-redesign/design-docs/profiles.md` for both request-side and stored-side projection.
- `SemanticIdentityPart.RelativePath` and `HiddenMemberPaths` use the canonical scope-relative member-path vocabulary published by the shared compiled-scope adapter rather than ad hoc string reconstruction.
- Visible stored collection-row entries expose semantic identity values in compiled order so backend can identify visible persisted rows by compiled semantic identity.
- Stored-scope and stored-row entries expose `HiddenMemberPaths` metadata sufficient for backend to account for every non-storage-managed compiled binding affected by a profiled row/scope as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed, including canonical key-unification storage columns, synthetic presence flags, and FK/descriptor bindings derived from hidden members; generated aliases stay indirect/read-only.
- The contract is sufficient for backend to overlay visible request/resolved values onto stored rows using compiled bindings; backend does not require Core to provide per-column visibility flags or rewritten storage rows.
- Backend validates emitted `JsonScope`, ancestor collection ancestry, and semantic-identity part ordering against the selected compiled-scope adapter and compiled metadata before using the contract.
- Deterministic contract-mismatch diagnostics surface when:
  - a Core-emitted `JsonScope` does not map to a compiled scope,
  - a Core-emitted ancestor chain does not line up to compiled collection ancestry, or
  - backend cannot line up a Core-emitted visible stored row/scope with the compiled plan shape expected for the resource.

### Core validation prerequisite

- Invalid writable profiles that exclude fields required to compute compiled semantic identity for persisted multi-item collection scopes are rejected by Core before backend runtime orchestration.

### Ownership boundary

- Backend does not implement profile member filtering, profile collection value predicates, or readable-vs-writable profile selection rules.
- Hidden vs absent semantics are not inferred from `WritableRequestBody` or `VisibleStoredBody` alone.

### Testing

- Unit or integration tests cover:
  - `NoProfileWriteBehavior`,
  - `ProfileRootCreateRejectedWhenNonCreatable` for profile-scoped `POST` create behavior that checks root creatability without requiring current-state projection,
  - profile-scoped update/upsert behavior that invokes the projector from a supplied current stored document, and
  - `ProfileVisibleRowUpdateWithHiddenRowPreservation` for at least one collection-scope visibility hand-off with stable scope addressing,
  - `ProfileVisibleButAbsentNonCollectionScope` plus `ProfileHiddenInlinedColumnPreservation` or `ProfileHiddenExtensionRowPreservation` for non-collection visibility and hidden-member preservation metadata, including one matched-row overlay case for hidden inlined or extension members, and
  - `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` with the required update-allowed/create-denied pairing proving an existing visible scope/item update remains allowed while the same profile marks a brand-new visible scope/item as non-creatable because required members are hidden, including the three-level parent-create-denied/child-denied chain from the profile design doc, and
  - at least one deterministic contract-mismatch case for unknown `JsonScope`, ancestor-chain mismatch, or unalignable stored-side visibility metadata.

## Tasks

1. Define the backend-facing abstractions used to receive optional `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext`, plus the selected compiled-scope catalog or equivalent adapter used by Core, including request/stored scope states, visible request/stored collection metadata, stable scope addresses, deterministic address-validation diagnostics, and hidden-member preservation metadata that downstream executors can apply through compiled bindings, key-unification canonical storage, synthetic presence flags, and hidden FK/descriptor bindings.
2. Thread `WritableRequestBody` into write-path flattening entry points without duplicating profile logic in backend.
3. For `POST` requests that would create a new document, consult Core-supplied root creatability before creating `dms.Document` or root rows; for profile-scoped update/upsert flows, accept the current stored document plus the selected compiled-scope adapter from the separate write-side load/reconstitution step and invoke the Core-owned stored-state projector.
4. Surface root/scope visibility, collection visibility details, hidden-member preservation metadata, and creatability metadata to downstream merge/no-op code so the executor can distinguish create-of-new-visible-data from update-of-existing-visible-data without re-evaluating profile rules, including the four-way binding-accounting split between visible/writable, hidden/preserved, clear-on-visible-absent, and storage-managed bindings.
5. Validate Core-emitted scope/row addresses and canonical member paths against the selected compiled-scope adapter/compiled metadata before downstream merge/no-op code uses them, and emit deterministic contract-mismatch diagnostics instead of guessing from ordinals or filtered JSON.
6. Add tests that prove the backend consumes the Core-owned contract without inferring hidden-vs-absent semantics from `WritableRequestBody` or `VisibleStoredBody` alone, using the shared scenario names where applicable: `ProfileRootCreateRejectedWhenNonCreatable`, one scope-addressed `ProfileVisibleRowUpdateWithHiddenRowPreservation` case, `ProfileVisibleButAbsentNonCollectionScope` plus `ProfileHiddenInlinedColumnPreservation` or `ProfileHiddenExtensionRowPreservation` with key-unified/presence/FK coverage, one `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` update-allowed/create-denied pair including the three-level chain, and one invalid-contract diagnostic case.
