# DMS Caching Strategy

## Overview

The Ed-Fi Data Management Service (DMS) implements a comprehensive caching
strategy to optimize performance and reduce latency for frequently accessed
data. All caching in DMS is **in-memory** and **local to each instance**,
meaning there is no distributed cache. This design provides simplicity and
fast access times while requiring consideration for multi-instance deployments.

## Cache Characteristics

### In-Memory vs Distributed

DMS uses exclusively **in-memory caching**:

- **No distributed cache** - Each DMS instance maintains its own cache
- **No Redis or external cache stores** - All caching uses either
  `IMemoryCache` or `ConcurrentDictionary`
- **Instance isolation** - Cache state is not shared between DMS instances

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
| DMS Instances    | Warm     | Loaded from CMS on startup     |
| ClaimSets        | Warm     | Loaded from CMS on startup     |
| CMS Token        | Warm     | Fetched as startup dependency  |
| App Context      | Cold     | Fetched per client on auth     |
| NpgsqlDataSource | Cold     | Created on first DB connection |

## Cache Implementations

### 1. Application Context Cache

**Purpose:** Caches application context data retrieved from the Configuration
Service, mapping API clients to their authorized DMS instances.

**Location:**

- `src/dms/core/.../Configuration/ApplicationContextCache.cs`
- `src/dms/core/.../Configuration/CachedApplicationContextProvider.cs`

**Implementation:** `IMemoryCache` (Microsoft.Extensions.Caching.Memory)

**Cache Structure:**

- **Key:** `ApplicationContext_{clientId}` (e.g., `ApplicationContext_my-api-client`)
- **Value:** Single `ApplicationContext` record containing:
  - `Id` - API client database ID
  - `ApplicationId` - Parent application ID
  - `ClientId` - Client identifier string
  - `ClientUuid` - Client UUID
  - `DmsInstanceIds` - List of authorized DMS instance IDs

**Multi-Tenancy Support:** No - one cache entry per API client, not per tenant.
Each client's `DmsInstanceIds` determine which data they can access.

**TTL:** 10 minutes (configurable via `CacheExpirationMinutes`)

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

- `src/dms/core/.../Security/ClaimSetsCache.cs`
- `src/dms/core/.../Security/CachedClaimSetProvider.cs`

**Implementation:** `IMemoryCache` (Microsoft.Extensions.Caching.Memory)

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
  "ConfigurationServiceSettings": {
    "CacheExpirationMinutes": 10
  }
}
```

**TTL:** 10 minutes (configurable via `CacheExpirationMinutes`)

**Multi-Tenancy Support:** Yes - one cache entry per tenant

**Warm-up:** Loaded on startup via `RetrieveAndCacheClaimSets()` in `Program.cs`

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
  - `ReloadId` - GUID that changes on schema reload
- **Value:** `JsonSchema` - pre-compiled JSON schema for request validation

**Configuration:** None required - cache lifetime is managed internally

**TTL:** Infinite (until schema reload)

**Cache Operations:**

| Operation | Method                   | Description              |
| --------- | ------------------------ | ------------------------ |
| Get/Add   | `GetOrAdd(...)`          | Lazy compilation         |
| Prime     | `Prime(docs, reloadId)`  | Pre-compiles all schemas |
| Reset     | `ResetIfReloadChanged()` | Clears on version change |

**Invalidation Strategy:**

The compiled schema cache uses a **reload ID pattern** for invalidation:

1. Each API schema load generates a new GUID (reload ID)
2. The reload ID is included in cache keys
3. When a request arrives with a different reload ID:
   - The entire cache is cleared
   - Cache is re-primed with new schemas
4. This ensures atomic schema updates without stale entries

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

**Implementation:** `IMemoryCache` (Microsoft.Extensions.Caching.Memory)

**Cache Structure:**

- **Key:** `ConfigServiceToken` (static string, single entry)
- **Value:** `string` - the OAuth access token (e.g., `"eyJhbGciOiJSUzI1NiIs..."`)

**TTL:** Dynamic - based on token `expires_in` value from OAuth response (default: 1800 seconds / 30 minutes)

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

### 6. DMS Instance Cache

**Purpose:** Caches DMS instance configurations (connection strings, route
contexts) from the Configuration Service for multi-instance routing.

**Location:**

- `src/dms/core/.../Configuration/ConfigurationServiceDmsInstanceProvider.cs`

**Implementation:** `ConcurrentDictionary<string, IList<DmsInstance>>` (custom)

**Cache Structure:**

- **Key:** Tenant identifier (empty string `""` for default/single-tenant, or tenant name)
- **Value:** `IList<DmsInstance>` - list of DMS instance records, each containing:
  - `Id` - Unique instance identifier
  - `InstanceType` - Type/category of the instance
  - `InstanceName` - Display name
  - `ConnectionString` - Database connection string (nullable)
  - `RouteContext` - Dictionary mapping route qualifier names to values
    (e.g., `{"schoolYear": "2024", "district": "255901"}`)

**TTL:** Application lifetime (no automatic expiration)

**Multi-Tenancy Support:** Yes - one cache entry per tenant

**Warm-up:** Loaded on startup via `InitializeDmsInstances()` in `Program.cs`

**Cache Operations:**

| Operation | Method                  | Description                  |
| --------- | ----------------------- | ---------------------------- |
| Load      | `LoadDmsInstances(t)`   | Fetches and caches instances |
| Get All   | `GetAll(tenant)`        | Returns all for tenant       |
| Get By ID | `GetById(id, tenant)`   | Returns specific instance    |
| Check     | `IsLoaded(tenant)`      | Checks if tenant data cached |
| Tenants   | `GetLoadedTenantKeys()` | Returns cached tenant keys   |

**Invalidation Strategy:**

- **No invalidation for existing entries** - cached instances persist until restart
- **Cache-miss fallback for new tenants** - `TenantValidator` triggers
  `LoadDmsInstances()` when an unknown tenant is requested, allowing new
  tenants to be added without restart
- **Limitation:** Updates to existing tenant instances or deleted tenants
  require application restart to take effect

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

**Warm-up:** Loaded on startup via `WarmUpOidcMetadataCache()` in `Program.cs`.
The startup will fail if OIDC metadata cannot be retrieved from the identity
provider, ensuring DMS doesn't accept requests until JWT authentication is
fully functional. When `BypassAuthorization` is enabled, the warm-up is skipped.

**Invalidation Strategy:**

- Automatic refresh after RefreshInterval (60 minutes)
- Background refresh after AutomaticRefreshInterval (24 hours)
- Handled internally by Microsoft.IdentityModel.Protocols library

---

## Summary Table

| Cache        | Mechanism    | Scope | TTL    | Tenant | Invalidation |
| ------------ | ------------ | ----- | ------ | ------ | ------------ |
| App Context  | IMemoryCache | Sing. | 10 min | No     | Manual + TTL |
| ClaimSets    | IMemoryCache | Sing. | 10 min | Yes    | Manual + TTL |
| Comp. Schema | ConcurDict   | Sing. | None   | No     | Reload ID    |
| CMS Token    | IMemoryCache | Sing. | 30 min | No     | TTL only     |
| NpgsqlDS     | ConcurDict   | Sing. | None   | N/A    | Shutdown     |
| DMS Instance | ConcurDict   | Sing. | None   | Yes    | Restart      |
| OIDC Meta    | ConfigMgr    | Sing. | 60 min | No     | Auto-refresh |

## Cache Invalidation Patterns

DMS uses several invalidation patterns:

### 1. TTL-Based Expiration

Used by: Application Context, ClaimSets, CMS Token, OIDC Metadata

Cache entries automatically expire after a configured time period. This is the
simplest pattern and requires no manual intervention.

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

Used by: NpgsqlDataSource, DMS Instances

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
  "ConfigurationServiceSettings": {
    "CacheExpirationMinutes": 10
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

| Setting         | Value        | Location                           |
| --------------- | ------------ | ---------------------------------- |
| CMS Token TTL   | 1800 seconds | `ConfigurationServiceToken...:45`  |
| Schema Cache    | No TTL       | Version-based invalidation         |

Note: App Context TTL is now configurable via `CacheExpirationMinutes` setting.

## Operational Considerations

### Multi-Instance Deployments

Since caches are local to each instance:

- Each DMS instance maintains independent cache state
- TTL expiration may cause temporary inconsistencies between instances
- Schema reloads should be coordinated across instances
- Consider load balancer sticky sessions if cache consistency is critical

### Memory Usage

- Compiled schemas can consume significant memory for large API schemas
- ClaimSets cache grows with tenant count in multi-tenant deployments
- NpgsqlDataSource cache grows with unique connection strings
- Monitor memory usage in production environments

### Cache Warming

DMS warms most caches during startup (see `Program.cs`):

- **DMS Instances** - Loaded from Configuration Service before accepting requests
- **ClaimSets** - Retrieved and cached for all tenants on startup
- **CMS Token** - Fetched as a dependency of the above operations
- **Compiled Schemas** - Primed via `ProvideApiSchemaMiddleware`
- **OIDC Metadata** - Fetched from identity provider on startup (fails fast if unavailable)

Only **Application Context** (per-client) and **NpgsqlDataSource**
(per-connection-string) remain cold, populated on first use.
