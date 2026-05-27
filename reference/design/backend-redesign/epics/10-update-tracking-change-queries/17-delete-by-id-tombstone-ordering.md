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
- A resource delete with cascaded child, nested-child, or `_ext` rows produces exactly one visible root tombstone in the appropriate `tracked_changes_*` table.
- The root tombstone's `ChangeVersion` is the final delete ChangeVersion exposed to Change Queries; no cascaded child or `_ext` trigger activity can leave a later visible root stamp or tracked-change row that advances an extraction watermark past the tombstone.
- Delete conflict behavior and diagnostics remain consistent with the delete-path epic.
- PostgreSQL and SQL Server integration tests cover regular resources, descriptors, at least one cascading-delete scenario for an abstract-resource family, at least one child-bearing resource delete, and at least one extension-bearing resource delete.

## Out of Scope

- Supporting direct `DELETE FROM dms.Document` outside the DMS write path as a Change Query tombstone-producing operation.
- Reworking delete conflict ProblemDetails.
