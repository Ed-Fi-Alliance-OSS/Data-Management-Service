---
jira: TBD
---

# Story: Shared Compiled-Scope Adapter Contract + Address Derivation Engine

## Description

Define the shared compiled-scope adapter contract types and implement the normative `ScopeInstanceAddress` / `CollectionRowAddress` derivation algorithm.

This is the foundation story for all Core profile support. Every other Core profile story (C2–C8) depends on the adapter contract and address derivation engine produced here.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Shared Compiled-Scope Adapter"
- `reference/design/backend-redesign/design-docs/profiles.md` §"Scope and Row Address Derivation"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

**Core responsibility coverage:** #9 (stable scope and row address derivation)

### Adapter Contract

Define the adapter contract types per `profiles.md` §"Shared Compiled-Scope Adapter" with 6 fields per compiled scope:

- `JsonScope` — exact compiled scope identifier (`DbTableModel.JsonScope` / `TableWritePlan.TableModel.JsonScope`)
- `ScopeKind` — `Root` | `NonCollection` | `Collection`
- `ImmediateParentJsonScope` — compiled parent scope; collection-aligned `_ext` scopes point at the aligned base scope
- `CollectionAncestorsInOrder` — compiled collection scopes from root-most to immediate parent collection ancestor
- `SemanticIdentityRelativePathsInOrder` — compiled non-empty semantic identity member paths for persisted multi-item collection scopes
- `CanonicalScopeRelativeMemberPaths` — canonical vocabulary for `SemanticIdentityPart.RelativePath` and `HiddenMemberPaths`

Note: `ScopeKind` intentionally does not distinguish inlined-vs-separate-table storage topology. That distinction is a backend-only concern resolved from `TableWritePlan` metadata at execution time. Core emits visibility and `HiddenMemberPaths` uniformly for all `NonCollection` scopes regardless of storage topology.

### Construction Responsibility

C1 delivers the contract types, the derivation engine, and a test-only adapter factory. The production adapter factory (populating from `TableWritePlan` / `CollectionMergePlan` / `DbTableModel` in the selected mapping set) is backend's responsibility, owned by DMS-1103 (`E07-S01b`) or a prerequisite task within it. C1's contract types do not reference backend compiled-plan types.

### Address Derivation Engine

Implement the normative 7-step algorithm from `profiles.md` §"Scope and Row Address Derivation":

1. Resolve compiled scope descriptor from adapter; emit exact `JsonScope`.
2. Derive `AncestorCollectionInstances` from `CollectionAncestorsInOrder`, reading semantic identity from each ancestor's JSON item in compiled order.
3. For each `SemanticIdentityPart`: `RelativePath` from adapter canonical vocabulary, `Value` from JSON, `IsPresent` preserving missing-vs-null.
4. `ScopeInstanceAddress` for non-collection scopes: `(JsonScope, AncestorCollectionInstances)`.
5. `CollectionRowAddress` for visible collection rows: `(JsonScope, ParentAddress, SemanticIdentityInOrder)`.
6. `_ext` segments participate as literal `JsonScope` but do not create ancestor entries; extension child collections reuse aligned base as parent/ancestor context.
7. Request-side uses `WritableRequestBody`; stored-side uses full current stored document. Both use the same adapter and rules.

## Acceptance Criteria

- Adapter contract surface matches `profiles.md` §"Shared Compiled-Scope Adapter" exactly: `JsonScope`, `ScopeKind`, `ImmediateParentJsonScope`, `CollectionAncestorsInOrder`, `SemanticIdentityRelativePathsInOrder`, `CanonicalScopeRelativeMemberPaths`.
- `ScopeKind` distinguishes `Root`, `NonCollection`, and `Collection`.
- Address derivation follows the normative 7-step algorithm from `profiles.md` §"Scope and Row Address Derivation".
- `ScopeInstanceAddress` is produced for root and non-collection scopes; `CollectionRowAddress` is produced for collection scopes.
- `SemanticIdentityPart.RelativePath` uses only the canonical vocabulary from the adapter.
- `_ext` segments participate as literal `JsonScope` segments without creating ancestor collection instances.
- Request-side and stored-side derivation produce identical addresses for the same scope/item when given the same JSON data.
- Unit tests cover derivation for:
  - root scope (empty ancestor list),
  - root-adjacent 1:1 scope,
  - single-level collection scope,
  - nested collection scope (two-level ancestor chain),
  - `_ext` scope at root level,
  - collection-aligned `_ext` child collection scope.

## Tasks

1. Define the compiled-scope adapter contract types (`JsonScope`, `ScopeKind`, `ImmediateParentJsonScope`, `CollectionAncestorsInOrder`, `SemanticIdentityRelativePathsInOrder`, `CanonicalScopeRelativeMemberPaths`) as immutable types suitable for caching with the mapping set.
2. Define the address types: `ScopeInstanceAddress`, `CollectionRowAddress`, `AncestorCollectionInstance`, `SemanticIdentityPart`.
3. Implement the normative 7-step address derivation algorithm that takes a compiled scope descriptor + JSON data and produces addresses.
4. Add tests proving correct derivation for root, 1:1, collection, nested collection, and `_ext` scopes against a test adapter, including request-side/stored-side alignment verification.
5. Assess compatibility between the existing `ProfileDefinition` shape and the adapter contract's canonical vocabulary. Document whether the existing profile loading infrastructure can produce definitions that C2–C8 can consume directly, or whether adaptation is needed. If adaptation is non-trivial, document the required changes and their scope so that C3 can incorporate them or escalate to a new story.
