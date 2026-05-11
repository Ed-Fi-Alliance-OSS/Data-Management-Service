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
- Supports the general query-parameter pattern defined by the DMS-1074 implementation, including `limit`, `offset`, `orderBy`, and `direction`.
- Supports endpoint-specific filters: `id` (long), `name` (string).

### `GET /v2/resourceClaims/{id}`

- Searches the valid projected hierarchy by `dmscs.ResourceClaim.Id`.
- Returns the matching projected claim node including its full recursive subtree.
- Returns `404 Not Found` when the requested id is absent.

### `GET /v2/resourceClaimActions`

- Returns a flat list of resource claim actions resolved from claim-set metadata.
- Resolves action names through `IClaimSetRepository.GetActions`.
- Each item includes `resourceClaimId` (long), `resourceName`, `claimName`, and `actions`.
- `actions` is an array of objects with only a `name` field (`{ name: string }`). There is no `actionId` in this shape.
- Supports the general query-parameter pattern defined by the DMS-1074 implementation, including `limit`, `offset`, `orderBy`, and `direction`.
- Supports endpoint-specific filter: `resourceName` (string).

### `GET /v2/resourceClaimActionAuthStrategies`

- Returns one flat item for each hierarchy claim with `DefaultAuthorization` actions.
- Includes `resourceClaimId`, `resourceName`, `claimName`, and `authorizationStrategiesForActions`.
- Resolves action names through `IClaimSetRepository.GetActions`.
- Resolves authorization strategy names through `IClaimSetRepository.GetAuthorizationStrategies`.
- Supports the general query-parameter pattern defined by the DMS-1074 implementation, including `limit`, `offset`, `orderBy`, and `direction`.
- Supports endpoint-specific filter: `resourceName` (string).

### Query parameter alignment

DMS-1074 defines the shared CMS implementation pattern for Admin API-style filtering and sorting parameters. These endpoints should use that implementation pattern rather than introducing a feature-specific query abstraction.

### Failure handling

- `GET /v2/resourceClaims`, `GET /v2/resourceClaimActions`, and `GET /v2/resourceClaimActionAuthStrategies` return `200 OK` with arrays on success.
- Query filters that match no records return `200 OK` with an empty array.
- `GET /v2/resourceClaims/{id}` returns `200 OK` with a JSON object when found and `404 Not Found` when absent.
- Unsupported `orderBy` values return the same `400 Bad Request` validation response produced by the DMS-1074 query-pattern implementation.
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

- These endpoints use the standard CMS tenant-resolution pattern already used by repositories such as `ClaimSetRepository`.
- Repository behavior is driven by the current request `TenantContext`, established by the existing tenant middleware.
- When multi-tenancy is enabled, `dmscs.ResourceClaim` and other tenant-aware lookups for these endpoints are scoped by `TenantId`.
- When multi-tenancy is disabled, those lookups use rows where `TenantId IS NULL`.
- This story does not introduce endpoint-specific tenant overrides or a new global-plus-tenant resource-claim model.
- This story does not define support for duplicate `ClaimName` values across tenants. The current schema retains a unique `ClaimName` constraint.

### Authorization

- These endpoints use `MapSecuredGet`.
- `MapSecuredGet` applies `ReadOnlyOrAdminScopePolicy` for these routes.

### Datastore support

- Repository registration follows the selected CMS datastore for supported implementations.
- PostgreSQL support uses the existing `dmscs.ResourceClaim` schema and seed data.
- This story targets PostgreSQL as the supported datastore path for these endpoints.
- MSSQL support for these endpoints is out of scope and may be added later with equivalent repository behavior and deployment artifacts without changing the public endpoint contract.
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
- Successful resource-claim-actions projection.
- Successful action/auth-strategy projection.
- Empty-result behavior for `resourceClaimActions` and `resourceClaimActionAuthStrategies` filters.
- Validation failure for unsupported `orderBy`, using the DMS-1074 query-pattern validation response.
- Query parameter filtering and pagination behavior.
- PostgreSQL-only behavior for this story.

## Tasks

1. Add the resource-claim read model and service projection over `IClaimsHierarchyRepository` plus `IResourceClaimRepository`.
2. Add a datastore-specific `IResourceClaimRepository` implementation and register it through datastore-specific wiring.
3. Add the resource-claims endpoint module and map the four routes listed in this story.
4. Map service result cases to explicit endpoint responses, preserving the required `200`, `400`, `404`, `401`/`403`, and generic `500` outcomes while using the same CMS error response patterns as comparable endpoints.
5. Add the resource-claim-actions service projection and endpoint, resolving action names through the existing claim-set repository.
6. Implement general query parameters (`limit`, `offset`, `orderBy`, `direction`) and endpoint-specific filters for all four endpoints by reusing the shared DMS-1074 query implementation pattern.
7. Resolve authorization strategy metadata through the existing claim-set repository APIs for the auth-strategies endpoint.
8. Register these endpoints with `MapSecuredGet` and document `ReadOnlyOrAdminScopePolicy` in the architecture brief and endpoint registration.
9. Keep this story scoped to the PostgreSQL implementation path, and document that MSSQL support can be added later without changing the public endpoint contract.
10. Add focused unit and endpoint tests for the success and failure cases listed above, including query parameter behavior and empty-filter-result behavior.
11. Run the relevant backend and frontend tests and format changed C# files with `dotnet csharpier format`.
