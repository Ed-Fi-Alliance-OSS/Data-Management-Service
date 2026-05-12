---
jira: DMS-1148
jira_url: https://edfi.atlassian.net/browse/DMS-1148
---

# Story: Serve Resource Claims Endpoints from CMS Claims Hierarchy

## Description

CMS needs Admin API-compatible read endpoints for resource claim metadata:

- `GET /v2/resourceClaims`
- `GET /v2/resourceClaims/{id}`
- `GET /v2/resourceClaimActions`
- `GET /v2/resourceClaimActionAuthStrategies`

Resource claim structure is stored in the CMS claims hierarchy JSON. The public response metadata comes from `dmscs.ResourceClaim`, keyed by the same full claim URI used in the hierarchy. Action and authorization-strategy details come from the existing claim-set repository.

This story covers read-only projections over those existing stores. It does not add resource-claim write endpoints, synthetic ids, fallback ids, or a new claims persistence model. Seeding or maintaining resource-claim metadata rows is also out of scope.

See the architecture brief at `reference/spikes/DMS-1082/spike-resource-claims-endpoints.md` for the full parity decisions, CMS divergences, failure contract, and open validation items. Response and failure contracts were verified against the ODS/Admin API source and published OpenAPI artifacts. The original tracking ticket for this feature area is [DMS-853](https://edfi.atlassian.net/browse/DMS-853).

## Acceptance Criteria

### `GET /v2/resourceClaims`

- Loads the single CMS claims hierarchy.
- Projects every hierarchy node to a `ResourceClaimResponse`.
- Joins each hierarchy node to `dmscs.ResourceClaim` by full claim URI (exact, case-sensitive).
- Returns `id`, `name`, `parentId`, `parentName`, and `children`.
- Uses `dmscs.ResourceClaim.ResourceName` for `name`, not the claim URI.
- Uses `0` and `null` for root `parentId` and `parentName`.
- Each root node includes its full recursive subtree via nested `children` arrays.
- Fails explicitly if any hierarchy node is missing resource-claim metadata.
- Supports the current CMS paging-query pattern, including `limit`, `offset`, `orderBy`, and `direction`.
- Supports endpoint-specific filters: `id` (long), `name` (string).
- Without filters, returns root nodes as the top-level collection. With `id` or `name` filters, returns matching nodes from anywhere in the hierarchy as the top-level collection, with each matching node retaining its original `parentId`, `parentName`, and full recursive `children` subtree.
- Applies `orderBy`, `direction`, `limit`, and `offset` to the top-level result collection after filtering. Paging and sorting do not remove or reorder descendant `children` within each returned subtree.

### `GET /v2/resourceClaims/{id}`

- Searches the valid projected hierarchy by `dmscs.ResourceClaim.Id`.
- Returns the matching projected claim node including its full recursive subtree.
- Returns `404 Not Found` when the requested id is absent.
- Accepts only its path parameter. Query parameters (`limit`, `offset`, `orderBy`, `direction`) do not apply to this route.

### `GET /v2/resourceClaimActions`

- Returns a flat list of resource claim actions resolved from claim-set metadata.
- Action membership for each resource claim is derived from the `DefaultAuthorization` entries in the hierarchy JSON — the same source used by `resourceClaimActionAuthStrategies`.
- Resolves action names through `IClaimSetRepository.GetActions`. `GetActions` provides name resolution only; it does not define which actions belong to a resource claim.
- Each item includes `resourceClaimId` (long), `resourceName`, `claimName`, and `actions`.
- `actions` is an array of objects with only a `name` field (`{ name: string }`). There is no `actionId` in this shape.
- Supports the current CMS paging-query pattern, including `limit`, `offset`, `orderBy`, and `direction`.
- Supports endpoint-specific filter: `resourceName` (string).

### `GET /v2/resourceClaimActionAuthStrategies`

- Returns one flat item for each hierarchy claim with `DefaultAuthorization` actions.
- Includes `resourceClaimId`, `resourceName`, `claimName`, and `authorizationStrategiesForActions`.
- Resolves action names through `IClaimSetRepository.GetActions`.
- Resolves authorization strategy names through `IClaimSetRepository.GetAuthorizationStrategies`.
- Supports the current CMS paging-query pattern, including `limit`, `offset`, `orderBy`, and `direction`.
- Supports endpoint-specific filter: `resourceName` (string).

### Query parameter alignment

The three list endpoints (`GET /v2/resourceClaims`, `GET /v2/resourceClaimActions`, and `GET /v2/resourceClaimActionAuthStrategies`) use the current CMS query implementation pattern rather than introducing a feature-specific query abstraction. `GET /v2/resourceClaims/{id}` accepts only its path parameter and does not support these query parameters.

- frontend query DTO bound with `[AsParameters]`
- endpoint-specific frontend query model for endpoint filters
- endpoint-specific `PagingQueryValidator<T>` allowlist for `orderBy`
- repository query model derived from `PagingQuery`

The shared paging behavior to preserve is:

- `offset` optional, but if provided must be `>= 0`
- `limit` optional, but if provided must be `> 0`
- `direction` optional, but if provided must be one of `asc`, `ascending`, `desc`, `descending`
- `orderBy` optional, but if provided must be in the endpoint allowlist
- omitting both `limit` and `offset` does not impose an implicit row cap
- omitting `direction` defaults to ascending behavior in current repository implementations unless explicitly documented otherwise

Endpoint-specific `orderBy` allowlists:

| Endpoint | Allowed `orderBy` values |
|---|---|
| `GET /v2/resourceClaims` | `id`, `name`, `parentId`, `parentName` |
| `GET /v2/resourceClaimActions` | `resourceClaimId`, `resourceName`, `claimName` |
| `GET /v2/resourceClaimActionAuthStrategies` | `resourceClaimId`, `resourceName`, `claimName` |

### Failure handling

- `GET /v2/resourceClaims`, `GET /v2/resourceClaimActions`, and `GET /v2/resourceClaimActionAuthStrategies` return `200 OK` with arrays on success.
- Query filters that match no records return `200 OK` with an empty array.
- `GET /v2/resourceClaims/{id}` returns `200 OK` with a JSON object when found and `404 Not Found` when absent.
- Unsupported `orderBy` values return the same `400 Bad Request` validation response pattern used by current CMS paging-query validators.
- Authorization failures remain `401` or `403`.
- Unhandled server-side exceptions return the same generic CMS unknown-error response shape used by comparable endpoints.
- The implementation must not invent new public endpoint failure types unless the team deliberately accepts a divergence from the spike document.
- `FailureHierarchyNotFound` maps to `404 Not Found`, consistent with existing CMS hierarchy-backed endpoint behavior.
- Other hierarchy or lookup integrity failures remain generic CMS `500` responses unless broader CMS behavior changes separately.

### Response model types

- Resource claim ids and parent ids use `long`.
- Authorization strategy ids use `long`.
- Action ids remain `int`.
- Matching is exact and case-sensitive unless a different policy is explicitly documented in code and tests.

### Tenant scope

- These endpoints use the current CMS tenant behavior of each dependency rather than introducing feature-specific tenant semantics.
- Request tenant scope is established by the existing tenant middleware.
- Tenant-aware lookups, such as `IClaimSetRepository.GetAuthorizationStrategies`, continue to use the current request `TenantContext`.
- `IClaimsHierarchyRepository` reads the single CMS claims hierarchy table. The current PostgreSQL schema does not carry a hierarchy `TenantId`.
- `dmscs.ResourceClaim` rows are global CMS configuration. The current PostgreSQL schema has no `TenantId` column and retains a unique `ClaimName` constraint.
- Resource-claim metadata lookup must not add a `TenantId` predicate, `TenantId` column, or tenant-specific duplicate-claim behavior as part of this story.
- This story does not introduce endpoint-specific tenant overrides or a new global-plus-tenant resource-claim model.
- This story does not define support for duplicate `ClaimName` values across tenants. The current schema retains a unique `ClaimName` constraint.

### Authorization

- These endpoints use `MapSecuredGet`.
- `MapSecuredGet` applies `ReadOnlyOrAdminScopePolicy` for these routes.

### Datastore support

- Repository registration follows the current CMS DI pattern while keeping unsupported datastore behavior explicit.
- PostgreSQL support uses the existing `dmscs.ResourceClaim` schema and seed data.
- This story targets PostgreSQL as the supported datastore path for these endpoints.
- MSSQL support for these endpoints is out of scope and may be added later with equivalent repository behavior and deployment artifacts without changing the public endpoint contract.
- The implementation must not silently route these endpoints to PostgreSQL-only repository code when CMS is configured for MSSQL.
- This story does not include broader datastore-composition cleanup beyond what is required to support the PostgreSQL path.

### Validation timing

- These endpoints follow the existing CMS startup/runtime pattern rather than introducing new health or startup validation.
- Invalid application configuration remains a startup concern only where CMS already validates options and startup configuration.
- Database deploy and initial claims bootstrap failures remain startup concerns only when the existing startup initialization paths are enabled.
- Missing claims hierarchy, multiple hierarchy rows, resource-claim metadata drift, missing lookup rows, and projection failures remain request-time concerns for these read endpoints.

### Tests cover

- Successful resource-claim hierarchy projection for the list endpoint.
- Successful single-item projection with full recursive subtree.
- Lookup by id - found and not found.
- Resource-claim list filtering by root and child `id`/`name`, including the rule that matching child nodes are returned with their own full subtree.
- Successful resource-claim-actions projection.
- Successful action/auth-strategy projection.
- Empty-result behavior for `resourceClaimActions` and `resourceClaimActionAuthStrategies` filters.
- Explicit failure when a hierarchy node has no matching resource-claim metadata row (metadata drift).
- Validation failure for unsupported `orderBy`, using the current CMS paging-query validation response pattern.
- Query parameter filtering and pagination behavior.
- PostgreSQL-only behavior for this story.

## Tasks

1. Add the resource-claim read model and service projection over `IClaimsHierarchyRepository` plus `IResourceClaimRepository`.
2. Add `IResourceClaimRepository` in the backend repository abstractions, add the PostgreSQL implementation under the PostgreSQL repository project, and register it with the current CMS DI pattern without enabling it for unsupported MSSQL execution.
3. Add the resource-claims endpoint module and map the four routes listed in this story.
4. Map service result cases to explicit endpoint responses, preserving the required `200`, `400`, `404`, `401`/`403`, and generic `500` outcomes while using the same CMS error response patterns as comparable endpoints.
5. Add the resource-claim-actions service projection and endpoint, resolving action names through the existing claim-set repository.
6. Implement general query parameters (`limit`, `offset`, `orderBy`, `direction`) and endpoint-specific filters for the three list endpoints by reusing the current CMS query implementation pattern based on frontend query DTOs, `PagingQueryValidator<T>`, and repository models derived from `PagingQuery`. Apply the hierarchy filtering and paging semantics defined in the acceptance criteria. `GET /v2/resourceClaims/{id}` accepts only its path parameter.
7. Resolve authorization strategy metadata through the existing claim-set repository APIs for the auth-strategies endpoint.
8. Register these endpoints with `MapSecuredGet` and document `ReadOnlyOrAdminScopePolicy` in the architecture brief and endpoint registration.
9. Keep this story scoped to the PostgreSQL implementation path, and document that MSSQL support can be added later without changing the public endpoint contract. Add an explicit guard so unsupported MSSQL configuration cannot use PostgreSQL-only resource-claim repositories.
10. Add focused unit and endpoint tests for the success and failure cases listed above, including query parameter behavior and empty-filter-result behavior.
11. Run the relevant backend and frontend tests and format changed C# files with `dotnet csharpier format`.
