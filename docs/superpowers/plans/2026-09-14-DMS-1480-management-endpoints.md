# DMS-1480 Management Endpoints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AppSettings:EnableManagementEndpoints` control registration of DMS claimset management endpoints while preserving the DMS-1476 authorization model.

**Architecture:** Enforce the global management switch inside `ManagementEndpointModule.MapEndpoints`, before the existing required-role mapping gate. Reuse the already-bound core `AppSettings` options instead of adding another frontend setting, and keep `EnableClaimsetReload` as the inner `ApiService` operation gate.

**Tech Stack:** .NET 10, ASP.NET Core minimal endpoint routing, NUnit, FluentAssertions, FakeItEasy, CSharpier.

**Spec:** `docs/superpowers/specs/2026-09-14-DMS-1480-management-endpoints-design.md`

## Global Constraints

- Use .NET 10 code style, including modern C# language features.
- Declare variables non-nullable.
- Use `is null` or `is not null` instead of `== null` or `!= null`.
- Use `System.Text.Json` for JSON serialization and parsing in .NET application code and build tooling.
- Do not introduce new `Newtonsoft.Json` dependencies or usages.
- Format code with `dotnet csharpier format <directory or file>`.
- Do not implement `JwtAuthentication:Authority` issuer pinning or removal.
- Do not add appsettings-wide or repository-wide dead-config enforcement.
- Do not redesign generic endpoint-module discovery.
- The accepted `.env` audit boundary is only `DMS_ENABLE_MANAGEMENT_ENDPOINTS`, `DMS_ENABLE_CLAIMSET_RELOAD`, and `DMS_MANAGEMENT_REQUIRED_ROLE`.

---

## File Structure

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs`
  - Consume core `AppSettings` and short-circuit management route mapping when `EnableManagementEndpoints=false`.
  - Preserve existing role gate, route shapes, handler authorization, and tenant-validation order.

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs`
  - Extend the `CreateFactory` helper to set `AppSettings:EnableManagementEndpoints`.
  - Add disabled-global-switch route absence tests.
  - Add disabled-global-switch logging regression test.
  - Keep existing positive and security tests on the enabled path.

- `docs/CONFIGURATION.md`
  - Document `EnableManagementEndpoints` as the global exposure switch for claimset management routes.
  - Document the hierarchy of global switch, required role, and `EnableClaimsetReload`.

- `docs/CACHING-STRATEGY.md`
  - Update claimset reload/view management endpoint documentation to include `EnableManagementEndpoints`.

No compose or `.env` files should be changed unless the bounded audit finds a management-trio binding mismatch. Current branch evidence shows the trio is already documented in `.env` files and bound through compose; the missing consumer is the code path for `EnableManagementEndpoints`.

---

### Task 1: Enforce Management Switch Registration Behavior

**Files:**
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs`
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs`

**Interfaces:**
- Consumes: Existing `CreateFactory`, `MappedRoutePatterns`, `RecordingLoggerProvider`, `ValidRequiredRole`, and route-registration tests.
- Consumes: Core `EdFi.DataManagementService.Core.Configuration.AppSettings.EnableManagementEndpoints`.
- Produces: `CreateFactory(..., bool enableManagementEndpoints = true, ...)`, which later tasks rely on for enabled and disabled management endpoint fixtures.
- Produces: `ManagementEndpointModule.MapEndpoints` behavior where disabled management endpoints are not registered and enabled management endpoints preserve the existing role gate.

- [ ] **Step 1: Extend the test factory with the global management switch setting**

Change the `CreateFactory` signature so the new parameter defaults to enabled for existing tests:

```csharp
internal static WebApplicationFactory<Program> CreateFactory(
    IApiService apiService,
    string? requiredRole,
    bool multiTenancy = false,
    bool enableClaimsetReload = true,
    bool enableManagementEndpoints = true,
    IJwtValidationService? jwtValidationService = null,
    string? roleClaimType = RoleClaimType,
    ITenantValidator? tenantValidator = null,
    RecordingLoggerProvider? loggerProvider = null
)
```

Add the setting to the in-memory configuration dictionary:

```csharp
Dictionary<string, string?> settings = new()
{
    ["AppSettings:MultiTenancy"] = multiTenancy ? "true" : "false",
    ["AppSettings:EnableClaimsetReload"] = enableClaimsetReload ? "true" : "false",
    ["AppSettings:EnableManagementEndpoints"] = enableManagementEndpoints ? "true" : "false",
    ["JwtAuthentication:ClientRole"] = "legacy-service",
};
```

- [ ] **Step 2: Add a single-tenant disabled route registration test**

Add this test near the existing positive route registration tests:

```csharp
[Test]
public void It_does_not_map_the_single_tenant_claimset_routes_when_management_endpoints_are_disabled()
{
    using WebApplicationFactory<Program> factory = CreateFactory(
        FakeApiService(),
        ValidRequiredRole,
        enableManagementEndpoints: false
    );

    IEnumerable<string> patterns = MappedRoutePatterns(factory);

    patterns.Should().NotContain("/management/reload-claimsets");
    patterns.Should().NotContain("/management/view-claimsets");
}
```

- [ ] **Step 3: Add a multi-tenant disabled route registration test**

Add this test near the multi-tenant route registration tests:

```csharp
[Test]
public void It_does_not_map_any_tenant_claimset_routes_when_management_endpoints_are_disabled()
{
    using WebApplicationFactory<Program> factory = CreateFactory(
        FakeApiService(),
        ValidRequiredRole,
        multiTenancy: true,
        enableManagementEndpoints: false
    );

    IEnumerable<string> patterns = MappedRoutePatterns(factory);

    patterns.Should().NotContain("/management/reload-claimsets");
    patterns.Should().NotContain("/management/view-claimsets");
    patterns.Should().NotContain("/management/{tenant}/reload-claimsets");
    patterns.Should().NotContain("/management/{tenant}/view-claimsets");
}
```

- [ ] **Step 4: Add a disabled-switch logging regression test**

Add this test near the existing warning tests:

```csharp
[Test]
public void It_stays_silent_when_management_endpoints_are_disabled_even_when_the_role_is_unusable()
{
    var loggerProvider = new RecordingLoggerProvider();
    using WebApplicationFactory<Program> factory = CreateFactory(
        FakeApiService(),
        requiredRole: null,
        enableManagementEndpoints: false,
        loggerProvider: loggerProvider
    );
    _ = MappedRoutePatterns(factory);

    loggerProvider
        .Entries.Should()
        .NotContain(entry =>
            entry.Category == typeof(ManagementEndpointModule).FullName && entry.Level == LogLevel.Warning
        );
}
```

- [ ] **Step 5: Run the new tests to verify they fail**

Run:

```powershell
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~Given_ManagementEndpointModule"
```

Expected: at least the new disabled route tests fail because `EnableManagementEndpoints=false` is not consumed yet. The disabled logging test should also fail because the existing role warning still runs when the global management surface is disabled.

- [ ] **Step 6: Add a core app settings alias**

Keep the existing frontend alias and add a core alias near it:

```csharp
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;
```

- [ ] **Step 7: Inject core app settings into the endpoint module**

Update the primary constructor parameters:

```csharp
public class ManagementEndpointModule(
    IOptions<FrontendAppSettings> frontendOptions,
    IOptions<CoreAppSettings> coreOptions,
    IOptions<ManagementEndpointsOptions> managementEndpointsOptions,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<ManagementEndpointModule> logger
) : IEndpointModule
```

- [ ] **Step 8: Short-circuit mapping before the role gate**

Replace the beginning of `MapEndpoints` with this structure:

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    if (!coreOptions.Value.EnableManagementEndpoints)
    {
        return;
    }

    bool multiTenancy = frontendOptions.Value.MultiTenancy;

    var managementEndpoints = endpoints.MapGroup("/management");

    // Fail closed at mapping time rather than at request time: without a usable required role
    // there is no way to authorize these endpoints, so they are not exposed at all.
    if (!IsRequiredRoleUsable())
    {
        return;
    }

    if (multiTenancy)
```

Do not change route shapes, handler methods, `AuthorizeAsync`, tenant validation, or `ApiService` calls.

- [ ] **Step 9: Run the management endpoint tests**

Run:

```powershell
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~Given_ManagementEndpointModule"
```

Expected: all `Given_ManagementEndpointModule` tests pass. This confirms the new disabled-switch behavior and the existing enabled-path route, warning, authorization, and tenant-ordering behavior.

- [ ] **Step 10: Commit the passing behavior change**

```powershell
git add src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs
git commit -m "fix: honor management endpoint enable switch"
```

---

### Task 2: Update Documentation and Perform the Bounded Env Audit

**Files:**
- Modify: `docs/CONFIGURATION.md`
- Modify: `docs/CACHING-STRATEGY.md`
- Reference only: `eng/docker-compose/.env.example`
- Reference only: `eng/docker-compose/.env.template`
- Reference only: `eng/docker-compose/.env.template.ds61`
- Reference only: `eng/docker-compose/local-dms.yml`
- Reference only: `eng/docker-compose/published-dms.yml`
- Reference only: `eng/azure-vm/compose/.env.example`
- Reference only: `eng/azure-vm/compose/docker-compose.yml`

**Interfaces:**
- Consumes: Implemented behavior from Task 1.
- Produces: Documentation that describes the management endpoint control hierarchy and confirms the accepted management-trio env audit boundary.

- [ ] **Step 1: Add `EnableManagementEndpoints` to `docs/CONFIGURATION.md`**

In the `AppSettings` table, add this row near `MultiTenancy` and `ManagementEndpoints:RequiredRole`:

```markdown
| EnableManagementEndpoints       | When `true`, allows the DMS claimset management endpoint surface to be registered. When `false`, `/management/reload-claimsets` and `/management/view-claimsets` are not mapped. Environment override: `AppSettings__EnableManagementEndpoints`. Default: `false` |
```

- [ ] **Step 2: Update the `ManagementEndpoints:RequiredRole` explanation**

Replace the paragraph that begins with `` `ManagementEndpoints:RequiredRole` must be one untrimmed token`` with:

```markdown
Claimset management endpoints use three separate controls. `EnableManagementEndpoints` is the
global exposure switch; when it is `false`, the claimset management routes are not mapped.
When it is `true`, `ManagementEndpoints:RequiredRole` must be one untrimmed token no longer
than 256 characters, and `JwtAuthentication:RoleClaimType` must be present. Values containing
ASCII whitespace, commas, semicolons, quotes, brackets, braces, or control characters are
invalid and leave the claimset management endpoints unmapped, as does a missing or blank
`JwtAuthentication:RoleClaimType`. A request to a mapped endpoint without a valid bearer token
receives `401`; a valid token whose claims do not include this exact role under the configured
role claim type receives `403`. `EnableClaimsetReload` is checked later by the claimset
operation handlers; it does not control route registration.
```

- [ ] **Step 3: Update the short claimset reload bullets in `docs/CACHING-STRATEGY.md`**

Replace the bullets around the existing claimset reload requirements with:

```markdown
- Requires `AppSettings:EnableManagementEndpoints: true` to map the DMS claimset management
  route surface.
- Requires a valid `AppSettings:ManagementEndpoints:RequiredRole`; the endpoints are not mapped
  without one, and callers must present a bearer token carrying that role under
  `JwtAuthentication:RoleClaimType`.
- Requires `AppSettings:EnableClaimsetReload: true` for the reload and view operations to execute
  after authorization.
```

- [ ] **Step 4: Update the later administrator paragraph in `docs/CACHING-STRATEGY.md`**

Replace the paragraph beginning with `Administrators can trigger cache invalidation through management endpoints.` with:

```markdown
Administrators can trigger cache invalidation through management endpoints. The DMS claimset
management routes are mapped only when `AppSettings:EnableManagementEndpoints` is `true`.
They are additionally mapped only when `AppSettings:ManagementEndpoints:RequiredRole` holds a
valid role token, and every request must present a bearer token carrying that role under
`JwtAuthentication:RoleClaimType`; requests without one receive `401`, and tokens lacking the
role receive `403`. `AppSettings:EnableClaimsetReload` is checked after authorization by the
reload and view operations.
```

- [ ] **Step 5: Perform the bounded management env trio audit**

Run:

```powershell
rg --hidden -n "DMS_ENABLE_MANAGEMENT_ENDPOINTS|DMS_ENABLE_CLAIMSET_RELOAD|DMS_MANAGEMENT_REQUIRED_ROLE" eng/docker-compose eng/azure-vm -g ".env*" -g "*.yml" -g "*.yaml"
```

Expected: the trio appears in DMS compose `.env` surfaces and is bound through `local-dms.yml`, `published-dms.yml`, and `eng/azure-vm/compose/docker-compose.yml`.

Run:

```powershell
rg -n "EnableManagementEndpoints|EnableClaimsetReload|ManagementEndpoints:RequiredRole|ManagementEndpoints__RequiredRole" src/dms docs eng/docker-compose eng/azure-vm
```

Expected:
- `EnableManagementEndpoints` is declared in core `AppSettings`, bound through compose, documented, and consumed by `ManagementEndpointModule`.
- `EnableClaimsetReload` is declared in core `AppSettings`, bound through compose, documented, and consumed by `ApiService.ReloadClaimsetsAsync` and `ApiService.ViewClaimsetsAsync`.
- `ManagementEndpoints:RequiredRole` is bound through `ManagementEndpointsOptions`, bound through compose, documented, and consumed by the existing role mapping/authorization flow.

If this audit finds an in-scope management-trio binding mismatch, fix only that mismatch in the same task. Do not expand into CMS, database, Kafka, Swagger, JWT authority, or unrelated env variables.

- [ ] **Step 6: Commit docs and any in-scope audit fixes**

```powershell
git add docs/CONFIGURATION.md docs/CACHING-STRATEGY.md eng/docker-compose eng/azure-vm
git commit -m "docs: clarify management endpoint controls"
```

If no `eng` files changed, the `git add` command stages only the modified docs.

---

### Task 3: Format, Verify, and Finalize

**Files:**
- Verify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs`
- Verify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs`
- Verify: `docs/CONFIGURATION.md`
- Verify: `docs/CACHING-STRATEGY.md`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: Formatted, verified implementation branch ready for review.

- [ ] **Step 1: Format touched C# files**

Run:

```powershell
dotnet csharpier format src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs
```

Expected: command exits `0`. If formatting changes files, continue to the next step before committing.

- [ ] **Step 2: Run focused management endpoint tests**

Run:

```powershell
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~Given_ManagementEndpointModule"
```

Expected: all filtered tests pass with `0` failures.

- [ ] **Step 3: Run the frontend unit test project**

Run:

```powershell
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj
```

Expected: project passes with `0` failures.

- [ ] **Step 4: Run a no-restore DMS solution build**

Run:

```powershell
dotnet build --no-restore ./src/dms/EdFi.DataManagementService.sln
```

Expected: build exits `0`.

- [ ] **Step 5: Review the final diff against the approved spec**

Run:

```powershell
git diff --stat
git diff -- src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs docs/CONFIGURATION.md docs/CACHING-STRATEGY.md
```

Confirm:
- The only behavior change is management endpoint route registration honoring `EnableManagementEndpoints`.
- The role gate still runs when management endpoints are enabled.
- The role warning does not run when management endpoints are disabled.
- `EnableClaimsetReload` still does not control route registration.
- Documentation describes the same hierarchy as the approved spec.

- [ ] **Step 6: Commit formatting or final verification fixes**

If Step 1 or review fixes changed files after Task 2, commit them:

```powershell
git add src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/ManagementEndpointModule.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/ManagementEndpointModuleTests.cs docs/CONFIGURATION.md docs/CACHING-STRATEGY.md
git commit -m "chore: format management endpoint switch changes"
```

If there are no changes, do not create an empty commit.

- [ ] **Step 7: Final status check**

Run:

```powershell
git status --short
git log --oneline -5
```

Expected: worktree is clean, and recent commits show the behavior, docs, and optional formatting commits for DMS-1480.
