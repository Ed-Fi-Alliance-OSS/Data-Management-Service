# Story: Maintain `dms.ReferenceEdge` (Diff-Based, By-Construction)

## Description

Maintain `dms.ReferenceEdge` as a correctness-critical derived reverse index:

- Edges are derived structurally from the same FK column bindings used for writes (“by-construction”).
- Store one row per `(ParentDocumentId, ChildDocumentId)` with `IsIdentityComponent` aggregated by OR.
- Use diff-based upsert to minimize churn (no-op updates should write zero rows).

Align with `reference/design/backend-redesign/transactions-and-concurrency.md` and `reference/design/backend-redesign/data-model.md`.

## Acceptance Criteria

- For a write that does not change referenced children, `dms.ReferenceEdge` writes 0 rows (diff-based no-op).
- For a write that changes references, the edge set after commit matches exactly the set implied by FK columns stored in resource tables (descriptors excluded).
- `IsIdentityComponent` is `true` iff at least one reference site to that child contributes to identity.
- If edge maintenance fails, the write fails and the transaction rolls back.

## Tasks

1. Implement structural edge extraction from row buffers/column bindings (no ad-hoc edge construction).
2. Implement per-dialect diff-based maintenance (stage + insert missing + update flag + delete stale).
3. Add unit/integration tests verifying:
   1. no-op update writes 0 edge changes,
   2. reference changes reflect in edges,
   3. identity-component OR aggregation behavior.

