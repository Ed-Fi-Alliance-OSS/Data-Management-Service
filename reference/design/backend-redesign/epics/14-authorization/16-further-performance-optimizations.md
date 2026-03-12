---
jira: DMS-1065
jira_url: https://edfi.atlassian.net/browse/DMS-1065
---

# Story: Further Performance Optimizations

## Description

The auth-redesign document identifies several performance optimizations that were intentionally kept out of scope for simplicity. These should be considered if bottlenecks are identified during performance testing.

Refer to the auth design for more information: `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Skip body-value authorization when identifying values are unchanged (POST/PUT)
- Short-circuit Namespace and Ownership checks in C# before hitting the database
- Grant access without SQL when the resource's EducationOrganizationId is directly in the client's token
- Extend bulk reference resolution to also resolve people's DocumentIds
- Convert authorization views to Indexed Views (SQL Server only)
- Improve PgSQL batch caching by using NpgsqlBatch

NOTE: Consider splitting this ticket
