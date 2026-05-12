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

See the architecture brief at `reference/spikes/DMS-1082/spike-resource-claims-endpoints.md` for parity decisions, CMS divergences, failure contract, and validation boundaries. Response and failure contracts were verified against the ODS/Admin API source and published OpenAPI artifacts. The original tracking ticket for this feature area is [DMS-853](https://edfi.atlassian.net/browse/DMS-853).

## Acceptance Criteria

### `GET /v2/resourceClaims`

- Loads the single CMS claims hierarchy.
- Projects every hierarchy node to a `ResourceClaimResponse`.
- Joins each hierarchy node to `dmscs.ResourceClaim` by exact full claim URI, without casing normalization.
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

The three list endpoints (`GET /v2/resourceClaims`, `GET /v2/resourceClaimActions`, and `GET /v2/resourceClaimActionAuthStrategies`) reuse the current CMS query pattern for paging, sorting, validation, and endpoint-specific filters. `GET /v2/resourceClaims/{id}` accepts only its path parameter and does not support these query parameters.

The shared paging behavior to preserve is the existing CMS behavior: optional `limit`/`offset`, validated `direction`, endpoint-specific `orderBy` allowlists, no implicit row cap when paging is omitted, and the current default sort direction behavior.

Endpoint-specific `orderBy` allowlists:

| Endpoint | Allowed `orderBy` values |
|---|---|
| `GET /v2/resourceClaims` | `id`, `name`, `parentId`, `parentName` |
| `GET /v2/resourceClaimActions` | `resourceClaimId`, `resourceName`, `claimName` |
| `GET /v2/resourceClaimActionAuthStrategies` | `resourceClaimId`, `resourceName`, `claimName` |

Additional query rules:

- `name` and `resourceName` filters are case-insensitive.
- When multiple filters are supplied, including `id` and `name` together for `GET /v2/resourceClaims`, these endpoints follow the existing Config query-filter behavior.
- When `orderBy` is omitted, default ordering is determined by the current query-parameter implementation, which defaults to `id`.

### Failure handling

- `GET /v2/resourceClaims`, `GET /v2/resourceClaimActions`, and `GET /v2/resourceClaimActionAuthStrategies` return `200 OK` with arrays on success.
- Query filters that match no records return `200 OK` with an empty array.
- `GET /v2/resourceClaims/{id}` returns `200 OK` with a JSON object when found and `404 Not Found` when absent.
- Unsupported `orderBy` values return the existing CMS `400 Bad Request` validation response pattern.
- Authorization failures remain `401` or `403`.
- Unhandled server-side exceptions return the same generic CMS unknown-error response shape used by comparable endpoints.
- The implementation must not invent new public endpoint failure types unless the team deliberately accepts a divergence from the spike document.
- `FailureHierarchyNotFound` maps to `404 Not Found`, consistent with existing CMS hierarchy-backed endpoint behavior.
- Other hierarchy or lookup integrity failures remain generic CMS `500` responses unless broader CMS behavior changes separately.

### Data integrity contract

The hierarchy projection must be complete with respect to the CMS claims hierarchy source. Every hierarchy node that participates in these endpoint responses must resolve to exactly one `dmscs.ResourceClaim` metadata row by the exact full claim URI.

If any required hierarchy node cannot be resolved to resource-claim metadata, the endpoint must not return a successful partial response. The request must fail explicitly using the existing CMS generic server-error response pattern for lookup or projection integrity failures.

Action and authorization-strategy lookup data is also complete-or-fail. If a `DefaultAuthorization` entry references an action or authorization strategy that cannot be resolved through the existing claim-set repository lookups, the endpoint must not return a successful partial response. It must fail using the existing CMS generic server-error response pattern for lookup or projection integrity failures.

This contract defines observable behavior, not an implementation shape. The implementation may detect metadata drift before projection, during recursive projection, through a validation helper, or inside an existing service/repository boundary. Do not silently skip unresolved hierarchy nodes, omit unresolved child subtrees, or return empty action/auth-strategy results because metadata is missing.

### Response model types

- Resource claim ids and parent ids use `long`.
- Authorization strategy ids use `long`.
- Action ids remain `int`.
- Matching uses the exact full claim URI without casing normalization unless a different policy is explicitly documented in code and tests.

### Tenant scope

- These endpoints use the current CMS tenant behavior of each dependency rather than introducing feature-specific tenant semantics.
- Request tenant scope is established by the existing tenant middleware.
- Tenant-aware lookups, such as `IClaimSetRepository.GetAuthorizationStrategies`, continue to use their current request tenant behavior.
- The claims hierarchy remains the structural source used by these endpoints.
- `dmscs.ResourceClaim` provides the resource-claim metadata for this projection.
- Resource-claim metadata lookup uses the exact full claim URI without casing normalization.
- This story adds no endpoint-specific tenant behavior.

### Authorization

- These endpoints use `MapSecuredGet`.
- `MapSecuredGet` applies `ReadOnlyOrAdminScopePolicy` for these routes.

### Datastore support

- Repository registration follows the current CMS DI pattern while keeping unsupported datastore behavior explicit.
- PostgreSQL support uses the existing `dmscs.ResourceClaim` schema and seed data.
- This story targets PostgreSQL as the supported datastore path for these endpoints.
- MSSQL support for these endpoints is out of scope and may be added later with equivalent repository behavior and deployment artifacts without changing the public endpoint contract.
- The implementation must not silently route these endpoints to PostgreSQL-only repository code when CMS is configured for MSSQL.
- Broader datastore-composition cleanup is out of scope.

### Validation timing

- These endpoints follow existing CMS startup/runtime behavior rather than introducing new health or startup validation.
- Missing claims hierarchy, multiple hierarchy rows, resource-claim metadata drift, missing lookup rows, and projection failures remain request-time concerns for these read endpoints.

### Tests cover

- Successful resource-claim hierarchy projection for the list endpoint.
- Successful single-item projection with full recursive subtree.
- Lookup by id - found and not found.
- Resource-claim list filtering by root and child `id`/`name`, including the rule that matching child nodes are returned with their own full subtree.
- Successful resource-claim-actions projection.
- Successful action/auth-strategy projection.
- Empty-result behavior for `resourceClaimActions` and `resourceClaimActionAuthStrategies` filters.
- Explicit failure when a hierarchy node has no matching resource-claim metadata row (metadata drift). This test must prove the endpoint does not return a successful partial tree, silently omit the unresolved node, or silently omit its descendant subtree.
- Explicit failure when `DefaultAuthorization` references an unresolved action id or authorization strategy id. This test must prove the endpoint does not return `200 OK` with a partial `actions` or `authorizationStrategiesForActions` collection.
- Validation failure for unsupported `orderBy`, using the current CMS paging-query validation response pattern.
- Query parameter filtering and pagination behavior, including case-insensitive `name`/`resourceName` filters, existing Config combined-filter behavior, and default `id` ordering when `orderBy` is omitted.
- Tenant-scoped requests follow existing CMS dependency behavior.
- PostgreSQL-only behavior for this story.

## Tasks

1. Add the resource-claim read models and service projections over the claims hierarchy, `dmscs.ResourceClaim`, and existing claim-set metadata lookups.
2. Add the resource-claim repository abstraction, PostgreSQL implementation, and datastore registration while keeping unsupported MSSQL behavior explicit.
3. Add the endpoint module and map the four read-only routes with `MapSecuredGet`.
4. Map service results to the public outcomes in the acceptance criteria using existing CMS response patterns.
5. Implement list filtering, sorting, and paging by reusing the current CMS query pattern and the hierarchy-specific semantics defined above.
6. Keep the implementation scoped to PostgreSQL support; add an explicit guard so unsupported MSSQL configuration cannot use PostgreSQL-only resource-claim repositories.
7. Add focused unit and endpoint tests for the success, failure, query, tenant, and PostgreSQL-only cases listed above.
