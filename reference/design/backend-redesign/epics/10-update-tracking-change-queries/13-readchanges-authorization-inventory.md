---
jira: Unassigned
---

# Story: Derive `ReadChanges` Authorization View Inventory

## Description

Derive `ReadChangesAuthorizationViewInfo` entries for the authorization views used by Change Query `/deletes` and `/keyChanges` endpoints.

The derived inventory describes the four `*IncludingDeletes` views as SQL-free model data, including view names, output columns, and ordered union arms over current tables and tracked-change tables. Dialect DDL emitters render the views from this inventory, and runtime authorization planners consume the same metadata.

## Acceptance Criteria

- The derived model includes `ReadChangesAuthorizationViewInfo` for:
  - `auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes`
  - `auth.EducationOrganizationIdToContactDocumentIdIncludingDeletes`
  - `auth.EducationOrganizationIdToStaffDocumentIdIncludingDeletes`
  - `auth.EducationOrganizationIdToStudentDocumentIdThroughDeletedResponsibility`
- Each view inventory records output columns using `DocumentId`-based people identifiers.
- Union arms cover current/current, current/tracked, tracked/current, and tracked/tracked association combinations where applicable.
- View derivation uses the current `auth.EducationOrganizationIdToEducationOrganizationId` hierarchy.
- The views are derived only when all five PrimaryAssociation resources are present in the derived relational model.
- The inventory uses tracked-change old-value columns for deleted or key-changed associations.
- Fixture coverage validates the expected union arms and column references.

## Dependencies

- `12-tracked-change-inventory.md`.
- Existing relationship authorization model and PrimaryAssociation resource detection.

## Out of Scope

- Rendering authorization view SQL.
- Runtime `ReadChanges` authorization filtering.
- Custom view-based authorization strategies.
