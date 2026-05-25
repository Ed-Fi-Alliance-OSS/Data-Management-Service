---
jira: DMS-1188
jira_url: https://edfi.atlassian.net/browse/DMS-1188
---

# Story: Apply `ReadChanges` Authorization to Change Query Endpoints

## Description

Apply `ReadChanges` authorization to `/deletes` and `/keyChanges`.

DMS supports the same ODS authorization strategies for Change Query endpoints except custom view-based strategies, which are deferred. Strategies with names shared by live `Read` authorization reuse existing authorization views. Strategies with `*IncludingDeletes` use the corresponding `ReadChanges` authorization views.

The authorization composition rules from `auth.md` apply unchanged.

## Acceptance Criteria

- `/deletes` and `/keyChanges` require the `ReadChanges` action.
- Missing `ReadChanges` claims return `403 Forbidden` with the authorization ProblemDetails defined in `auth.md`.
- `NoFurtherAuthorizationRequired` works for `ReadChanges`.
- `NamespaceBased` works for descriptor exceptions such as `CrisisTypeDescriptor` and `NonMedicalImmunizationExemptionDescriptor`.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` work against tracked-change old-value columns where appropriate.
- `RelationshipsWithEdOrgsAndPeopleIncludingDeletes` uses the `EducationOrganizationIdToStudentDocumentIdIncludingDeletes`, `EducationOrganizationIdToContactDocumentIdIncludingDeletes`, or `EducationOrganizationIdToStaffDocumentIdIncludingDeletes` view as appropriate.
- `RelationshipsWithStudentsOnlyIncludingDeletes` uses the student including-deletes view.
- `RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes` uses `EducationOrganizationIdToStudentDocumentIdThroughDeletedResponsibility`.
- People authorization predicates use denormalized old/new person `DocumentId` values from tracked-change tables rather than joining intermediate resources.
- Multiple authorization strategies compose with the semantics defined in `auth.md`.
- Authorization predicates apply before paging and totalCount.
- Custom view-based `ReadChanges` strategies fail as not implemented or unsupported according to the existing authorization failure conventions for deferred strategies.
- Tests cover `/deletes` and `/keyChanges` for namespace, EdOrg-only, EdOrg-and-people, students-only, and responsibility-through-deleted strategies.

## Dependencies

- `15-readchanges-authorization-view-ddl.md`.
- `23-deletes-endpoint.md`.
- `24-keychanges-endpoint.md`.
- Existing relational authorization strategy infrastructure.

## Out of Scope

- Custom view-based authorization strategies.
- Snapshot authorization behavior.
- Feature-disabled Change Query behavior.
