---
jira: TBD
---

# Story: Typed Profile Error Classification

## Description

Define and implement typed profile error categories that distinguish invalid profile definitions, invalid usage, writable validation failures, creatability violations, Core/backend contract mismatches, and binding-accounting failures.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibility #15
- `reference/design/backend-redesign/design-docs/profiles.md` §"Validation and Error Semantics"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on:
- C3 (`01a-c3-request-visibility-and-writable-shaping.md`) — produces writable validation failures
- C4 (`01a-c4-request-creatability-and-collection-validation.md`) — produces creatability violations and duplicate rejections

**Core responsibility coverage:** #15 (structured error classification)

This story unblocks `DMS-1104` (`reference/design/backend-redesign/epics/07-relational-write-path/05b-profile-error-classification.md`), which classifies and maps these failures to DMS error shapes.

## Acceptance Criteria

### Error Categories

The typed failure contract must distinguish:

1. **Invalid profile definition** — writable profile hides compiled semantic-identity fields for persisted multi-item collections (from C2), or other structural profile errors detected before runtime.
2. **Invalid profile usage** — wrong profile mode for the operation, profile not found for the resource, or other usage-level errors.
3. **Writable-profile validation failure** — submitted data forbidden by the writable profile: members not in the profile surface, collection items failing value filters.
4. **Creatability violation** — new visible instance would be created but required members are hidden by the profile. Covers root resource, 1:1 scope, nested/common-type scope, collection item, extension scope, and extension collection item.
5. **Core/backend contract mismatch** — Core-emitted `JsonScope` does not map to a compiled scope, ancestor chain does not match compiled collection ancestry, or stored-side visibility metadata cannot be lined up to the compiled plan shape.
6. **Binding-accounting failure** — a profiled binding cannot be classified as visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed.

### Behavior

- Each error category produces a deterministic typed failure that the API layer can map to a consistent client-visible response.
- All profile failures short-circuit before DML proceeds.
- Matched visible scope/item updates are not misclassified as creatability failures when hidden required members exist but the stored visible instance already supplies them.
- Invalid writable profiles that exclude compiled semantic-identity members are caught at profile validation time (C2), not at merge time.

### Testing

- Each of the six error categories produces the correct typed failure.
- Creatability violations include:
  - `ProfileRootCreateRejectedWhenNonCreatable`,
  - `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` for 1:1, nested/common-type, collection, extension scope, and extension collection item, and
  - the three-level parent-create-denied/child-denied chain.
- Matched visible scope/item update that remains allowed under the same profile is not misclassified.
- At least one contract-mismatch case (unknown `JsonScope`, ancestor-chain mismatch, or unalignable stored-side visibility).
- At least one binding-accounting failure case.

## Tasks

1. Define the typed failure contract with discriminated categories for the six error classes, including enough diagnostic detail for each to be actionable.
2. Integrate error production into the C2 (compatibility validation), C3 (request shaping/validation), and C4 (creatability/duplicate validation) pipelines.
3. Define the contract-mismatch and binding-accounting failure shapes for backend to emit when Core-emitted addresses or member paths do not match compiled metadata.
4. Add tests covering each error category, the creatability violation scenarios from the shared profile scenario matrix, and the matched-visible-update-allowed case.
