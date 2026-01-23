---
jira: DMS-1006
jira_url: https://edfi.atlassian.net/browse/DMS-1006
---

# Story: Change Query Candidate Selection (Journal-Driven)

## Description

Implement the backend selection algorithm for Change Queries using:

- `dms.DocumentChangeEvent` (representation changes),
- and “journal + verify” against `dms.Document.ContentVersion`,

per the guidance in `reference/design/backend-redesign/design-docs/update-tracking.md`.

This story focuses on the data selection mechanics and paging, not on HTTP surface area.

## Acceptance Criteria

- Given a `(minChangeVersion, maxChangeVersion)` window, the selection returns the correct set of impacted documents (best-effort minimization allowed).
- Selection is deterministic and pageable.
- Integration tests validate:
  - local content changes appear in the window,
  - upstream identity changes cause dependents to appear in the window (because FK-cascade updates materialize as local changes).

## Tasks

1. Implement SQL for selecting local changes from `dms.DocumentChangeEvent` by resource and window.
2. Implement verification against current `dms.Document.ContentVersion` (filter stale journal rows).
3. Implement stable paging and deterministic ordering for returned candidates.
4. Add integration tests for change selection scenarios.
