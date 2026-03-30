---
jira: DMS-1112
jira_url: https://edfi.atlassian.net/browse/DMS-1112
---

# Story: Typed Profile Error Classification

## Description

Define the shared typed profile error contract used across Core and backend for invalid profile definitions, invalid usage, writable validation failures, creatability violations, Core/backend contract mismatches, and binding-accounting failures.

C8 defines the type hierarchy for all six error categories before the consuming stories implement detection. Detection stays in the story that owns the relevant processing: category 1 in C2, category 2 in C5 (profile-mode validation gate), category 3 in C3/C4, category 4 in C4, category 5 in DMS-1106 (contract mismatch), and category 6 in DMS-1104 (binding-accounting failure). This keeps all downstream stories on one shared failure vocabulary instead of inventing local error shapes.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibility #15
- `reference/design/backend-redesign/design-docs/profiles.md` §"Validation and Error Semantics"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on: None. This story defines the shared failure contract that C2, C3, C4, C5, DMS-1106, and DMS-1104 consume.

**Core responsibility coverage:** #15 (structured error classification)

This story unblocks the profile stories that emit typed failures and `DMS-1104` (`reference/design/backend-redesign/epics/07-relational-write-path/05b-profile-error-classification.md`), which classifies and maps those failures to DMS error shapes.

## Acceptance Criteria

### Error Categories

The typed failure contract must distinguish:

1. **Invalid profile definition** — writable profile hides compiled semantic-identity fields for persisted multi-item collections (from C2), or other structural profile errors detected before runtime.
2. **Invalid profile usage** — wrong profile mode for the operation, profile not found for the resource, or other usage-level errors.
3. **Writable-profile validation failure** — submitted data forbidden by the writable profile: members not in the profile surface, collection items failing value filters, or duplicate visible collection-item collisions after writable-profile shaping.
4. **Creatability violation** — new visible instance would be created but required members are hidden by the profile. Covers root resource, 1:1 scope, nested/common-type scope, collection item, extension scope, and extension collection item.
5. **Core/backend contract mismatch** — Core-emitted `JsonScope` does not map to a compiled scope, ancestor chain does not match compiled collection ancestry, or stored-side visibility metadata cannot be lined up to the compiled plan shape.
6. **Binding-accounting failure** — a profiled binding cannot be classified as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed.

### Behavior

- C8 provides one deterministic typed-failure contract that the API layer can map to consistent client-visible responses.
- Detection stays in the owning stories; C8 does not wait on those stories to define the contract.
- All profile failures short-circuit before DML once emitted by the owning story.
- Invalid writable profiles that exclude compiled semantic-identity members are still caught at profile validation time (C2), not at merge time.

### Testing

- Each of the six category shapes can be instantiated with representative diagnostic detail.
- Category-3 examples cover both forbidden member/value failures and duplicate visible collection-item collisions.
- Category-4 examples cover:
  - `ProfileRootCreateRejectedWhenNonCreatable`,
  - `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` for 1:1, nested/common-type, collection, extension scope, and extension collection item, and
  - the three-level parent-create-denied/child-denied chain.
- Type shapes for categories 5 (contract mismatch) and 6 (binding-accounting failure) are ready for backend stories DMS-1106 and DMS-1104 to emit without redefining them.

## Tasks

1. Define the typed failure contract with discriminated categories for the six error classes, including enough diagnostic detail for each to be actionable.
2. Provide constructors/factories or equivalent shared abstractions that consumer stories use when they emit categories 1–6.
3. Document emitter ownership for each category so consuming stories wire detection into the correct pipeline stage: category 1 in C2, category 2 in C5, category 3 in C3/C4, category 4 in C4, category 5 in DMS-1106, and category 6 in DMS-1104.
4. Add representative tests covering each category shape, including duplicate visible collection-item collisions for category 3 and the creatability violation scenarios from the shared profile scenario matrix for category 4.
