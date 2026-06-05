---
jira: DMS-1178
jira_url: https://edfi.atlassian.net/browse/DMS-1178
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
  - `auth.EducationOrganizationIdToStudentDocumentIdDeletedResponsibility`
- Each view uses the union arms recorded in inventory without re-deriving table paths inside the dialect emitter.
- Each emitted view combines current-association and tracked-change arms with SQL `UNION`, not `UNION ALL`.
- `UNION` is required so duplicate authorization pairs produced by current association arms and tracked-change arms are eliminated before runtime authorization predicates consume the view.
- Views join against the current `auth.EducationOrganizationIdToEducationOrganizationId` hierarchy.
- Views reference current association tables and tracked-change tables as specified by inventory.
- Views are not emitted when the PrimaryAssociation resource guard fails.
- DDL manifests include the emitted authorization views.
- PostgreSQL and SQL Server fixture tests validate the rendered view shape, including use of `UNION` and absence of `UNION ALL`.

## Out of Scope

- Runtime authorization predicate generation.
- Non-Change Query relationship authorization views.
- Custom view-based authorization strategies.
