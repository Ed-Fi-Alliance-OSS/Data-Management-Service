# Story: Audit/Repair Tool for `dms.ReferenceEdge`

## Description

Provide an operational tool to audit and repair `dms.ReferenceEdge` integrity, addressing the highest-risk item called out in `reference/design/backend-redesign/strengths-risks.md`.

Modes:
- Targeted: rebuild edges for one document (by `DocumentUuid`/`DocumentId`).
- Full scan: audit all documents and optionally repair (maintenance-window use).

## Acceptance Criteria

- Tool can:
  - compute the expected edge set for a document from relational FK columns,
  - compare to existing `dms.ReferenceEdge`,
  - repair by replacing/diffing edges.
- Tool emits a clear report (counts + mismatches) and exits non-zero on detected drift when running in audit-only mode.
- Tool does not require authorization objects and does not rely on `dms.DocumentCache`.

## Tasks

1. Implement edge recomputation logic that enumerates all `..._DocumentId` FK columns for a resource’s tables.
2. Implement audit diff and repair write path (transactional).
3. Implement CLI/script surface for targeted and full-scan modes.
4. Add an integration test that:
   1. tampers `dms.ReferenceEdge`,
   2. runs repair,
   3. validates edges are restored.

