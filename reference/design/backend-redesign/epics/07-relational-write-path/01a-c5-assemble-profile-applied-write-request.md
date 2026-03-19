---
jira: TBD
---

# Story: Assemble ProfileAppliedWriteRequest

## Description

Thin integration story: compose the outputs of C3 (request-side visibility/shaping) and C4 (creatability/collection validation) into the complete `ProfileAppliedWriteRequest` contract.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Minimum Core Write Contract"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on:
- C3 (`01a-c3-request-visibility-and-writable-shaping.md`) — `WritableRequestBody`, `RequestScopeStates` (without creatability)
- C4 (`01a-c4-request-creatability-and-collection-validation.md`) — `RootResourceCreatable`, creatability flags, `VisibleRequestCollectionItems`

**Core responsibility coverage:** #7 (writable request shaping — final assembly)

This story produces the `ProfileAppliedWriteRequest` that backend consumes in `E07-S01b` (DMS-1103). It also feeds into C6 for `ProfileAppliedWriteContext` assembly.

## Acceptance Criteria

- `ProfileAppliedWriteRequest` is assembled with:
  - `WritableRequestBody` from C3,
  - `RootResourceCreatable` from C4,
  - `RequestScopeStates` from C3 with `Creatable` flags populated by C4, and
  - `VisibleRequestCollectionItems` from C4.
- When no writable profile applies, no `ProfileAppliedWriteRequest` is produced (backend treats all scopes as visible). See "No-Profile Passthrough Path" in the delivery plan: the absence of a profile contract bypasses the entire profile write state machine — creatability analysis, hidden-member preservation, and binding-accounting are all skipped. Backend does NOT produce a degenerate "all visible" contract.
- The assembled contract is semantically equivalent to the shape defined in `profiles.md` §"Minimum Core Write Contract".
- Integration test: given a writable profile definition + compiled-scope adapter + request JSON, the full assembly pipeline (C3 → C4 → C5) produces the correct composite `ProfileAppliedWriteRequest`.
- The no-profile path is tested: absence of a writable profile produces no request contract.

## Tasks

1. Implement the assembly function that takes C3 outputs (`WritableRequestBody`, `RequestScopeStates`) and C4 outputs (`RootResourceCreatable`, creatability flags, `VisibleRequestCollectionItems`) and produces `ProfileAppliedWriteRequest`.
2. Implement the no-profile path that produces no request contract.
3. Add integration tests proving full assembly from profile + adapter + request JSON, and the no-profile path.
