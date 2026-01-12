# Story: Reconstitute JSON from Hydrated Rows (Including `_ext`)

## Description

Implement JSON reconstitution from hydrated relational rows:

- Rebuild the JSON structure for the resource using the compiled reconstitution plan.
- Preserve array ordering using `Ordinal` columns.
- Overlay `_ext` subtrees from extension tables, emitting `_ext` only when values exist.

## Acceptance Criteria

- Reconstituted JSON matches the expected shape for:
  - scalar properties,
  - inlined objects,
  - collections and nested collections (order preserved),
  - `_ext` at root and within collection elements.
- Reconstitution does not emit `_ext` when there are no extension values.
- Unit/integration tests validate reconstitution for at least one “nested + ext” fixture.

## Tasks

1. Implement a reconstitution engine that consumes hydrated rows and writes JSON deterministically.
2. Implement collection assembly using `Ordinal` ordering and scope keys.
3. Implement `_ext` overlay rules per `reference/design/backend-redesign/extensions.md`.
4. Add tests validating end-to-end “write then read” JSON equivalence for representative fixtures.

