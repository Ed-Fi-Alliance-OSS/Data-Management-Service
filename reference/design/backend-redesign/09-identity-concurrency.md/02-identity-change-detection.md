# Story: Detect Identity Projection Changes Reliably

## Description

Detect whether a write changes the document’s identity projection values, so that:

- `dms.ReferentialIdentity` is updated only when necessary,
- identity closure recompute runs only when necessary,
- and `IdentityVersion/IdentityLastModifiedAt` are stamped only on actual identity projection changes.

Identity projection includes scalar identity parts and identity components sourced from references (via FK `..._DocumentId` columns).

## Acceptance Criteria

- No-op writes (no content change and no identity projection change) do not:
  - allocate new stamps,
  - update `dms.ReferentialIdentity`,
  - or run closure recompute.
- Identity changes are detected when:
  - scalar identity values change, or
  - identity-component reference targets change.
- Tests cover both false positives (avoid) and false negatives (disallowed).

## Tasks

1. Implement identity projection computation for “new” values (from the flattened write model).
2. Implement retrieval/computation of “old” values needed for comparison (from current stored rows/indexes).
3. Implement deterministic comparison logic and surface “identity changed” as a first-class outcome.
4. Add unit tests for identity change detection scenarios (scalar + reference-sourced).

