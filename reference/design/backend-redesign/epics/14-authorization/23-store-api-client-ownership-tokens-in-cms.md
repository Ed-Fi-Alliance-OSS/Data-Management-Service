---
jira: DMS-1372
jira_url: https://edfi.atlassian.net/browse/DMS-1372
---

# Story: Store and Maintain API-client Ownership Tokens in CMS

## Description

Add tenant-scoped ownership-token maintenance to the DMS Configuration Service (CMS) so
administrators can configure the creator and read/modify ownership tokens used by an API client.
Expose those values through the existing limited-access API-client response so DMS can consume
them without adding ownership claims to access tokens.

## Acceptance Criteria

### Persistence

- PostgreSQL and SQL Server add the `dmscs.OwnershipToken` catalog,
  `dmscs.ApiClient.CreatorOwnershipTokenId`, and the `dmscs.ApiClientOwnershipToken` assignment
  table defined by the decision record.
- Ownership-token IDs are CMS-generated positive `SMALLINT` values in the global range 1–32,767.
  IDs are durable and are never wrapped or reused.
- Catalog rows include the existing CMS audit fields, a required non-blank description of at most
  50 characters, and the nullable tenant association used by current CMS tenant-owned
  aggregates. Descriptions are not unique, and audit values follow the existing CMS create/update
  conventions.
- The creator-token foreign key is nullable and non-cascading. The read/modify assignment table
  has a composite key that prevents duplicate `(ApiClientId, OwnershipTokenId)` rows.
- The same catalog token can be assigned to multiple API clients in the same tenant as a creator
  token, a read/modify token, or both.
- Deleting an API client removes its assignment rows but does not remove catalog tokens. No
  ownership-token hard-delete operation is introduced.
- A new API client has a null creator token and an empty read/modify token collection.

### Tenant isolation

- In multitenant mode, catalog creation stamps the current tenant and every catalog or assignment
  operation is scoped to that tenant.
- In single-tenant mode, catalog rows store a null tenant ID and remain accessible through the
  existing single-tenant behavior.
- A token from another tenant cannot be discovered or assigned to an API client.
- API-client tenant scope continues to be resolved through its existing application and vendor
  relationships.

### Ownership-token catalog API

- The existing secured endpoint policy protects these routes:
  - `POST /v3/ownershipTokens/` creates a token and returns 201, a `Location` header, and its ID.
  - `GET /v3/ownershipTokens/` returns the current tenant's paged token collection.
  - `GET /v3/ownershipTokens/{id}` returns one visible token.
  - `PUT /v3/ownershipTokens/{id}` updates only the description and returns 204.
- Catalog paging uses the existing `offset`, `limit`, `orderBy`, and `direction` contract.
  Supported ordering fields are `id` and `description`, with `id` ascending as the default.
- The ID in a catalog update body must equal the route ID.
- The first implementation does not add catalog search, description filtering, or a `DELETE`
  endpoint.

### API-client ownership API

- `GET /v3/apiClients/{id}/ownership` returns the API client's complete ownership configuration.
- `PUT /v3/apiClients/{id}/ownership` atomically replaces the nullable
  `creatorOwnershipTokenId` and required `ownershipTokenIds` collection and returns 204.
- The read/modify collection accepts zero through 1,999 distinct token IDs. The creator token is
  independent and is not implicitly added to that collection.
- Every referenced token must exist in the API client's tenant. A missing or cross-tenant
  reference rejects the complete replacement without changing the prior configuration.
- Repeating a successful PUT with the same representation remains successful and leaves the same
  effective configuration.
- Both database providers support the 1,999-token boundary without exceeding SQL Server's
  2,100-parameter limit and without issuing one database roundtrip per assignment. Validation and
  replacement occur in one transaction.
- Existing API-client POST and PUT requests do not accept ownership fields. The ownership
  subresource is the only mutation path.

### DMS-facing response and failures

- The limited-access `GET /v3/apiClients/{clientId}` response includes
  `creatorOwnershipTokenId` and an ascending, deduplicated `ownershipTokenIds` collection.
- The limited-access API-client collection response uses the same ownership representation.
- An API client with no ownership configuration returns a null creator token and an empty
  read/modify collection.
- Validation failures use the existing CMS `application/problem+json` contract:
  - malformed IDs, invalid descriptions, duplicate assignments, route/body ID mismatch, or more
    than 1,999 read/modify IDs return 400;
  - a target API client or target catalog token not visible in the current tenant returns 404;
  - a replacement referencing a missing or cross-tenant token returns 409; and
  - identity-range exhaustion fails with the existing 500 unknown-failure contract without
    wrapping or reusing an ID.

### Verification

- Unit tests cover command validation, tenant scoping, response ordering, and endpoint failure
  mapping.
- PostgreSQL and SQL Server integration tests cover schema constraints, audit behavior, shared
  tokens, tenant isolation, transactional full replacement, idempotent replacement, and the
  1,999-token boundary.
- Focused CMS E2E tests cover catalog maintenance, API-client ownership maintenance, and the
  limited-access response consumed by DMS.

## Dependencies and Boundaries

- This story and
  [Load and Cache API-client Ownership Tokens from CMS in DMS](24-load-and-cache-api-client-ownership-tokens-from-cms.md)
  both block [DMS-1060](11-ownership-auth-strategy.md) and
  [DMS-1410](11b-ownership-auth-get-many.md).
- This story owns the CMS wire contract and must be available before the DMS story's end-to-end
  acceptance. Development may proceed in parallel against the approved decision record.
- This story does not add ownership JWT claims, `/oauth/token_info` fields, Admin App UI, token
  retirement, document-ownership transfer, or DMS authorization behavior.
