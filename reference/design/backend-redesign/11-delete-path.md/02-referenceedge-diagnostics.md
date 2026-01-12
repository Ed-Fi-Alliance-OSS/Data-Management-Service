# Story: Provide “Who References Me?” Diagnostics via `dms.ReferenceEdge`

## Description

Provide a consistent diagnostic query (and backend helper) to list referencing documents/resources for a given `ChildDocumentId`, per `reference/design/backend-redesign/transactions-and-concurrency.md`.

This supports delete conflict responses and operational tooling.

## Acceptance Criteria

- Diagnostic query returns:
  - referencing `DocumentUuid`,
  - referencing `(ProjectName, ResourceName)` via `dms.ResourceKey`,
  - deterministic ordering for readability.
- Query is efficient (uses the `IX_ReferenceEdge_ChildDocumentId` index) and returns distinct results.

## Tasks

1. Implement the diagnostic query using `dms.ReferenceEdge` joined to `dms.Document` and `dms.ResourceKey`.
2. Integrate the diagnostic output into delete conflict handling.
3. Add integration tests that:
   1. create a parent referencing a child,
   2. attempt delete child,
   3. assert conflict output includes the parent resource.

