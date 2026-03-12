---
jira: DMS-1050
jira_url: https://edfi.atlassian.net/browse/DMS-1050
---

# Story: Emit People Auth Views

## Description

Generate the DDL that creates the people auth views per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Generated DDL includes the next views:
  - EducationOrganizationIdToStudentDocumentId
  - EducationOrganizationIdToContactDocumentId
  - EducationOrganizationIdToStaffDocumentId
  - EducationOrganizationIdToStudentDocumentIdThroughResponsibility
- The views return the DocumentId instead of the USI, with person-prefixed output column names: `Student_DocumentId`, `Contact_DocumentId`, `Staff_DocumentId`.
- The view definitions are hard-coded (not generalized) because people types are rarely added/modified and their definitions are not easily generalizable (e.g. Staff joins against two association tables; Contact goes through Student).
- These views target DS 5.2. DS 4 and below (which use `Parent` instead of `Contact`) are out of scope.
- DDL output for small fixtures is snapshot-testable and deterministic.
- This should be for both SQL Server and PostgreSQL.

## Tasks

1. Implement DDL emission for the people auth views.
2. Ensure deterministic ordering of statements (phased ordering per `ddl-generation.md`).
3. Add snapshot tests that validate DDL output for a small fixture (both dialects).
