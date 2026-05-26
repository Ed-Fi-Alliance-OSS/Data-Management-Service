---
jira: DMS-1164
jira_url: https://edfi.atlassian.net/browse/DMS-1164
---

# Slice 5: Implement People Relationship Auth Core

## Description

Create the shared People-involved relationship authorization core used by both:

- `DMS-1095`: People-involved Relationship-based Authorization for GET-many
- `DMS-1158`: People Relationship CRUD

This story does not authorize any endpoint by itself. It provides reusable strategy classification, securable subject resolution, auth view selection, SQL predicate/check building inputs, and failure-hint metadata for People-involved relationship strategies.

## Strategies In Scope

- `RelationshipsWithEdOrgsAndPeople`
- `RelationshipsWithEdOrgsAndPeopleInverted`
- `RelationshipsWithPeopleOnly`
- `RelationshipsWithStudentsOnly`
- `RelationshipsWithStudentsOnlyThroughResponsibility`

## Acceptance Criteria

- The core classifies the five People-involved relationship strategies as known People relationship strategies instead of treating them as unsupported or unknown metadata.
- The core determines participating securable element kinds per strategy:
  - `RelationshipsWithEdOrgsAndPeople`: EducationOrganization, Student, Contact, Staff.
  - `RelationshipsWithEdOrgsAndPeopleInverted`: EducationOrganization, Student, Contact, Staff, with inverted EdOrg hierarchy filtering.
  - `RelationshipsWithPeopleOnly`: Student, Contact, Staff.
  - `RelationshipsWithStudentsOnly`: Student only.
  - `RelationshipsWithStudentsOnlyThroughResponsibility`: Student only, using the responsibility-based student auth view.
- Person securable elements resolve to DocumentId-based authorization subjects, not UniqueId/USI values.
- Direct person references resolve to the person DocumentId column on the subject resource table.
- Transitive person references resolve to an ordered join path through intermediate resource tables, using `ResolveSecurableElementColumnPath(subjectResourceFullName, securableElement)`.
- The core selects the correct auth view/table per person subject:
  - Student: `auth.EducationOrganizationIdToStudentDocumentId`
  - Contact: `auth.EducationOrganizationIdToContactDocumentId`
  - Staff: `auth.EducationOrganizationIdToStaffDocumentId`
  - Student through responsibility: `auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility`
- For strategies that include EducationOrganization subjects, the core reuses the EdOrg subject-resolution behavior established by DMS-1055/DMS-1056 rather than introducing child-table EdOrg predicates.
- Inverted strategy behavior is explicit: EdOrg hierarchy predicates swap Source/Target filtering; person auth view semantics remain the same unless a later design change says otherwise.
- The core exposes operation-neutral auth specs that downstream stories can consume for:
  - GET-many page/count filtering in `DMS-1095`.
  - GET-by-id, POST, PUT, and DELETE checks in `DMS-1158`.
- The core preserves relationship OR composition metadata so multiple relationship strategies can be combined by consuming stories without losing strategy identity or index ordering.
- The core provides failure-hint metadata for each auth view per `auth.md`:
  - StudentSchoolAssociation hint.
  - StudentContactAssociation hint.
  - Staff employment/assignment hint.
  - StudentEducationOrganizationResponsibilityAssociation hint.
- If a configured People relationship strategy produces no applicable authorization subjects, the core returns a security-configuration failure with resource, strategy, and securable element details.
- Unit tests cover strategy classification, subject-kind selection, auth view selection, inverted EdOrg behavior, transitive person path handling, responsibility-based student handling, and no-subject security failures.

## Out of Scope

- No GET-many filtering behavior.
- No GET-by-id, POST, PUT, or DELETE authorization execution.
- No endpoint ProblemDetails mapping.
- No database execution or roundtrip batching.
- No new auth views or DDL emission.

## Reviewer Focus

Reviewers should focus on whether People authorization subjects are represented as reusable DocumentId-based specs that GET-many and CRUD consumers can share without re-resolving strategy-specific behavior.
