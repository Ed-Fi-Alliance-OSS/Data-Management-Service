# Identity Management

This chapter is for the operator who turns on Identity Management in the Ed-Fi API and keeps it running.
It says what the feature is, how to enable it, what a client sees when it calls, how quickly a change to a client or a claim set takes effect, and what to do when access must be removed urgently.

How a provider plugin is packaged, delivered, allowlisted, and trusted is not restated here.
See [PLUGINS.md](../src/plugins/EdFi.Api.Plugins/PLUGINS.md) for the implementer's side and the [Plugins chapter of OPERATIONS.md](./OPERATIONS.md#plugins) for the operator's.
How to write a provider, and what it must do for each operation, is in the `EdFi.Api.Identity` package's readme, [the implementer guide](../src/dms/core/EdFi.DataManagementService.Identity/IDENTITY.md).
The guide is the authority on provider obligations, and this chapter links to it rather than repeating them.

## What Identity Management is

Identity Management is a surface of five operations under `/identity/v2/identities`: create, get by UniqueId, find, search, and polling for the results of an asynchronous find or search.
The Ed-Fi API owns the routes, authentication, the tenant checks, and every problem response.
**A provider plugin supplies the operations themselves.**
The plugin registers an implementation of `IIdentityService`, and the Ed-Fi API calls it for the identity work behind each route.
The Ed-Fi API ships no provider of its own.

The surface is off by default.
When it is on and no provider is registered, a request that passes every check the Ed-Fi API makes answers `404` with the problem type `urn:ed-fi:api:identities:operation-not-supported`.

## Enabling it

Three things must all be true before an identity operation runs.

1. **The surface is switched on.**
   Set `AppSettings:EnableIdentityManagement` to `true`.
   The environment override is `AppSettings__EnableIdentityManagement`, and the shipped Docker Compose files map it from `DMS_ENABLE_IDENTITY_MANAGEMENT`.
   The default is `false`, and with it off none of the identity routes, the identity OpenAPI document, its listing in the metadata index, or the `identity` Discovery URL exist.
   See [AppSettings](./CONFIGURATION.md#appsettings).
2. **A provider plugin is delivered and allowlisted.**
   The plugin's directory name must appear in `Plugins:Allowed`, as for any plugin.
   See [Plugins](./CONFIGURATION.md#plugins).
3. **An API client holds the identity claim.**
   A client reaches an operation only through a claim set that grants the identity service claim for that operation.
   A client with no data store assignment can hold such a claim set and reaches no resource data.
   See [CMS client lifecycle](#cms-client-lifecycle).

The provider declares which of the five operations it supports.
An operation the provider does not advertise answers `404` with `urn:ed-fi:api:identities:operation-not-supported`.
This check runs before the content type, the body and duplicate properties are validated, so a request with a malformed body still gets this `404`.

## What a client sees

Problem responses are `application/problem+json`, except the `400` for a malformed or empty body or a duplicate property, which DMS sends as `application/json` with the same problem body.
The table lists the statuses a client can see and where each comes from.
The implementer guide's [results and status codes](../src/dms/core/EdFi.DataManagementService.Identity/IDENTITY.md#results-and-status-codes) section is the complete table, including the statuses that come from the provider's own results.

| Status | Problem type | Cause |
| --- | --- | --- |
| `400` | `urn:ed-fi:api:bad-request`, or `urn:ed-fi:api:bad-request:data-validation-failed` | A malformed or empty body, a duplicate property, a body of the wrong top-level shape, a blank route value, or `InvalidProperties` from the provider. |
| `401` | | No bearer token, a token that cannot be validated, or an API client that does not belong to the tenant in the URL. |
| `403` | | The client's claim set does not grant the identity service claim for the operation. |
| `404` | see below | Four different situations. |
| `415` | | A `Content-Type` other than `application/json` or `text/json` on a `POST`. |
| `429` | | The global rate limiter rejected the request. See [Rate limiting](#rate-limiting). |
| `500` | `urn:ed-fi:api:identities:provider-configuration`, or `urn:ed-fi:api:system:configuration:security` | The provider cannot be activated or its capabilities cannot be read, or the identity claim is not configured as the host requires. See [Authorization strategy](#authorization-strategy). |
| `502` | `urn:ed-fi:api:identities:provider-contract-violation`, `urn:ed-fi:api:identities:upstream-failure`, or `urn:ed-fi:api:identities:job-failed` | The provider returned a result the contract forbids, an operation call threw, or an accepted job failed permanently. |
| `503` | | The tenant catalog or the client-to-tenant binding cannot be resolved right now. |

### The four 404 responses

Four situations answer `404`, and the problem body tells them apart.
Switch on the problem `type`, and where two share one, on `detail`.

| Situation | `type` | `detail` |
| --- | --- | --- |
| The provider reported that nothing matches: no such identity or job, a mismatched or expired job, or an unknown or unauthorized namespace | `urn:ed-fi:api:identities:not-found` | `The specified data could not be found.` |
| The provider does not advertise the requested operation | `urn:ed-fi:api:identities:operation-not-supported` | `The identity provider does not support this operation.` |
| The tenant in the URL does not exist, on a multi-tenant host, after authentication | `urn:ed-fi:api:not-found` | `The specified tenant could not be found.` |
| Identity Management is switched off, so no identity route exists | `urn:ed-fi:api:not-found` | `The specified data could not be found.` |

**The tenant-not-found and the feature-off responses share one problem type, so only `detail` separates them.**
The `Cache-Control: no-store` header does not separate them either.
The Ed-Fi API adds it to every non-successful response, the catch-all `404` for an unmatched route included.

### Wrong tenant, missing tenant, and an unanswerable check

On a multi-tenant host three different answers can come from the tenant in the URL, and an operator diagnosing a client should tell them apart.

| Answer | Meaning |
| --- | --- |
| `401` | The token is valid and the tenant exists, but the API client's application is not bound to that tenant. |
| `404` with `The specified tenant could not be found.` | The tenant does not exist. |
| `503` | The Ed-Fi API could not answer: the tenant list could not be refreshed, or the Configuration Service could not say whether the client belongs to the tenant. |

The order matters.
Authentication runs first, then the tenant-exists check, then the client-to-tenant binding, so a caller with no valid token never learns whether a tenant exists.
A `503` is a statement about the Ed-Fi API's ability to answer, never a statement that the tenant or the binding is absent.
The Ed-Fi API never serves a stale membership answer after the tenant snapshot expires.
See [Datastore independence and the tenant snapshot](#datastore-independence-and-the-tenant-snapshot).

## The tenant and qualifier boundary

The Ed-Fi API validates that the tenant in the URL exists, after authentication.
It binds the authenticated API client to that tenant, so a client created in one tenant cannot call another.
It passes the route qualifiers, such as `districtId` and `schoolYear`, to the provider as context, and the provider decides what they mean.
**It does not make data store authorization part of identity.**
Identity operations never resolve a data store, and an API client's data store assignments neither grant nor limit them.

How the tenant and qualifier names and values are compared, and how a provider scopes its namespaces and job ownership to them, is a provider obligation.
See [Namespaces, grants and context equality](../src/dms/core/EdFi.DataManagementService.Identity/IDENTITY.md#namespaces-grants-and-context-equality) in the implementer guide.
In a single-tenant deployment there is no tenant segment and the provider receives no tenant.

## How long a change takes to apply

Identity access is decided from cached facts, and a change in the Configuration Service becomes visible when the relevant cached fact refreshes.
**None of these lifetimes is a revocation guarantee.**

| What is cached | Default lifetime | What it affects |
| --- | --- | --- |
| The API client's application context, per client and tenant | `600` seconds (`CacheSettings:ApplicationContextCacheExpirationSeconds`) | Whether the client still exists and belongs to the tenant. |
| The claim sets, per tenant spelling | `600` seconds (`CacheSettings:ClaimSetsCacheExpirationSeconds`) | Which claims and actions a claim set grants. |
| A successful bearer-token validation | `300` seconds (`JwtAuthentication:ValidatedTokenCacheEntryMaxLifetimeSeconds`), and never past the token's expiry less `JwtAuthentication:ClockSkewSeconds` | Whether the token is validated again. A token is accepted until its expiry plus the clock skew whether or not it is cached. |
| The tenant snapshot | `60` seconds, fixed | Whether the tenant in the URL exists. |
| A provider's own policy caches | Set by the provider | Which clients and namespaces the provider grants. |

What follows from those lifetimes.

- **Deleting an API client may stay invisible for up to the application cache lifetime.**
  Until the cached application context expires, the Ed-Fi API still treats the client as bound to its tenant.
  A lookup that finds no client is not cached, so a client that never existed is refused at once.
- **Removing an action from a claim set may stay invisible for up to the claim cache lifetime.**
  The change appears when the cached claim sets refresh, or when an operator [reloads them](#reloading-the-claim-set-cache).
- **Changing the claim set name assigned to a client does not rewrite a token already issued.**
  The token carries the scope it was issued with, and it keeps that scope until it expires.
  Only a token issued after the change carries the new name.
- **Neither the 60-second tenant lifetime nor ten minutes is a revocation guarantee.**
  The 60-second snapshot bounds how long a deleted tenant can still be accepted, and it says nothing about a client.
  The ten-minute figure is the default for two caches, and a deployment can set either to another value.
  A provider's policy caches and any fill already in flight when a change is made add to the delay.
- **Resetting a client's credential does not invalidate a JWT it was already issued.**
  The old token stays valid until it expires, plus the clock skew.
- **All of this state is local to each replica.**
  A reload, a restart, or an expiry on one replica does nothing to another.

The cache design itself, including how concurrent misses share one fetch, is in the [Caching Strategy](./CACHING-STRATEGY.md#1-application-context-cache).

## Reloading the claim-set cache

The Ed-Fi API has one operator route that clears an identity-relevant cache, and it clears the claim sets.
Every gate below must be open before it works.

1. **`AppSettings:EnableManagementEndpoints` is `true`.**
   Otherwise the `/management` routes are not mapped at all.
2. **`AppSettings:ManagementEndpoints:RequiredRole` holds a usable role, and `JwtAuthentication:RoleClaimType` is set.**
   Without both, the routes are not mapped.
   Every call must then carry a bearer token that holds that role under `JwtAuthentication:RoleClaimType`.
   A call with no valid token answers `401`, and a valid token without the role answers `403`.
3. **`AppSettings:EnableClaimsetReload` is `true`.**
   It is checked last, after authorization, and when it is `false` the route answers a `404` with no body.
   It does not control whether the route is mapped.

The routes are `POST /management/reload-claimsets` on a single-tenant host and `POST /management/{tenant}/reload-claimsets` on a multi-tenant host.
**On a multi-tenant host the unscoped `/management/reload-claimsets` answers `404`, because it names no tenant, even for a caller that is authorized.**
See [Configuration](./CONFIGURATION.md#appsettings) for the three controls.

A reload has a narrow effect.

- It clears the claim sets of the one replica that served the request.
- It clears only the tenant spelling the request names.
  `North` and `north` are two entries, so a successful reload for `North` leaves any access cached under `north` in place.
- Other replicas keep their entries until each expires or is reloaded in turn.
- A fetch that began before the reload never writes its older answer back into the cache.

**A full purge is therefore a drain and a restart of every serving replica, or waiting for every earlier entry to expire.**
Reloading each spelling you know of, and verifying the result, is not a complete purge, because it cannot reach a spelling nobody tried.

## Application reload and urgent revocation

**The application-context cache has no operator reload.**
Reloading an application context is an internal operation of the Ed-Fi API, and it has no route, no endpoint, and no configuration switch.
This chapter introduces no new revocation or cache API.

With the default in-process cache, clearing the application context means draining requests and restarting every serving replica, or waiting for the cached entries to expire.
A deployment that added a distributed cache must also invalidate those entries and the local copies each replica holds.

**To remove access urgently, use the full procedure.**

1. Stop the affected traffic to every replica, so nothing refills a cache from the old policy while you work.
2. Update the policy: delete or change the API client, or change the claim set, in the Configuration Service.
3. Clear the caches.
   Drain and restart every serving replica so that every claim-cache spelling of the tenant is cleared along with the application contexts.
   Clear any distributed cache and each provider's own policy cache as well.
4. Verify denial before you resume traffic.
   Send a request with the original token to each replica, on the tenant route spelled in each case variant you can think of, and confirm that each is refused.
5. Resume traffic.

The verification supplements the full reset and cannot replace it.
A request you did not send proves nothing about the entries you did not test.
Restarting does not shorten a token that was already issued, so an issued token still works until it expires, plus the clock skew, wherever a cache still says it may.

## Rate limiting

The identity routes are subject to the same global rate limiter as the rest of the Ed-Fi API, configured under `RateLimit`.
See [RateLimit](./CONFIGURATION.md#ratelimit).
A request the limiter rejects answers `429`.
It carries a `Retry-After` header only when the limiter supplies one.

Every response from an identity operation route carries `Cache-Control: no-store`, whatever its status, and the `429` is no exception.
The Ed-Fi API sets the header after routing and before the rate limiter runs, so a request the limiter rejects, which never reaches Core, still carries it.
The implementer guide's [Cache-Control](../src/dms/core/EdFi.DataManagementService.Identity/IDENTITY.md#cache-control) section states the rule for providers.

## Datastore independence and the tenant snapshot

Identity operations never read or write a data store.
**This holds only for a request on a host that has initialized and is running.**
The fatal startup phases, such as a failed schema validation, and the readiness behavior are unchanged, so a host that did not start serves no identity request either.

The tenant check on a multi-tenant host reads one snapshot of tenant names per process.

- **The snapshot is fresh for 60 seconds** after a successful refresh.
  Once it expires, nothing answers from it, and the next request waits for a new one.
- **One refresh runs per process.**
  Concurrent callers share it.
- **A tenant name missing from a fresh snapshot triggers one shared refresh**, at most once every 5 seconds, so a tenant created a moment ago is recognized within seconds.
- **A failed refresh starts a 5-second cooldown**, during which no new refresh begins.
- **There is no stale membership after expiry.**
  A tenant deleted in the Configuration Service can still be accepted for up to 60 seconds, and never longer through this check.
- **When a refresh is required and fails, the request answers `503`.**
  A failed refresh does not fail a request the fresh snapshot can still answer.

The 60-second, 5-second, and 30-second values are fixed and have no configuration.
See [Identity tenant snapshot](./CACHING-STRATEGY.md#9-identity-tenant-snapshot).

## CMS client lifecycle

An API client for identity operations is created, updated, and removed through the Configuration Service like any other.
A client may be created with no data store assignment, read and updated, have its credential reset, acquire a token, and be deleted.
An empty assignment grants no resource access.
The full lifecycle, with the requests and the expected responses, is in [Clients With No Data Store Assignment](./API-CLIENT-AND-INSTANCE-CONFIGURATION.md#clients-with-no-data-store-assignment).

## Cancellation, cache fills, and accepted jobs

Three things that all look like "the request was cancelled" are different.

- **A caller that cancels stops waiting.**
  On the tenant check, a caller's own cancellation propagates and is never turned into a `404` or `503` answer.
- **A shared cache fill is not cancelled for the other callers waiting on it.**
  The claim sets and the tenant snapshot are fetched by one shared fill that runs on no caller's token, so one caller cancelling ends only its own wait.
- **An accepted asynchronous job keeps running.**
  A cancelled or dropped request does not cancel a job the provider already accepted, and the provider retains its result for its own documented period.
  See [Asynchronous requests and jobs](../src/dms/core/EdFi.DataManagementService.Identity/IDENTITY.md#asynchronous-requests-and-jobs).

The host bounds two of its own fetches at **30 seconds** each: the tenant snapshot refresh and the claim-set fill.
A fetch that runs past that bound counts as a failure.
Those bounds are separate from the provider calls.
**The Ed-Fi API applies no timeout to a call into an `IIdentityService` and never retries it.**
A slow provider holds the request open until the provider answers or the client gives up, so a provider must bound its own calls.

## Authorization strategy

The identity service claim must carry exactly one authorization strategy, `NoFurtherAuthorizationRequired`.
Identity operations have no resource, no data store, and no ownership for a strategy to filter on.
Any other configuration fails closed: the request answers `500` with the problem type `urn:ed-fi:api:system:configuration:security`, and the log names the claim set and the action.
Correct the claim in the Configuration Service, and the next request after the claim sets refresh succeeds.

## CORS

A browser-based client that reads the asynchronous flow must read the `Location` header of a `202` and of an incomplete results poll.
**The Ed-Fi API exposes `Location` through CORS only to the one origin named by `Cors:SwaggerUIOrigin`, and only when `AppSettings:EnableIdentityManagement` is `true`.**
`Cors:SwaggerUIOrigin` defaults to `http://localhost:8082`, and its environment override is `Cors__SwaggerUIOrigin`.
The shipped Docker Compose files map it from `DMS_SWAGGER_UI_URL`.

A browser client served from any other origin cannot read the header, so deploy it at that origin or change the setting.
Clients that are not browsers are not affected.
With the surface off, nothing extra is exposed.
See [Cors](./CONFIGURATION.md#cors).

## Logging

Identity request paths, which can carry a UniqueId or a results token, are redacted before any log sink writes them.
The redaction is always on, and it does not cover the provider exception detail logged at `Debug`.
See the `Path` field in [Logging Policy](./LOGGING.md#cms-and-dms-request-log-console-contract).
