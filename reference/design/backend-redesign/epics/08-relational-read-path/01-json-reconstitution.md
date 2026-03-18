---
jira: DMS-990
jira_url: https://edfi.atlassian.net/browse/DMS-990
---

# Story: Reconstitute JSON from Hydrated Rows (Including `_ext`)

## Description

Implement JSON reconstitution from hydrated relational rows:

- Dependency note: readable profile projection in this story is hard-blocked on `reference/design/backend-redesign/epics/07-relational-write-path/01a-core-profile-delivery-plan.md`, which plans the Core-owned readable-profile output this story invokes.
- Rebuild the JSON structure for the resource using the compiled reconstitution plan.
- Preserve array ordering using `Ordinal` columns.
- Attach nested collections and scope-aligned extension rows using stable parent row identity.
- Overlay `_ext` subtrees from extension tables, emitting `_ext` only when values exist.
- When readable profile semantics apply, pass the full reconstituted document through the Core-owned readable projector instead of filtering in backend.

## Acceptance Criteria

- Reconstituted JSON matches the expected shape for:
  - scalar properties,
  - inlined objects,
  - collections and nested collections (order preserved),
  - `_ext` at root and within collection elements.
- Reconstitution does not emit `_ext` when there are no extension values.
- Backend does not reimplement readable profile filtering; readable profiles are applied by Core after full reconstitution using the Core-owned contract planned by `01a-core-profile-delivery-plan.md`.
- Unit/integration tests validate reconstitution for at least one “nested + ext” fixture.

## Tasks

1. Implement a reconstitution engine that consumes hydrated rows and writes JSON deterministically.
2. Implement collection assembly using stable parent row identity plus `Ordinal` ordering.
3. Implement `_ext` overlay rules per `reference/design/backend-redesign/design-docs/extensions.md`.
4. Invoke the Core-owned readable profile projector after full reconstitution when a readable profile applies; backend does not introduce a parallel backend-owned filtering path.
5. Add tests validating end-to-end “write then read” JSON equivalence for representative fixtures.
