---
jira: DMS-1095
jira_url: https://edfi.atlassian.net/browse/DMS-1095
---

# Story: Implement People-involved Relationship-based Authorization for GET-many

## Description

Extend the GET-many authorization subquery framework established in [DMS-1055](https://edfi.atlassian.net/browse/DMS-1055) by consuming the People relationship authorization core established in [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056) Slice 5 (`07-relationship-auth-crud/05-people-relationship-auth-core.md`), per:

- `reference/design/backend-redesign/design-docs/auth.md`

DMS-1056 owns shared People strategy classification, Student/Contact/Staff DocumentId path resolution, auth-view selection, through-responsibility selection, hint metadata, and parameterization contracts. This story integrates that core into the GET-many page/count SQL path.

For the EducationOrganization portion of mixed EdOrg-and-People relationship strategies, DMS-1095 inherits DMS-1055's ODS-parity GET-many subject scope: only DMS concrete root-table EdOrg authorization subjects participate. Child-table EdOrg paths remain out of scope unless a later story explicitly introduces different DMS semantics.

## Dependencies

- Depends on [DMS-1055](https://edfi.atlassian.net/browse/DMS-1055) for the EdOrg-only GET-many framework.
- Depends on [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056) Slice 5 for the shared People relationship authorization core.

## Acceptance Criteria

- The following relationship-based strategies are implemented for GET-many:
  - RelationshipsWithEdOrgsAndPeople — includes EducationOrganization, Student, Contact, and Staff securable elements.
  - RelationshipsWithEdOrgsAndPeopleInverted — same as above with inverted EdOrg filtering (bottom-to-top).
  - RelationshipsWithPeopleOnly — includes only Student, Contact, and Staff securable elements.
  - RelationshipsWithStudentsOnly — includes only Student securable elements.
  - RelationshipsWithStudentsOnlyThroughResponsibility — includes only Student securable elements, using the EducationOrganizationIdToStudentDocumentIdThroughResponsibility auth view.
- GET-many results are filtered based on the configured strategy; unauthorized resources are never returned.
- People-related securable elements (Student, Contact, Staff) use the DocumentId path-resolution and auth-view metadata supplied by DMS-1056 Slice 5.
- All shared framework behavior from DMS-1055 (OR semantics, IN subquery approach, pagination, caching, TVP threshold) applies to the strategies implemented here.
- This story replaces the temporary DMS-1055 GET-many 501 Not Implemented behavior for People relationship strategies. When mixed with EdOrg-only relationship strategies, People relationship strategies are added to the relationship OR group instead of causing the unsupported mixed-strategy failure.
- Works for both PostgreSQL and SQL Server.

NOTE: People-involved GET-by-id, POST, PUT, and DELETE endpoint execution remains out of this story and is expected to consume the same People core in follow-on People CRUD work.
