# Story: Integrate the Core/Backend Profile Write Contract

## Description

Implement the backend-side orchestration for profile-constrained writes described in:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/overview.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`

This story introduces the request-scoped Core/backend profile hand-off needed before profiled create and stable-identity merge execution:

- Core may supply a `ProfileAppliedWriteRequest` containing `WritableRequestBody`, root-resource creatability, request-scope visibility, and visible request collection-item metadata,
- for `POST` requests that would create a new document, backend consults a Core-owned root-creatability decision before inserting `dms.Document` or root rows,
- for update/upsert-to-existing flows, backend accepts the separately loaded current stored document and invokes a Core-owned projector to obtain `ProfileAppliedWriteContext`, including `VisibleStoredBody`, stored-scope visibility, visible stored collection-row metadata, and hidden-member preservation metadata required by the merge executor's compiled-binding overlay behavior.

Backend must not re-evaluate profile member filters or collection-item value predicates itself, and assumes Core has already rejected invalid writable profiles that hide compiled semantic-identity members required for persisted collection merges.

## Acceptance Criteria

### Request/context orchestration

- When no writable profile applies, the backend write pipeline behaves as it does today and treats all stored scopes as visible.
- When a writable profile applies:
  - backend consumes `WritableRequestBody` for flattening,
  - for a `POST` that would create a new document, backend can determine whether the root resource instance is creatable before persistence starts,
  - backend receives structured request-side scope/item visibility metadata rather than inferring behavior from filtered request JSON,
  - for update/upsert-to-existing flows, the contract accepts the current stored document produced by the separate write-side load/reconstitution step, and
  - backend invokes the Core-owned projector to obtain `ProfileAppliedWriteContext` without reinterpreting profile rules itself.

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
- The contract distinguishes create-of-new-visible-data from update-of-existing-visible-data:
  - `RootResourceCreatable` applies only when the write would create a new document/root row,
  - `RequestScopeState.Creatable` applies only when `Visibility=VisiblePresent` and no visible stored scope exists at that address, and
  - `VisibleRequestCollectionItem.Creatable` applies only when no visible stored row matches by compiled semantic identity.
- Existing visible scopes/rows remain updatable even when `Creatable=false`; hidden stored data does not convert a create attempt into an update.
- Every scope/item entry carries the compiled `JsonScope` plus a stable scope-instance address that uses ancestor collection semantic identities instead of request ordinals.
- Visible stored collection-row entries expose semantic identity values in compiled order so backend can identify visible persisted rows by compiled semantic identity.
- Stored-scope and stored-row entries expose `HiddenMemberPaths` metadata sufficient for backend to preserve hidden columns and hidden inlined values on matched rows/scopes.
- The contract is sufficient for backend to overlay visible request/resolved values onto stored rows using compiled bindings; backend does not require Core to provide per-column visibility flags or rewritten storage rows.

### Core validation prerequisite

- Invalid writable profiles that exclude fields required to compute compiled semantic identity for persisted multi-item collection scopes are rejected by Core before backend runtime orchestration.

### Ownership boundary

- Backend does not implement profile member filtering, profile collection value predicates, or readable-vs-writable profile selection rules.
- Hidden vs absent semantics are not inferred from `WritableRequestBody` or `VisibleStoredBody` alone.

### Testing

- Unit or integration tests cover:
  - no-profile behavior,
  - profile-scoped `POST` create behavior that checks root creatability without requiring current-state projection,
  - profile-scoped update/upsert behavior that invokes the projector from a supplied current stored document, and
  - visibility/creatability hand-off for at least one collection scope and one non-collection scope, including stable scope addressing, hidden-member preservation metadata, and one matched-row overlay case for hidden inlined or extension members, and
  - one paired scenario proving an existing visible scope/item update remains allowed while the same profile marks a brand-new visible scope/item as non-creatable because required members are hidden.

## Tasks

1. Define the backend-facing abstractions used to receive optional `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext`, including request/stored scope states, visible request/stored collection metadata, stable scope addresses, and hidden-member preservation metadata that downstream executors can apply through compiled bindings.
2. Thread `WritableRequestBody` into write-path flattening entry points without duplicating profile logic in backend.
3. For `POST` requests that would create a new document, consult Core-supplied root creatability before creating `dms.Document` or root rows; for profile-scoped update/upsert flows, accept the current stored document from the separate write-side load/reconstitution step and invoke the Core-owned stored-state projector.
4. Surface root/scope visibility, collection visibility details, hidden-member preservation metadata, and creatability metadata to downstream merge/no-op code so the executor can distinguish create-of-new-visible-data from update-of-existing-visible-data without re-evaluating profile rules.
5. Add tests that prove the backend consumes the Core-owned contract without inferring hidden-vs-absent semantics from `WritableRequestBody` or `VisibleStoredBody` alone, including root-resource create rejection, one scope-addressed collection scenario, one matched-row hidden-member overlay scenario, and one update-allowed/create-denied pair.
