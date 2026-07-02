# DMS-1227 Oversized Request Body Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return HTTP 413 for oversized DMS requests, keep the limit configurable without hardcoded byte-count math, and add reliable relational E2E coverage for the oversized-body OWASP path.

**Architecture:** Add one config-backed request-body-size setting in the frontend host and flow it into Kestrel and form limits. Keep the oversized-body response handling local to `LoggingMiddleware`, and keep the E2E reliability change isolated to one dedicated oversized-request step that adds `Expect: 100-continue` only for the new scenario.

**Tech Stack:** .NET 10, ASP.NET Core, Kestrel, NUnit, FluentAssertions, Playwright API request context, Reqnroll, C#

---

### Task 1: Make the request-body size configurable in the frontend host

**Files:**
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Configuration/AppSettings.cs`
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json`
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/ConfigurationTests.cs`

- [ ] **Step 1: Write the failing check**

Add a unit-test-friendly expectation in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/ConfigurationTests.cs` that `AppSettings` exposes a named `MaxRequestBodySizeBytes` value and that `AppSettingsValidator` rejects a non-positive value.

```csharp
// in AppSettings.cs
public required int MaxRequestBodySizeBytes { get; set; }

// in AppSettingsValidator.Validate(...)
if (options.MaxRequestBodySizeBytes <= 0)
{
    return ValidateOptionsResult.Fail("AppSettings value MaxRequestBodySizeBytes must be greater than zero");
}
```

- [ ] **Step 2: Run the current frontend unit tests to confirm the new setting is missing**

Run: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter FullyQualifiedName~ConfigurationTests --nologo --verbosity minimal`

Expected: a configuration-related failure until the setting is added and wired through.

- [ ] **Step 3: Implement the config-backed limit**

Wire `builder.Configuration.GetValue<int>("AppSettings:MaxRequestBodySizeBytes")` into both `FormOptions` and `KestrelServerOptions`, and replace the inline `10 * 1024 * 1024` expressions with the configured value.

```csharp
int maxRequestBodySizeBytes = builder.Configuration.GetValue<int>("AppSettings:MaxRequestBodySizeBytes");

builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = maxRequestBodySizeBytes;
    options.MultipartBodyLengthLimit = maxRequestBodySizeBytes;
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = maxRequestBodySizeBytes;
});
```

Update `appsettings.json` so the default remains 10 MB, but expressed as a config value instead of code-level byte math.

```json
"MaxRequestBodySizeBytes": 10485760
```

- [ ] **Step 4: Run the config-focused unit tests again**

Run: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter FullyQualifiedName~ConfigurationTests --nologo --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit the frontend configuration change**

```bash
git add src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Configuration/AppSettings.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json
git commit -m "feat: make frontend request body size configurable"
```

### Task 2: Map oversized-body failures to 413 in the logging middleware

**Files:**
- Modify: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/LoggingMiddleware.cs`
- Create: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Infrastructure/LoggingMiddlewareTests.cs`

- [ ] **Step 1: Write the failing middleware test**

Add a focused NUnit fixture that throws `BadHttpRequestException` with `StatusCodes.Status413PayloadTooLarge` from the next delegate and asserts the response is 413 instead of 500.

```csharp
[Test]
public async Task It_returns_413_when_kestrel_reports_request_body_too_large()
{
    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();

    var middleware = new LoggingMiddleware(_ => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

    await middleware.Invoke(context, NullLogger<LoggingMiddleware>.Instance);

    context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
}
```

Also add one regression check that a normal exception still follows the existing 500 path.

- [ ] **Step 2: Run the new test file and confirm it fails on the current middleware**

Run: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter FullyQualifiedName~LoggingMiddlewareTests --nologo --verbosity minimal`

Expected: the oversized-body assertion fails until the middleware branch is added.

- [ ] **Step 3: Implement the targeted 413 branch**

In `LoggingMiddleware.Invoke`, detect `BadHttpRequestException` with `StatusCode == StatusCodes.Status413PayloadTooLarge`, log it as an expected oversized-body rejection, set the response status to 413 when the response has not started, and return without writing the generic 500 body or wrapping the exception.

```csharp
catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
{
    logger.LogWarning(
        ex,
        "Request body exceeded the configured limit: {Method} {Path} - TraceId: {TraceId}",
        sanitizedMethod,
        sanitizedPath,
        context.TraceIdentifier
    );

    if (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
    }

    return;
}
```

Keep the existing generic `catch (Exception ex)` path for everything else.

- [ ] **Step 4: Run the middleware tests again**

Run: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter FullyQualifiedName~LoggingMiddlewareTests --nologo --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit the middleware fix and unit coverage**

```bash
git add src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/LoggingMiddleware.cs src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Infrastructure/LoggingMiddlewareTests.cs
git commit -m "feat: return 413 for oversized request bodies"
```

### Task 3: Add a dedicated oversized-request E2E helper and scenario

**Files:**
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/AppSettings.cs`
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/StepDefinitions/StepDefinitions.cs`
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature`

- [ ] **Step 1: Write the failing E2E scenario and helper signature**

Add a new scenario tagged with `@relational-backend` and `@relational-ci-shard-3` that uses the existing `schools` background seed and calls a new step like:

```gherkin
When a POST request is made to "/ed-fi/schools" with an oversized JSON body
Then it should respond with 413
```

Add a new step definition method that currently does not exist:

```csharp
[When("a POST request is made to {string} with an oversized JSON body")]
public async Task WhenAPostRequestIsMadeToWithAnOversizedJsonBody(string url)
```

- [ ] **Step 2: Run the targeted E2E feature and confirm the step is undefined**

Run: `pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-3&FullyQualifiedName~OversizedRequestBody'`

Expected: step-definition failure until the new helper is implemented.

- [ ] **Step 3: Implement the helper using the shared configured limit**

Expose the max request body size from `src/dms/tests/EdFi.DataManagementService.Tests.E2E/AppSettings.cs` as a new property, backed by `appsettings.json`, and build the oversized JSON body in code until its UTF-8 byte count exceeds that value.

```csharp
public static int MaxRequestBodySizeBytes => _settings.MaxRequestBodySizeBytes;

internal sealed record AppSettingsValues(
    string DmsPort,
    string ConfigServicePort,
    string AuthenticationService,
    string DataStoreDatabaseName,
    int MaxRequestBodySizeBytes
);
```

In `StepDefinitions.cs`, add a helper that sends the request with `Expect: 100-continue` only for this oversized path, and define the oversized-body generator in the same file so there is no undefined helper name in the plan.

```csharp
private async Task ExecuteOversizedPostRequest(string url)
{
    string body = CreateOversizedSchoolJson(AppSettings.MaxRequestBodySizeBytes);
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = GetDmsTokenFromContext(),
        ["Content-Type"] = "application/json",
        ["Expect"] = "100-continue",
    };

    _apiResponse = await _playwrightContext.ApiRequestContext!.PostAsync(
        url,
        new() { DataByte = Encoding.UTF8.GetBytes(body), Headers = headers }
    );
}

private static string CreateOversizedSchoolJson(int maxRequestBodySizeBytes)
{
    var institutionName = new StringBuilder("Oversized School");
    string body;

    do
    {
        institutionName.Append('x');
        body = JsonSerializer.Serialize(new
        {
            schoolId = 999999,
            nameOfInstitution = institutionName.ToString(),
            gradeLevels = new[]
            {
                new { gradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade" },
            },
            educationOrganizationCategories = new[]
            {
                new
                {
                    educationOrganizationCategoryDescriptor = "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
                },
            },
        });
    }
    while (Encoding.UTF8.GetByteCount(body) <= maxRequestBodySizeBytes);

    return body;
}
```

- [ ] **Step 4: Run the targeted E2E scenario again**

Run: `pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-3&FullyQualifiedName~OversizedRequestBody'`

Expected: PASS with a captured 413 response.

- [ ] **Step 5: Commit the E2E helper and scenario**

```bash
git add src/dms/tests/EdFi.DataManagementService.Tests.E2E/AppSettings.cs src/dms/tests/EdFi.DataManagementService.Tests.E2E/StepDefinitions/StepDefinitions.cs src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature src/dms/tests/EdFi.DataManagementService.Tests.E2E/appsettings.json
git commit -m "test: add oversized request body relational coverage"
```

### Task 4: Verify the end-to-end result and clean up any gaps

**Files:**
- Review: all files changed in Tasks 1-3

- [ ] **Step 1: Run the full targeted frontend unit slice**

Run: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --nologo --verbosity minimal`

Expected: PASS.

- [ ] **Step 2: Run the relational E2E filter from the repo root**

Run: `pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-3&FullyQualifiedName~OversizedRequestBody'`

Expected: PASS, with the scenario returning 413 and no generic 500 logging path.

- [ ] **Step 3: Inspect the DMS logs for the oversized-body path**

Check the `dms-local` stack logs and confirm the oversized request is logged as an expected 413 rejection, not as an unhandled application failure.

- [ ] **Step 4: Fix any mismatch between config default and test helper limit**

If the unit or E2E assertions drift from the configured value, update the shared `MaxRequestBodySizeBytes` source in `AppSettings` and `appsettings.json` rather than hardcoding a byte literal in the helper.

- [ ] **Step 5: Commit the verification-only cleanup if needed**

```bash
git add -A
git commit -m "test: verify oversized request body handling"
```

