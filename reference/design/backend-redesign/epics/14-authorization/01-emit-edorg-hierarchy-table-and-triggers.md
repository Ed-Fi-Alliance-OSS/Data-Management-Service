---
jira: DMS-1049
jira_url: https://edfi.atlassian.net/browse/DMS-1049
---

# Story: Emit the auth.EducationOrganizationIdToEducationOrganizationId Table and Related Triggers

## Description

Generate the DDL for the auth.EducationOrganizationIdToEducationOrganizationId table and the related triggers per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Generated DDL includes:
  - The auth.EducationOrganizationIdToEducationOrganizationId table with its primary key on (SourceEducationOrganizationId, TargetEducationOrganizationId)
  - Two lookup indexes on the table:
    - (SourceEducationOrganizationId) INCLUDE (TargetEducationOrganizationId) — used by non-inverted strategies
    - (TargetEducationOrganizationId) INCLUDE (SourceEducationOrganizationId) — used by inverted strategies
  - Triggers on all concrete Education Organization tables that keep the EdOrg hierarchy up to date
- Triggers use denormalized/unified stored columns (i.e. the canonical EdOrgId columns on the resource tables) and do not join with the EducationOrganization table at trigger-time.
- All identifiers are quoted per dialect.
- DDL output for small fixtures is snapshot-testable and deterministic.

## Tasks

1. Implement DDL emission for each table/trigger listed above, using the dialect writer.
2. Ensure deterministic ordering of statements (phased ordering per `ddl-generation.md`).
3. Add snapshot tests that validate DDL output for a small fixture (both dialects).
