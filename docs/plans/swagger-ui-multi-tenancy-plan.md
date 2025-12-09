# Plan: Multi-Tenancy Support in Swagger UI

## Overview

Add a tenant dropdown to the custom Swagger UI that allows users to select which tenant's API they want to interact with, similar to the existing school year dropdown.

## Current Architecture Analysis

### How School Year Dropdown Works Today

1. **Backend** (`MetadataEndpointModule.cs:23-78`): Queries `IDmsInstanceProvider.GetAll()` and extracts school years from `RouteContext` where key equals "schoolYear"
2. **OpenAPI Spec**: Returns `servers[].variables["School Year Selection"].enum` with available years
3. **Frontend** (`edfi-school-year-from-spec.js`): Parses the enum, creates dropdown, injects year into API requests

### How Multi-Tenancy Works Today

1. **URL Structure**: `/{tenant}/data/{**dmsPath}` when `AppSettings.MultiTenancy=true`
2. **Tenant Source**: Tenants are stored in Config Service database, accessed via `/v2/tenants` endpoint
3. **Tenant Position**: Tenant is the FIRST segment in URL, BEFORE any route qualifiers (schoolYear, districtId)
4. **Validation**: `TenantValidationMiddleware` validates format (alphanumeric, hyphens, underscores, max 256 chars)

### Key Difference from School Year

| Aspect | School Year | Tenant |
|--------|-------------|--------|
| Data Source | `DmsInstance.RouteContext` | Config Service `/v2/tenants` |
| URL Position | After tenant (if any) | First segment |
| Loaded From | OpenAPI spec servers.variables | Needs new endpoint or spec addition |

---

## Implementation Plan

### Phase 1: Backend Changes

#### Step 1.1: Create Tenant List Endpoint (or extend existing)

**Option A**: Use existing Config Service endpoint
- Endpoint: `GET /v2/tenants` already exists
- Challenge: Swagger UI would need to make authenticated call to Config Service

**Option B (Recommended)**: Add tenant list to DMS metadata
- Create new endpoint: `GET /metadata/tenants` in DMS
- DMS proxies to Config Service or caches tenant list
- Returns simple JSON array of tenant names

**File to modify**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/MetadataEndpointModule.cs`

```csharp
// Add new endpoint
endpoints.MapGet("/metadata/tenants", GetTenants);

internal async Task GetTenants(HttpContext httpContext, ITenantsProvider tenantsProvider)
{
    var tenants = await tenantsProvider.GetAllTenantNames();
    await httpContext.Response.WriteAsJsonAsync(tenants);
}
```

#### Step 1.2: Add Tenant Variable to OpenAPI Spec Servers

**File to modify**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/MetadataEndpointModule.cs`

Modify `GetServers()` method to include tenant variable when multi-tenancy is enabled:

```csharp
private static async Task<JsonArray> GetServers(
    HttpContext httpContext,
    IDmsInstanceProvider dmsInstanceProvider,
    ITenantsProvider tenantsProvider,
    IOptions<AppSettings> appSettings)
{
    var serverObject = new JsonObject();
    var variables = new JsonObject();

    // Add tenant variable if multi-tenancy enabled
    if (appSettings.Value.MultiTenancy)
    {
        var tenants = await tenantsProvider.GetAllTenantNames();
        if (tenants.Count > 0)
        {
            variables["Tenant"] = new JsonObject
            {
                ["default"] = tenants.First(),
                ["description"] = "Tenant Selection",
                ["enum"] = new JsonArray(tenants.Select(t => JsonValue.Create(t)).ToArray()),
            };
        }
    }

    // Add school year variable (existing logic)
    // ...

    // Build URL template based on what's enabled
    // e.g., "{baseUrl}/{Tenant}/{School Year Selection}/data"
}
```

#### Step 1.3: Create ITenantsProvider Interface and Implementation

**New file**: `src/dms/core/EdFi.DataManagementService.Core/Configuration/ITenantsProvider.cs`

```csharp
public interface ITenantsProvider
{
    Task<IReadOnlyList<string>> GetAllTenantNames();
    Task LoadTenants();
    bool IsLoaded { get; }
}
```

**New file**: `src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceTenantsProvider.cs`

- Fetches tenants from Config Service `/v2/tenants`
- Caches results in memory
- Supports periodic refresh

---

### Phase 2: Frontend Changes

#### Step 2.1: Create New Tenant Plugin

**New file**: `eng/docker-compose/custom-swagger-ui/edfi-tenant-from-spec.js`

Similar structure to `edfi-school-year-from-spec.js`:

```javascript
window.EdFiTenant = function () {
    let selectedTenant = null;
    let tenants = [];

    // Extract tenants from OpenAPI spec servers section
    const extractTenantsFromSpec = (spec) => {
        // Look for "Tenant" variable in servers[].variables
        // Similar to school year extraction
    };

    // Create tenant selector UI (dropdown)
    const createTenantSelector = () => {
        // Create dropdown similar to school year
        // Position it BEFORE the school year dropdown
    };

    // Request interceptor to inject tenant into API calls
    const requestInterceptor = (req) => {
        // Inject tenant as first path segment
        // e.g., /data/ed-fi/schools -> /acme-corp/data/ed-fi/schools
    };

    return {
        statePlugins: { ... },
        fn: { requestInterceptor },
        afterLoad: initialize
    };
};
```

#### Step 2.2: Update swagger-initializer.js

**File to modify**: `eng/docker-compose/custom-swagger-ui/swagger-initializer.js`

```javascript
// Add tenant plugin
if (window.EdFiTenant) {
    plugins.push(window.EdFiTenant);
    console.log('Ed-Fi Tenant plugin enabled');
}

// Update request interceptor to handle both tenant AND school year
requestInterceptor: (req) => {
    const tenantSelect = document.querySelector('.tenant-select');
    const schoolYearSelect = document.querySelector('.school-year-select');

    const tenant = tenantSelect?.value;
    const schoolYear = schoolYearSelect?.value;

    // Build URL: /{tenant}/{schoolYear}/data/...
    // Handle all combinations
}
```

#### Step 2.3: Update index.html

**File to modify**: `eng/docker-compose/custom-swagger-ui/index.html`

Add script reference:
```html
<script src="./edfi-tenant-from-spec.js"></script>
```

#### Step 2.4: Update swagger-ui.yml Docker Compose

**File to modify**: `eng/docker-compose/swagger-ui.yml`

Add environment variable for enabling tenant dropdown:
```yaml
environment:
  - DMS_SWAGGER_UI_ENABLE_TENANTS=${DMS_SWAGGER_UI_ENABLE_TENANTS:-false}
```

---

### Phase 3: Configuration and Integration

#### Step 3.1: Environment Variables

Add new environment variables:
- `DMS_SWAGGER_UI_ENABLE_TENANTS`: Enable/disable tenant dropdown (default: false)
- Swagger UI reads this to conditionally show tenant selector

#### Step 3.2: Update start-local-dms.ps1

**File to modify**: `eng/docker-compose/start-local-dms.ps1`

Add parameter for multi-tenancy mode that sets appropriate environment variables.

#### Step 3.3: Conditional UI Display

The tenant dropdown should only appear when:
1. `AppSettings.MultiTenancy=true` on DMS backend
2. OpenAPI spec contains `servers[].variables["Tenant"]`
3. At least one tenant exists in the system

---

### Phase 4: URL Construction Logic

The request interceptor must handle all combinations:

| Multi-Tenancy | Route Qualifiers | URL Pattern |
|---------------|------------------|-------------|
| No | None | `/data/{resource}` |
| No | schoolYear | `/{schoolYear}/data/{resource}` |
| Yes | None | `/{tenant}/data/{resource}` |
| Yes | schoolYear | `/{tenant}/{schoolYear}/data/{resource}` |
| Yes | district,schoolYear | `/{tenant}/{district}/{schoolYear}/data/{resource}` |

The frontend must parse the OpenAPI spec URL template and substitute variables in correct order.

---

## File Changes Summary

### New Files
1. `src/dms/core/EdFi.DataManagementService.Core/Configuration/ITenantsProvider.cs`
2. `src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceTenantsProvider.cs`
3. `eng/docker-compose/custom-swagger-ui/edfi-tenant-from-spec.js`

### Modified Files
1. `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/MetadataEndpointModule.cs`
2. `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs` (DI registration)
3. `eng/docker-compose/custom-swagger-ui/swagger-initializer.js`
4. `eng/docker-compose/custom-swagger-ui/index.html`
5. `eng/docker-compose/swagger-ui.yml`
6. `eng/docker-compose/start-local-dms.ps1`

---

## Testing Plan

1. **Unit Tests**: Test `ITenantsProvider` implementation
2. **Integration Tests**: Test OpenAPI spec generation with tenants
3. **Manual Testing**:
   - Single tenant mode (dropdown hidden)
   - Multi-tenant mode (dropdown visible)
   - Tenant + school year combination
   - API calls include correct tenant in URL

---

## Alternative Approaches Considered

### Alternative 1: Fetch Tenants from Config Service Directly
- Swagger UI calls `/v2/tenants` on Config Service
- **Rejected**: Requires authentication, CORS configuration, exposes Config Service

### Alternative 2: Hardcode Tenant List in Environment Variable
- Pass tenant list as `DMS_TENANTS=tenant1,tenant2,tenant3`
- **Rejected**: Not dynamic, requires restart to add tenants

### Alternative 3: Generalize the Dropdown System
- Create a single "route variables" plugin that handles any server variable
- **Consider for future**: More maintainable but larger refactor

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Performance: Loading tenants on every spec request | Cache tenant list with TTL, background refresh |
| Security: Exposing tenant list | Tenant names are not sensitive; list is needed for routing |
| Complexity: Multiple dropdowns interaction | Clear UI ordering, thorough testing |
| Breaking existing deployments | Feature flag, backward compatible defaults |

---

## Estimated Complexity

- **Backend changes**: Medium (new provider, spec modification)
- **Frontend changes**: Medium (new plugin similar to existing)
- **Integration**: Low-Medium (configuration, testing)

Total: ~2-3 days of development effort
