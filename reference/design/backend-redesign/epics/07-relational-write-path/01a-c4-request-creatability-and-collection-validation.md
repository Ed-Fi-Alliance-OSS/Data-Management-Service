---
jira: TBD
---

# Story: Request-Side Creatability Analysis + Duplicate Collection-Item Validation

## Description

Implement the full top-down creatability decision model from `profiles.md` and duplicate visible collection-item validation by compiled semantic identity.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibilities #5, #6
- `reference/design/backend-redesign/design-docs/profiles.md` §"Creatability Decision Model"
- `reference/design/backend-redesign/design-docs/profiles.md` §"Minimum Core Write Contract"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on:
- C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — adapter for address derivation
- C2 (`01a-c2-semantic-identity-compatibility-validation.md`) — assumes incompatible profiles are already rejected
- C3 (`01a-c3-request-visibility-and-writable-shaping.md`) — consumes visibility classification

**Core responsibility coverage:**
- #5 (writable request validation — duplicate collection items)
- #6 (creatability analysis)

### Creatability

Creatability is the Core-owned answer to "can we create a new visible instance here?" per `profiles.md` §"Creatability Decision Model". It is evaluated top-down. Existing visible data may remain updatable even when the same profile forbids creation of a brand-new visible instance.

### Duplicate Validation

Submitted visible collection/common-type/extension collection items that collide on compiled semantic identity within the same stable parent address must be rejected before backend flattening, merge planning, or DML begins.

## Acceptance Criteria

### Creatability

- `RootResourceCreatable` is emitted for profile-constrained creates per the normative decision procedure.
- `RequestScopeState.Creatable` is populated for each non-collection scope:
  - `true` only when `Visibility=VisiblePresent`, no visible stored scope exists at the same address, and all creation-required members are exposed by the writable profile.
  - `false` for `VisibleAbsent` and `Hidden` scopes.
- `VisibleRequestCollectionItem` entries are emitted for every visible submitted collection item with:
  - `Address` — `CollectionRowAddress` derived using C1,
  - `Creatable` — `true` only when no visible stored row matches by compiled semantic identity and creation-required members are exposed.
- Creatability is evaluated top-down: a child scope/item is not creatable unless its immediate visible parent already exists or is creatable in the same request.
- Storage-managed values (`DocumentId`, `CollectionItemId`, timestamps, etc.) are not treated as creation-required members.
- A creation-required member hidden by the writable profile makes the create attempt non-creatable.
- Matched visible stored scopes/rows may be updated even when `Creatable=false`.
- Hidden stored data does not convert a create-of-new-visible-data attempt into an update-of-existing-visible-data.

### Duplicate Validation

- `VisibleRequestCollectionItems` contains at most one item per `CollectionRowAddress`.
- If writable-profile shaping would emit two visible submitted items with the same stable parent address and compiled semantic identity, Core rejects the request before backend receives the contract.

### Testing

- Three-level chain creatability: existing root → middle collection/common-type scope → descendant extension child collection, proving parent creatability gates children.
- Update-allowed/create-denied pairing: existing visible scope/item update remains allowed while a new visible scope/item is rejected because required members are hidden.
- Duplicate visible collection items by compiled semantic identity within the same parent are rejected.
- Non-creatable new 1:1, nested/common-type, collection/common-type, extension scope, and extension collection-item cases.
- Storage-managed values excluded from creation-required member analysis.

## Tasks

1. Implement `RootResourceCreatable` decision per the normative procedure: creation-required root members must be visible or system-supplied; newly visible children must be creatable.
2. Implement scope-level creatability: for each `VisiblePresent` non-collection scope without a visible stored scope at the same address, check creation-required members and parent creatability.
3. Implement collection-item creatability: for each visible request item without a visible stored row match by compiled semantic identity, check creation-required members and parent creatability.
4. Implement duplicate visible collection-item detection: group visible items by `CollectionRowAddress` and reject duplicates.
5. Add tests covering the three-level chain, update-allowed/create-denied pairing, duplicate rejection, and storage-managed exclusion.
