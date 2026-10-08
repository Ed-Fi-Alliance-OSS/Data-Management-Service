# DMS Caching Strategy

## Overview

The Ed-Fi API (DMS) implements a comprehensive caching
strategy to optimize performance and reduce latency for frequently accessed
data. All caching in DMS is **in-memory** and **local to each instance**,
meaning there is no distributed cache. This design provides simplicity and
fast access times while requiring consideration for multi-instance deployments.

## Cache Characteristics

### In-Memory vs Distributed

DMS uses exclusively **in-memory caching**:

- **No distributed cache** - Each data store maintains its own cache
- **No Redis or external cache stores** - Caching uses `HybridCache` (with
  in-memory storage only) or `ConcurrentDictionary`
- **Instance isolation** - Cache state is not shared between data stores

This approach is appropriate for DMS because:

- Cached data is either static (API schemas) or eventually consistent
  (claim sets, application contexts)
- TTL-based expiration ensures data freshness within acceptable bounds
- The operational simplicity outweighs the benefits of distributed caching

### Hot vs Cold Cache

DMS **warms most caches on startup** to minimize latency for initial requests.
Only per-client and per-connection caches remain cold:

| Cache            | Strategy | Behavior                       |
| ---------------- | -------- | ------------------------------ |
| Compiled Schemas | Warm     | Pre-compiled on startup        |
| OIDC Metadata    | Warm     | Loaded during startup          |
| data stores    | Warm     | Loaded from CMS on startup     |
| ClaimSets        | Warm     | Loaded from CMS on startup     |
| CMS Token        | Warm     | Fetched as startup dependency  |
| App Context      | Cold     | Fetched per client on auth     |
| Identity Tenants | Cold     | Fetched on first identity call |
| NpgsqlDataSource | Cold     | Created on first DB connection |

## Cache Implementations

### 1. Application Context Cache

**Purpose:** Caches application context data retrieved from the Configuration
Service, mapping API clients to their authorized data stores.

**Location:**

- `src/dms/core/.../Configuration/CachedApplicationContextProvider.cs`

**Implementation:** `HybridCache` (Microsoft.Extensions.Caching.Hybrid) with
built-in stampede protection

**Cache Structure:**

- **Key:** scoped to the requesting tenant:
  - Single tenant: `ApplicationContext:single:{clientId}`
    (e.g., `ApplicationContext:single:my-api-client`)
  - Multi-tenant: `ApplicationContext:tenant:{tenant.ToLowerInvariant()}:{clientId}`
    (e.g., `ApplicationContext:tenant:tenant-a:my-api-client`)
- **Value:** Single `ApplicationContext` record containing:
  - `Id` - API client database ID
  - `ApplicationId` - Parent application ID
  - `ClientId` - Client identifier string
  - `ClientUuid` - Client UUID
  - `DataStoreIds` - List of authorized data store IDs
  - `CreatorOwnershipTokenId` - Ownership token assigned to documents this
    client creates, or null when the client has none
  - `OwnershipTokenIds` - Ownership tokens this client is authorized against

**Multi-Tenancy Support:** Yes - the tenant is part of the cache key, so the
same API client resolves and caches an independent context per tenant. Each
client's `DataStoreIds` determine which data they can access.

**Dependency Scope:** Every authenticated resource request path that runs
`ProfileResolutionMiddleware` requires a resolvable application context, because
profile resolution needs the `ApplicationId`. This is broader than the
ownership-gated operations alone. With a cold or missing cache entry, a CMS
outage or a malformed CMS response makes those requests fail closed with
`503 Service Unavailable`. A `NotFound` context still maps to
`401 Unauthorized` as an invalid token.

**TTL:** 10 minutes (configurable via `CacheSettings:ApplicationContextCacheExpirationSeconds`)

**Cache Operations:**

| Operation | Method                        | Description             |
| --------- | ----------------------------- | ----------------------- |
| Set       | `CacheApplicationContext()`   | Stores context with TTL |
| Get       | `GetCachedApplicationContext` | Retrieves cached ctx    |
| Remove    | `ClearCacheForClient()`       | Removes specific client |

Note: Bulk cache clearing is not supported. Individual entries expire via TTL
or can be cleared per-client. For emergency clearing, restart the service.

**Invalidation Strategy:**

- TTL-based expiration after the configured duration
- Manual invalidation via `ReloadApplicationByClientIdAsync()`
- Follows cache-aside pattern with fallback to Configuration Service

---

### 2. ClaimSets Cache

**Purpose:** Caches security claim sets containing authorization rules and
resource access permissions from the Configuration Service.

**Location:**

- `src/dms/core/.../Security/CachedClaimSetProvider.cs`

**Implementation:** `IMemoryCache`, which keeps the full claim-set graph in process and avoids HybridCache payload serialization limits.
Concurrent misses for one key share a single fetch from the Configuration Service, and each caller stops waiting on its own cancellation without aborting the fetch for the others.
A caller whose request is already cancelled gets no answer, not even a cached one.
The shared fetch runs on no caller's token and is bounded by a 30-second budget; host shutdown does not cancel it, so a request still draining gets its claim sets.
A reload advances a per-key generation, so a fetch that started before the reload never writes the pre-reload claim sets back into the cache.

**Cache Structure:**

- **Key:** `ClaimSets` (single-tenant) or `ClaimSets:{tenant}` (multi-tenant), with the tenant spelled exactly as the request spelled it
- **Value:** `IList<ClaimSet>` - list of claim set records, each containing:
  - `Name` - Claim set name (e.g., "SIS Vendor", "Ed-Fi Sandbox")
  - `ResourceClaims` - List of `ResourceClaim` records, each containing:
    - `Name` - Resource name (e.g., "students", "educationOrganizations")
    - `Action` - Allowed action (Create, Read, Update, Delete)
    - `AuthorizationStrategies` - Array of authorization strategy configurations

**Configuration:**

```json
{
  "CacheSettings": {
    "ClaimSetsCacheExpirationSeconds": 600
  }
}
```

**TTL:** 10 minutes (configurable via `CacheSettings:ClaimSetsCacheExpirationSeconds`)

**Multi-Tenancy Support:** Yes - one cache entry per tenant

**Warm-up:** Loaded on startup by `CacheClaimSetsTask` (Order 410), run by `DmsStartupOrchestrator` during the `InitializeAuthMetadata` phase.

**Cache Operations:**

| Operation | Method                          | Description                                         |
| --------- | ------------------------------- | --------------------------------------------------- |
| Get       | `GetAllClaimSets(tenant)`       | Returns the cached list, or joins or starts a fetch |
| Remove    | `InvalidateCacheAsync(tenant)`  | Removes the tenant's entry and any in-flight fetch  |

**Invalidation Strategy:**

- TTL-based expiration after the configured duration
- Manual invalidation via `/management/reload-claimsets` management endpoint
- A reload is replica-local and exact-spelling only: it clears the entry on the one DMS replica
  that serves it, and only for the tenant spelling the request names.
  Other replicas keep their entries, and so does another spelling of the same tenant on that
  replica (`north` after a reload for `North`), until each entry expires.
  Draining or restarting every replica clears all cached variants.
- Requires `AppSettings:EnableManagementEndpoints: true` to map the DMS claimset management
  route surface.
- Requires a valid `AppSettings:ManagementEndpoints:RequiredRole`; the endpoints are not mapped
  without one, and callers must present a bearer token carrying that role under
  `JwtAuthentication:RoleClaimType`.
- Requires `AppSettings:EnableClaimsetReload: true` for the reload and view operations to execute
  after authorization. See [Configuration](./CONFIGURATION.md#appsettings).

---

### 3. Compiled JSON Schema Cache

**Purpose:** Caches pre-compiled JSON schemas for document validation,
avoiding expensive schema compilation on every request.

**Location:**

- `src/dms/core/.../Validation/CompiledSchemaCache.cs`

**Implementation:** `ConcurrentDictionary<SchemaCacheKey, JsonSchema>` (custom)

**Cache Structure:**

- **Key:** `SchemaCacheKey` record struct containing:
  - `ProjectName` - API project name (e.g., "Ed-Fi")
  - `ResourceName` - Resource name (e.g., "students", "schools")
  - `Method` - HTTP method (POST or PUT)
- **Value:** `JsonSchema` - pre-compiled JSON schema for request validation

**Configuration:** None required - cache lifetime is managed internally

**TTL:** Infinite (schema is fixed at startup and immutable for the process lifetime)

**Cache Operations:**

| Operation | Method          | Description              |
| --------- | --------------- | ------------------------ |
| Get/Add   | `GetOrAdd(...)` | Lazy compilation         |
| Prime     | `Prime(docs)`   | Pre-compiles all schemas |

**Invalidation Strategy:**

The compiled schema cache does not require runtime invalidation. The API schema is loaded
once at startup and is immutable for the lifetime of the process. The cache is primed
during startup and entries are never evicted or replaced.

**Priming Behavior:**

- On startup, `ProvideApiSchemaMiddleware` calls `Prime()` to pre-compile all
  schemas
- Priming iterates through all project schemas and resources
- Both POST and PUT schema variants are compiled

---

### 4. Configuration Service Token Cache

**Purpose:** Caches OAuth bearer tokens used to authenticate with the
Configuration Management Service (CMS).

**Location:**

- `src/dms/core/.../Security/ConfigurationServiceTokenHandler.cs`

**Implementation:** `HybridCache` (Microsoft.Extensions.Caching.Hybrid) with
built-in stampede protection

**Cache Structure:**

- **Key:** `ConfigServiceToken` (static string, single entry)
- **Value:** `string` - the OAuth access token (e.g., `"eyJhbGciOiJSUzI1NiIs..."`)

**TTL:** 25 minutes (fixed, safely less than typical 30-minute token lifetime)

**Cache Operations:**

| Operation | Method                     | Description            |
| --------- | -------------------------- | ---------------------- |
| Get       | `TryGetValue(key, out t)`  | Retrieves cached token |
| Set       | `Set(key, tok, TimeSpan)`  | Stores with lifetime   |

**Invalidation Strategy:**

- TTL matches the token's `expires_in` value from CMS response
- No manual invalidation - relies on automatic expiration
- Cache-aside pattern: fetches new token when cache miss occurs

---

### 5. NpgsqlDataSource Cache

**Purpose:** Caches PostgreSQL `NpgsqlDataSource` instances to enable proper
connection pooling and avoid connection string parsing overhead.

**Location:**

- `src/dms/backend/.../Postgresql/NpgsqlDataSourceCache.cs`
- `src/dms/backend/.../Postgresql/NpgsqlDataSourceProvider.cs`

**Implementation:** `Dictionary<string, entry>` guarded by a lock (singleton). Every entry carries
its data source, a lease count, and a retirement flag; the public surface is leased-only, so a data
source cannot be obtained without also holding the claim that keeps it alive.

**Cache Structure:**

- **Key:** Configured connection string (e.g., `"Host=localhost;Database=edfi;..."`)
- **Value:** `NpgsqlDataSource` instance - manages its own internal connection pool - plus its lease
  count and retirement state

**Connection Pool Settings:**

```csharp
csb.NoResetOnClose = true;           // Skip RESET/DISCARD on return
csb.ApplicationName = "EdFi.DMS";    // For monitoring
csb.AutoPrepareMinUsages = 3;        // Auto-prepare after 3 uses
csb.MaxAutoPrepare = 256;            // Max prepared statements
```

**TTL:** None. An entry lives while the Configuration Service still names its connection string or
any lease on it is outstanding.

**Architecture:**

- **Singleton Cache:** Shared across all requests, keyed by connection string
- **Scoped Provider:** Per-request access holding one lazily taken lease for the request's
  write-once effective target

**Cache Operations:**

| Operation | Method                                | Description                                           |
| --------- | ------------------------------------- | ----------------------------------------------------- |
| Lease     | `AcquireLease(connStr)`               | Builds or reuses the source, counting a claim on it   |
| Open      | `OpenLeasedConnectionAsync(connStr)`  | Leases and opens a connection, transferring both      |
| Reconcile | `Reconcile(snapshot)`                 | Retires entries no configuration names any more       |
| Dispose   | `Dispose()`                           | Disposes all remaining sources at shutdown            |

**Invalidation Strategy:**

- Configuration reconciliation: when a data-store publication stops naming a connection string, the
  entry is retired and its data source disposed once the last lease is released
- All remaining data sources disposed on application shutdown as a backstop
- Implements `IDisposable` for proper cleanup

---

### 6. data store cache

**Purpose:** Caches data store configurations (connection strings, route
contexts) from the Configuration Service for multi-instance routing.

**Location:**

- `src/dms/core/.../Configuration/ConfigurationServiceDataStoreProvider.cs`

**Implementation:** `ConcurrentDictionary<string, TenantCacheEntry>` (custom) where each entry tracks the cached list plus the last refresh timestamp

**Cache Structure:**

- **Key:** Tenant identifier (empty string `""` for default/single-tenant, or tenant name)
- **Value:** `IList<DataStore>` - list of data store records, each containing:
  - `Id` - Unique data store identifier
  - `DataStoreType` - Type/category of the data store
  - `Name` - Display name
  - `ConnectionString` - Database connection string (nullable)
  - `RouteContext` - Dictionary mapping route qualifier names to values
    (e.g., `{"schoolYear": "2024", "district": "255901"}`)

**TTL:** Application lifetime (no automatic expiration)

**Multi-Tenancy Support:** Yes - one cache entry per tenant

**Warm-up:** Loaded on startup via `InitializeDataStores()` in `Program.cs`

**Cache Operations:**

| Operation | Method                  | Description                  |
| --------- | ----------------------- | ---------------------------- |
| Load      | `LoadDataStores(t)`   | Fetches and caches instances |
| Refresh   | `RefreshInstancesIfExpiredAsync(t)` | Reloads the cache when TTL expires |
| Get All   | `GetAll(tenant)`        | Returns all for tenant       |
| Get By ID | `GetById(id, tenant)`   | Returns specific instance    |
| Check     | `IsLoaded(tenant)`      | Checks if tenant data cached |
| Tenants   | `GetLoadedTenantKeys()` | Returns cached tenant keys   |

**Invalidation Strategy:**

- **TTL-based refresh** - `ResolveDataStoreMiddleware` checks `RefreshInstancesIfExpiredAsync()` on every request and reloads the cached configuration when the configured TTL expires. The refresh logs `"data store cache expired for tenant {Tenant} ..."` when a reload happens.
- **Cache-miss fallback for new tenants** - `TenantValidator` triggers `LoadDataStores()` when an unknown tenant is requested, allowing new tenants to be added without restart.
- **Limitation:** Updates to existing tenant instances or deleted tenants still require either a TTL expiration (or manual reload) or application restart to take effect if the TTL is disabled.

---

### 7. OIDC Metadata Cache

**Purpose:** Caches OpenID Connect discovery metadata including JWT signing
keys for token validation.

**Location:**

- `src/dms/core/.../DmsCoreServiceExtensions.cs`

**Implementation:** `ConfigurationManager<OpenIdConnectConfiguration>`
(Microsoft.IdentityModel.Protocols)

**Cache Structure:**

- **Key:** Implicit - singleton instance bound to configured `MetadataAddress`
- **Value:** `OpenIdConnectConfiguration` object containing:
  - `Issuer` - Token issuer identifier
  - `TokenEndpoint` - URL for token requests
  - `AuthorizationEndpoint` - URL for authorization
  - `JsonWebKeySet` - JWT signing keys (JWKS) for signature validation
  - Additional OIDC metadata fields

**Configuration:**

```json
{
  "JwtAuthentication": {
    "RefreshIntervalMinutes": 60,
    "AutomaticRefreshIntervalHours": 24,
    "MetadataAddress": "http://localhost:5126/.well-known/openid-configuration"
  }
}
```

**TTL:**

- **RefreshInterval:** 60 minutes (minimum time between forced refreshes)
- **AutomaticRefreshInterval:** 24 hours (background refresh interval)

**Warm-up:** Loaded on startup by `WarmUpOidcMetadataTask` (Order 400), run by
`DmsStartupOrchestrator` during the `InitializeAuthMetadata` phase. The startup
will fail if OIDC metadata cannot be retrieved from the identity provider, or if
the retrieved document's `issuer` does not equal `JwtAuthentication:Authority`
exactly, or if its `jwks_uri` is not on the `JwtAuthentication:MetadataAddress` origin
(a refresh that meets such a `jwks_uri` keeps the last good metadata and is retried until a
fetch succeeds; once AutomaticRefreshInterval is due the library retries on every request, so
only the first refusal is logged as an error and repeats at debug level), ensuring DMS
doesn't accept requests until JWT authentication is fully functional. When `BypassAuthorization` is enabled, the warm-up is skipped.

**Invalidation Strategy:**

- Background refresh after AutomaticRefreshInterval (24 hours), handled
  internally by Microsoft.IdentityModel.Protocols library
- An issuer mismatch at request time rejects the token and triggers
  `RequestRefresh()`, at most once per RefreshInterval (60 minutes); later
  requests succeed once a matching document is adopted. Only the first rejected
  request is logged as an error; repeats log at debug level until then

---

## Stampede Protection

Stampede protection ensures that when multiple concurrent requests experience a cache miss, only one fetch runs while the others wait for its result.
The application-context and Configuration Service token caches get it from `Microsoft.Extensions.Caching.Hybrid` (HybridCache).
The claim-set cache and the identity tenant snapshot share one in-flight fetch task instead, so a caller that stops waiting never aborts the fetch the others still need.

### How It Works

1. **First request** experiences a cache miss and starts the fetch
2. **Subsequent concurrent requests** for the same key wait on that same fetch
3. **The fetch runs once**, and its result is cached
4. **All waiting requests** receive that result

This prevents the "thundering herd" problem where N concurrent requests
could trigger N redundant backend calls, potentially overwhelming the
Configuration Service during cache expiration under high load.

### Protected Caches

| Cache                          | Stampede Protected | Implementation                 |
| ------------------------------ | ------------------ | ------------------------------ |
| ClaimSets                      | Yes                | One shared fetch per key       |
| Application Context            | Yes                | HybridCache.GetOrCreateAsync   |
| Configuration Service Token    | Yes                | HybridCache.GetOrCreateAsync   |
| Identity tenant snapshot       | Yes                | One shared refresh per process |
| Compiled Schemas               | No*                | ConcurrentDictionary.GetOrAdd  |
| NpgsqlDataSource               | No*                | Single-winner publish under lock |
| data stores                  | No                 | Direct assignment on startup   |
| OIDC Metadata                  | Yes                | ConfigurationManager (built-in)|

*These caches provide no waiting behavior - concurrent requests may execute
the factory multiple times, though only one result is stored. Compiled Schemas
uses `ConcurrentDictionary.GetOrAdd()`; the data-source cache builds outside
its lock, publishes a single winner under it, and disposes the losing
candidate.

### Configuration

HybridCache is configured in `WebApplicationBuilderExtensions.cs`:

```csharp
webAppBuilder.Services.AddMemoryCache();
webAppBuilder.Services.AddHybridCache();
```

Per-cache TTL is configured via `CacheSettings`:

```json
{
  "CacheSettings": {
    "ClaimSetsCacheExpirationSeconds": 600,
    "ApplicationContextCacheExpirationSeconds": 600,
    "TokenCacheExpirationSeconds": 1500,
    "ProfileCacheExpirationSeconds": 1800,
    "DataStoreCacheRefreshEnabled": true,
    "DataStoreCacheExpirationSeconds": 600,
    "DerivativeValidationCacheExpirationSeconds": 600
  }
}
```

Default values: ClaimSets, AppContext, data store, and derivative validation = 10 minutes; Token = 25 minutes; Profile = 30 minutes.

### Derivative validation cache expiration

`CacheSettings.DerivativeValidationCacheExpirationSeconds` sets how long a validation verdict for a
read replica or snapshot database stays cached.

| Configured value | Effective value | Logged |
| ---------------- | --------------- | ------ |
| absent           | `600`           | no     |
| `1`–`3600`       | as configured   | no     |
| `0` or negative  | `600`           | warning naming the configured and effective value |
| above `3600`     | `3600`          | warning naming the configured and effective value |

**A non-positive value means "use the default", not "never expire."** That inverts the convention of
`DataStoreCacheExpirationSeconds` in the same section, where `0` or a negative value keeps the cached
configuration until an explicit reload. The inversion is deliberate and there is no way to opt out of
expiry: a derivative is a database an operator can rebuild or repoint without telling DMS, so a
verdict about one that never expired would outlive the database it describes.

**The resolved value is further bounded by the data store cache TTL.** A derivative's connection
string comes from the cached data store configuration, so when `DataStoreCacheRefreshEnabled` is
`true` and `DataStoreCacheExpirationSeconds` is positive, the effective expiration is the smaller of
the two, and a verdict never outlives the connection string it was reached for. When refresh is
disabled, or `DataStoreCacheExpirationSeconds` is `0` or negative, that configuration is held until an
explicit reload; there is no shorter lifetime to bound by, so the resolved value is used as is. The
result is bounded in every case, because the resolved value already is.

---

### 8. Validation caches

Two in-process caches hold what DMS has concluded about a database: `DatabaseFingerprintProvider`
caches the `dms.EffectiveSchema` fingerprint, and `ResourceKeyValidationCacheProvider` caches the
resource-key seed verdict. Both are `ConcurrentDictionary` singletons in the DMS process, not
distributed, so each instance in a multi-instance deployment validates independently.

Both are keyed by `(policy class, configured connection string)`. The connection string is the one
configured in the Configuration Service, never a provider-realized form, so cache identity needs no
connection-string parsing and a value no provider could open still has a stable identity.

The policy class is `Primary` for a parent's own database and `Derivative` for a read replica or
snapshot. Including it in the key is what keeps a primary and a derivative apart when their configured
text happens to be identical — otherwise one of them would inherit the other's lifetime.

The key deliberately carries no data store id and no tenant. Two data stores configured with the same
connection string are the same database, so they share one entry and one verdict; that is the point of
keying on what is being validated rather than on who asked for it. A verdict is likewise not per
tenant: a database two tenants can reach is validated once.

| Policy class | Successful verdict | Negative verdict (unprovisioned, wrong hash, key mismatch) | Fault |
| --- | --- | --- | --- |
| `Primary` | cached for the process lifetime | cached for the process lifetime | evicted, except a malformed-fingerprint failure, which is retained |
| `Derivative` | cached until `CacheSettings.DerivativeValidationCacheExpirationSeconds` elapses, bounded by the data store cache TTL | dropped by the request that read it | evicted |

The asymmetry is deliberate. Repairing a primary requires an operator and a service restart either
way, so re-probing per request buys nothing. A derivative can be rebuilt, reseeded, or repointed
underneath a running service with nothing telling DMS, so a verdict about one must not outlive the
request that found it unusable.

Two mechanisms enforce that. Faults are evicted by the cache entry itself, naming exactly the entry
whose production failed. Negative verdicts are dropped by the middleware that interpreted them —
a missing `dms.EffectiveSchema` row, a schema-hash mismatch, and a resource-key mismatch are all
successful reads of a bad answer rather than exceptions, so only the reader can classify them — using
a token that names exactly the entry it observed. Every removal, expiry included, is version-exact:
a late verdict from a superseded entry can never delete the replacement.

Startup validation (`ValidateStartupInstancesTask`) uses `Primary` keys only. It does not enumerate,
probe, or prime any derivative, because a derivative may be intentionally offline between extraction
windows; the first request routed to one is what reaches it.

### 9. Identity tenant snapshot

**Purpose:** Answers whether the tenant named in an identity request's URL exists, for the `/identity/v2` routes only.

**Location:**

- `src/dms/core/.../Identity/IdentityTenantSnapshot.cs`

**Implementation:** One immutable, case-insensitive set of tenant names per process, fetched through the Configuration Service tenant list (`IDataStoreProvider.LoadTenants`).
It never loads data stores or decrypts connection strings.
Concurrent cold, expired, or missing-name callers share one refresh, which runs on no caller's token with a 30-second budget and ends early when the host shuts down.

**Freshness:**

- A snapshot is fresh for 60 seconds after a successful refresh completes, and a stale snapshot never answers.
- A name missing from a fresh snapshot triggers one shared refresh, at most once every 5 seconds after a successful refresh, so a newly created tenant is recognized within seconds while repeated unknown names cannot drive back-to-back fetches.
- A tenant deleted in the Configuration Service can still be accepted for up to 60 seconds.

**Failures:** When a refresh fails, its waiters are answered from the snapshot if it is still fresh, and otherwise with `503`.
No new refresh starts for 5 seconds after a failure.
A caller's own cancellation propagates and is never turned into an answer.

**Configuration:** None; the 60-second, 5-second, and 30-second values are fixed.

---

## Summary Table

| Cache        | Mechanism    | Scope | TTL    | Tenant | Stampede | Invalidation |
| ------------ | ------------ | ----- | ------ | ------ | -------- | ------------ |
| App Context  | HybridCache  | Sing. | 10 min | Yes    | Yes      | Manual + TTL |
| ClaimSets    | MemoryCache  | Sing. | 10 min | Yes    | Yes      | Manual + TTL |
| Id. Tenants  | Snapshot     | Sing. | 60 s   | N/A    | Yes      | TTL + miss refresh |
| Comp. Schema | ConcurDict   | Sing. | None   | No     | No       | Reload ID    |
| CMS Token    | HybridCache  | Sing. | 25 min | No     | Yes      | TTL only     |
| NpgsqlDS     | Dict + lock  | Sing. | None   | N/A    | No       | Reconcile + Shutdown |
| data store | ConcurDict   | Sing. | Configurable (CacheSettings.DataStoreCacheExpirationSeconds) | Yes    | No       | TTL + Restart      |
| OIDC Meta    | ConfigMgr    | Sing. | 60 min | No     | Yes      | Auto-refresh |
| Fingerprint  | ConcurDict   | Sing. | Primary: none. Derivative: CacheSettings.DerivativeValidationCacheExpirationSeconds, bounded by the data store TTL | No: keyed by policy class + configured connection string, so data stores and tenants sharing a connection string share one entry | Yes | TTL + reader token + fault eviction |
| Resource key | ConcurDict   | Sing. | Primary: none. Derivative: same as above | No: same key as above | Yes | TTL + reader token + fault eviction |

## Cache Invalidation Patterns

DMS uses several invalidation patterns:

### 1. TTL-Based Expiration

Used by: Application Context, ClaimSets, CMS Token, OIDC Metadata, data store cache

Cache entries automatically expire after a configured time period. This is the
simplest pattern and requires no manual intervention.

The data store cache tracks the last refresh timestamp and exposes
`RefreshInstancesIfExpiredAsync()`. `ResolveDataStoreMiddleware` invokes it at the
start of every request so that once `CacheSettings.DataStoreCacheExpirationSeconds`
elapses the next request reloads the configuration and logs
"data store cache expired for tenant {Tenant} ...".

### 2. Version-Based (Reload ID) Invalidation

Used by: Compiled Schema Cache

A unique identifier is associated with each version of the cached data. When
the version changes, all cache entries become stale and are replaced.

```text
Request arrives with reloadId = "abc-123"
  |
Current cache reloadId = "xyz-789" (different)
  |
Clear entire cache
  |
Set current reloadId = "abc-123"
  |
Re-prime cache with new schemas
```

### 3. Manual Invalidation

Used by: ClaimSets (via `/management/reload-claimsets` endpoint)

Administrators can trigger cache invalidation through management endpoints.
The DMS claimset management routes are mapped only when `AppSettings:EnableManagementEndpoints` is
`true`. They are additionally mapped only when `AppSettings:ManagementEndpoints:RequiredRole` holds a
valid role token, and every request must present a bearer token carrying that role under
`JwtAuthentication:RoleClaimType`; requests without one receive `401`, and tokens lacking the
role receive `403`. `AppSettings:EnableClaimsetReload` is checked after authorization by the
reload and view operations.

### 4. Lifetime-Based (Application Shutdown)

Used by: data stores; NpgsqlDataSource as a backstop only

Cache persists for the entire application lifetime and is only cleared on
shutdown. Suitable for rarely-changing configuration data. For NpgsqlDataSource
the shutdown disposal is the last resort: entries are ordinarily retired by
configuration reconciliation and disposed when their last lease is released.

## Design Patterns

### Decorator Pattern

`CachedApplicationContextProvider` and `CachedClaimSetProvider` wrap their
underlying providers to add caching behavior transparently:

```text
IClaimSetProvider
  |
CachedClaimSetProvider (decorator)
  |
ConfigurationServiceClaimSetProvider (implementation)
```

### Cache-Aside Pattern

All caches implement cache-aside:

1. Check cache for data
2. If found, return cached data
3. If not found, fetch from source
4. Store in cache
5. Return data

### Lazy Compilation

The compiled schema cache uses `ConcurrentDictionary.GetOrAdd()` to compile
schemas on-demand only when first requested.

## Configuration Reference

### appsettings.json

```json
{
  "CacheSettings": {
    "ClaimSetsCacheExpirationSeconds": 600,
    "ApplicationContextCacheExpirationSeconds": 600,
    "TokenCacheExpirationSeconds": 1500,
    "ProfileCacheExpirationSeconds": 1800,
    "DataStoreCacheRefreshEnabled": true,
    "DataStoreCacheExpirationSeconds": 600,
    "DerivativeValidationCacheExpirationSeconds": 600
  },
  "JwtAuthentication": {
    "RefreshIntervalMinutes": 60,
    "AutomaticRefreshIntervalHours": 24
  },
  "AppSettings": {
    "EnableManagementEndpoints": false,
    "EnableClaimsetReload": false,
    "ManagementEndpoints": {
      "RequiredRole": ""
    }
  }
}
```

### Hard-Coded Defaults

| Setting         | Value      | Location                         |
| --------------- | ---------- | -------------------------------- |
| CMS Token TTL   | 25 minutes | `CacheSettings` (configurable)   |
| Schema Cache    | No TTL     | Version-based invalidation       |

Note: All cache TTLs are now configurable via `CacheSettings`.

## Operational Considerations

### Multi-Instance Deployments

Since caches are local to each instance:

- Each data store maintains independent cache state
- TTL expiration may cause temporary inconsistencies between instances
- Schema is fixed at startup; no cross-instance coordination is needed for schema state
- Consider load balancer sticky sessions if cache consistency is critical

### Memory Usage

- Compiled schemas can consume significant memory for large API schemas
- ClaimSets cache grows with tenant count in multi-tenant deployments
- NpgsqlDataSource cache grows with unique connection strings
- Monitor memory usage in production environments

### Cache Warming

DMS warms most caches during startup (see `Program.cs`):

- **data stores** - Loaded from Configuration Service before accepting requests
- **ClaimSets** - Retrieved and cached for all tenants on startup
- **CMS Token** - Fetched as a dependency of the above operations
- **Compiled Schemas** - Primed via `ProvideApiSchemaMiddleware`
- **OIDC Metadata** - Fetched from identity provider on startup (fails fast if unavailable)

Only **Application Context** (per-client) and **NpgsqlDataSource**
(per-connection-string) remain cold, populated on first use.
