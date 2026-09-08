---
jira: DMS-1015
jira_url: https://edfi.atlassian.net/browse/DMS-1015
---

# Story: Audit/Repair Tool for `dms.ReferentialIdentity`

## Description

Provide an operational tool to audit and repair `dms.ReferentialIdentity` integrity, addressing a high-correctness-risk item called out in `reference/design/backend-redesign/design-docs/strengths-risks.md`.

Modes:
- Targeted: rebuild `ReferentialId`s for one document (by `DocumentUuid`/`DocumentId`).
- Full scan: audit all documents and optionally repair (maintenance-window use).

## Acceptance Criteria

- Tool can:
  - compute expected referential ids for a document from relational source-of-truth (identity projection),
  - compare to existing `dms.ReferentialIdentity` rows (primary + alias rows),
  - repair by replacing incorrect/missing rows transactionally.
- Tool emits a clear report (counts + mismatches) and exits non-zero on detected drift when running in audit-only mode.
- Tool does not require authorization objects and does not rely on `dms.DocumentCache`.

## Tasks

1. Implement referential-id recomputation for a document using the compiled mapping set (same UUIDv5 algorithm + identity projection rules as Core).
2. Implement audit diff and repair write path (transactional replace of `dms.ReferentialIdentity` rows for the target `DocumentId`).
3. Implement CLI/script surface for targeted and full-scan modes.
4. Add an integration test that:
   1. tampers `dms.ReferentialIdentity`,
   2. runs repair,
   3. validates mappings are restored.
