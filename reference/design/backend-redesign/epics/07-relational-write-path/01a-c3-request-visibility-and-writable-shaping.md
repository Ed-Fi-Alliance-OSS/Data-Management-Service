---
jira: TBD
---

# Story: Request-Side Visibility Classification + Writable Request Shaping

## Description

Implement request-side visibility classification for all compiled scopes and produce the shaped `WritableRequestBody` plus structured `RequestScopeState` entries.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibilities #2, #3, #4, #7, #10, #14
- `reference/design/backend-redesign/design-docs/profiles.md` §"Minimum Core Write Contract"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on: C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — consumes adapter for address derivation and canonical vocabulary.

**Core responsibility coverage:**
- #2 (readable/writable profile selection)
- #3 (recursive member filtering)
- #4 (recursive collection item value filtering)
- #7 (writable request shaping)
- #10 (visibility signaling — request side)
- #14 (extension profile semantics — request side)

This story produces the request-side outputs needed by C4 (creatability), C5 (assembly), C6 (stored-side projection uses the same visibility rules), and C8 (error classification).

## Acceptance Criteria

- `WritableRequestBody` is produced by applying writable-profile member filtering and canonicalization to the request body.
- For every compiled non-collection scope, a `RequestScopeState` entry is emitted with:
  - `Address` — `ScopeInstanceAddress` derived using the normative algorithm from C1,
  - `Visibility` — `VisiblePresent`, `VisibleAbsent`, or `Hidden`, and
  - `Creatable` — initially `false`; populated by C4.
- Visibility classification correctly distinguishes:
  - `VisiblePresent` — scope is included in the writable profile and the request provides data for it,
  - `VisibleAbsent` — scope is included in the writable profile but the request does not provide data for it, and
  - `Hidden` — scope is excluded from the writable profile.
- Recursive member filtering applies `IncludeOnly`, `ExcludeOnly`, and `IncludeAll` filter modes across root, embedded objects, collections, common types, and extensions.
- Collection item value filtering evaluates writable-profile predicates on visible collection items and rejects submitted items that fail profile value filters as validation failures rather than silently pruning them.
- Extension scopes (`_ext` at root and within collection/common-type elements) follow the same visibility and filtering rules as base resource data.
- `RequestScopeState` entries cover root-adjacent 1:1 scopes, nested/common-type scopes, and `_ext` scopes.
- For every visible submitted collection item, a `VisibleRequestCollectionItem` entry (without `Creatable` flag) is emitted with `Address` — `CollectionRowAddress` derived using C1's engine. C3 enumerates these while walking the request body for shaping; C4 enriches them with creatability flags.
- Unit tests cover:
  - shaping for root, 1:1, collection, nested, and `_ext` scopes,
  - `IncludeOnly`, `ExcludeOnly`, and `IncludeAll` filter modes,
  - correct visibility classification for present, absent, and hidden scopes,
  - collection item value filter rejection (not silent pruning), and
  - extension scope visibility following base-data rules.

## Tasks

1. Implement request-side visibility classification: walk all compiled scopes from the adapter and classify each as `VisiblePresent`, `VisibleAbsent`, or `Hidden` based on the writable profile definition and request body.
2. Implement recursive member filtering across root, embedded objects, collections, common types, and extensions for `IncludeOnly`, `ExcludeOnly`, and `IncludeAll` modes.
3. Implement collection item value filtering that evaluates writable-profile predicates on visible items and produces validation failures for items that fail.
4. Enumerate visible collection items from the shaped request body, deriving `CollectionRowAddress` for each using C1's address derivation engine. Emit `VisibleRequestCollectionItem` entries (without `Creatable`) for C4 to enrich.
5. Produce `WritableRequestBody` (filtered/canonicalized request JSON) and `RequestScopeState` entries for all non-collection scopes, deriving addresses using the C1 adapter.
6. Add tests covering all scope types, filter modes, visibility states, value filter rejection, collection item enumeration, and `_ext` behavior.
