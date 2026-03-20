---
jira: DMS-1114
jira_url: https://edfi.atlassian.net/browse/DMS-1114
---

# Story: Semantic Identity Compatibility Validation

## Description

Implement the Core-owned pre-runtime gate that rejects writable profile definitions hiding compiled semantic-identity fields for persisted multi-item collection scopes.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibility #12
- `reference/design/backend-redesign/design-docs/profiles.md` §"Minimum Core Write Contract" (normative requirement: Core MUST reject such profiles)

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on:
- C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — consumes adapter contract for `SemanticIdentityRelativePathsInOrder`
- C8 (`01a-c8-typed-profile-error-classification.md`) — supplies the shared typed error contract for category-1 invalid-profile-definition failures

**Core responsibility coverage:** #12 (semantic identity compatibility validation)

This is a pre-runtime validation gate. Backend write stories assume that writable profiles reaching runtime merge execution have already passed this check. Invalid profiles that hide compiled semantic-identity members must be rejected before request-time persistence logic runs.

## Acceptance Criteria

- Writable profile definitions that exclude a field required to compute the compiled semantic identity of a persisted multi-item collection scope are rejected with a C8 category-1 structured error.
- The validation uses `SemanticIdentityRelativePathsInOrder` from the compiled-scope adapter to determine which fields are required for semantic identity.
- Valid writable profiles that expose all semantic-identity fields pass validation.
- Writable profiles on single-item or non-persisted scopes are not incorrectly rejected by this gate.
- The validation is performed before request-time merge execution, not at merge time.
- The structured error identifies:
  - the affected collection scope (`JsonScope`),
  - the hidden semantic-identity field(s), and
  - the writable profile that caused the incompatibility.
- Unit tests cover:
  - a valid profile that exposes all semantic-identity fields passes,
  - a profile hiding one semantic-identity field on a persisted multi-item collection fails,
  - a profile hiding a non-identity field on a collection scope passes,
  - a profile on a single-item scope is not subject to this gate.

## Tasks

1. Implement the compatibility check: for each persisted multi-item collection scope in the compiled adapter, verify that the writable profile exposes all `SemanticIdentityRelativePathsInOrder` members.
2. Produce a C8 category-1 structured error that identifies the incompatible scope, hidden identity fields, and profile source.
3. Integrate the check into the profile validation path so it runs before runtime merge execution.
4. Add tests proving valid profiles pass, identity-hiding profiles fail, and non-identity field hiding is allowed.
