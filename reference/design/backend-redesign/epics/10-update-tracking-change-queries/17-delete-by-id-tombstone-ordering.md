---
jira: DMS-1180
jira_url: https://edfi.atlassian.net/browse/DMS-1180
---

# Story: Delete Concrete Rows Before `dms.Document`

## Description

Change the delete-by-id relational write path so a document deletion issues two ordered statements inside the same transaction:

1. Delete the concrete resource row, or the `dms.Descriptor` row for descriptor resources.
2. Delete the corresponding `dms.Document` row.

The first delete fires the resource or descriptor stamping trigger while `dms.Document` still exists. That lets the trigger bump `ContentVersion`, read `DocumentUuid`, and insert a tombstone with the correct `ChangeVersion`. Deleting `dms.Document` first would cascade the resource row before the trigger could read the document row, silently losing the tombstone.

## Acceptance Criteria

- Delete-by-id for regular resources deletes the concrete resource root row before deleting `dms.Document`.
- Delete-by-id for descriptor resources deletes the `dms.Descriptor` row before deleting `dms.Document`.
- Both statements execute in the same transaction.
- The existing `ON DELETE CASCADE` FK from resource rows to `dms.Document` remains as a referential-integrity safety net.
- A successful resource delete inserts a tracked-change tombstone with the bumped `ContentVersion`.
- A successful descriptor delete inserts a descriptor tombstone with the bumped `ContentVersion`.
- The mirror update inside the delete trigger is allowed to affect zero rows because the deleted concrete row is already gone.
- Delete conflict behavior and diagnostics remain consistent with the delete-path epic.
- Integration tests cover regular resources, descriptors, and at least one cascading-delete scenario for an abstract-resource family.

## Dependencies

- Existing delete-by-id story in `../11-delete-path/00-delete-by-id.md`.
- `16-tracked-change-trigger-rendering.md`.

## Out of Scope

- Supporting direct `DELETE FROM dms.Document` outside the DMS write path as a Change Query tombstone-producing operation.
- Reworking delete conflict ProblemDetails.
