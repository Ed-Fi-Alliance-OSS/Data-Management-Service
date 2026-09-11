# DMS-1440: Add DMS education-organization discovery projection and CMS HTTP reader

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Build the authenticated HTTP contract that DMS exposes for education-organization discovery and the CMS client consumed by DMS-1441 refresh jobs.

- Endpoint: DMS exposes a service-only projection endpoint discovered from DMS Discovery. The endpoint follows the DMS route-context prefix pattern, for example `GET /{tenant}/management/education-organizations?dataStoreId={id}&limit={limit}&cursor={cursor}` in multi-tenant mode and `GET /management/education-organizations?dataStoreId={id}&limit={limit}&cursor={cursor}` in single-tenant mode. If route qualifiers are configured, they appear in the same prefix position used by DMS Discovery/Core API routes, before `/management`. The exact path is a DMS contract decision; CMS discovers it rather than hard-coding it.
- Contract: DMS returns a stable `educationOrganizationModel` projection plus opaque pagination and contract metadata. CMS does not read DMS tables, columns, joins, views, or `dms.EffectiveSchema`.
- Projection: DMS enumerates State Education Agency, Education Service Center, Local Education Agency, and School rows into the Admin API v3 education-organization shape.
- Validation: DMS fails closed on incompatible internal mapping/schema state, duplicate IDs, contradictory relationships, unknown tenant/data-store targets, and unavailable target data stores. CMS classifies HTTP failures for DMS-1441 retry and stale-snapshot behavior.
- Boundaries: domain-data reads go through authenticated DMS HTTP. Administrative database access for template restoration, ownership checks, and physical database deletion remains a separate DMS-1438/DMS-1439 concern.

### Description

CMS needs to discover the education organizations present in each configured DMS data store for the Management API projection. The previous direct-SQL design kept refresh orchestration in CMS but coupled CMS to DMS's physical schema through a versioned relational read contract. Compatibility checks can detect that coupling, but they do not remove it: CMS would still need to know DMS tables, columns, joins, generated schema changes, and provider-specific storage rules.

This story replaces that reader with a DMS-owned HTTP projection. DMS keeps all knowledge of its physical schema and mapping internals inside the DMS process. CMS receives only the stable projection contract needed by DMS-1441.

**Existing endpoint evaluation**

The existing DMS HTTP surface should be evaluated before implementing the dedicated endpoint:

- Public Ed-Fi resource collection endpoints can expose State Education Agencies, Education Service Centers, Local Education Agencies, and Schools, but CMS would have to know four resource routes, paging behavior, parent-reference shapes, profile/link variants, and hierarchy precedence. Those endpoints also use ordinary caller authorization semantics that may filter data by claim set, namespace, profile, or education-organization assignments. They are not a stable service projection for Management API refresh.
- `oauth/token_info` is token-specific. It reports the caller's authorization context and ancestry using token-info field names and DMS discriminator values such as `Ed-Fi:School`; it does not enumerate a target data store's complete Management API projection.
- Discovery currently advertises API URLs and can be extended to advertise the new projection URL template, but it does not itself return domain data.

Because none of the existing endpoints provides a complete, caller-independent, Management API-shaped projection, DMS-1440 requires a dedicated DMS endpoint. Avoiding service authentication setup is not sufficient justification to return to direct SQL.

**Scope**

- Add a DMS-owned projection handler that uses DMS's internal schema/mapping/read services to return only the required Management API fields: education-organization ID, institution name, nullable short name, discriminator, and nullable direct parent ID.
- Add authenticated DMS HTTP route(s) for the projection. The route is service-only and is not a public Management API v3 route.
- Add Discovery metadata so CMS can locate the endpoint template, token endpoint, and supported projection contract version for the requested tenant/route context.
- Add service authentication and authorization for CMS-to-DMS calls. CMS obtains a client-credentials token from the discovered DMS OAuth endpoint or the configured shared identity provider, sends it as a bearer token, and DMS authorizes a least-privilege service permission such as `edfi_dms/education_organization_projection.read`.
- Add tenant and data-store targeting. CMS passes the CMS ordinary data-store ID from its catalog; DMS resolves that ID inside the current tenant and rejects absent, cross-tenant, disabled, or not-yet-routable targets.
- Add cursor or equivalent pagination with deterministic ordering by numeric education-organization ID and an implementation-defined maximum page size. CMS must read all pages before replacing a snapshot.
- Add CMS provider-neutral HTTP client abstractions for DMS endpoint discovery, token acquisition/reuse, projection paging, timeout/cancellation, HTTP failure classification, and secret-safe diagnostics.
- Preserve the exact four core type semantics: State Education Agency, Education Service Center, Local Education Agency, and School. Extension-defined education-organization types are excluded until the Management API contract defines discriminator and parent semantics for them.

**API surface**

DMS-1440 adds a DMS service endpoint, not a CMS Management API endpoint:

```http
GET /{tenant}/management/education-organizations?dataStoreId=3788&limit=500&cursor=opaque
Authorization: Bearer {service-token}
Accept: application/json
```

The single-tenant equivalent omits `{tenant}`. If DMS route qualifiers are configured, Discovery must return the correct URL template with the full route-context prefix, for example `/{tenant}/{districtId}/{schoolYear}/management/education-organizations`. CMS does not infer the path layout. DMS implementation should treat this as a fixed route-context endpoint, consistent with OAuth/token-info/changeQueries route construction. The existing claimset management endpoints use the older `/management/{tenant}` shape; this projection endpoint should not extend that legacy pattern because it targets a tenant/data-store route context and is advertised through Discovery.

Successful responses use a stable projection envelope:

```json
{
  "contractVersion": "educationOrganizationProjection.v1",
  "dataStoreId": 3788,
  "nextCursor": null,
  "items": [
    {
      "educationOrganizationId": 255901001,
      "nameOfInstitution": "Grand Bend High School",
      "shortNameOfInstitution": "Grand Bend HS",
      "discriminator": "edfi.School",
      "parentId": 255901
    }
  ]
}
```

`educationOrganizationId` and `parentId` are `int64`/`long`; `shortNameOfInstitution`, `parentId`, and `nextCursor` are nullable. The cursor is opaque to CMS. DMS must not expose table names, column names, generated SQL, database connection strings, or raw `dms.EffectiveSchema` internals in this response.

Failures return DMS problem details with status codes and stable problem types sufficient for CMS classification:

- `401`/`403`: authentication or service-authorization failure; non-transient job failure.
- `404`: tenant or data-store target is not found in DMS's current routing view; non-transient for refresh-one and target-failed for refresh-all unless a later schedule observes the store after cache refresh.
- `409` or another pinned client-error problem type: DMS can reach the target but cannot produce a valid projection because of unsupported internal schema/mapping state, duplicate IDs, or contradictory relationships; non-transient.
- `429`, `503`, network errors, and client-side timeouts: transient and retryable under DMS-1437 policy.

No error body may include secrets, physical database names unless already public through the management contract, SQL text, or another tenant's data.

**Architecture and boundaries**

DMS owns the domain-data projection because it owns the Resources/Descriptors/Discovery API implementation, route-to-data-store resolution, mapping set, relationship semantics, and physical schema. CMS owns Management API refresh orchestration, schedules, retries, job status, snapshot persistence, and aggregate responses.

The DMS handler may reuse internal DMS read/mapping components where appropriate, but the HTTP response contract is intentionally smaller and different from token-info. DMS converts its internal discriminator and hierarchy knowledge into Admin API values such as `edfi.School`; CMS never receives or depends on DMS internal values such as `Ed-Fi:School`.

CMS adds no DMS project/package reference and no direct target-database reader for domain data. Its dependency on DMS is an authenticated HTTP contract discovered at runtime and validated by contract tests. Administrative database operations remain explicitly separate: DMS-1438/DMS-1439 may still use administrative database credentials for managed provisioning, identity verification, and physical deletion, but those credentials are not used for education-organization refresh reads.

**Dependencies**

- Existing DMS route, Discovery, OAuth/token, tenant validation, data-store cache, mapping, and relational read infrastructure.
- Existing CMS ordinary data-store catalog, tenant context, authorization policies, service-credential configuration, and `IHttpClientFactory`.
- DMS-1437 durable jobs, retries, timeout-aware cancellation, and job polling for the consuming refresh workflow.
- The final pinned Admin API/OpenAPI revision for the Management API projection shape consumed by DMS-1441.

**Blockers**

- No DMS-1438 blocker is required for the domain-data read path. DMS-1438 remains relevant only to managed provisioning/deletion and eventual DMS routability of newly created stores.
- DMS and CMS must agree on the service-authentication contract, projection URL discovery field, problem-type taxonomy, and `educationOrganizationProjection.v1` wire shape before DMS-1441 can complete refresh implementation.

**Out of scope**

- CMS snapshot persistence, refresh endpoints, schedules, and tenant aggregate responses; those belong to DMS-1441.
- Application education-organization assignment validation.
- Direct CMS SQL reads from DMS domain tables or a versioned CMS relational read contract.
- Extension-defined education-organization types until the Management API contract defines their projection semantics.
- Historical/deleted education organizations not present in the active target data store.
- Administrative template restoration, database lifecycle, and physical deletion; those remain DMS-1438/DMS-1439 concerns.

**Risks and implementation considerations**

- DMS instance unavailability can delay refresh. DMS-1441 must preserve existing snapshots on every unavailable/timeout/failure path and expose refresh failure truthfully through job status.
- DMS data-store cache lag after managed create can produce a temporary `404` for a newly registered store. CMS treats that target as failed for the current refresh and later scheduled/manual refresh can converge once DMS discovers the store.
- The service credential is powerful because it can read all education organizations for a tenant/data store. It must be least-privilege, rotatable, startup-validated where configured, redacted in logs, and denied access to ordinary resource write APIs.
- The endpoint can return large result sets. DMS must bound page size and response body size; CMS must stream or page through the response and commit only after a complete read.

**Minimum HTTP contract to define**

DMS-1440 must check in a contract document or OpenAPI fragment for the service endpoint before implementation. At minimum it must cover:

| Contract element | Required behavior |
| --- | --- |
| Discovery field | URL template for the projection endpoint and supported contract version; tenant and route qualifiers use the existing DMS route-context prefix pattern and are represented by DMS, not inferred by CMS. |
| Authentication | Client-credentials bearer token from the discovered OAuth/identity endpoint; token caching honors expiry and cancellation. |
| Authorization | Dedicated least-privilege service permission accepted only by the projection endpoint. |
| Targeting | Tenant from DMS route context plus required CMS ordinary `dataStoreId`; DMS rejects absent/cross-tenant/not-routable targets. |
| Projection fields | `educationOrganizationId`, `nameOfInstitution`, `shortNameOfInstitution`, `discriminator`, and `parentId` with Management API nullability and `int64` IDs. |
| Hierarchy | School uses LEA; LEA uses parent LEA, then ESC, then SEA; ESC uses SEA; SEA has no parent. |
| Pagination | Deterministic order, bounded `limit`, opaque nullable `nextCursor`, no duplicate or skipped rows across pages for one logical read. |
| Failure taxonomy | Stable problem types for auth, target not found, unsupported projection state, invalid source relationships, transient unavailable, timeout/cancellation, and rate limiting. |
| Redaction | No secrets, SQL, physical schema details, or cross-tenant data in responses or logs. |

### Acceptance Criteria

1. DMS exposes an authenticated service endpoint for education-organization projection and advertises its URL template and supported contract version through Discovery. CMS discovers the endpoint and does not hard-code deployment path, tenant, or route-qualifier layout.
2. The endpoint requires a bearer token obtained with client credentials and authorizes a dedicated least-privilege service permission. Tokens without that permission, ordinary application tokens, missing tokens, and expired tokens cannot read the projection.
3. CMS has a provider-neutral HTTP reader contract that handles Discovery lookup, token acquisition/cache, paged reads, timeout, cancellation, and secret-safe failure classification without referencing DMS projects or opening target database connections for domain-data reads.
4. DMS resolves the requested tenant and CMS ordinary `dataStoreId` through its own data-store routing/cache. A missing, cross-tenant, disabled, or not-yet-routable target returns a classified failure and no projection rows.
5. DMS enumerates exactly State Education Agency, Education Service Center, Local Education Agency, and School without requiring CMS to know resource routes, table names, IDs, joins, or internal mapping details.
6. Core discriminators match the pinned Admin API projection values: `edfi.StateEducationAgency`, `edfi.EducationServiceCenter`, `edfi.LocalEducationAgency`, and `edfi.School`. DMS internal token-info values such as `Ed-Fi:School` are not exposed.
7. Core `parentId` precedence matches the pinned Admin API behavior: School uses its Local Education Agency; Local Education Agency uses parent Local Education Agency, then Education Service Center, then State Education Agency; Education Service Center uses State Education Agency; State Education Agency has no parent. Self and transitive-only ancestors are not returned as the direct parent.
8. Duplicate identifiers or contradictory core relationships fail with a stable non-transient problem type rather than returning ambiguous or partial results.
9. The endpoint returns deterministic paged results ordered by numeric education-organization ID, preserves `int64` identifiers, handles an empty data store, and uses an opaque cursor that CMS treats as data.
10. CMS reads all pages for one target before returning success to DMS-1441. A failed page, invalid cursor response, malformed JSON body, timeout, cancellation, or unsuccessful HTTP status prevents snapshot replacement for that target.
11. DMS distinguishes transient unavailability/rate limiting from non-transient projection/schema/data problems in HTTP status and problem type. CMS maps those outcomes into DMS-1437 retry categories without leaking secrets.
12. The projection endpoint is cancellation-aware on both sides: request aborts cancel DMS work, CMS job cancellation cancels token/discovery/page requests, and timeout cancellation is classified without writing partial snapshots.
13. Extension-defined education-organization types are excluded. DMS neither guesses nor synthesizes discriminator or parent semantics absent from the Management API contract.
14. No CMS code reads DMS domain tables, validates `dms.EffectiveSchema`, depends on DMS generated DDL, or stores physical schema details for education-organization refresh.
15. Administrative database access for managed provisioning, ownership verification, template restoration, and physical deletion remains allowed only through DMS-1438/DMS-1439; it is not used by DMS-1440 domain-data reads.

### Tasks

**Implementation**

1. Define and check in the DMS service endpoint contract/OpenAPI fragment, including Discovery advertisement, authentication/authorization, tenant/data-store targeting, paging, and failure taxonomy.
2. Add the DMS projection handler and route using DMS-owned mapping/read services, with cancellation, deterministic ordering, duplicate/contradiction validation, and secret-safe diagnostics.
3. Add DMS service-authorization policy/configuration for the projection permission and document how CMS obtains the client-credentials token in self-contained and external identity-provider deployments.
4. Add the CMS HTTP reader abstraction, typed result/failure categories, token and Discovery clients, pagination loop, timeout/cancellation behavior, and redacted logging.
5. Document the supported projection contract versions, endpoint discovery behavior, operational configuration, and coordinated DMS/CMS upgrade process.

**Verification**

- DMS unit/API tests for Discovery advertisement, service authentication/authorization failures, tenant/data-store targeting, exact four core discriminators, parent-precedence rules, exclusion of extension types, short-name nullability, duplicate IDs, contradictory relationships, deterministic ordering, pagination, cancellation, and problem-type mapping.
- CMS unit tests for endpoint discovery, token acquisition and expiry refresh, timeout/cancellation, paging through multiple pages, malformed/partial response handling, retry classification, redaction, and no database-connection use for domain-data reads.
- Cross-component API/E2E tests with DMS stores containing representative State Education Agency, Education Service Center, Local Education Agency, and School hierarchies, including `int64` IDs, empty stores, unavailable DMS, unavailable target data store, and a newly created store not yet visible in DMS's cache.
