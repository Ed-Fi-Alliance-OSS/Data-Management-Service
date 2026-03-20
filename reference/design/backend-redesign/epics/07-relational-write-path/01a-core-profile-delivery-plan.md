---
jira: DMS-1110
jira_url: https://edfi.atlassian.net/browse/DMS-1110
---

# Story: Core Profile Support Delivery Plan Spike

## Description

Produce the Core-owned delivery plan that makes backend profile support implementation-ready.

This spike is the planning prerequisite for backend stories that consume Core profile outputs:

- `reference/design/backend-redesign/epics/07-relational-write-path/01b-profile-write-context.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/01c-current-document-for-profile-projection.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/05b-profile-error-classification.md`
- `reference/design/backend-redesign/epics/08-relational-read-path/01-json-reconstitution.md`

The spike turns the ownership statements in `reference/design/backend-redesign/design-docs/profiles.md` into a concrete Core-side implementation plan and follow-on story inventory. It is design/planning work only; runtime implementation lands in the follow-on stories created from this plan.

## Acceptance Criteria

- A Core-owned profile support delivery plan exists and is explicitly aligned to `reference/design/backend-redesign/design-docs/profiles.md`.
- The plan explicitly covers:
  - profile metadata loading and validation,
  - readable vs writable profile selection,
  - recursive request shaping and writable validation,
  - duplicate visible collection-item validation by compiled semantic identity within a stable parent scope,
  - stored-state projection for profiled writes,
  - `ScopeInstanceAddress` and `CollectionRowAddress` derivation,
  - creatability analysis,
  - readable projection after full reconstitution, and
  - typed profile error classification.
- The plan defines the shared mapping-set-scoped compiled-scope catalog, adapter, or equivalent contract Core will use to derive request-side and stored-side addresses from compiled scope metadata plus JSON data without depending directly on backend `TableWritePlan` / `CollectionMergePlan` types.
- The plan specifies the minimum contract surface for that adapter, including `JsonScope`, scope kind, immediate parent scope, collection ancestors, semantic identity relative paths in compiled order, and the canonical scope-relative member-path vocabulary used for `SemanticIdentityPart.RelativePath` and `HiddenMemberPaths`.
- The plan states that Core emits `HiddenMemberPaths` in that canonical vocabulary, while backend resolves those paths to physical bindings through its own compiled write metadata.
- The plan produces a concrete follow-on story inventory and sequencing for the Core work needed by the blocked backend/read-path stories above.
- The dependency map and affected backend/read-path stories show this spike as a hard prerequisite instead of assuming the Core outputs already exist.

## Deliverables

The delivery plan document and follow-on story files are:

- **Delivery plan:** `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`
- **C1:** `01a-c1-compiled-scope-adapter-and-address-derivation.md` — Shared Compiled-Scope Adapter Contract + Address Derivation Engine (Tier 0)
- **C2:** `01a-c2-semantic-identity-compatibility-validation.md` — Semantic Identity Compatibility Validation (Tier 1)
- **C3:** `01a-c3-request-visibility-and-writable-shaping.md` — Request-Side Visibility Classification + Writable Request Shaping (Tier 1)
- **C4:** `01a-c4-request-creatability-and-collection-validation.md` — Request-Side Creatability Analysis + Duplicate Collection-Item Validation (Tier 2)
- **C5:** `01a-c5-assemble-profile-applied-write-request.md` — Orchestrate Profile Write Pipeline + Assemble ProfileAppliedWriteRequest (Tier 2)
- **C6:** `01a-c6-stored-state-projection-and-hidden-member-paths.md` — Stored-State Projection + HiddenMemberPaths Computation (Tier 3)
- **C7:** `01a-c7-readable-profile-projection.md` — Readable Profile Projection After Reconstitution (Tier 0 — independent, no C-story dependencies)
- **C8:** `01a-c8-typed-profile-error-classification.md` — Typed Profile Error Classification (Tier 3)

## Tasks

1. Break the Core-owned profile surface into concrete implementation slices across request shaping, stored-state projection, readable projection, address derivation, duplicate visible collection-item validation by compiled semantic identity, creatability, and error classification.
2. Define the shared compiled-scope metadata/adapter Core needs so request-side and stored-side address derivation, canonical member-path emission, and `HiddenMemberPaths` generation align with backend compiled plans without exposing backend runtime plan internals directly.
3. Produce the follow-on story inventory and recommended sequencing for the Core work required by profiled writes and readable-profile projection.
4. Update the dependency notes in the blocked backend/read-path stories so they point to this spike explicitly.
