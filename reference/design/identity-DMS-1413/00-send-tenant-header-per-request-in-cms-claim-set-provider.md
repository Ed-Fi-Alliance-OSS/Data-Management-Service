---
jira: TBD
source_spike: DMS-1413
---

# Story: Send the Tenant Header Per Request in the CMS Claim-Set Provider

## Description

Correct a cross-tenant authorization defect in the CMS-backed claim-set provider so that identity
service-claim authorization can safely become another caller of that path.

`ConfigurationServiceClaimSetProvider.GetAllClaimSets` mutates the shared `HttpClient`'s
`Authorization` and `Tenant` default request headers before each fetch, while
`CachedClaimSetProvider`'s stampede lock is keyed per tenant. Two cold misses for different tenants
are therefore mutually unsynchronized, and one tenant's authorization metadata can be fetched and
cached under another tenant's key.

The window is not the startup path. `CacheClaimSetsTask` warms each tenant sequentially at boot, so
concurrent cold misses occur after cache expiry or an explicit invalidation, which recurs for the
life of the process.

This is a pre-existing defect on the resource authorization path, not one introduced by Identity
Management. It is filed as its own story so it can be reviewed and released as a security fix rather
than buried in a feature story, and the identity API surface story declares it as a dependency.

The fix is the pattern the rest of DMS Core already uses.
`ConfigurationServiceApplicationProvider`, `ConfigurationServiceDataStoreProvider`, and
`ConfigurationServiceProfileProvider` all send the tenant on the individual `HttpRequestMessage`,
the last of them with an explicit comment saying it does so for thread safety. The claim-set
provider is the remaining outlier, and the backend redesign already states the rule for
application-context retrieval.

## Acceptance Criteria

- `ConfigurationServiceClaimSetProvider` sends the CMS bearer token and the `Tenant` header on the
  individual `HttpRequestMessage` for each fetch.
- The provider no longer writes to `configurationServiceApiClient.Client.DefaultRequestHeaders`.
- A request for a tenant omits the `Tenant` header entirely when the tenant is null or empty,
  preserving single-tenant behavior.
- Two concurrent cold cache misses for different tenants each receive their own tenant's
  authorization metadata, proven by a controlled test that holds both fetches open simultaneously
  and asserts the header observed on each outbound request.
- Each tenant's claim sets are cached under that tenant's own cache key after the concurrent misses
  resolve.
- Existing single-tenant and sequential multitenant claim-set behavior is unchanged, including
  `CacheClaimSetsTask` startup warming and `InvalidateCacheAsync`.
- No other Configuration Service provider is modified by this story.

## Tasks

1. Move the `Authorization` and `Tenant` headers onto a per-request `HttpRequestMessage` in
   `ConfigurationServiceClaimSetProvider`.
2. Add a concurrent two-tenant cold-miss test that asserts the per-request header on each outbound
   call and the resulting per-tenant cache contents.
3. Confirm the existing claim-set provider and cached-provider tests still pass unchanged.
