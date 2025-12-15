# .NET 10 Upgrade Summary

This document summarizes the code changes required to upgrade the Ed-Fi Data Management Service from .NET 8 to .NET 10.

## Overview

The upgrade was relatively straightforward, requiring changes in the following categories:
1. Project file target framework updates
2. Docker base image updates
3. CI/CD workflow updates
4. NuGet package version updates
5. Code changes for breaking API changes
6. Removal of packages now included in the framework

---

## Code Changes

### 1. ForwardedHeadersOptions.KnownNetworks Renamed to KnownIPNetworks

**Files Changed:**
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Program.cs`

**Change:**
```csharp
// Before (.NET 8)
options.KnownNetworks.Clear();

// After (.NET 10)
options.KnownIPNetworks.Clear();
```

**Reason:** In .NET 10, `ForwardedHeadersOptions.KnownNetworks` was renamed to `KnownIPNetworks` for clarity. The old property is marked with `[Obsolete]` and treated as an error (ASPDEPR005).

---

### 2. X509Certificate2 Constructor Replaced with X509CertificateLoader

**Files Changed:**
- `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs`

**Change:**
```csharp
// Before (.NET 8)
cert = new X509Certificate2(certPath, certPassword);

// For loading without password:
new X509Certificate2(certPath)

// For loading with password:
new X509Certificate2(certPath, certPassword)

// After (.NET 10)
cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);

// For loading without password:
X509CertificateLoader.LoadCertificateFromFile(certPath)

// For loading with password:
X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword)
```

**Reason:** In .NET 10, the `X509Certificate2` constructors that load from files are obsolete (SYSLIB0057). The new `X509CertificateLoader` class provides better security defaults and clearer semantics:
- `LoadCertificateFromFile()` - For PEM/CER files without private keys
- `LoadPkcs12FromFile()` - For PFX/PKCS#12 files (potentially with private keys and passwords)

---

### 3. Code Style Fixes (IDE0011, IDE0040)

**Files Changed:**
- `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/StepDefinitions/InstanceKafkaStepDefinitions.cs`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/StepDefinitions/ScenarioVariables.cs`

**Changes:**

```csharp
// IDE0011: Add braces to 'if' statement
// Before
if (m.ValueAsJson == null)
    return false;

// After
if (m.ValueAsJson == null)
{
    return false;
}

// IDE0040: Accessibility modifiers required
// Before
class ScenarioVariables

// After
internal class ScenarioVariables
```

**Reason:** .NET 10 SDK includes updated analyzers with stricter defaults. These were previously warnings but are now errors when `TreatWarningsAsErrors` is enabled.

---

### 4. Trace.Assert and Debug.Assert - S3236 CallerArgumentExpression Fix

**Files Changed:**
- `src/dms/core/EdFi.DataManagementService.Core/Validation/EqualityConstraintValidator.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Validation/DecimalValidator.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Extraction/ReferenceExtractor.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Model/SchoolYearEnumerationDocument.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Model/DescriptorDocument.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ResourceActionAuthorizationMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Handler/UpdateByIdHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ParseBodyMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ExtractDocumentSecurityElementsMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ExtractDocumentInfoMiddleware.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs`

**Change:**
```csharp
// Before (.NET 8) - Two-parameter overload
Trace.Assert(condition, "Error message");
Debug.Assert(condition, "Error message");

// After (.NET 10) - Three-parameter overload to avoid CallerArgumentExpression
Trace.Assert(condition, "Error message", "");
Debug.Assert(condition, "Error message", "");
```

**Reason:** In .NET 10, the `Trace.Assert(bool, string?)` and `Debug.Assert(bool, string?)` overloads now include a `[CallerArgumentExpression]` attribute on the message parameter. When you pass an explicit message string, SonarAnalyzer rule S3236 warns that you're "hiding" the automatic caller information.

The fix is to use the three-parameter overload `(bool condition, string? message, string? detailMessage)` which does not have the `CallerArgumentExpression` attribute and allows explicit messages without triggering the warning. This is the [official workaround recommended by SonarSource](https://community.sonarsource.com/t/false-positive-on-s3236-when-calling-debug-assert-with-message/138761).

---

### 5. Removed Microsoft.Extensions.Configuration.EnvironmentVariables Package

**Files Changed:**
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj`
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj`

**Change:**
```xml
<!-- Removed this PackageReference -->
<PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" />
```

**Reason:** In .NET 10, `Microsoft.Extensions.Configuration.EnvironmentVariables` is now included in the framework. Having it as an explicit package reference triggers NU1510 warnings about package pruning. The solution is simply to remove the explicit reference.

---

### 6. Updated Respawn Package for Security Vulnerabilities

**Files Changed:**
- `src/Directory.Packages.props`

**Change:**
```xml
<!-- Before -->
<PackageVersion Include="Respawn" Version="6.2.1" />

<!-- After -->
<PackageVersion Include="Respawn" Version="7.0.0" />
```

**Reason:** Respawn 6.2.1 had transitive dependencies on `Azure.Identity` and `System.Drawing.Common` with known security vulnerabilities (CVE-2024-35255, CVE-2021-24112). These triggered NU1902, NU1903, and NU1904 warnings. Updating to Respawn 7.0.0 resolves these vulnerabilities.

---

## NuGet Package Updates

The following packages were updated to .NET 10 compatible versions in `src/Directory.Packages.props`:

| Package | Old Version | New Version |
|---------|-------------|-------------|
| Microsoft.AspNetCore.Authentication.JwtBearer | 8.0.8 | 10.0.1 |
| Microsoft.AspNetCore.Mvc.Testing | 8.0.8 | 10.0.1 |
| Microsoft.Extensions.Caching.Abstractions | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Caching.Memory | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Configuration | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Configuration.Abstractions | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Configuration.Binder | 9.0.9 | 10.0.1 |
| Microsoft.Extensions.Configuration.Json | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.DependencyInjection | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Hosting | 8.0.0 | 10.0.1 |
| Microsoft.Extensions.Hosting.Abstractions | 8.0.0 | 10.0.1 |
| Microsoft.Extensions.Http | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Logging | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Logging.Abstractions | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Logging.Console | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Logging.Debug | 9.0.1 | 10.0.1 |
| Microsoft.Extensions.Options | 9.0.1 | 10.0.1 |
| Respawn | 6.2.1 | 7.0.0 |

**Note:** `Microsoft.Extensions.Configuration.EnvironmentVariables` was removed from the package list as it is now included in the .NET 10 framework.

---

### 7. DuplicatePropertiesMiddleware Rewritten for .NET 10 Compatibility

**Files Changed:**
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/DuplicatePropertiesMiddleware.cs`

**Problem:**
The original `DuplicatePropertiesMiddleware` relied on parsing the `ArgumentException.Message` thrown by `System.Text.Json` when accessing properties on a `JsonObject` with duplicate keys. In .NET 10, the exception message format changed, causing the regex patterns to no longer match, resulting in `"unknown"` being returned instead of the actual JSON path.

**Solution:**
Completely rewrote the middleware to use `Utf8JsonReader` for direct duplicate detection. This approach:
- Scans the raw JSON string token-by-token
- Tracks property names at each object nesting level using a `HashSet<string>`
- Maintains a path stack to build accurate JSON paths (e.g., `$.classPeriods[0].classPeriodReference.classPeriodName`)
- Is independent of exception message formats, making it robust against future .NET changes

**Key Implementation Details:**
```csharp
// Uses Utf8JsonReader to scan raw JSON
var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

// Tracks property names at each object level for duplicate detection
var propertyNamesStack = new Stack<HashSet<string>>();

// Tracks current path for accurate error reporting
var pathStack = new Stack<PathSegment>();

// When PropertyName token is encountered, check for duplicate
if (!currentProperties.Add(propertyName))
{
    // Duplicate found! Return the JSON path
    return BuildJsonPath(pathStack, propertyName);
}
```

**Benefits:**
- Preserves backward-compatible error message format with exact JSON paths
- No dependency on exception message parsing
- Works consistently across all .NET versions

**Future Optimization Opportunity:**
The current implementation parses the JSON twice: once via `Utf8JsonReader` in `DuplicatePropertiesMiddleware` to detect duplicates, and once via `JsonNode.Parse()` in the upstream `ParseBodyMiddleware`. This could be optimized by:

1. Moving duplicate detection into `ParseBodyMiddleware` before `JsonNode.Parse()` is called
2. Removing `DuplicatePropertiesMiddleware` from the pipeline entirely
3. Using `ReadOnlySpan<byte>` from the original request to avoid the `Encoding.UTF8.GetBytes()` allocation

This would reduce the work to a single pass for valid JSON (the common case) and eliminate a middleware hop.

---

### 8. Minimal API [FromForm] Binding Behavior Change

**Files Changed:**
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/TokenEndpointModule.cs`

**Problem:**
In .NET 10, Minimal APIs return an empty 400 response when `[FromForm]` binding fails with an empty request body, rather than passing through to the endpoint handler. This caused the `/connect/register` endpoint (and other form-based endpoints) to return an empty response body instead of the expected validation error messages.

Making the `[FromForm]` parameter nullable was insufficient because .NET 10 still attempts form binding and fails with an empty body before invoking the handler.

**Solution:**
Removed `[FromForm]` parameters entirely and manually read form data from `HttpContext.Request`:

```csharp
// Before (.NET 8)
private async Task<IResult> RegisterClient(
    RegisterRequest.Validator validator,
    [FromForm] RegisterRequest model,  // Binding fails with empty body in .NET 10
    ...
)
{
    await validator.GuardAsync(model);
}

// After (.NET 10)
private async Task<IResult> RegisterClient(
    RegisterRequest.Validator validator,
    // [FromForm] removed - binding fails before handler invoked with empty body
    IIdentityProviderRepository clientRepository,
    IOptions<IdentitySettings> identitySettings,
    HttpContext httpContext
)
{
    // Manually read form data to handle empty form bodies in .NET 10
    RegisterRequest model = new();
    if (httpContext.Request.HasFormContentType)
    {
        var form = await httpContext.Request.ReadFormAsync();
        model = new RegisterRequest
        {
            ClientId = form["ClientId"].ToString(),
            ClientSecret = form["ClientSecret"].ToString(),
            DisplayName = form["DisplayName"].ToString(),
        };
    }

    await validator.GuardAsync(model);
    // Now validation errors are properly returned even with empty form body
}
```

**Endpoints Updated:**

*IdentityModule.cs (Configuration Service):*
- `RegisterClient` - Removed `[FromForm] RegisterRequest`, manually reads form data
- `GetClientAccessToken` - Removed `[FromForm] TokenRequest`, refactored to always read form manually
- `IntrospectToken` - Removed `[FromForm] IntrospectionRequest`, manually reads form data
- `RevokeToken` - Removed `[FromForm] RevocationRequest`, manually reads form data

*TokenEndpointModule.cs (DMS):*
- `HandleFormData` - Removed `[FromForm] TokenRequest`, manually reads form data

**Reason:** This is a behavioral change in .NET 10's Minimal API model binding. When form data is empty or cannot be bound, .NET 10 returns an immediate 400 response without invoking the endpoint handler - even with nullable parameters. The only solution is to bypass `[FromForm]` binding entirely and read form data directly from `HttpContext.Request`.

---

### 9. SDK Generation Target Framework

**Files Changed:**
- `build-sdk.ps1`
- `.github/workflows/scheduled-smoke-test.yml`

**Problem:**
The OpenAPI Generator CLI (version 7.9.0) does not support .NET 10 as a target framework. It only supports: netstandard1.3-2.1, net47, net48, net6.0, net7.0, net8.0.

**Solution:**
Changed the SDK generation target framework from `net10.0` to `net8.0`:

```powershell
# Before
--additional-properties "packageName=$PackageName,targetFramework=net10.0,netCoreProjectFile=true"

# After
--additional-properties "packageName=$PackageName,targetFramework=net8.0,netCoreProjectFile=true"
```

Also updated the SDK DLL paths in the smoke test workflow to use `net8.0` instead of `net10.0`.

**Reason:** The generated SDK is a separate client library that doesn't need to target the same framework as the main DMS application. It can safely target `net8.0` while the main application targets `net10.0`. When OpenAPI Generator adds support for .NET 10, this can be updated.

---

### 10. Smoke Test Tool Target Framework

**Files Changed:**
- `eng/smoke_test/modules/SmokeTest.psm1`

**Problem:**
The `EdFi.Suite3.SmokeTest.Console` NuGet package (version 7.2.413) does not include a .NET 10 build. The package only contains builds for earlier target frameworks (net8.0).

**Solution:**
Changed the tool path from `net10.0` to `net8.0`:

```powershell
# Before
$path = (Join-Path -Path ($ToolPath).Trim() -ChildPath "tools/net10.0/any/EdFi.SmokeTest.Console.dll")

# After
$path = (Join-Path -Path ($ToolPath).Trim() -ChildPath "tools/net8.0/any/EdFi.SmokeTest.Console.dll")
```

**Reason:** The smoke test console is an external Ed-Fi package published to Azure Artifacts. It's a separate tool that runs against the DMS API and does not need to target the same framework. The .NET 8.0 build runs correctly on a system with .NET 10 installed.

---

## Build Results

Both solutions build successfully with **0 warnings** and **0 errors**:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

---

## Conclusion

The .NET 10 upgrade is complete. Both solutions build successfully without any warnings or errors. All breaking changes have been addressed:

1. **API Renames** - `KnownNetworks` → `KnownIPNetworks`, `X509Certificate2` → `X509CertificateLoader`
2. **Package Updates** - All Microsoft.Extensions.* packages updated to 10.0.1, Respawn updated to 7.0.0
3. **Package Removals** - `Microsoft.Extensions.Configuration.EnvironmentVariables` removed (now in framework)
4. **Analyzer Fixes** - S3236 CallerArgumentExpression, IDE0011 braces, IDE0040 accessibility modifiers
5. **Middleware Rewrite** - `DuplicatePropertiesMiddleware` rewritten to use `Utf8JsonReader` for .NET 10 compatibility
6. **Minimal API Binding** - `[FromForm]` removed, form data read manually from `HttpContext.Request`
7. **SDK Generation** - Target framework kept at `net8.0` (OpenAPI Generator doesn't support .NET 10 yet)
8. **Smoke Test Tool** - Tool path kept at `net8.0` (external package doesn't have .NET 10 build)
