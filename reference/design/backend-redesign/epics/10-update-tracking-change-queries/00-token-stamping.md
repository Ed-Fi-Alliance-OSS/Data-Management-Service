# Story: Stamp `ContentVersion/IdentityVersion` (Global Sequence) + No-Op Detection

## Description

Implement write-side stamping per `reference/design/backend-redesign/update-tracking.md`:

- Allocate one global stamp per write operation (from `dms.ChangeVersionSequence`).
- Apply the stamp to:
  - `ContentVersion/ContentLastModifiedAt` when persisted content changes,
  - `IdentityVersion/IdentityLastModifiedAt` when identity projection changes,
  - both when both change (reuse the same stamp).
- Do not allocate a stamp for no-op writes.

## Acceptance Criteria

- Content-only changes update `ContentVersion` and `ContentLastModifiedAt`.
- Identity-only changes update `IdentityVersion` and `IdentityLastModifiedAt`.
- Combined changes reuse a single allocated stamp for both tokens.
- No-op writes do not allocate a new stamp and do not update token columns.

## Tasks

1. Implement a stamp allocator that reads `NEXT VALUE FOR dms.ChangeVersionSequence` (dialect-specific).
2. Integrate stamping with write execution and identity change detection.
3. Ensure stamping is applied consistently during identity closure recompute (impacted documents that truly change identity).
4. Add unit/integration tests for:
   1. content-only, identity-only, both, and no-op scenarios.

