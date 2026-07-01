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

- **Key:** `ApplicationContext_{clientId}` (e.g., `ApplicationContext_my-api-client`)
- **Value:** Single `ApplicationContext` record containing:
  - `Id` - API client database ID
  - `ApplicationId` - Parent application ID
  - `ClientId` - Client identifier string
  - `ClientUuid` - Client UUID
  - `DataStoreIds` - List of authorized data store IDs

**Multi-Tenancy Support:** No - one cache entry per API client, not per tenant.
Each client's `DataStoreIds` determine which data they can access.

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

- TTL-based expiration after configured minutes
- Manual invalidation via `ReloadApplicationByClientIdAsync()`
- Follows cache-aside pattern with fallback to Configuration Service

---

### 2. ClaimSets Cache

**Purpose:** Caches security claim sets containing authorization rules and
resource access permissions from the Configuration Service.

**Location:**

- `src/dms/core/.../Security/CachedClaimSetProvider.cs`

**Implementation:** `HybridCache` (Microsoft.Extensions.Caching.Hybrid) with
built-in stampede protection

**Cache Structure:**

- **Key:** `ClaimSetsCache` (single-tenant) or `ClaimSetsCache:{tenant}` (multi-tenant)
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

| Operation | Method                | Description              |
| --------- | --------------------- | ------------------------ |
| Set       | `CacheClaimSets()`    | Stores with TTL          |
| Get       | `GetCachedClaimSets`  | Retrieves tenant data    |
| Remove    | `ClearCache(tenant)`  | Removes tenant's cache   |

**Invalidation Strategy:**

- TTL-based expiration after configured minutes
- Manual invalidation via `/claimsets/reload` management endpoint
- Requires `AppSettings:EnableClaimsetReload: true` to enable manual reload

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

**Implementation:** `ConcurrentDictionary<string, NpgsqlDataSource>` (singleton)

**Cache Structure:**

- **Key:** Database connection string (e.g., `"Host=localhost;Database=edfi;..."`)
- **Value:** `NpgsqlDataSource` instance - manages its own internal connection pool

**Connection Pool Settings:**

```csharp
csb.NoResetOnClose = true;           // Skip RESET/DISCARD on return
csb.ApplicationName = "EdFi.DMS";    // For monitoring
csb.AutoPrepareMinUsages = 3;        // Auto-prepare after 3 uses
csb.MaxAutoPrepare = 256;            // Max prepared statements
```

**TTL:** Application lifetime (disposed on shutdown)

**Architecture:**

- **Singleton Cache:** Shared across all requests, keyed by connection string
- **Scoped Provider:** Per-request access with instance-level caching

**Cache Operations:**

| Operation  | Method                 | Description                 |
| ---------- | ---------------------- | --------------------------- |
| Get/Create | `GetOrCreate(connStr)` | Creates or retrieves source |
| Dispose    | `Dispose()`            | Disposes all cached sources |

**Invalidation Strategy:**

- No runtime invalidation
- All data sources disposed on application shutdown
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
will fail if OIDC metadata cannot be retrieved from the identity provider,
ensuring DMS doesn't accept requests until JWT authentication is fully
functional. When `BypassAuthorization` is enabled, the warm-up is skipped.

**Invalidation Strategy:**

- Automatic refresh after RefreshInterval (60 minutes)
- Background refresh after AutomaticRefreshInterval (24 hours)
- Handled internally by Microsoft.IdentityModel.Protocols library

---

## Stampede Protection

DMS uses `Microsoft.Extensions.Caching.Hybrid` (HybridCache) to provide stampede
protection for frequently accessed caches. Stampede protection ensures that
when multiple concurrent requests experience a cache miss, only one request
executes the factory function to fetch data while others wait for the result.

### How It Works

1. **First request** experiences cache miss and acquires internal lock
2. **Subsequent concurrent requests** for the same key wait on the lock
3. **Factory executes once**, result is cached
4. **All waiting requests** receive the cached result
5. **Lock is released**

This prevents the "thundering herd" problem where N concurrent requests
could trigger N redundant backend calls, potentially overwhelming the
Configuration Service during cache expiration under high load.

### Protected Caches

| Cache                          | Stampede Protected | Implementation                 |
| ------------------------------ | ------------------ | ------------------------------ |
| ClaimSets                      | Yes                | HybridCache.GetOrCreateAsync   |
| Application Context            | Yes                | HybridCache.GetOrCreateAsync   |
| Configuration Service Token    | Yes                | HybridCache.GetOrCreateAsync   |
| Compiled Schemas               | No*                | ConcurrentDictionary.GetOrAdd  |
| NpgsqlDataSource               | No*                | ConcurrentDictionary.GetOrAdd  |
| data stores                  | No                 | Direct assignment on startup   |
| OIDC Metadata                  | Yes                | ConfigurationManager (built-in)|

*These caches use `ConcurrentDictionary.GetOrAdd()` which provides atomic
factory execution but not waiting behavior - concurrent requests may execute
the factory multiple times, though only one result is stored.

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
    "DataStoreCacheExpirationSeconds": 600
  }
}
```

Default values: ClaimSets, AppContext, and data store = 10 minutes; Token = 25 minutes; Profile = 30 minutes.

---

## Summary Table

| Cache        | Mechanism    | Scope | TTL    | Tenant | Stampede | Invalidation |
| ------------ | ------------ | ----- | ------ | ------ | -------- | ------------ |
| App Context  | HybridCache  | Sing. | 10 min | No     | Yes      | Manual + TTL |
| ClaimSets    | HybridCache  | Sing. | 10 min | Yes    | Yes      | Manual + TTL |
| Comp. Schema | ConcurDict   | Sing. | None   | No     | No       | Reload ID    |
| CMS Token    | HybridCache  | Sing. | 25 min | No     | Yes      | TTL only     |
| NpgsqlDS     | ConcurDict   | Sing. | None   | N/A    | No       | Shutdown     |
| data store | ConcurDict   | Sing. | Configurable (CacheSettings.DataStoreCacheExpirationSeconds) | Yes    | No       | TTL + Restart      |
| OIDC Meta    | ConfigMgr    | Sing. | 60 min | No     | Yes      | Auto-refresh |

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

Used by: ClaimSets (via `/claimsets/reload` endpoint)

Administrators can trigger cache invalidation through management endpoints.
Requires `EnableClaimsetReload: true` in configuration.

### 4. Lifetime-Based (Application Shutdown)

Used by: NpgsqlDataSource, data stores

Cache persists for the entire application lifetime and is only cleared on
shutdown. Suitable for rarely-changing configuration data.

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
    "DataStoreCacheExpirationSeconds": 600
  },
  "JwtAuthentication": {
    "RefreshIntervalMinutes": 60,
    "AutomaticRefreshIntervalHours": 24
  },
  "AppSettings": {
    "EnableClaimsetReload": false
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
