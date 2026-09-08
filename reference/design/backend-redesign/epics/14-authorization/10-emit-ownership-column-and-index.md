---
jira: DMS-1059
jira_url: https://edfi.atlassian.net/browse/DMS-1059
---

# Story: Emit the CreatedByOwnershipTokenId Column and Index

## Description

Generate the DDL for the CreatedByOwnershipTokenId column in the dms.Document table and its related index per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Generated DDL includes:
  - A CreatedByOwnershipTokenId column of type smallint in the dms.Document table as nullable.
  - An index on the dms.Document.CreatedByOwnershipTokenId column.
- All identifiers are quoted per dialect.
- DDL output for small fixtures is snapshot-testable and deterministic.
