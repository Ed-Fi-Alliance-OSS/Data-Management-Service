# Story: Change Query Candidate Selection (Journal-Driven)

## Description

Implement the backend selection algorithm for Change Queries using:

- `dms.DocumentChangeEvent` (local changes),
- `dms.IdentityChangeEvent` (identity changes),
- `dms.ReferenceEdge` (reverse lookups to expand impacted parents),

per the guidance in `reference/design/backend-redesign/update-tracking.md`.

This story focuses on the data selection mechanics and paging, not on HTTP surface area.

## Acceptance Criteria

- Given a `(minChangeVersion, maxChangeVersion)` window, the selection returns the correct set of impacted documents (best-effort minimization allowed).
- Selection is deterministic and pageable.
- Reverse expansion uses `dms.ReferenceEdge` to find documents impacted by dependency identity changes.
- Integration tests validate:
  - local content changes appear in the window,
  - upstream identity changes cause dependents to appear in the window.

## Tasks

1. Implement SQL for selecting local changes from `dms.DocumentChangeEvent` by resource and window.
2. Implement reverse expansion using `dms.IdentityChangeEvent` + `dms.ReferenceEdge(Child → Parent)`.
3. Implement stable paging and deterministic ordering for returned candidates.
4. Add integration tests for change selection scenarios.

