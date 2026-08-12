---
jira: DMS-1060
jira_url: https://edfi.atlassian.net/browse/DMS-1060
---

# Story: Implement Ownership-based Authorization for GET-by-id, POST, PUT, and DELETE

## Description

Implement ownership-based authorization for single-record operations per:

- `reference/design/backend-redesign/design-docs/auth.md`

`CreatorOwnershipTokenId` and `OwnershipTokenIds` come from the tenant-qualified CMS
`ApplicationContext`, not JWT claims. DMS resolves and caches this context through
`GET /v3/apiClients/{clientId}`.

CMS limits assignments to 1,999 ownership tokens; DMS defensively fails at 2,000 or more.

## Acceptance Criteria

- POST stamps every newly created document from `CreatorOwnershipTokenId`, including resources not configured with `OwnershipBased`; a missing creator token stamps null.
- GET-by-id checks the stored token before reconstitution and returns 403 on null or mismatch.
- PUT checks the stored token before mutation and never changes it.
- DELETE checks the stored token before deletion.
- `OwnershipTokenIds` authorizes reads and mutations, while the single `CreatorOwnershipTokenId` is only for creation stamping.
- Failures use `AUTH1` with the configured strategy index and map to `auth.md` sections 2.13 and 2.14.
- Checks share the operation's database roundtrip and abort later statements in the batch.
- Ownership executes last among AND strategies and composes with the other configured strategies.
- PostgreSQL and SQL Server are supported; SQL Server uses parameterized `IN` below 2,000 ownership tokens and fails at 2,000 or more without a TVP.

NOTE: GET-many ownership filtering is implemented by [DMS-1410](https://edfi.atlassian.net/browse/DMS-1410) in `11b-ownership-auth-get-many.md`.
