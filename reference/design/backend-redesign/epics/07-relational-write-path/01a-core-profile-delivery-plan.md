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
  - stored-state projection for profiled writes,
  - `ScopeInstanceAddress` and `CollectionRowAddress` derivation,
  - creatability analysis,
  - readable projection after full reconstitution, and
  - typed profile error classification.
- The plan defines the shared compiled-scope metadata, adapter, or equivalent contract Core will use to derive request-side and stored-side addresses from compiled scope metadata plus JSON data.
- The plan produces a concrete follow-on story inventory and sequencing for the Core work needed by the blocked backend/read-path stories above.
- The dependency map and affected backend/read-path stories show this spike as a hard prerequisite instead of assuming the Core outputs already exist.

## Tasks

1. Break the Core-owned profile surface into concrete implementation slices across request shaping, stored-state projection, readable projection, address derivation, creatability, and error classification.
2. Define the shared compiled-scope metadata/adapter Core needs so request-side and stored-side address derivation align with backend compiled plans.
3. Produce the follow-on story inventory and recommended sequencing for the Core work required by profiled writes and readable-profile projection.
4. Update the dependency notes in the blocked backend/read-path stories so they point to this spike explicitly.
