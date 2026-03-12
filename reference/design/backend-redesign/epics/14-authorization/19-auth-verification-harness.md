---
jira: DMS-1096
jira_url: https://edfi.atlassian.net/browse/DMS-1096
---

# Story: Verification harness for emitted auth DB objects

## Description

DDL verification harness covers auth-related objects per `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`.

## Acceptance Criteria
- The snapshot tests introduced in DMS-959 also cover auth objects (defined as blockers in this ticket), meaning that when an auth object definition changes, the change is detected by the following snapshots:
    - relational-model.manifest.json
    - pgsql.sql and mssql.sql
- Test(s) ensure that auth objects are always emitted once, regardless of loaded extensions.
- Test(s) ensure that inserts/updates made to concrete Education Organizations update the `auth.EducationOrganizationIdToEducationOrganizationId` table accordingly.
