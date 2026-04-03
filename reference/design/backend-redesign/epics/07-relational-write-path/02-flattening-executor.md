---
jira: DMS-983
jira_url: https://edfi.atlassian.net/browse/DMS-983
---

# Story: Establish the Initial Relational Write Seam and Flatten Validated Write Bodies

## Description

Implement `DMS-983` as the initial non-descriptor relational write seam behind `UseRelationalBackend`, not as a standalone flattener-only slice.

- When `UseRelationalBackend=false`, host composition continues to register the legacy document-store path.
- When `UseRelationalBackend=true`, host composition registers a new `RelationalDocumentStoreRepository` as the exclusive `IDocumentStoreRepository` and adds the relational runtime prerequisites for both PostgreSQL and SQL Server. The relational path does not delegate unsupported operations back to the legacy repository.
- Applies to non-descriptor `POST` and `PUT` flows. Descriptor writes remain out of scope for this story and must fail deterministically with explicit guidance to the dedicated descriptor-write follow-on, rather than falling back to the legacy repository.
- `GET`, `DELETE`, and other unsupported relational operations fail deterministically with explicit not-implemented responses while the relational repository is active.
- The repository seam for this story must: select the resolved mapping set and compiled write plan, resolve target context (`create-new` vs `existing`), run reference resolution, invoke the flattener through a backend-local contract, and hand the shaped result to a capturable terminal stage that `DMS-984` will later replace with real persistence.
- Missing `MappingSet` behavior remains owned by the existing middleware-level `503`; missing non-descriptor `ResourceWritePlan` instances and other unsupported relational-plan gaps fail deterministically inside the relational path.
- Startup failure remains reserved for missing DI/configuration or broken relational composition, not for request-scoped mapping-set, write-plan, descriptor, or unsupported-operation guard rails.
- Authorization execution remains out of scope for this story. The seam must leave room for future authorization checks after reference resolution and before persistence, but `DMS-983` does not introduce backend authorization behavior.
- Existing write behaviors that depend on later persistence/concurrency work, including `PUT` not-found handling, `If-Match` / ETag mismatch handling, immutable-identity enforcement, and guarded no-op short-circuiting, are intentionally deferred from this story and must not be recovered through legacy fallback.
- The backend-local flattening input contract uses `JsonNode` for the selected request body, wrapped in a small request type so later callers can change the body source without redesigning the flattener contract.
- The flattener consumes the existing `ResolvedReferenceSet` at the repository boundary and adapts it into flattener-local hot-loop lookups rather than introducing a second request-wide reference-resolution contract.
- Traverse the selected JSON once, tracking request sibling order and ordinal path for nested scopes.
- Emit a shaped flattened result aligned to `DMS-984`: the root row, root-extension rows, and collection/common-type candidates. Collection-aligned 1:1 scopes whose keys depend on unresolved base `CollectionItemId` values stay attached to the owning collection candidate instead of being emitted as fully bound standalone rows.
- Populate scalar columns from strict JSON value reads, FK columns from resolved `DocumentId` and `DescriptorId` values, and all non-storage-assigned precomputed bindings, including key-unification values and synthetic presence flags. Storage-assigned root `DocumentId` values and new `CollectionItemId` values remain unresolved for create flows.
- Compute deterministic semantic collection identities for persisted multi-item collection scopes without evaluating profile rules in backend, and reject duplicate submitted semantic identities under the same stable parent in this story.
- Profile-applied request-body selection remains out of scope here and is deferred to follow-on story `DMS-1123` / `reference/design/backend-redesign/epics/07-relational-write-path/02b-profile-applied-request-flattening.md`.

This re-scope intentionally separates the initial relational seam and backend flattening mechanics from the Core-owned profile body-selection hand-off so backend can land the real repository boundary, traversal, row-buffer emission, reference binding, and terminal-stage handoff before `DMS-1106`. Moderate refactoring around path reading, reference lookup adaptation, and candidate contracts is in scope where it reduces duplication for `DMS-984` and `DMS-1123`; broad persistence-executor refactors remain out of scope.

## Acceptance Criteria

- `UseRelationalBackend=false` leaves legacy runtime behavior unchanged, while `UseRelationalBackend=true` registers the relational repository as the exclusive `IDocumentStoreRepository` and adds the required PostgreSQL and SQL Server relational write-path services.
- Non-descriptor `POST` and `PUT` requests enter the new relational repository seam when the flag is on, and no relational operation delegates back to the legacy repository.
- The flattener accepts a caller-supplied `JsonNode` body through a backend-local input contract and does not depend directly on `ProfileAppliedWriteRequest` types in this story.
- For this story's runtime path, the caller supplies the normal validated request body; profile-applied request-body selection remains out of scope and is deferred to `02b-profile-applied-request-flattening.md`.
- `POST` performs real create-vs-existing target-context selection before flattening, and `PUT` resolves the existing-document target context before flattening, including existing `DocumentId` and `DocumentUuid` values when known.
- Relational writes call the real `IReferenceResolver`, preserve existing reference-failure semantics, and short-circuit before flattening and terminal-stage handoff when reference resolution fails.
- Authorization remains out of scope for `DMS-983`; the relational seam leaves room for future authorization checks after reference resolution and before persistence without reshaping the flattener contract.
- Flattening produces a deterministic root row, root-extension rows, and collection candidates that preserve request sibling order.
- References inside nested collections are written to the correct child rows using concrete-path resolution plus ordinal-path mapping.
- Collection candidates carry the scope, ordinal-path, request-order, semantic-identity, and candidate-attached aligned-scope data needed for later binding to existing or newly reserved `CollectionItemId` values, including root-level and collection-aligned extension child collections.
- Collection-aligned 1:1 scopes such as `$.addresses[*]._ext.sample` remain attached to the owning base collection candidate until stable `CollectionItemId` binding exists.
- Root and nested `_ext` rows/candidates are emitted only when extension values exist for the scope.
- Flattening populates all non-storage-assigned precomputed bindings required by the compiled write plan, including key-unification values and synthetic presence flags; storage-assigned root and collection identities remain unresolved for create flows.
- Duplicate submitted semantic identities under the same stable parent are detected in `DMS-983` and fail deterministically before terminal handoff.
- Backend scalar reading is strict against compiled types and does not reimplement Core-side coercion. On the supported public HTTP path, Core still owns permissive request normalization/coercion before the selected body reaches the backend seam.
- No general-purpose JSONPath engine is invoked per value in the hot loop.
- After successful plan selection, target-context resolution, reference resolution, and flattening, the repository invokes a narrow `IRelationalWriteTerminalStage` contract with operation kind, target context, `ResourceWritePlan`, selected body, `ResolvedReferenceSet`, flattened output, and trace or diagnostic identifiers.
- The default production terminal stage returns an explicit not-yet-implemented failure, while tests can substitute a capturing fake without changing repository logic.
- Unsupported relational operations, descriptor writes, and missing non-descriptor relational-plan prerequisites fail deterministically with explicit guard-rail messages; missing `MappingSet` behavior remains the existing request-time `503`.
- Startup failures are reserved for broken relational DI/configuration rather than request-scoped guard rails.
- `PUT` not-found handling, `If-Match` / ETag mismatch handling, immutable-identity enforcement, and guarded no-op behavior remain intentionally deferred and do not fall back to legacy execution when `UseRelationalBackend=true`.
- Tests cover:
  - PostgreSQL and SQL Server composition with `UseRelationalBackend`,
  - Core-fronted `POST` and `PUT` happy paths through the real repository seam with a capturing terminal stage,
  - reference-failure short-circuiting,
  - mapping-set and write-plan guard rails,
  - the “supplied body is authoritative” seam,
  - nested collections and references at multiple depths,
  - `_ext` at root, a root-level extension child collection, `_ext` within a collection, and a collection-aligned extension child collection,
  - one richer fixture-backed flattening case plus one authoritative DS 5.2-style fixture, and
  - one top-level ASP.NET smoke with `UseRelationalBackend=true`.

## Authorization Batching Consideration

Authorization is out of scope for this story, but the new repository seam should still be structured so later backend authorization work can run after reference resolution and before persistence without reshaping the `FlatteningInput`, `FlattenedWriteSet`, or terminal-stage contracts. See `reference/design/backend-redesign/design-docs/auth.md` and `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` for the intended eventual ordering.

## Tasks

1. Add the exclusive `UseRelationalBackend` repository swap in host composition for PostgreSQL and SQL Server so the relational repository becomes the only `IDocumentStoreRepository` when the flag is on.
2. Define the backend-local `DMS-983` write contracts: operation kind, target context, `FlatteningInput`, `FlattenedWriteSet`, the root row, root-extension rows, collection candidates, candidate-attached aligned-scope data, and `IRelationalWriteTerminalStage`.
3. Implement `RelationalDocumentStoreRepository` as the exclusive opt-in write repository, with deterministic guard rails for unsupported operations, descriptor writes, and missing non-descriptor relational-plan prerequisites.
4. Add target-context selection ahead of flattening for `POST` and `PUT`, including real create-vs-existing detection and propagation of existing `DocumentId` and `DocumentUuid` values when known.
5. Integrate `IReferenceResolver` into the relational repository flow and short-circuit on missing or incompatible document and descriptor references using the existing failure semantics.
6. Adapt `ResolvedReferenceSet` into flattener-local hot-loop lookups for document and descriptor FK binding by ordinal path.
7. Implement the flattener walker for root and non-collection scopes so it traverses the selected body once, emits deterministic row buffers, treats the selected body as authoritative input, and performs strict scalar reading against compiled types.
8. Populate all non-storage-assigned compiled write bindings during flattening, including key-unification precomputed values and synthetic presence flags, while leaving storage-assigned root and collection identities unresolved for create flows.
9. Implement collection/common-type candidate extraction so candidates carry ordinal path, request sibling order, semantic identity, scalar or FK or precomputed values, and fail deterministically on duplicate submitted semantic identities under the same stable parent.
10. Implement `_ext` handling for root-level, nested, root-level extension child collection, and collection-aligned scopes, including the rule that collection-aligned 1:1 scopes stay attached to the owning base collection candidate until stable `CollectionItemId` binding exists.
11. Implement `IRelationalWriteTerminalStage` orchestration with a production default that returns an explicit not-implemented failure and a test seam that supports a capturing fake.
12. Add composition, repository, Core-fronted seam, and one top-level HTTP smoke test that cover the new relational write path using focused runtime-plan fixtures plus one authoritative DS 5.2-style fixture, including the “supplied body is authoritative” seam.
