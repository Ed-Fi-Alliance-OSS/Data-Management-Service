---
jira: DMS-1060
jira_url: https://edfi.atlassian.net/browse/DMS-1060
---

# Story: Implement Ownership-based Authorization Strategy

## Description

Implement the ownership-based authorization strategy for all CRUD operations per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- POST: When creating a resource, if the API client has a CreatorOwnershipTokenId configured, the CreatedByOwnershipTokenId column in dms.Document is set to that value. If the API client has no CreatorOwnershipTokenId, the column is set to NULL. This stamping happens for all resources, not just those configured with the Ownership-based strategy.
- GET-many: Results are filtered by joining with dms.Document and applying CreatedByOwnershipTokenId IN ({ApiClientOwnershipTokens}). Resources with a NULL CreatedByOwnershipTokenId are excluded (not returned).
- GET-by-id: An authorization check is executed against the stored CreatedByOwnershipTokenId before reconstitution. If the value is NULL or does not match any of the client's ownership tokens, a 403 Forbidden response is returned and no reconstitution occurs.
- PUT: Authorization is checked against the currently stored CreatedByOwnershipTokenId. If unauthorized, the update does not happen and a 403 Forbidden is returned. The CreatedByOwnershipTokenId is not updated on PUT — it retains the value from the original creation.
- DELETE: Authorization is checked against the stored CreatedByOwnershipTokenId before deletion. If unauthorized, the delete does not happen and a 403 Forbidden is returned.
- An API client can have multiple ownership tokens (from ApiClientOwnershipTokens) used for read/modify authorization, but only one CreatorOwnershipTokenId used for stamping on creation.
- When authorization fails, an AUTH1 error is thrown with the strategy index in the message, aborting the batch and allowing ProblemDetails generation. ProblemDetails follow `auth.md` §"ProblemDetails" — specifically §2.13 (ownership mismatch) and §2.14 (ownership uninitialized).
- Auth checks are batched in the same DB roundtrip as other statements (reconstitution, insert, delete, etc.) to match the roundtrip targets in the design doc.
- Ownership-based executes last among the AND strategies (after Namespace-based and Custom view-based).
- Ownership-based is combined with AND when other strategy types are configured for the resource.
- Works for both PostgreSQL and SQL Server. For SQL Server, when the client's ownership token list has fewer than 2,000 entries, use a parameterized IN clause; when >= 2,000, throw an error (no TVP is used for ownership tokens).
