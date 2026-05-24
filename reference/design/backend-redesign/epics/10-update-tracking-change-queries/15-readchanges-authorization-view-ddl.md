---
jira: Unassigned
---

# Story: Emit `ReadChanges` Authorization Views

## Description

Render the four `*IncludingDeletes` authorization views from `ReadChangesAuthorizationViewInfo`.

These views preserve ODS Change Query authorization semantics while adapting the backing identifiers to DMS `DocumentId`-based people resources. They are used only by `ReadChanges` authorization for `/deletes` and `/keyChanges`.

## Acceptance Criteria

- PostgreSQL and SQL Server DDL emit the derived `ReadChangesAuthorizationViewInfo` entries.
- The emitted views are:
  - `auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes`
  - `auth.EducationOrganizationIdToContactDocumentIdIncludingDeletes`
  - `auth.EducationOrganizationIdToStaffDocumentIdIncludingDeletes`
  - `auth.EducationOrganizationIdToStudentDocumentIdThroughDeletedResponsibility`
- Each view uses the union arms recorded in inventory without re-deriving table paths inside the dialect emitter.
- Views join against the current `auth.EducationOrganizationIdToEducationOrganizationId` hierarchy.
- Views reference current association tables and tracked-change tables as specified by inventory.
- Views are not emitted when the PrimaryAssociation resource guard fails.
- DDL manifests include the emitted authorization views.
- PostgreSQL and SQL Server fixture tests validate the rendered view shape.

## Dependencies

- `13-readchanges-authorization-inventory.md`.
- `14-tracked-change-table-ddl.md`.

## Out of Scope

- Runtime authorization predicate generation.
- Non-Change Query relationship authorization views.
- Custom view-based authorization strategies.
