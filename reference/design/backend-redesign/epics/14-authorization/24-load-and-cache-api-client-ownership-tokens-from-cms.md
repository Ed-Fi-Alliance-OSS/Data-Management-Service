---
jira: DMS-1373
jira_url: https://edfi.atlassian.net/browse/DMS-1373
---

# Story: Load and Cache API-client Ownership Tokens from CMS in DMS

## Description

Extend DMS application-context retrieval so the creator and read/modify ownership tokens maintained
by CMS are available to the relational authorization pipeline. Keep the existing application-context
cache, make its requests and keys tenant-aware, and fail closed when a required context cannot be
resolved.

## Acceptance Criteria

### Application context

- `ApplicationContext` includes a nullable `CreatorOwnershipTokenId` and a non-null,
  read-only `OwnershipTokenIds` collection of `short` values from
  `GET /v3/apiClients/{clientId}`.
- Ownership values remain separate from JWT-derived `ClientAuthorizations`; no ownership claim or
  identity-provider mapper is added.
- Application-context lookup returns typed `Success`, `NotFound`, and `Unavailable` results.
  Malformed successful CMS responses are classified as unavailable.
- The resolved application context is propagated into `RelationalAuthorizationContext` alongside
  the existing JWT-derived client authorizations without implementing the DMS-1060 ownership SQL
  strategy in this story.

### Request-scoped resolution

- A request-scoped holder memoizes its first success, not-found, or unavailable result for the
  remainder of the request.
- Application context is resolved for every POST so DMS-1060 can stamp every newly created
  document, including resources that do not use `OwnershipBased`.
- GET, PUT, and DELETE resolve application context when their selected authorization strategies
  include `OwnershipBased`. Existing profile behavior may independently require it.
- Each authenticated resource request performs at most one application-context resolution when
  required.

### Tenant-aware CMS access

- Application-context lookup and explicit reload accept the current tenant.
- When a tenant is present, the CMS provider sends it in the `Tenant` header on the individual
  request. Single-tenant requests omit the header.
- Tenant selection does not mutate shared `HttpClient.DefaultRequestHeaders`.
- Cache keys are:
  - `ApplicationContext:single:{clientId}` in single-tenant mode; and
  - `ApplicationContext:tenant:{normalizedTenant}:{clientId}` in multitenant mode, where
    `normalizedTenant` is `tenant.ToLowerInvariant()`.
- Lookup and reload normalize the tenant identically. The original request tenant is retained for
  the CMS header.

### Cache behavior

- `CachedApplicationContextProvider` continues to use `HybridCache` and its per-key stampede
  protection.
- `ApplicationContextCacheExpirationSeconds` remains configurable with a default of 600 seconds.
  DMS validates that the configured value is positive; this story does not impose a maximum.
- Only successful contexts are stored in `HybridCache`. Not-found, unavailable, and malformed
  results are not negatively cached.
- A normal cache miss issues one CMS request. A not-found result does not cause an immediate
  second reload request.
- Explicit reload removes and reloads only the matching normalized tenant/client cache key and
  issues one CMS request.
- No push or event-driven cache invalidation is introduced. Configuration changes may remain
  stale for the configured cache lifetime.

### Failure behavior

- When required application context is not already cached:
  - an API client not found in the current tenant results in 401;
  - CMS unavailability or a malformed response results in 503; and
  - DMS never substitutes an empty ownership configuration for a failed lookup.
- A successful context with a null creator token and an empty read/modify collection is valid and
  is not treated as a lookup failure.
- A cached successful context permits the request to continue during a CMS outage until that
  cache entry expires.
- Public failure responses do not disclose ownership-token values.

### Verification and documentation

- Focused tests cover typed provider results, one-request cache misses, request-scoped result reuse,
  success-only caching, explicit reload, expiration validation, and fail-closed mappings.
- Multitenancy tests prove that two tenants using the same client ID cannot share a cache entry and
  that lookup and reload use the same normalized key.
- Pipeline and integration tests prove that context is requested for every POST and for
  ownership-authorized GET, PUT, and DELETE operations, while unrelated requests do not gain an
  unnecessary lookup beyond existing profile requirements.
- The ownership-delivery statement in
  [the authorization design](../../design-docs/auth.md#authentication) is corrected to reference
  the CMS application-context contract instead of JWT claims as part of implementing this story.

## Dependencies and Boundaries

- [Store and Maintain API-client Ownership Tokens in CMS](23-store-api-client-ownership-tokens-in-cms.md)
  owns the CMS response contract and must be available before this story's end-to-end acceptance.
- Both stories block [DMS-1060](11-ownership-auth-strategy.md); this story supplies context but does
  not implement ownership stamping, filters, authorization checks, or SQL.
- This story does not add ownership JWT claims, `/oauth/token_info` fields, push invalidation,
  Admin App UI, token lifecycle administration, or document-ownership transfer.
