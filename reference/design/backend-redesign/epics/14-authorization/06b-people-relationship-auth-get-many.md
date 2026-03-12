---
jira: DMS-1095
jira_url: https://edfi.atlassian.net/browse/DMS-1095
---

# Story: Implement People-involved Relationship-based Authorization for GET-many

## Description

Extend the authorization subquery framework established in [DMS-1055](https://edfi.atlassian.net/browse/DMS-1055) with the relationship-based strategies that involve People securable elements, per:

- `reference/design/backend-redesign/design-docs/auth.md`

People-related securable elements (Student, Contact, Staff) require transitive joins through intermediate tables when the person reference is indirect (e.g., CourseTranscript -> StudentAcademicRecord -> Student), unlike EducationOrganization columns which are always directly on the resource table. This ticket adds that transitive-join resolution on top of the framework delivered by DMS-1055.

## Acceptance Criteria

- The following relationship-based strategies are implemented for GET-many:
  - RelationshipsWithEdOrgsAndPeople — includes EducationOrganization, Student, Contact, and Staff securable elements.
  - RelationshipsWithEdOrgsAndPeopleInverted — same as above with inverted EdOrg filtering (bottom-to-top).
  - RelationshipsWithPeopleOnly — includes only Student, Contact, and Staff securable elements.
  - RelationshipsWithStudentsOnly — includes only Student securable elements.
  - RelationshipsWithStudentsOnlyThroughResponsibility — includes only Student securable elements, using the EducationOrganizationIdToStudentDocumentIdThroughResponsibility auth view.
- GET-many results are filtered based on the configured strategy; unauthorized resources are never returned.
- People-related securable elements (Student, Contact, Staff) are resolved using DocumentId (not UniqueId/USI) by joining through intermediate tables when the person reference is transitive (e.g., CourseTranscript -> StudentAcademicRecord -> Student).
- All shared framework behavior from DMS-1055 (OR semantics, IN subquery approach, pagination, caching, TVP threshold) applies to the strategies implemented here.
- Works for both PostgreSQL and SQL Server.

NOTE: The GET-by-id, POST, PUT, and DELETE scenarios will be implemented in [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056).
