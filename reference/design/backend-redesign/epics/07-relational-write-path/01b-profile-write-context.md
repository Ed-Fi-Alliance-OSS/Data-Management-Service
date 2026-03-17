# Story: Integrate the Core/Backend Profile Write Contract

## Description

Implement the backend-side orchestration for profile-constrained writes described in:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/overview.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`

This story introduces the request-scoped Core/backend profile hand-off needed before profiled create and stable-identity merge execution:

- Core may supply a `ProfileAppliedWriteRequest.WritableRequestBody`,
- for `POST` requests that would create a new document, backend consults a Core-owned root-creatability decision before inserting `dms.Document` or root rows,
- for update/upsert-to-existing flows, backend accepts the separately loaded current stored document and invokes a Core-owned projector to obtain `ProfileAppliedWriteContext`, including `VisibleStoredBody` plus the visibility/creatability details required by the merge executor.

Backend must not re-evaluate profile member filters or collection-item value predicates itself, and assumes Core has already rejected invalid writable profiles that hide compiled semantic-identity members required for persisted collection merges.

## Acceptance Criteria

### Request/context orchestration

- When no writable profile applies, the backend write pipeline behaves as it does today and treats all stored scopes as visible.
- When a writable profile applies:
  - backend consumes `WritableRequestBody` for flattening,
  - for a `POST` that would create a new document, backend can determine whether the root resource instance is creatable before persistence starts,
  - for update/upsert-to-existing flows, the contract accepts the current stored document produced by the separate write-side load/reconstitution step, and
  - backend invokes the Core-owned projector to obtain `ProfileAppliedWriteContext` without reinterpreting profile rules itself.

### Minimum contract coverage

- The backend-facing contract is sufficient to distinguish:
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
- The contract exposes enough collection visibility detail for backend to identify visible persisted rows by compiled semantic identity.

### Core validation prerequisite

- Invalid writable profiles that exclude fields required to compute compiled semantic identity for persisted multi-item collection scopes are rejected by Core before backend runtime orchestration.

### Ownership boundary

- Backend does not implement profile member filtering, profile collection value predicates, or readable-vs-writable profile selection rules.
- Hidden vs absent semantics are not inferred from filtered JSON alone.

### Testing

- Unit or integration tests cover:
  - no-profile behavior,
  - profile-scoped `POST` create behavior that checks root creatability without requiring current-state projection,
  - profile-scoped update/upsert behavior that invokes the projector from a supplied current stored document, and
  - visibility/creatability hand-off for at least one collection scope and one non-collection scope.

## Tasks

1. Define the backend-facing abstractions used to receive optional `WritableRequestBody`, root-resource creatability, and `ProfileAppliedWriteContext`.
2. Thread `WritableRequestBody` into write-path flattening entry points without duplicating profile logic in backend.
3. For `POST` requests that would create a new document, consult Core-supplied root creatability before creating `dms.Document` or root rows; for profile-scoped update/upsert flows, accept the current stored document from the separate write-side load/reconstitution step and invoke the Core-owned stored-state projector.
4. Surface root/scope visibility, collection visibility details, and creatability metadata to downstream merge/no-op code.
5. Add tests that prove the backend consumes the Core-owned contract without inferring hidden-vs-absent semantics from filtered JSON alone, including root-resource create rejection.
