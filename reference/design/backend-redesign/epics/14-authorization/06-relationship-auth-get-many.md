---
jira: DMS-1055
jira_url: https://edfi.atlassian.net/browse/DMS-1055
---

# Story: Implement EdOrg-only Relationship-based Authorization for GET-many

## Description

Implement the EdOrg-only relationship-based authorization strategies for the GET-many scenario, plus the shared authorization subquery framework, per:

- `reference/design/backend-redesign/design-docs/auth.md`

This ticket delivers the complete authorization subquery pipeline (SQL generation, caching, pagination, TVP threshold, OR semantics, inverted strategies) proven end-to-end with the simpler EdOrg case. People-involved strategies are handled in [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095).

## Acceptance Criteria

### EdOrg-only strategies

- The following relationship-based strategies are implemented for GET-many:
  - RelationshipsWithEdOrgsOnly — includes only EducationOrganization securable elements.
  - RelationshipsWithEdOrgsOnlyInverted — swaps the Source/Target filtering in the auth.EducationOrganizationIdToEducationOrganizationId table (bottom-to-top instead of top-to-bottom).
- GET-many results are filtered based on the configured strategy; unauthorized resources are never returned.

### Shared authorization subquery framework

- Authorization subqueries filter the auth views/table using the EdOrgIds from the client's token.
- When multiple relationship-based strategies are configured for the same resource, they are combined with OR semantics.
- No duplicate results are returned (uses IN subquery approach, not JOIN).
- Pagination (offset/limit) and total count work correctly with the authorization filter applied.
- Resource-specific SQL checks are lazily generated on first request and cached by (EffectiveSchemaHash, resource, securableElement).
- Works for both PostgreSQL and SQL Server. For SQL Server, when the token's EdOrgId list has fewer than 2,000 entries, use a parameterized IN clause; otherwise, use a TVP of type dms.BigIntTable.

NOTE: The People-involved strategies (RelationshipsWithEdOrgsAndPeople, RelationshipsWithEdOrgsAndPeopleInverted, RelationshipsWithPeopleOnly, RelationshipsWithStudentsOnly, RelationshipsWithStudentsOnlyThroughResponsibility) will be implemented in [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095).

NOTE: The GET-by-id, POST, PUT, and DELETE scenarios will be implemented in [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056).
