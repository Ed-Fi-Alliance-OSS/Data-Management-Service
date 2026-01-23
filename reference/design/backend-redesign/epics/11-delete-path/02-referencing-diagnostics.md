---
jira: DMS-1012
jira_url: https://edfi.atlassian.net/browse/DMS-1012
---

# Story: Provide “Who References Me?” Diagnostics Without a Reverse-Edge Table

## Description

Provide a consistent diagnostic query (and backend helper) to list referencing documents/resources for a given foreign key violation without relying on a runtime-maintained reverse-edge table.

This supports delete conflict responses and operational tooling.

## Acceptance Criteria

- Given a violated FK constraint, diagnostics return:
  - referencing `DocumentUuid`,
  - referencing `(ProjectName, ResourceName)` via `dms.ResourceKey`,
  - deterministic ordering for readability.
- Diagnostics do not require scanning every table; they target the referencing table implied by the violated constraint name.

## Tasks

1. Implement deterministic parsing of violated FK constraint names to identify the referencing table + FK column.
2. Implement a diagnostic query that selects referencing `DocumentId`s from that table (by `FkColumn = @DeletedDocumentId`) and joins to `dms.Document`/`dms.ResourceKey`.
3. Integrate the diagnostic output into delete conflict handling.
4. Add integration tests that:
   1. create a parent referencing a child,
   2. attempt delete child,
   3. assert conflict output includes the parent resource.
