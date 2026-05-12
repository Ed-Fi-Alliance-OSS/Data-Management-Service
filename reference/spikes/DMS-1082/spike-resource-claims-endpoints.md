# DMS-1082 Architecture Brief: Resource Claims Endpoints in CMS

## 1. Summary

The Ed-Fi Admin API exposes resource claim browsing endpoints used by tooling and administrators to inspect claim structure and authorization defaults. DMS authorization follows the ODS/API resource-claim model: a request needs the resource/action grant first, and then the action-specific authorization strategy is evaluated.

CMS did not previously expose equivalent read endpoints. This spike confirmed that the necessary information already exists in CMS across two sources — the claims hierarchy JSON and the `dmscs.ResourceClaim` metadata table — and that read-only projections over those sources are sufficient to achieve Admin API parity without schema changes or new write paths.

The spike answered:

1. Can the CMS data model support the same information in read form? **Yes.**
2. What is the least risky way to expose it? **Runtime projection over existing configuration stores.**

The executable story for this work is in `reference/spikes/DMS-1082/ticket-resource-claims-endpoints.md`. The original tracking ticket for this feature area is [DMS-853](https://edfi.atlassian.net/browse/DMS-853); the spike (DMS-1082) and implementation story (DMS-1148) supersede it.

The response and failure contracts in this document are normative and were verified against the ODS/Admin API source and published Admin API OpenAPI artifacts.

---

## 2. In-Scope Endpoints

The following four endpoints are in scope:

| Route | Description |
|---|---|
| `GET /v2/resourceClaims` | Returns the full projected claim hierarchy as a collection of root nodes, each containing its full recursive subtree. |
| `GET /v2/resourceClaims/{id}` | Returns the projected node matching the given `dmscs.ResourceClaim.Id`, including its full recursive subtree. Returns `404` when the requested id is absent. |
| `GET /v2/resourceClaimActions` | Returns a flat list of resource claim actions resolved from claim-set metadata. |
| `GET /v2/resourceClaimActionAuthStrategies` | Returns a flat list of resource claims with default authorization, including action and strategy details. |

---

## 3. Out-of-Scope and Follow-Up Endpoints

| Route | Status |
|---|---|
| `POST /v2/resourceClaims`, `PUT`, `DELETE` | Out of scope. These endpoints are read-only projections; no write paths are added. |
| `/v2/claimSets/{id}/resourceClaimActions/...` | Out of scope for this work. Not part of this parity set. |
| Seed, bootstrap, or maintenance of resource-claim metadata rows | Explicitly out of scope. See Section 6. |

---

## 4. Parity Expectations

### Route and response shape parity

These endpoints match the Admin API route surface and response shape. Divergences are explicit and listed in Section 5.

### Hierarchy and collection semantics

- `GET /v2/resourceClaims` returns a collection of **root nodes**. Each root node includes its full recursive subtree via nested `children` arrays.
- `GET /v2/resourceClaims/{id}` returns the **selected node with its full recursive subtree**.
- `GET /v2/resourceClaimActions` returns a flat list. No tree structure.
- `GET /v2/resourceClaimActionAuthStrategies` returns a flat list with nested `authorizationStrategiesForActions` per item.

Sample `resourceClaims` response node (normative contract):

```json
{
  "id": 4,
  "name": "educationOrganizations",
  "parentId": 0,
  "parentName": null,
  "children": [
    {
      "id": 229,
      "name": "school",
      "parentId": 4,
      "parentName": "educationOrganizations",
      "children": []
    }
  ]
}
```

Sample `resourceClaimActions` response item (normative contract — verified against `management-api-2.3.0.yaml`):

```json
{
  "resourceClaimId": 42,
  "resourceName": "Student",
  "claimName": "http://ed-fi.org/identity/claims/ed-fi/student",
  "actions": [
    { "name": "Create" },
    { "name": "Read" },
    { "name": "Update" },
    { "name": "Delete" }
  ]
}
```

> **Note:** `actions` contains objects with only a `name` field. There is no `actionId` in this response shape. This differs from `resourceClaimActionAuthStrategies`, which does include `actionId` inside `authorizationStrategiesForActions`.

Sample `resourceClaimActionAuthStrategies` response item (normative contract):

```json
{
  "resourceClaimId": 42,
  "resourceName": "Student",
  "claimName": "http://ed-fi.org/identity/claims/ed-fi/student",
  "authorizationStrategiesForActions": [
    {
      "actionId": 1,
      "actionName": "Read",
      "authorizationStrategies": [
        {
          "authStrategyId": 7,
          "authStrategyName": "RelationshipsWithEdOrgsOnly"
        }
      ]
    }
  ]
}
```

### Query parameter parity

CMS already has an implemented query-parameter pattern for paged read endpoints. The resource-claim endpoints should follow that current pattern using a frontend query DTO bound with `[AsParameters]`, a feature-specific `PagingQueryValidator<T>`, and a repository query model derived from `PagingQuery`.

The three list endpoints (`GET /v2/resourceClaims`, `GET /v2/resourceClaimActions`, and `GET /v2/resourceClaimActionAuthStrategies`) support these general query parameters:

| Parameter | Type | Notes |
|---|---|---|
| `limit` | int | Maximum items to return |
| `offset` | int | Pagination offset |
| `orderBy` | string | Field name to sort by |
| `direction` | string | `asc`/`ascending` or `desc`/`descending` |

`GET /v2/resourceClaims/{id}` accepts only its path parameter. Query parameters (`limit`, `offset`, `orderBy`, `direction`) do not apply to this route.

Endpoint-specific filter parameters:

| Endpoint | Filter Parameters |
|---|---|
| `GET /v2/resourceClaims` | `id` (long), `name` (string) |
| `GET /v2/resourceClaimActions` | `resourceName` (string) |
| `GET /v2/resourceClaimActionAuthStrategies` | `resourceName` (string) |

Filter, sort, and paging semantics:

- `GET /v2/resourceClaims` first builds the valid projected hierarchy. Without filters, the result collection contains the root nodes. With `id` or `name` filters, the result collection contains matching nodes from anywhere in the hierarchy, and each matching node still includes its full recursive subtree. The original `parentId` and `parentName` values are preserved.
- For `GET /v2/resourceClaims`, `orderBy`, `direction`, `limit`, and `offset` apply to the top-level result collection after filtering. They do not page, sort, or remove descendant `children` within each returned subtree.
- For `GET /v2/resourceClaimActions` and `GET /v2/resourceClaimActionAuthStrategies`, filters, sorting, and paging apply to the flat projected collection.
- Required `orderBy` allowlists are `id`, `name`, `parentId`, and `parentName` for `resourceClaims`; `resourceClaimId`, `resourceName`, and `claimName` for `resourceClaimActions`; and `resourceClaimId`, `resourceName`, and `claimName` for `resourceClaimActionAuthStrategies`.

Current CMS validation and paging behavior to mirror:

- `offset` is optional and, when provided, must be greater than or equal to `0`
- `limit` is optional and, when provided, must be greater than `0`
- `direction` is optional and must be one of `asc`, `ascending`, `desc`, or `descending`
- `orderBy` is optional and, when provided, must be validated against an endpoint-specific allowlist
- when both `limit` and `offset` are omitted, the current `PagingQuery` implementation applies no implicit row cap
- when `direction` is omitted, repository implementations default to ascending order unless a repository-specific behavior explicitly differs

Implementation should follow the existing CMS code pattern used by modules such as `VendorModule`, `ClaimSetModule`, and `ProfileModule`:

- frontend binding model in `FrontendQueryModels.cs`
- validation in `PagingQueryValidators.cs`
- repository paging contract in `PagingQuery.cs`

Admin API query behaviors intentionally not implemented must be listed as explicit omissions in the story acceptance criteria.

---

## 5. Intentional CMS Divergences

These are accepted, deliberate differences from the Admin API. They are not compatibility gaps.

| Divergence | Rationale |
|---|---|
| Public and persisted IDs use `long` (`BIGINT`) | CMS uses `bigint` identifiers throughout. `ResourceClaimResponse.Id`, `ParentId`, `ResourceClaimActionAuthStrategyResponse.ResourceClaimId`, and `AuthStrategyId` are `long`. `ActionId` remains `int`, matching the existing action repository model. |
| Authorization uses `MapSecuredGet` | These endpoints use the standard CMS secured GET pattern: `MapSecuredGet`, which applies `ReadOnlyOrAdminScopePolicy`. No new authorization model is introduced. |

---

## 6. Data and Metadata Assumptions

### The two-source dependency

The CMS response contract depends on two sources staying aligned:

1. **Claims hierarchy JSON** — the structural source of truth. Stored as a nested document. Provides claim URI, parent/child relationships, and `DefaultAuthorization` entries.
2. **`dmscs.ResourceClaim` table** — the metadata source. Provides the stable `long` id, the display resource name (`ResourceName`), and the claim URI used to match against the hierarchy.

The hierarchy determines which nodes appear in the response. The metadata table enriches those nodes. A metadata row without a corresponding hierarchy node is not returned. A hierarchy node without a matching metadata row is a data integrity failure.

### Seed/bootstrap boundary

Creating, seeding, or maintaining `dmscs.ResourceClaim` rows is **explicitly out of scope** for this spike and any direct implementation ticket derived from it. This applies to:

- base bootstrap metadata
- extension claim metadata
- custom claim metadata
- homograph or dynamically composed claim metadata

The endpoints assume required metadata already exists. If it does not exist, the projection fails explicitly — it does not silently skip nodes or return partial results.

### Matching policy

Hierarchy node to metadata row matching is by full claim URI, exact and case-sensitive, unless a different policy is explicitly documented in code and tests.

### Tenant-scope policy

These endpoints should follow the current CMS tenant behavior of each dependency instead of introducing feature-specific tenant semantics:

- tenant scope is established per request by the existing tenant-resolution middleware
- tenant-aware repository lookups use the current `TenantContext`
- `IClaimSetRepository.GetAuthorizationStrategies` follows its existing tenant-aware behavior: when multi-tenancy is enabled, it scopes lookup rows by `TenantId`; when multi-tenancy is disabled, it uses rows where `TenantId IS NULL`
- `IClaimsHierarchyRepository` reads the single CMS claims hierarchy table; the current PostgreSQL schema does not carry a hierarchy `TenantId`
- `dmscs.ResourceClaim` is global CMS configuration; the current PostgreSQL schema has no `TenantId` column and retains a unique `ClaimName` constraint

No endpoint-specific tenant behavior is introduced for this feature area. The endpoint contract must not define a new "global plus tenant override" model unless CMS adds that behavior to the backing schema and repositories in separate work.

This spike does not define support for tenant-specific duplicates of the same claim URI. Do not add a `TenantId` predicate, `TenantId` column, or duplicate-claim semantics to `dmscs.ResourceClaim` as part of DMS-1148.

---

## 7. Authorization

These endpoints use the standard CMS secured GET pattern for read endpoints. Endpoint registration should use `MapSecuredGet`, which applies `ReadOnlyOrAdminScopePolicy` along with the existing service policy requirement. No new authorization model is introduced for this feature area.

---

## 8. Failure Contract

The public failure behavior should stay aligned with comparable CMS read endpoints while preserving the functional outcomes required for this feature area. This section defines externally visible behavior only. CMS may still enforce internal integrity checks, but those checks must not invent new public error categories unless the team explicitly accepts a divergence.

### Public contract

| Endpoint or Condition | API Behavior | Response Shape |
|---|---|---|
| `GET /v2/resourceClaims` success | `200 OK` | JSON array |
| `GET /v2/resourceClaimActions` success | `200 OK` | JSON array |
| `GET /v2/resourceClaimActionAuthStrategies` success | `200 OK` | JSON array |
| Query filter matches no records | `200 OK` | Empty JSON array |
| `GET /v2/resourceClaims/{id}` and id exists | `200 OK` | JSON object |
| `GET /v2/resourceClaims/{id}` and id is absent | `404 Not Found` | Existing CMS not-found error body |
| Unsupported `orderBy` value | `400 Bad Request` | Existing CMS validation error body |
| Authorization failure | `401` or `403` | Existing CMS auth error body |
| Unhandled server-side exception | `500 Internal Server Error` | Existing CMS unknown-error body |

### Response shape notes

- `404` for missing `resourceClaims/{id}` should use the same CMS not-found response structure as comparable endpoints.
- `400` validation errors should use the same CMS validation response structure as comparable endpoints.
- Generic `500` responses should use the same CMS unknown-error response structure as comparable endpoints.

### Important constraint

This spike should not define new public endpoint failures such as:

- missing hierarchy row -> `404`
- duplicate metadata row -> custom `500` contract
- missing action lookup row -> custom `500` contract
- missing authorization strategy lookup row -> custom `500` contract

Those may still be important CMS implementation concerns, but they belong in internal validation rules, implementation notes, or follow-up decisions unless the team intentionally chooses to diverge from existing CMS endpoint behavior.

Hierarchy repository failures should follow existing CMS patterns:

- `FailureHierarchyNotFound` maps the same way it already does in `AuthorizationMetadataModule`: `404 Not Found`.
- Other hierarchy or lookup integrity failures remain generic CMS `500` responses unless a broader CMS policy changes separately.

---

## 9. Validation Items

This section records implementation-boundary notes that remain relevant for planning. It is not an open question about public endpoint behavior unless explicitly stated.

1. **MSSQL datastore support** - The `dmscs.ResourceClaim` table and seed data currently exist only in the PostgreSQL deployment scripts. This spike scopes these endpoints to PostgreSQL only. MSSQL is unsupported for this feature area until equivalent repository and deployment support are added in later work. The implementation must not silently route these endpoints to PostgreSQL-only repository code when CMS is configured for MSSQL. This spike does not attempt broader datastore-composition cleanup.

2. **Startup versus request-time validation** - These endpoints should follow the existing CMS pattern:

   - configuration validation failures remain startup concerns only where CMS already validates configuration during startup
   - database deploy and initial claims bootstrap failures remain startup concerns only when those existing startup paths are enabled
   - hierarchy lookup failures, duplicate hierarchy rows, metadata drift, and related repository or projection failures remain request-time concerns for read endpoints

   This spike does not introduce new startup or health-check validation for resource-claim endpoint data integrity beyond existing CMS startup behavior.

3. **`GET /v2/resourceClaimActions` response shape** - **Resolved.** Verified against `management-api-2.3.0.yaml`: the `actions` array contains `{ name: string }` objects only. There is no `actionId` in this response shape. See Section 4 for the normative sample.

---

## 10. Deliverable

The output of this spike is the companion story `reference/spikes/DMS-1082/ticket-resource-claims-endpoints.md` (Jira: DMS-1148), which contains the full acceptance criteria and task breakdown for implementation.

---

## Implementation Approach

The intended service implementation walks the claim hierarchy at runtime and joins each node with the corresponding metadata row from `dmscs.ResourceClaim`.

- `GET /v2/resourceClaims`: the service walks the hierarchy, projects each root node to `ResourceClaimResponse`, and includes the full recursive subtree.
- `GET /v2/resourceClaims/{id}`: the service resolves the selected node by id and returns that node with its full recursive subtree.
- `GET /v2/resourceClaimActions`: the service derives action membership for each resource claim from the `DefaultAuthorization` entries in the hierarchy JSON — the same source used by `resourceClaimActionAuthStrategies`. `IClaimSetRepository.GetActions` is used only to resolve action names from action identifiers; it does not define which actions belong to a resource claim. The projection emits a flat list.
- `GET /v2/resourceClaimActionAuthStrategies`: the service traverses the hierarchy, filters to claims with `DefaultAuthorization`, resolves action names and authorization strategy names through the existing claim-set repository APIs, and emits a flat response.

This design reuses existing configuration stores without introducing a parallel persistence model or schema changes.

### Technical tradeoffs

**Runtime join versus materialized claim table.** The hierarchy-to-metadata match happens at request time. Benefits: no schema change, no data migration, no extra write path. Costs: response quality depends on both sources; each request walks the tree in memory.

**Shared projection depth for list and single-item views.** The collection endpoint returns full recursive trees for root nodes, and the `{id}` endpoint returns the selected node with its full recursive subtree. Benefit: one consistent projection contract across both read endpoints. Cost: the single-item lookup may still need to project or traverse a larger portion of the hierarchy before locating the requested node.

**Exact-match dependency.** The hierarchy and `dmscs.ResourceClaim` must agree on the full claim URI. If they drift, the projection fails explicitly. This is the highest operational risk in the design and must not become a silent omission or partial response.

### Implementation rules

- Preserve the hierarchy JSON as the structural source of truth.
- Preserve the resource-claim table as the metadata lookup source.
- Keep all four endpoints read-only.
- Fail explicitly when required lookup data is missing.
- Treat PostgreSQL as the supported datastore path for this work; MSSQL support can be added later without changing the endpoint contract.
- Treat `dmscs.ResourceClaim` as global CMS configuration in DMS-1148. Do not add tenant-specific resource-claim metadata behavior in this work.
- Reuse the current CMS query pattern implemented through `FrontendPagingQuery`, endpoint-specific frontend query DTOs, `PagingQueryValidator<T>`, and repository query models derived from `PagingQuery` instead of introducing a feature-specific filtering or sorting abstraction here.
- Do not add write endpoints, schema redesigns, synthetic ids, or fallback ids.
- Start from the companion story and the current `main` code paths. Key paths to examine: endpoint module pattern, repository result records, datastore registration, `IClaimsHierarchyRepository.GetClaimsHierarchy`, `IClaimSetRepository.GetActions`, `IClaimSetRepository.GetAuthorizationStrategies`.
