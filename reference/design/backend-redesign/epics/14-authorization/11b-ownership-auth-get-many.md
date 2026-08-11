---
jira: DMS-1410
jira_url: https://edfi.atlassian.net/browse/DMS-1410
---

# Story: Implement Ownership-based Authorization for GET-many

## Description

Implement ownership-based authorization for GET-many per
[`reference/design/backend-redesign/design-docs/auth.md`](../../design-docs/auth.md).
`OwnershipTokenIds` comes from the tenant-qualified CMS `ApplicationContext`.
GET-many does not consume `CreatorOwnershipTokenId` or JWT ownership claims.
CMS limits assignments to 1,999 ownership tokens; DMS defensively fails at 2,000 or more.

## Acceptance Criteria

- Filter against `dms.Document.CreatedByOwnershipTokenId` using the API client's `OwnershipTokenIds`.
- Exclude null and nonmatching ownership values without returning a 403.
- Apply authorization before pagination and total-count calculation.
- Treat an empty ownership-token list as an empty page and `totalCount = 0` when requested.
- Apply Ownership-based as an AND filter with Namespace-based, custom view-based, and the relationship-strategy OR group.
- Execute Ownership-based last among AND strategies.
- Replace the temporary `DMS-1055` 501 for supported mixed configurations containing Ownership-based.
- Preserve duplicate-free query results.
- Support PostgreSQL and SQL Server; use parameterized SQL Server `IN` below 2,000 tokens and fail at 2,000 or more without a TVP.
- Cover ownership-only, mixed-strategy, null-token, empty-token-list, pagination/count, parameter-limit, and both-provider cases.
