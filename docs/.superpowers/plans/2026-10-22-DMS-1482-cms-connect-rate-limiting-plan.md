# DMS-1482 Configuration Service `/connect/*` Rate Limiting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rate limit the Configuration Service's four `/connect/*` endpoints per source address and per `client_id`, answering 429 before any client secret is verified.

**Architecture:** A singleton `ConnectRateLimiter` holds two `PartitionedRateLimiter<string>` budgets (fixed window, no queue). A middleware placed after CORS and before authentication applies the source budget to every endpoint carrying `ConnectRateLimitMetadata`, without reading the body. The token and revoke handlers apply the client budget immediately before their manager call, after their existing parsing and shape checks. One shared helper writes the 429 in the route's contract: Ed-Fi problem details, or the OAuth error body on `/connect/revoke`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `System.Threading.RateLimiting` (shared framework), NUnit, FluentAssertions, FakeItEasy, `WebApplicationFactory<Program>`.

**Spec:** `reference/design/configuration-service/DMS-1482-cms-connect-rate-limiting.md` (approved 2026-10-22). Read it before starting; section references below (§4.2 and so on) point into it.

## Global Constraints

- Configuration section `ConnectRateLimit`: `Enabled` (default `true`), `SourcePermitLimit` (120), `ClientPermitLimit` (30), `WindowSeconds` (60). Fixed window, `QueueLimit = 0`, enforced per process.
- When `Enabled` is true, every number must be a positive integer. Invalid values are reported through the existing `ReportInvalidConfiguration` path in `Program.cs`.
- Ed-Fi 429 body: `detail` = `The number of allowed requests has been exceeded. Retry the request later.`, `type` = `urn:ed-fi:management-api:too-many-requests`, `title` = `Too Many Requests`, `status` = 429, `correlationId` = `HttpContext.TraceIdentifier`, `validationErrors` = `{}`, `errors` = `[]`, media type `application/problem+json`.
- OAuth 429 body (revoke only): `error` = `temporarily_unavailable`, `error_description` = the same detail text, media type `application/json`.
- `Retry-After` = the lease's `MetadataName.RetryAfter` rounded up to whole seconds. Omit it when absent. Add no other headers (`Cache-Control` belongs to DMS-1484).
- Source key: `Connection.RemoteIpAddress` after forwarded headers are applied. IPv4-mapped IPv6 becomes IPv4, native IPv6 is masked to /64, and null maps to the shared `"unknown"` partition.
- Client key: the `client_id` from the route's existing parser, compared with `StringComparer.OrdinalIgnoreCase`, truncated to 256 characters for keying only.
- Never log a client ID, IP address, credential or token. A rejection log carries only the gate (`source`/`client`) and the route template.
- Do not change the existing `too-many-tokens` 429, any DMS code, or token, revoke and register parsing order.
- Code style (AGENTS.md):
  - modern C# with file-scoped namespaces;
  - `is null` / `is not null`;
  - `System.Text.Json` only;
  - NUnit `Given_` fixtures with arrange and act in `[SetUp]` and `It_` assertions;
  - no sleeps or timing races;
  - format with `dotnet csharpier format <file>`.
- Window renewal is not tested: `FixedWindowRateLimiterOptions` has no `TimeProvider`, and AGENTS.md disallows real-time waits.

## Paths used throughout

- `FE` = `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore`
- `FET` = `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit`
- `BET` = `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit`
- `DM` = `src/config/datamodel/EdFi.DmsConfigurationService.DataModel`

Run every command from the repository root.

## File structure

| File | Status | Responsibility |
|---|---|---|
| `DM/Infrastructure/FailureResponse.cs` | modify | `ForTooManyRequests` Ed-Fi body |
| `FE/Infrastructure/FailureResults.cs` | modify | `TooManyRequests` `IResult` |
| `FE/Configuration/ConnectRateLimitSettings.cs` | create | settings + validator |
| `FE/Infrastructure/WebApplicationBuilderExtensions.cs` | modify | bind/validate settings, register limiter |
| `FE/Program.cs` | modify | options check, middleware placement |
| `FE/appsettings.json` | modify | shipped defaults |
| `FE/Infrastructure/ConnectRateLimiter.cs` | create | two budgets and partition keys |
| `FE/Infrastructure/ConnectRateLimitMetadata.cs` | create | route marker |
| `FE/Infrastructure/ConnectRateLimitRejection.cs` | create | shared 429 writer and logging |
| `FE/Middleware/ConnectSourceRateLimitMiddleware.cs` | create | source gate |
| `FE/Modules/IdentityModule.cs` | modify | marker on four routes; client gate in token and revoke |
| `BET/Infrastructure/FailureResponseTests.cs` | modify | factory tests |
| `FET/Infrastructure/FailureResultsTests.cs` | modify | result tests |
| `FET/Configuration/ConnectRateLimitSettingsValidatorTests.cs` | create | validator tests |
| `FET/Configuration/ConnectRateLimitSettingsTests.cs` | create | shipped values, invalid-config host |
| `FET/Infrastructure/ConnectRateLimiterTests.cs` | create | limiter unit tests |
| `FET/Infrastructure/RemoteIpStartupFilter.cs` | create | shared test-only remote IP filter (moved from `ForwardedHeadersTests`) |
| `FET/ForwardedHeadersTests.cs` | modify | use shared filter |
| `FET/Infrastructure/ConnectRateLimitTestHost.cs` | create | shared host + fakes for limiter tests |
| `FET/Middleware/ConnectSourceRateLimitMiddlewareTests.cs` | create | middleware unit tests |
| `FET/Middleware/ConnectRateLimitPipelineTests.cs` | create | source-gate pipeline tests |
| `FET/Modules/IdentityModuleRateLimitTests.cs` | create | client-gate pipeline tests |
| `eng/docker-compose/local-config.yml`, `published-config.yml`, `.env.example` | modify | compose surface |
| `eng/docker-compose/.env.e2e`, `.env.config.e2e`, `.env.config.mssql.e2e`, `.env.config.mssql.multitenant.e2e`, `.env.routeContext.e2e`, `.env.cursorpartitions.e2e`, `.env.smoke`, `.env.smoke.ds61` | modify | generous CI limits |
| `docs/CONFIGURATION.md`, `reference/design/configuration-service/CS-AUTH.md`, `docs/changelog/8.1.0.md` | modify | documentation |

**CI env file selection (resolves the spec's §4.5 open item).** Workflow and script usage shows these files feed E2E or smoke runs:
- `.env.e2e`: DMS E2E shards, the weekend build, nightly Keycloak and the SDK package build.
- `.env.config.e2e`: the `build-config.ps1` default and `setup-local-cms.ps1`.
- `.env.config.mssql.e2e` and `.env.config.mssql.multitenant.e2e`: CMS PR MSSQL lanes.
- `.env.routeContext.e2e`: Instance Management E2E.
- `.env.cursorpartitions.e2e`: cursor-partition E2E.
- `.env.smoke` and `.env.smoke.ds61`: smoke tests.

Deliberately unchanged:
- `.env.template*`: template builds, not E2E or smoke, so the shipped defaults apply.
- `.env.multitenancy`: developer stack.
- `.env.mssql`, `.env.document`, `.env.ds`: overlays that do not set CMS identity values.
- `eng/azure-vm/compose`: it doesn't map `BearerTokenPerClientLimit` either, and the `appsettings.json` defaults apply.

---

### Task 1: Ed-Fi too-many-requests failure body

**Files:**
- Modify: `DM/Infrastructure/FailureResponse.cs`
- Modify: `FE/Infrastructure/FailureResults.cs`
- Test: `BET/Infrastructure/FailureResponseTests.cs`
- Test: `FET/Infrastructure/FailureResultsTests.cs`

**Interfaces:**
- Produces: `public static JsonNode FailureResponse.ForTooManyRequests(string correlationId)`; `internal static IResult FailureResults.TooManyRequests(string correlationId)`.

- [ ] **Step 1: Write the failing factory tests**

In `BET/Infrastructure/FailureResponseTests.cs`, add this case to the `_factoryCases` array in `Given_each_failure_factory`, directly after the `ForTooManyTokens` case:

```csharp
            new(
                nameof(FailureResponse.ForTooManyRequests),
                () => FailureResponse.ForTooManyRequests(CorrelationId),
                "urn:ed-fi:management-api:too-many-requests",
                429,
                "Too Many Requests"
            ),
```

Add this fixture after the `Given_a_too_many_tokens_failure` fixtures, inside `FailureResponseTests`:

```csharp
    [TestFixture]
    public class Given_a_too_many_requests_failure
    {
        private JsonNode _result = null!;

        [SetUp]
        public void Setup() => _result = FailureResponse.ForTooManyRequests(CorrelationId);

        [Test]
        public void It_uses_the_too_many_requests_type() =>
            _result["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");

        [Test]
        public void It_has_the_too_many_requests_title() =>
            _result["title"]!.GetValue<string>().Should().Be("Too Many Requests");

        [Test]
        public void It_has_status_429() => _result["status"]!.GetValue<int>().Should().Be(429);

        [Test]
        public void It_has_the_retry_detail() =>
            _result["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The number of allowed requests has been exceeded. Retry the request later.");

        [Test]
        public void It_carries_the_correlation_id() =>
            _result["correlationId"]!.GetValue<string>().Should().Be(CorrelationId);

        [Test]
        public void It_has_empty_extension_members()
        {
            _result["validationErrors"]!.AsObject().Count.Should().Be(0);
            _result["errors"]!.AsArray().Count.Should().Be(0);
        }
    }
```

- [ ] **Step 2: Write the failing result test**

In `FET/Infrastructure/FailureResultsTests.cs`, add after `Given_a_too_many_tokens_failure_result`:

```csharp
    [TestFixture]
    public class Given_a_too_many_requests_failure_result
    {
        private ExecutedResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _result = await ExecuteAsync(FailureResults.TooManyRequests("corr-requests"));
        }

        [Test]
        public void It_returns_429() => _result.StatusCode.Should().Be(429);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _result.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_has_the_too_many_requests_type() =>
            _result.Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");

        [Test]
        public void It_has_a_body_status_matching_the_http_status() =>
            _result.Body["status"]!.GetValue<int>().Should().Be(429);

        [Test]
        public void It_includes_the_correlation_id() =>
            _result.Body["correlationId"]!.GetValue<string>().Should().Be("corr-requests");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResponseTests"`
Expected: build FAILS with CS0117, "'FailureResponse' does not contain a definition for 'ForTooManyRequests'".

- [ ] **Step 4: Implement the factory**

In `DM/Infrastructure/FailureResponse.cs`, add the field after `_tooManyTokensType`:

```csharp
    private static readonly string _tooManyRequestsType = $"{_typePrefix}:too-many-requests";
```

Add the factory directly after `ForTooManyTokens`:

```csharp
    /// <summary>
    /// 429 response for a request refused by the <c>/connect/*</c> rate limiter (DMS-1482). The type
    /// mirrors DMS's established <c>urn:ed-fi:api:too-many-requests</c>, and the detail is the text DMS
    /// uses for its own rate-limit rejection. Distinct from <see cref="ForTooManyTokens"/>, which is the
    /// active-token cap and whose text DMS parses.
    /// </summary>
    public static JsonNode ForTooManyRequests(string correlationId) =>
        CreateBaseJsonObject(
            detail: "The number of allowed requests has been exceeded. Retry the request later.",
            type: _tooManyRequestsType,
            title: "Too Many Requests",
            status: 429,
            correlationId: correlationId,
            errors: []
        );
```

In `FE/Infrastructure/FailureResults.cs`, add after `TooManyTokens`:

```csharp
    /// <summary>
    /// Structured 429 <c>urn:ed-fi:management-api:too-many-requests</c> response for a <c>/connect/*</c>
    /// request refused by the rate limiter (DMS-1482).
    /// </summary>
    public static IResult TooManyRequests(string correlationId)
    {
        return Results.Json(
            FailureResponse.ForTooManyRequests(correlationId),
            contentType: _errorContentType,
            statusCode: 429
        );
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResponseTests"`
Expected: PASS. Every `Given_each_failure_factory` case passes, the new one included.

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResultsTests"`
Expected: PASS.

- [ ] **Step 6: Format and commit**

```bash
dotnet csharpier format src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Infrastructure/FailureResponse.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/FailureResults.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Infrastructure/FailureResponseTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/FailureResultsTests.cs
git add src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Infrastructure/FailureResponse.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/FailureResults.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Infrastructure/FailureResponseTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/FailureResultsTests.cs
git commit -m "[DMS-1482] Add the Configuration Service too-many-requests failure response"
```

---

### Task 2: `ConnectRateLimit` settings, validation and shipped defaults

**Files:**
- Create: `FE/Configuration/ConnectRateLimitSettings.cs`
- Modify: `FE/Infrastructure/WebApplicationBuilderExtensions.cs` (options block, around lines 75-95)
- Modify: `FE/Program.cs` (`ReportInvalidConfiguration`, around lines 319-334)
- Modify: `FE/appsettings.json`
- Test: `FET/Configuration/ConnectRateLimitSettingsValidatorTests.cs`
- Test: `FET/Configuration/ConnectRateLimitSettingsTests.cs`

**Interfaces:**
- Produces: `public class ConnectRateLimitSettings`, which has `const string SectionName = "ConnectRateLimit"`, `bool Enabled`, `int SourcePermitLimit`, `int ClientPermitLimit` and `int WindowSeconds`. Also `public class ConnectRateLimitSettingsValidator : IValidateOptions<ConnectRateLimitSettings>`. `IOptions<ConnectRateLimitSettings>` is registered in DI.

- [ ] **Step 1: Write the failing validator tests**

Create `FET/Configuration/ConnectRateLimitSettingsValidatorTests.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

public class ConnectRateLimitSettingsValidatorTests
{
    [TestFixture]
    public class Given_the_default_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitSettingsValidator().Validate(null, new ConnectRateLimitSettings());

        [Test]
        public void It_succeeds() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture("SourcePermitLimit", 0)]
    [TestFixture("SourcePermitLimit", -1)]
    [TestFixture("ClientPermitLimit", 0)]
    [TestFixture("ClientPermitLimit", -5)]
    [TestFixture("WindowSeconds", 0)]
    [TestFixture("WindowSeconds", -60)]
    public class Given_a_non_positive_value(string setting, int value)
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var settings = new ConnectRateLimitSettings();
            typeof(ConnectRateLimitSettings).GetProperty(setting)!.SetValue(settings, value);
            _result = new ConnectRateLimitSettingsValidator().Validate(null, settings);
        }

        [Test]
        public void It_fails() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_names_the_setting() =>
            _result.Failures.Should().ContainSingle().Which.Should().Be($"ConnectRateLimit:{setting} must be a positive integer.");
    }

    [TestFixture]
    public class Given_several_invalid_values
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitSettingsValidator().Validate(
                null,
                new ConnectRateLimitSettings { SourcePermitLimit = 0, WindowSeconds = 0 }
            );

        [Test]
        public void It_reports_each_one() =>
            _result
                .Failures.Should()
                .BeEquivalentTo(
                    "ConnectRateLimit:SourcePermitLimit must be a positive integer.",
                    "ConnectRateLimit:WindowSeconds must be a positive integer."
                );
    }

    [TestFixture]
    public class Given_limiting_is_disabled_with_zero_values
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = new ConnectRateLimitSettingsValidator().Validate(
                null,
                new ConnectRateLimitSettings
                {
                    Enabled = false,
                    SourcePermitLimit = 0,
                    ClientPermitLimit = 0,
                    WindowSeconds = 0,
                }
            );

        [Test]
        public void It_succeeds() => _result.Succeeded.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Write the failing shipped-value and host tests**

Create `FET/Configuration/ConnectRateLimitSettingsTests.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// The shipped appsettings.json is read from the build output rather than through the host, because the
/// host under test reads appsettings.Test.json too (pattern: ClientSecretHashingIterationsSettingsTests).
/// </summary>
[TestFixture]
public class Given_the_shipped_connect_rate_limit_settings
{
    private IConfigurationSection _section = null!;

    [SetUp]
    public void Setup()
    {
        IConfigurationRoot shippedConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        _section = shippedConfiguration.GetSection(ConnectRateLimitSettings.SectionName);
    }

    [Test]
    public void It_enables_limiting() => bool.Parse(_section["Enabled"]!).Should().BeTrue();

    [Test]
    public void It_ships_a_source_limit_of_120() => _section["SourcePermitLimit"].Should().Be("120");

    [Test]
    public void It_ships_a_client_limit_of_30() => _section["ClientPermitLimit"].Should().Be("30");

    [Test]
    public void It_ships_a_60_second_window() => _section["WindowSeconds"].Should().Be("60");

    [Test]
    public void It_matches_the_model_defaults()
    {
        var defaults = new ConnectRateLimitSettings();
        defaults.Enabled.Should().BeTrue();
        defaults.SourcePermitLimit.Should().Be(120);
        defaults.ClientPermitLimit.Should().Be(30);
        defaults.WindowSeconds.Should().Be(60);
    }
}

/// <summary>
/// Invalid ConnectRateLimit values go through ReportInvalidConfiguration: the host keeps running and every
/// request is answered with the generic Ed-Fi 500, so the limiter is never constructed.
/// </summary>
[TestFixture]
public class Given_an_invalid_connect_rate_limit_setting_at_startup
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpResponseMessage _response = null!;
    private JsonObject _body = null!;

    [SetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["ConnectRateLimit:ClientPermitLimit"] = "0" }
                    )
            );
        });
        using HttpClient client = _factory.CreateClient();
        _response = await client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
        );
        _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_answers_500() => _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

    [Test]
    public void It_uses_the_internal_server_error_contract() =>
        _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:internal-server-error");
}
```

With valid settings, the introspect request in the second fixture answers 400 ("token parameter is missing"). That's the control `Given_an_introspection_request_without_a_token` in `IdentityModuleTests` already covers, so a 500 here can only come from the configuration report.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ConnectRateLimitSettings"`
Expected: build FAILS with CS0246, "The type or namespace name 'ConnectRateLimitSettings' could not be found".

- [ ] **Step 4: Create the settings and validator**

Create `FE/Configuration/ConnectRateLimitSettings.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

/// <summary>
/// Limits for the anonymous <c>/connect/*</c> endpoints (DMS-1482), bound from the top-level
/// <c>ConnectRateLimit</c> section. Each budget is a fixed window of <see cref="WindowSeconds"/> with no
/// queue, enforced per process. See docs/CONFIGURATION.md#connectratelimit.
/// </summary>
public class ConnectRateLimitSettings
{
    public const string SectionName = "ConnectRateLimit";

    /// <summary>When false, no <c>/connect/*</c> request is rate limited.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Requests admitted per source address in each window, across all four <c>/connect/*</c> routes.
    /// A native IPv6 source is counted per /64.
    /// </summary>
    public int SourcePermitLimit { get; set; } = 120;

    /// <summary>Token and revocation requests admitted per <c>client_id</c> in each window, across all sources.</summary>
    public int ClientPermitLimit { get; set; } = 30;

    public int WindowSeconds { get; set; } = 60;
}

public class ConnectRateLimitSettingsValidator : IValidateOptions<ConnectRateLimitSettings>
{
    public ValidateOptionsResult Validate(string? name, ConnectRateLimitSettings options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        AddFailureUnlessPositive(failures, nameof(options.SourcePermitLimit), options.SourcePermitLimit);
        AddFailureUnlessPositive(failures, nameof(options.ClientPermitLimit), options.ClientPermitLimit);
        AddFailureUnlessPositive(failures, nameof(options.WindowSeconds), options.WindowSeconds);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddFailureUnlessPositive(List<string> failures, string setting, int value)
    {
        if (value < 1)
        {
            failures.Add($"{ConnectRateLimitSettings.SectionName}:{setting} must be a positive integer.");
        }
    }
}
```

- [ ] **Step 5: Register, check and ship the settings**

In `FE/Infrastructure/WebApplicationBuilderExtensions.cs`, extend the options chain directly after `.AddSingleton<IValidateOptions<ReverseProxySettings>, ReverseProxySettingsValidator>()`:

```csharp
            .Configure<ConnectRateLimitSettings>(
                webApplicationBuilder.Configuration.GetSection(ConnectRateLimitSettings.SectionName)
            )
            .AddSingleton<IValidateOptions<ConnectRateLimitSettings>, ConnectRateLimitSettingsValidator>()
```

In `FE/Program.cs`, inside `ReportInvalidConfiguration`, add after the `ReverseProxySettings` line:

```csharp
        _ = app.Services.GetRequiredService<IOptions<ConnectRateLimitSettings>>().Value;
```

In `FE/appsettings.json`, insert this section between the closing `}` of `IdentitySettings` and `"AllowedHosts": "*"`:

```json
    "ConnectRateLimit": {
        "Enabled": true,
        "SourcePermitLimit": 120,
        "ClientPermitLimit": 30,
        "WindowSeconds": 60
    },
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ConnectRateLimitSettings"`
Expected: PASS (all validator, shipped-value and invalid-startup tests).

- [ ] **Step 7: Format and commit**

```bash
dotnet csharpier format src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Configuration/ConnectRateLimitSettings.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Program.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Configuration/ConnectRateLimitSettingsValidatorTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Configuration/ConnectRateLimitSettingsTests.cs
git add src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Configuration/ConnectRateLimitSettings.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Program.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/appsettings.json src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Configuration/ConnectRateLimitSettingsValidatorTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Configuration/ConnectRateLimitSettingsTests.cs
git commit -m "[DMS-1482] Add ConnectRateLimit settings with validation and shipped defaults"
```

---

### Task 3: `ConnectRateLimiter` budgets and partition keys

**Files:**
- Create: `FE/Infrastructure/ConnectRateLimiter.cs`
- Modify: `FE/Infrastructure/WebApplicationBuilderExtensions.cs` (register the singleton)
- Test: `FET/Infrastructure/ConnectRateLimiterTests.cs`

**Interfaces:**
- Consumes: `ConnectRateLimitSettings` (Task 2).
- Produces:
  - `public sealed class ConnectRateLimiter : IDisposable`, registered as a DI singleton, with `RateLimitLease AttemptSource(HttpContext context)` and `RateLimitLease AttemptClient(string clientId)`.
  - `internal static string SourcePartitionKey(IPAddress? address)` and `internal static string ClientPartitionKey(string clientId)`.
  - `internal const string UnknownSourceKey = "unknown"` and `internal const int MaxClientKeyLength = 256`.

- [ ] **Step 1: Write the failing limiter tests**

Create `FET/Infrastructure/ConnectRateLimiterTests.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class ConnectRateLimiterTests
{
    private static ConnectRateLimiter CreateLimiter(int sourceLimit, int clientLimit, bool enabled = true) =>
        new(
            Options.Create(
                new ConnectRateLimitSettings
                {
                    Enabled = enabled,
                    SourcePermitLimit = sourceLimit,
                    ClientPermitLimit = clientLimit,
                    WindowSeconds = 60,
                }
            )
        );

    private static HttpContext ContextFrom(string? address) =>
        new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = address is null ? null : IPAddress.Parse(address) },
        };

    private static bool Admits(RateLimitLease lease)
    {
        using (lease)
        {
            return lease.IsAcquired;
        }
    }

    [TestFixture]
    public class Given_source_addresses
    {
        [TestCase(null, "unknown")]
        [TestCase("198.51.100.7", "198.51.100.7")]
        [TestCase("::ffff:198.51.100.7", "198.51.100.7")]
        [TestCase("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
        [TestCase("2001:db8:1:2::1", "2001:db8:1:2::/64")]
        [TestCase("::1", "::/64")]
        public void It_keys_each_address(string? address, string expectedKey) =>
            ConnectRateLimiter
                .SourcePartitionKey(address is null ? null : IPAddress.Parse(address))
                .Should()
                .Be(expectedKey);
    }

    [TestFixture]
    public class Given_client_ids
    {
        [Test]
        public void It_keeps_a_short_id() => ConnectRateLimiter.ClientPartitionKey("ClientA").Should().Be("ClientA");

        [Test]
        public void It_truncates_a_long_id_to_256_characters() =>
            ConnectRateLimiter.ClientPartitionKey(new string('x', 300)).Should().Be(new string('x', 256));
    }

    [TestFixture]
    public class Given_a_client_that_exhausts_its_budget
    {
        private readonly List<bool> _sameClient = [];
        private bool _caseVariant;
        private bool _otherClient;
        private bool _sourceAfterClientExhausted;
        private bool _retryAfterPresent;
        private TimeSpan _retryAfter;

        [SetUp]
        public void Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 2, clientLimit: 2);
            _sameClient.Add(Admits(limiter.AttemptClient("ClientA")));
            _sameClient.Add(Admits(limiter.AttemptClient("ClientA")));
            using (RateLimitLease rejected = limiter.AttemptClient("ClientA"))
            {
                _sameClient.Add(rejected.IsAcquired);
                _retryAfterPresent = rejected.TryGetMetadata(MetadataName.RetryAfter, out _retryAfter);
            }
            _caseVariant = Admits(limiter.AttemptClient("CLIENTA"));
            _otherClient = Admits(limiter.AttemptClient("ClientB"));
            _sourceAfterClientExhausted = Admits(limiter.AttemptSource(ContextFrom("198.51.100.7")));
        }

        [Test]
        public void It_admits_up_to_the_limit_then_rejects() =>
            _sameClient.Should().Equal(true, true, false);

        [Test]
        public void It_shares_the_budget_across_case_variants() => _caseVariant.Should().BeFalse();

        [Test]
        public void It_keeps_another_client_independent() => _otherClient.Should().BeTrue();

        [Test]
        public void It_keeps_the_source_budget_independent() => _sourceAfterClientExhausted.Should().BeTrue();

        [Test]
        public void It_supplies_a_retry_after_within_the_window()
        {
            _retryAfterPresent.Should().BeTrue();
            _retryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        }
    }

    [TestFixture]
    public class Given_long_client_ids_sharing_a_256_character_prefix
    {
        private bool _second;

        [SetUp]
        public void Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 10, clientLimit: 1);
            string prefix = new('c', 256);
            _ = Admits(limiter.AttemptClient(prefix + "-one"));
            _second = Admits(limiter.AttemptClient(prefix + "-two"));
        }

        [Test]
        public void It_shares_one_budget() => _second.Should().BeFalse();
    }

    [TestFixture]
    public class Given_sources_in_and_outside_one_ipv6_64
    {
        private bool _sameSlash64;
        private bool _otherSlash64;
        private bool _unknownSecond;

        [SetUp]
        public void Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1, clientLimit: 10);
            _ = Admits(limiter.AttemptSource(ContextFrom("2001:db8::1")));
            _sameSlash64 = Admits(limiter.AttemptSource(ContextFrom("2001:db8::2")));
            _otherSlash64 = Admits(limiter.AttemptSource(ContextFrom("2001:db8:0:1::1")));
            _ = Admits(limiter.AttemptSource(ContextFrom(null)));
            _unknownSecond = Admits(limiter.AttemptSource(ContextFrom(null)));
        }

        [Test]
        public void It_shares_a_budget_within_a_64() => _sameSlash64.Should().BeFalse();

        [Test]
        public void It_separates_another_64() => _otherSlash64.Should().BeTrue();

        [Test]
        public void It_limits_unknown_sources_as_one_partition() => _unknownSecond.Should().BeFalse();
    }

    [TestFixture]
    public class Given_limiting_is_disabled
    {
        private readonly List<bool> _results = [];

        [SetUp]
        public void Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1, clientLimit: 1, enabled: false);
            for (int i = 0; i < 5; i++)
            {
                _results.Add(Admits(limiter.AttemptClient("ClientA")));
                _results.Add(Admits(limiter.AttemptSource(ContextFrom("198.51.100.7"))));
            }
        }

        [Test]
        public void It_admits_every_request() => _results.Should().OnlyContain(admitted => admitted);
    }

    [TestFixture]
    public class Given_a_disposed_limiter
    {
        private ConnectRateLimiter _limiter = null!;

        [SetUp]
        public void Setup()
        {
            _limiter = CreateLimiter(sourceLimit: 1, clientLimit: 1);
            _limiter.Dispose();
        }

        [Test]
        public void It_refuses_further_use() =>
            FluentActions.Invoking(() => _limiter.AttemptClient("ClientA")).Should().Throw<ObjectDisposedException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ConnectRateLimiterTests"`
Expected: build FAILS with CS0246, "'ConnectRateLimiter' could not be found".

- [ ] **Step 3: Implement the limiter**

Create `FE/Infrastructure/ConnectRateLimiter.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The two independent <c>/connect/*</c> budgets (DMS-1482 D2): one per source address and one per
/// <c>client_id</c> across all sources. A request must be admitted by every budget that applies to it.
/// Each budget is a fixed window with no queue, so a rejection is immediate; enforcement is per process.
/// Keys come from the transport address and the parsed <c>client_id</c> only, never from a lookup, so a
/// rejection says nothing about whether a client exists. A singleton; the container disposes it.
/// </summary>
public sealed class ConnectRateLimiter : IDisposable
{
    /// <summary>The one partition for requests whose remote address is unknown; never exempt.</summary>
    internal const string UnknownSourceKey = "unknown";

    /// <summary>Longer client ids are truncated for keying only, which can only merge partitions.</summary>
    internal const int MaxClientKeyLength = 256;

    private readonly PartitionedRateLimiter<string> _sourceLimiter;
    private readonly PartitionedRateLimiter<string> _clientLimiter;

    public ConnectRateLimiter(IOptions<ConnectRateLimitSettings> options)
    {
        ConnectRateLimitSettings settings = options.Value;
        _sourceLimiter = CreateLimiter(settings, settings.SourcePermitLimit, StringComparer.Ordinal);

        // Case-insensitive, matching RegisterClient.IsUnique and the MSSQL client lookup, so case variants
        // of one client_id cannot buy fresh budgets (reference/adr-client-id-casing.md).
        _clientLimiter = CreateLimiter(settings, settings.ClientPermitLimit, StringComparer.OrdinalIgnoreCase);
    }

    public RateLimitLease AttemptSource(HttpContext context) =>
        _sourceLimiter.AttemptAcquire(SourcePartitionKey(context.Connection.RemoteIpAddress));

    public RateLimitLease AttemptClient(string clientId) =>
        _clientLimiter.AttemptAcquire(ClientPartitionKey(clientId));

    public void Dispose()
    {
        _sourceLimiter.Dispose();
        _clientLimiter.Dispose();
    }

    /// <summary>
    /// The address after <c>UseForwardedHeaders</c>, so only a trusted proxy can change it. A native IPv6
    /// address is keyed on its /64, because a subscriber normally holds a whole /64 and could otherwise
    /// rotate addresses for fresh budgets (D6).
    /// </summary>
    internal static string SourcePartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownSourceKey;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"{new IPAddress(bytes)}/64";
        }

        return address.ToString();
    }

    internal static string ClientPartitionKey(string clientId) =>
        clientId.Length > MaxClientKeyLength ? clientId[..MaxClientKeyLength] : clientId;

    private static PartitionedRateLimiter<string> CreateLimiter(
        ConnectRateLimitSettings settings,
        int permitLimit,
        IEqualityComparer<string> comparer
    ) =>
        PartitionedRateLimiter.Create<string, string>(
            key =>
                settings.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        key,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                    )
                    : RateLimitPartition.GetNoLimiter(key),
            comparer
        );
}
```

In `FE/Infrastructure/WebApplicationBuilderExtensions.cs`, register the singleton next to the other `AddSingleton` calls, directly after `webApplicationBuilder.Services.AddSingleton<SecretValueCache>();`:

```csharp
        // Holds the /connect/* budgets for the life of the process; disposed by the container (DMS-1482).
        webApplicationBuilder.Services.AddSingleton<ConnectRateLimiter>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ConnectRateLimiterTests"`
Expected: PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/ConnectRateLimiter.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/ConnectRateLimiterTests.cs
git add src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/ConnectRateLimiter.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/ConnectRateLimiterTests.cs
git commit -m "[DMS-1482] Add per-source and per-client connect rate limiter"
```

---

### Task 4: Source gate middleware, route marker and shared rejection

**Files:**
- Create: `FE/Infrastructure/ConnectRateLimitMetadata.cs`
- Create: `FE/Infrastructure/ConnectRateLimitRejection.cs`
- Create: `FE/Middleware/ConnectSourceRateLimitMiddleware.cs`
- Modify: `FE/Modules/IdentityModule.cs` (`MapEndpoints`, lines 33-58)
- Modify: `FE/Program.cs` (between `app.UseCors` and `app.UseAuthentication`)
- Create: `FET/Infrastructure/RemoteIpStartupFilter.cs`
- Modify: `FET/ForwardedHeadersTests.cs` (remove the nested `RemoteIpStartupFilter`, use the shared one)
- Create: `FET/Infrastructure/ConnectRateLimitTestHost.cs`
- Test: `FET/Middleware/ConnectSourceRateLimitMiddlewareTests.cs`
- Test: `FET/Middleware/ConnectRateLimitPipelineTests.cs`

**Interfaces:**
- Consumes: `ConnectRateLimiter` (Task 3), `FailureResponse.ForTooManyRequests` / `FailureResults.TooManyRequests` (Task 1), the existing `OAuthErrorContractMetadata`, `OAuthErrorResponseWriter` and `FailureResponseWriter`.
- Produces:
  - `internal sealed class ConnectRateLimitMetadata`, with `static readonly Instance`.
  - `internal static class ConnectRateLimitRejection`, with:
    - constants `SourceGate = "source"`, `ClientGate = "client"`, `OAuthError = "temporarily_unavailable"` and `Description`;
    - `Task WriteAsync(HttpContext, RateLimitLease, string gate, ILogger)`;
    - `IResult Create(HttpContext, RateLimitLease, string gate, ILogger)`.
  - `public class ConnectSourceRateLimitMiddleware`.
  - Test-only: `internal sealed class RemoteIpStartupFilter`, with `const string HeaderName = "X-Test-Remote-Ip"`.
  - Test-only: `internal static class ConnectRateLimitTestHost` (members shown below), reused in Task 5.

- [ ] **Step 1: Move the test-only remote IP filter**

Create `FET/Infrastructure/RemoteIpStartupFilter.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Test-only: TestServer leaves the connection remote IP null, so this sets it from a request header
/// ahead of the application pipeline (and so ahead of UseForwardedHeaders), letting tests simulate
/// distinct, trusted and untrusted peers deterministically.
/// </summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(
                async (context, nextMiddleware) =>
                {
                    if (
                        context.Request.Headers.TryGetValue(HeaderName, out var value)
                        && IPAddress.TryParse(value.ToString(), out var ip)
                    )
                    {
                        context.Connection.RemoteIpAddress = ip;
                    }

                    await nextMiddleware();
                }
            );
            next(app);
        };
}
```

In `FET/ForwardedHeadersTests.cs`:
- delete the nested `private sealed class RemoteIpStartupFilter` (the last member of the fixture);
- add `using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;`;
- leave the `"X-Test-Remote-Ip"` literals as they are (same value as `RemoteIpStartupFilter.HeaderName`).

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~Given_A_Reverse_Proxy_Configuration"`
Expected: PASS (unchanged behavior). If `System.Net` or `Microsoft.AspNetCore.Builder` usings are now unused in `ForwardedHeadersTests.cs`, remove them.

- [ ] **Step 2: Create the shared test host**

Create `FET/Infrastructure/ConnectRateLimitTestHost.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using FakeItEasy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// An isolated host for the DMS-1482 limiter tests. Every factory gets its own ConnectRateLimiter
/// singleton, so budgets never leak between fixtures. A null limit leaves the value to the shipped or
/// environment configuration. The token and revocation managers default to fakes that always succeed,
/// so an admitted request is answered 200 and a rejected one 429.
/// </summary>
internal static class ConnectRateLimitTestHost
{
    public const string ClientSecret = "test123@Puiu";

    public static WebApplicationFactory<Program> Create(
        int? sourcePermitLimit = null,
        int? clientPermitLimit = null,
        bool? enabled = null,
        ITokenManager? tokenManager = null,
        ITokenRevocationManager? revocationManager = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    Dictionary<string, string?> settings = [];
                    if (enabled is not null)
                    {
                        settings["ConnectRateLimit:Enabled"] = enabled.Value.ToString();
                    }
                    if (sourcePermitLimit is not null)
                    {
                        settings["ConnectRateLimit:SourcePermitLimit"] = sourcePermitLimit.Value.ToString(
                            CultureInfo.InvariantCulture
                        );
                    }
                    if (clientPermitLimit is not null)
                    {
                        settings["ConnectRateLimit:ClientPermitLimit"] = clientPermitLimit.Value.ToString(
                            CultureInfo.InvariantCulture
                        );
                    }
                    configuration.AddInMemoryCollection(settings);
                }
            );
            builder.ConfigureServices(collection =>
            {
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => new RegisterRequest.Validator());
                ITokenManager tokens = tokenManager ?? SucceedingTokenManager();
                collection.AddTransient(_ => tokens);
                collection.RemoveAll<ITokenRevocationManager>();
                collection.AddSingleton(revocationManager ?? CompletingRevocationManager());
            });
        });

    public static ITokenManager SucceedingTokenManager()
    {
        ITokenManager tokenManager = A.Fake<ITokenManager>();
        A.CallTo(() => tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
            .Returns(
                new TokenResult.Success("""{"access_token":"input123token","expires_in":900,"token_type":"bearer"}""")
            );
        return tokenManager;
    }

    public static ITokenRevocationManager CompletingRevocationManager()
    {
        ITokenRevocationManager revocationManager = A.Fake<ITokenRevocationManager>();
        A.CallTo(() => revocationManager.RevokeTokenAsync(A<TokenRevocationRequest>._, A<CancellationToken>._))
            .Returns(new TokenRevocationResult.Completed());
        return revocationManager;
    }

    public static FormUrlEncodedContent TokenForm(string clientId, string grantType = "client_credentials") =>
        new([new("client_id", clientId), new("client_secret", ClientSecret), new("grant_type", grantType)]);

    public static FormUrlEncodedContent RevokeForm(string clientId) =>
        new([new("token", "opaque-token"), new("client_id", clientId), new("client_secret", ClientSecret)]);

    public static FormUrlEncodedContent EmptyForm() => new(Array.Empty<KeyValuePair<string, string>>());

    public static HttpRequestMessage Post(string path, HttpContent content, string? remoteIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (remoteIp is not null)
        {
            request.Headers.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        }
        return request;
    }

    public static AuthenticationHeaderValue Basic(string clientId) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{ClientSecret}")));

    public static async Task<List<int>> SendAll(HttpClient client, IEnumerable<Func<HttpRequestMessage>> requests)
    {
        List<int> statuses = [];
        foreach (Func<HttpRequestMessage> request in requests)
        {
            using HttpResponseMessage response = await client.SendAsync(request());
            statuses.Add((int)response.StatusCode);
        }
        return statuses;
    }
}
```

- [ ] **Step 3: Write the failing middleware unit tests**

Create `FET/Middleware/ConnectSourceRateLimitMiddlewareTests.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

public class ConnectSourceRateLimitMiddlewareTests
{
    private const string RemoteIp = "198.51.100.7";
    private const string TokenRoute = "connect/token/{**contextPath}";

    private static ConnectRateLimiter CreateLimiter(int sourceLimit) =>
        new(
            Options.Create(
                new ConnectRateLimitSettings
                {
                    SourcePermitLimit = sourceLimit,
                    ClientPermitLimit = 100,
                    WindowSeconds = 60,
                }
            )
        );

    private static DefaultHttpContext CreateContext(params object[] metadata)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-limit" };
        context.Connection.RemoteIpAddress = IPAddress.Parse(RemoteIp);
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(
            new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(TokenRoute),
                0,
                new EndpointMetadataCollection(metadata),
                "test"
            )
        );
        return context;
    }

    private static async Task<string> BodyOf(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    [TestFixture]
    public class Given_requests_to_an_endpoint_without_the_marker
    {
        private int _nextCalls;

        [SetUp]
        public async Task Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1);
            var middleware = new ConnectSourceRateLimitMiddleware(_ =>
            {
                _nextCalls++;
                return Task.CompletedTask;
            });
            for (int i = 0; i < 3; i++)
            {
                await middleware.InvokeAsync(
                    CreateContext(),
                    limiter,
                    new TestLogger<ConnectSourceRateLimitMiddleware>()
                );
            }
        }

        [Test]
        public void It_passes_every_request_through() => _nextCalls.Should().Be(3);
    }

    [TestFixture]
    public class Given_a_marked_ed_fi_route_over_the_source_limit
    {
        private int _nextCalls;
        private DefaultHttpContext _rejected = null!;
        private JsonObject _body = null!;
        private TestLogger<ConnectSourceRateLimitMiddleware> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1);
            _logger = new TestLogger<ConnectSourceRateLimitMiddleware>();
            var middleware = new ConnectSourceRateLimitMiddleware(_ =>
            {
                _nextCalls++;
                return Task.CompletedTask;
            });
            await middleware.InvokeAsync(CreateContext(ConnectRateLimitMetadata.Instance), limiter, _logger);
            _rejected = CreateContext(ConnectRateLimitMetadata.Instance);
            await middleware.InvokeAsync(_rejected, limiter, _logger);
            _body = JsonNode.Parse(await BodyOf(_rejected))!.AsObject();
        }

        [Test]
        public void It_admits_the_first_request_only() => _nextCalls.Should().Be(1);

        [Test]
        public void It_answers_429() => _rejected.Response.StatusCode.Should().Be(429);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _rejected.Response.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_writes_the_too_many_requests_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");

        [Test]
        public void It_uses_the_trace_identifier_as_correlation_id() =>
            _body["correlationId"]!.GetValue<string>().Should().Be("trace-limit");

        [Test]
        public void It_sets_a_whole_second_retry_after()
        {
            int seconds = int.Parse(_rejected.Response.Headers.RetryAfter.ToString());
            seconds.Should().BeInRange(1, 60);
        }

        [Test]
        public void It_logs_the_gate_and_route_only()
        {
            string logged = _logger.Entries.Should().ContainSingle().Which.State!.ToString()!;
            logged.Should().Contain("source").And.Contain(TokenRoute).And.NotContain(RemoteIp);
        }
    }

    [TestFixture]
    public class Given_a_marked_oauth_route_over_the_source_limit
    {
        private DefaultHttpContext _rejected = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1);
            var middleware = new ConnectSourceRateLimitMiddleware(_ => Task.CompletedTask);
            var logger = new TestLogger<ConnectSourceRateLimitMiddleware>();
            await middleware.InvokeAsync(
                CreateContext(ConnectRateLimitMetadata.Instance, OAuthErrorContractMetadata.Instance),
                limiter,
                logger
            );
            _rejected = CreateContext(ConnectRateLimitMetadata.Instance, OAuthErrorContractMetadata.Instance);
            await middleware.InvokeAsync(_rejected, limiter, logger);
            _body = JsonNode.Parse(await BodyOf(_rejected))!.AsObject();
        }

        [Test]
        public void It_answers_429() => _rejected.Response.StatusCode.Should().Be(429);

        [Test]
        public void It_uses_the_oauth_content_type() =>
            _rejected.Response.ContentType.Should().Be("application/json; charset=utf-8");

        [Test]
        public void It_writes_the_oauth_error_body()
        {
            _body.Count.Should().Be(2);
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            _body["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The number of allowed requests has been exceeded. Retry the request later.");
        }
    }
}
```

- [ ] **Step 4: Write the failing pipeline tests**

Create `FET/Middleware/ConnectRateLimitPipelineTests.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.ConnectRateLimitTestHost;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// The source gate in the real pipeline (DMS-1482 REQ-1, REQ-2, REQ-3, REQ-4). Each fixture owns its host,
/// so its limiter starts empty. Admitted requests are answered by fakes or by validation; only the
/// limiter answers 429.
/// </summary>
public class ConnectRateLimitPipelineTests
{
    private const string Detail = "The number of allowed requests has been exceeded. Retry the request later.";

    [TestFixture]
    public class Given_an_enumeration_that_varies_client_id_from_one_source
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 2, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("guess-1"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("guess-2"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("guess-3"), "198.51.100.7"),
                ]
            );
        }

        [Test]
        public void It_rejects_the_request_past_the_source_limit() => _statuses.Should().Equal(200, 200, 429);
    }

    [TestFixture]
    public class Given_requests_that_vary_host_tenant_suffix_and_path_case
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 2, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a"), "198.51.100.7"),
                    () =>
                    {
                        HttpRequestMessage request = Post("/CONNECT/TOKEN/district-1", TokenForm("b"), "198.51.100.7");
                        request.Headers.Host = "other.example";
                        request.Headers.Add("Tenant", "tenant-b");
                        return request;
                    },
                    () => Post("/connect/token/district-2/2026", TokenForm("c"), "198.51.100.7"),
                ]
            );
        }

        [Test]
        public void It_counts_every_variant_against_one_source_budget() =>
            _statuses.Should().Equal(200, 200, 429);
    }

    [TestFixture]
    public class Given_a_second_source_address
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("b"), "198.51.100.7"),
                    () => Post("/connect/token", TokenForm("c"), "198.51.100.8"),
                ]
            );
        }

        [Test]
        public void It_gives_each_address_its_own_budget() => _statuses.Should().Equal(200, 429, 200);
    }

    [TestFixture]
    public class Given_ipv6_sources
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a"), "2001:db8::1"),
                    () => Post("/connect/token", TokenForm("b"), "2001:db8::2"),
                    () => Post("/connect/token", TokenForm("c"), "2001:db8:0:1::1"),
                ]
            );
        }

        [Test]
        public void It_shares_a_budget_within_a_64_and_not_across() => _statuses.Should().Equal(200, 429, 200);
    }

    [TestFixture("/connect/token", false)]
    [TestFixture("/connect/register", false)]
    [TestFixture("/connect/introspect", false)]
    [TestFixture("/connect/revoke", true)]
    public class Given_a_connect_route_over_the_source_limit(string path, bool oauthContract)
    {
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            using (await client.SendAsync(Post(path, EmptyForm()))) { }
            _response = await client.SendAsync(Post(path, EmptyForm()));
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _response.Dispose();

        [Test]
        public void It_answers_429() => ((int)_response.StatusCode).Should().Be(429);

        [Test]
        public void It_answers_in_the_route_contract()
        {
            if (oauthContract)
            {
                _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
                _body.Count.Should().Be(2);
                _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
                _body["error_description"]!.GetValue<string>().Should().Be(Detail);
                return;
            }

            _response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _body.Count.Should().Be(7);
            _body["detail"]!.GetValue<string>().Should().Be(Detail);
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["title"]!.GetValue<string>().Should().Be("Too Many Requests");
            _body["status"]!.GetValue<int>().Should().Be(429);
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_sends_a_whole_second_retry_after() =>
            _response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        [Test]
        public void It_keeps_the_security_headers() =>
            _response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
    }

    [TestFixture]
    public class Given_routes_that_are_not_connect_endpoints
    {
        private List<int> _nonConnect = null!;
        private List<int> _connect = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _nonConnect = await SendAll(
                client,
                [
                    () => new HttpRequestMessage(HttpMethod.Get, "/health"),
                    () => new HttpRequestMessage(HttpMethod.Get, "/health"),
                    () => Post("/connectx/token", TokenForm("a")),
                    () => Post("/connectx/token", TokenForm("a")),
                    () => new HttpRequestMessage(HttpMethod.Get, "/v3/vendors"),
                    () => new HttpRequestMessage(HttpMethod.Get, "/v3/vendors"),
                ]
            );
            _connect = await SendAll(
                client,
                [() => Post("/connect/token", TokenForm("a")), () => Post("/connect/token", TokenForm("b"))]
            );
        }

        [Test]
        public void It_never_limits_them() => _nonConnect.Should().NotContain(429);

        [Test]
        public void It_leaves_the_connect_budget_unspent() => _connect.Should().Equal(200, 429);
    }

    [TestFixture]
    public class Given_limiting_is_disabled
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1,
                clientPermitLimit: 1,
                enabled: false
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("a")),
                    () => Post("/connect/token", TokenForm("a")),
                    () => Post("/connect/token", TokenForm("a")),
                ]
            );
        }

        [Test]
        public void It_admits_every_request() => _statuses.Should().Equal(200, 200, 200);
    }

    /// <summary>
    /// Order of operations: the source gate runs before the revoke handler reads the body, so a malformed
    /// body past the limit is answered 429, while the same body within the limit still reaches the
    /// handler's encoding check (review R08).
    /// </summary>
    [TestFixture]
    public class Given_a_malformed_revocation_body
    {
        private const string MalformedBody = "token=%GG&client_id=c&client_secret=s";
        private HttpResponseMessage _withinLimit = null!;
        private string _withinLimitBody = null!;
        private HttpResponseMessage _pastLimit = null!;

        private static StringContent Malformed() =>
            new(MalformedBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _withinLimit = await client.SendAsync(Post("/connect/revoke", Malformed()));
            _withinLimitBody = await _withinLimit.Content.ReadAsStringAsync();
            _pastLimit = await client.SendAsync(Post("/connect/revoke", Malformed()));
        }

        [TearDown]
        public void TearDown()
        {
            _withinLimit.Dispose();
            _pastLimit.Dispose();
        }

        [Test]
        public void It_still_rejects_the_encoding_within_the_limit()
        {
            _withinLimit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(_withinLimitBody)!["error_description"]!
                .GetValue<string>()
                .Should()
                .Be("The request form payload is malformed.");
        }

        [Test]
        public void It_answers_429_past_the_limit_without_reading_the_body() =>
            ((int)_pastLimit.StatusCode).Should().Be(429);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_a_trusted_reverse_proxy
    {
        private const string UseForwardedHeadersEnv = "AppSettings__ReverseProxy__UseForwardedHeaders";
        private const string KnownProxiesEnv = "AppSettings__ReverseProxy__KnownProxies";
        private List<int> _viaTrustedProxy = null!;
        private List<int> _fromUntrustedPeer = null!;

        private static Func<HttpRequestMessage> Forwarded(string peer, string forwardedFor, string clientId) =>
            () =>
            {
                HttpRequestMessage request = Post("/connect/token", TokenForm(clientId), peer);
                request.Headers.Add("X-Forwarded-For", forwardedFor);
                return request;
            };

        [SetUp]
        public async Task Setup()
        {
            Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, "true");
            Environment.SetEnvironmentVariable(KnownProxiesEnv, "10.0.0.5");
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1, clientPermitLimit: 1000);
            using HttpClient client = factory.CreateClient();
            _viaTrustedProxy = await SendAll(
                client,
                [
                    Forwarded("10.0.0.5", "198.51.100.1", "a"),
                    Forwarded("10.0.0.5", "198.51.100.2", "b"),
                    Forwarded("10.0.0.5", "198.51.100.1", "c"),
                ]
            );
            _fromUntrustedPeer = await SendAll(
                client,
                [Forwarded("203.0.113.9", "198.51.100.3", "d"), Forwarded("203.0.113.9", "198.51.100.4", "e")]
            );
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, null);
            Environment.SetEnvironmentVariable(KnownProxiesEnv, null);
        }

        [Test]
        public void It_keys_on_the_forwarded_client_behind_a_trusted_proxy() =>
            _viaTrustedProxy.Should().Equal(200, 200, 429);

        [Test]
        public void It_ignores_forwarded_headers_from_an_untrusted_peer() =>
            _fromUntrustedPeer.Should().Equal(200, 429);
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_the_source_limit_set_through_the_environment
    {
        private const string SourceLimitEnv = "ConnectRateLimit__SourcePermitLimit";
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            Environment.SetEnvironmentVariable(SourceLimitEnv, "1");
            await using WebApplicationFactory<Program> factory = Create();
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [() => Post("/connect/token", TokenForm("a")), () => Post("/connect/token", TokenForm("b"))]
            );
        }

        [TearDown]
        public void TearDown() => Environment.SetEnvironmentVariable(SourceLimitEnv, null);

        [Test]
        public void It_applies_the_environment_value() => _statuses.Should().Equal(200, 429);
    }

    [TestFixture]
    public class Given_a_host_that_is_disposed
    {
        private ConnectRateLimiter _limiter = null!;

        [SetUp]
        public void Setup()
        {
            WebApplicationFactory<Program> factory = Create();
            using (factory.CreateClient())
            {
                _limiter = factory.Services.GetRequiredService<ConnectRateLimiter>();
            }
            factory.Dispose();
        }

        [Test]
        public void It_disposes_the_limiter() =>
            FluentActions.Invoking(() => _limiter.AttemptClient("a")).Should().Throw<ObjectDisposedException>();
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ConnectSourceRateLimitMiddlewareTests|FullyQualifiedName~ConnectRateLimitPipelineTests"`
Expected: build FAILS with CS0246, "'ConnectSourceRateLimitMiddleware' could not be found" and "'ConnectRateLimitMetadata' could not be found".

- [ ] **Step 6: Create the marker and the shared rejection**

Create `FE/Infrastructure/ConnectRateLimitMetadata.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking the four <c>/connect/*</c> routes that
/// <see cref="Middleware.ConnectSourceRateLimitMiddleware"/> limits per source address (DMS-1482). Routing,
/// not string matching, decides which requests carry it, so path base, route-context suffixes and path
/// case need no handling here.
/// </summary>
internal sealed class ConnectRateLimitMetadata
{
    public static readonly ConnectRateLimitMetadata Instance = new();

    private ConnectRateLimitMetadata() { }
}
```

Create `FE/Infrastructure/ConnectRateLimitRejection.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The 429 answer for a <c>/connect/*</c> request refused by <see cref="ConnectRateLimiter"/>, shared by
/// the source gate (middleware, <see cref="WriteAsync"/>) and the client gate (handlers, <see cref="Create"/>)
/// so both answer identically. The format follows the route's error contract, the same signal
/// <see cref="GlobalExceptionHandler"/> uses: the OAuth body on routes carrying
/// <see cref="OAuthErrorContractMetadata"/> (only <c>/connect/revoke</c>), Ed-Fi problem details otherwise.
/// </summary>
internal static class ConnectRateLimitRejection
{
    public const string SourceGate = "source";
    public const string ClientGate = "client";

    /// <summary>RFC 6749 and RFC 7009 define no throttling code; DMS-1491 may revisit this value.</summary>
    public const string OAuthError = "temporarily_unavailable";

    public const string Description = "The number of allowed requests has been exceeded. Retry the request later.";

    public static Task WriteAsync(HttpContext context, RateLimitLease lease, string gate, ILogger logger)
    {
        Prepare(context, lease, gate, logger);
        return UsesOAuthContract(context)
            ? OAuthErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                OAuthError,
                Description,
                context.RequestAborted
            )
            : FailureResponseWriter.WriteAsync(
                context,
                FailureResponse.ForTooManyRequests(context.TraceIdentifier),
                context.RequestAborted
            );
    }

    public static IResult Create(HttpContext context, RateLimitLease lease, string gate, ILogger logger)
    {
        Prepare(context, lease, gate, logger);
        return UsesOAuthContract(context)
            ? OAuthErrorResponseWriter.Create(StatusCodes.Status429TooManyRequests, OAuthError, Description)
            : FailureResults.TooManyRequests(context.TraceIdentifier);
    }

    private static void Prepare(HttpContext context, RateLimitLease lease, string gate, ILogger logger)
    {
        // The gate and the route template are fixed, service-owned text. The client id, the address
        // and every credential stay out of the log.
        logger.LogInformation(
            "Connect rate limit rejected a request at the {Gate} gate for route {Route}",
            gate,
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown"
        );

        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            // Rounded up so a client never retries sooner than the limiter recommends (DMS precedent).
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                CultureInfo.InvariantCulture
            );
        }
    }

    private static bool UsesOAuthContract(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<OAuthErrorContractMetadata>() is not null;
}
```

- [ ] **Step 7: Create the middleware**

Create `FE/Middleware/ConnectSourceRateLimitMiddleware.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// The source gate for the <c>/connect/*</c> endpoints (DMS-1482). Applies the per-source budget to every
/// endpoint carrying <see cref="ConnectRateLimitMetadata"/> and answers a refusal with 429 without reading
/// the request body, so the handlers' own parsing and validation, revoke's encoding check included, stay
/// exactly as they are. The per-client budget is applied later, inside the token and revoke handlers.
/// </summary>
public class ConnectSourceRateLimitMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <remarks>
    /// The limiter is resolved per request, not in the constructor: the pipeline is built even when
    /// ReportInvalidConfiguration has found invalid ConnectRateLimit settings, and constructing the
    /// limiter then would fail startup instead of being reported.
    /// </remarks>
    public async Task InvokeAsync(
        HttpContext context,
        ConnectRateLimiter rateLimiter,
        ILogger<ConnectSourceRateLimitMiddleware> logger
    )
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ConnectRateLimitMetadata>() is null)
        {
            await _next(context);
            return;
        }

        using RateLimitLease lease = rateLimiter.AttemptSource(context);
        if (!lease.IsAcquired)
        {
            await ConnectRateLimitRejection.WriteAsync(context, lease, ConnectRateLimitRejection.SourceGate, logger);
            return;
        }

        await _next(context);
    }
}
```

- [ ] **Step 8: Mark the routes and place the middleware**

In `FE/Modules/IdentityModule.cs` `MapEndpoints`, replace the three single-line mappings and add the marker to revoke:

```csharp
        endpoints
            .MapPost("connect/register/{**contextPath}", RegisterClient)
            .DisableAntiforgery()
            .WithMetadata(ConnectRateLimitMetadata.Instance);
        endpoints
            .MapPost("connect/token/{**contextPath}", GetClientAccessToken)
            .DisableAntiforgery()
            .WithMetadata(ConnectRateLimitMetadata.Instance);
        endpoints
            .MapPost("connect/introspect/{**contextPath}", IntrospectToken)
            .DisableAntiforgery()
            .WithMetadata(ConnectRateLimitMetadata.Instance);
```

In the revoke mapping's `.WithMetadata(...)`, add `ConnectRateLimitMetadata.Instance` as a fourth argument after `BearerAuthenticationExemptMetadata.Instance`.

In `FE/Program.cs`, replace:

```csharp
app.UseCors("AllowSwaggerUI");
app.UseAuthentication();
```

with:

```csharp
app.UseCors("AllowSwaggerUI");

// The /connect/* source gate (DMS-1482). After forwarded headers, so the remote address is the trusted
// one; inside both exception boundaries and after CORS, so Swagger UI can read a 429; before
// authentication, so a refused request does no bearer work. The per-client budget is applied inside the
// token and revoke handlers, immediately before secret verification.
app.UseMiddleware<ConnectSourceRateLimitMiddleware>();
app.UseAuthentication();
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ConnectSourceRateLimitMiddlewareTests|FullyQualifiedName~ConnectRateLimitPipelineTests|FullyQualifiedName~Given_A_Reverse_Proxy_Configuration"`
Expected: PASS.

- If `It_never_limits_them` fails, look at what the six non-connect requests returned. `/health` or `/v3/vendors` can legitimately be 401, 404 or 500 in the test host; only 429 is wrong. Investigate before changing any assertion.
- If a source fixture shows 400 where 200 was expected, check `TokenForm`'s secret against `TokenRequest.Validator`.

- [ ] **Step 10: Run the existing identity and pipeline suites**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~IdentityModuleTests|FullyQualifiedName~Middleware|FullyQualifiedName~GlobalExceptionHandlerTests"`
Expected: PASS with no changes to existing tests. Every existing fixture builds its own host, and none sends more than 120 connect requests to it.

- [ ] **Step 11: Format and commit**

```bash
dotnet csharpier format src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit
git add src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/ConnectRateLimitMetadata.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/ConnectRateLimitRejection.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Middleware/ConnectSourceRateLimitMiddleware.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Program.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/RemoteIpStartupFilter.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/ConnectRateLimitTestHost.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/ForwardedHeadersTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Middleware/ConnectSourceRateLimitMiddlewareTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Middleware/ConnectRateLimitPipelineTests.cs
git status --short
git commit -m "[DMS-1482] Rate limit /connect/* per source address before the body is read"
```

`git status --short` must show nothing else staged. If csharpier reformatted files outside this task, restore them with `git checkout -- <file>` before committing.

---

### Task 5: Client gate in the token and revoke handlers

**Files:**
- Modify: `FE/Modules/IdentityModule.cs` (`GetClientAccessToken` around lines 226-305, `RevokeToken` around lines 428-531, usings)
- Test: `FET/Modules/IdentityModuleRateLimitTests.cs`

**Interfaces:**
- Consumes: `ConnectRateLimiter.AttemptClient(string)` (Task 3), `ConnectRateLimitRejection.Create(...)` and `ConnectRateLimitRejection.ClientGate` (Task 4), and `ConnectRateLimitTestHost` (Task 4).
- Produces: nothing new for later tasks.

- [ ] **Step 1: Write the failing client-gate tests**

Create `FET/Modules/IdentityModuleRateLimitTests.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using static EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure.ConnectRateLimitTestHost;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// The per-client gate inside /connect/token and /connect/revoke (DMS-1482 REQ-1, REQ-2, REQ-3). The
/// source budget is raised out of the way. The managers are fakes, so "the manager was not called" is
/// exactly "the secret was not verified".
/// </summary>
public class IdentityModuleRateLimitTests
{
    private const string Detail = "The number of allowed requests has been exceeded. Retry the request later.";

    private static void ManagerCalls(ITokenManager tokenManager, int times) =>
        A.CallTo(() => tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
            .MustHaveHappened(times, Times.Exactly);

    private static void RevocationCalls(ITokenRevocationManager revocationManager, int times) =>
        A.CallTo(() => revocationManager.RevokeTokenAsync(A<TokenRevocationRequest>._, A<CancellationToken>._))
            .MustHaveHappened(times, Times.Exactly);

    [TestFixture]
    public class Given_a_client_over_its_token_limit
    {
        private readonly ITokenManager _tokenManager = SucceedingTokenManager();
        private List<int> _statuses = null!;
        private HttpResponseMessage _rejected = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2,
                tokenManager: _tokenManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [() => Post("/connect/token", TokenForm("ClientA")), () => Post("/connect/token", TokenForm("ClientA"))]
            );
            _rejected = await client.SendAsync(Post("/connect/token", TokenForm("ClientA")));
            _body = JsonNode.Parse(await _rejected.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _rejected.Dispose();

        [Test]
        public void It_admits_up_to_the_limit() => _statuses.Should().Equal(200, 200);

        [Test]
        public void It_answers_429_past_the_limit() => ((int)_rejected.StatusCode).Should().Be(429);

        [Test]
        public void It_uses_the_ed_fi_contract()
        {
            _rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["detail"]!.GetValue<string>().Should().Be(Detail);
            _body["status"]!.GetValue<int>().Should().Be(429);
        }

        [Test]
        public void It_sends_a_whole_second_retry_after() =>
            _rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);

        [Test]
        public void It_does_not_verify_the_rejected_secret() => ManagerCalls(_tokenManager, 2);
    }

    [TestFixture]
    public class Given_one_client_exhausted_and_other_callers
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1000, clientPermitLimit: 1);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientB"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("CLIENTA"), "198.51.100.1"),
                    () => Post("/connect/token", TokenForm("ClientA"), "198.51.100.2"),
                ]
            );
        }

        [Test]
        public void It_limits_the_client_across_case_variants_and_sources_but_not_other_clients() =>
            _statuses.Should().Equal(200, 200, 429, 429);
    }

    [TestFixture]
    public class Given_a_client_authenticating_with_a_basic_header
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1000, clientPermitLimit: 1);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () =>
                    {
                        HttpRequestMessage request = Post(
                            "/connect/token",
                            new FormUrlEncodedContent([new("grant_type", "client_credentials")])
                        );
                        request.Headers.Authorization = Basic("ClientA");
                        return request;
                    },
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keys_on_the_parsed_basic_client_id() => _statuses.Should().Equal(200, 429);
    }

    /// <summary>
    /// Order of operations: requests the handler rejects before secret verification keep their answer
    /// and spend no client budget.
    /// </summary>
    [TestFixture]
    public class Given_token_requests_that_fail_before_secret_verification
    {
        private readonly ITokenManager _tokenManager = SucceedingTokenManager();
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                tokenManager: _tokenManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA", grantType: "password")),
                    () => Post("/connect/token", TokenForm("ClientA", grantType: "password")),
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keeps_their_answers_and_admits_the_valid_request() => _statuses.Should().Equal(400, 400, 200);

        [Test]
        public void It_verifies_only_the_valid_request() => ManagerCalls(_tokenManager, 1);
    }

    [TestFixture]
    public class Given_a_client_over_its_revocation_limit
    {
        private readonly ITokenRevocationManager _revocationManager = CompletingRevocationManager();
        private List<int> _statuses = null!;
        private HttpResponseMessage _rejected = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 2,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [() => Post("/connect/revoke", RevokeForm("ClientA")), () => Post("/connect/revoke", RevokeForm("ClientA"))]
            );
            _rejected = await client.SendAsync(Post("/connect/revoke", RevokeForm("ClientA")));
            _body = JsonNode.Parse(await _rejected.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown() => _rejected.Dispose();

        [Test]
        public void It_admits_up_to_the_limit() => _statuses.Should().Equal(200, 200);

        [Test]
        public void It_answers_429_in_the_oauth_format()
        {
            ((int)_rejected.StatusCode).Should().Be(429);
            _rejected.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            _body.Count.Should().Be(2);
            _body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
            _body["error_description"]!.GetValue<string>().Should().Be(Detail);
        }

        [Test]
        public void It_does_not_revoke_for_the_rejected_request() => RevocationCalls(_revocationManager, 2);
    }

    [TestFixture]
    public class Given_revocations_that_fail_shape_checks
    {
        private readonly ITokenRevocationManager _revocationManager = CompletingRevocationManager();
        private List<int> _statuses = null!;

        private static FormUrlEncodedContent WithoutToken() =>
            new([new("client_id", "ClientA"), new("client_secret", ClientSecret)]);

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(
                sourcePermitLimit: 1000,
                clientPermitLimit: 1,
                revocationManager: _revocationManager
            );
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/revoke", WithoutToken()),
                    () => Post("/connect/revoke", WithoutToken()),
                    () => Post("/connect/revoke", RevokeForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_keeps_their_answers_and_admits_the_valid_request() => _statuses.Should().Equal(400, 400, 200);

        [Test]
        public void It_revokes_only_for_the_valid_request() => RevocationCalls(_revocationManager, 1);
    }

    [TestFixture]
    public class Given_token_and_revocation_requests_for_one_client
    {
        private List<int> _statuses = null!;

        [SetUp]
        public async Task Setup()
        {
            await using WebApplicationFactory<Program> factory = Create(sourcePermitLimit: 1000, clientPermitLimit: 2);
            using HttpClient client = factory.CreateClient();
            _statuses = await SendAll(
                client,
                [
                    () => Post("/connect/token", TokenForm("ClientA")),
                    () => Post("/connect/revoke", RevokeForm("ClientA")),
                    () => Post("/connect/token", TokenForm("ClientA")),
                ]
            );
        }

        [Test]
        public void It_spends_one_client_budget_across_both_routes() => _statuses.Should().Equal(200, 200, 429);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~IdentityModuleRateLimitTests"`
Expected: FAIL. Each `_past_the_limit` / `It_spends_one_client_budget` / `It_limits_the_client…` / `It_keys_on_the_parsed_basic_client_id` assertion gets 200 where 429 was expected, and the manager-call counts are too high. `Given_token_requests_that_fail_before_secret_verification` and `Given_revocations_that_fail_shape_checks` already pass. They are order-of-operations guards that must keep passing after Step 3.

- [ ] **Step 3: Add the client gate to both handlers**

In `FE/Modules/IdentityModule.cs`, add `using System.Threading.RateLimiting;` to the usings.

Change the `GetClientAccessToken` signature to:

```csharp
    private static async Task<IResult> GetClientAccessToken(
        TokenRequest.Validator validator,
        [FromServices] ITokenManager tokenManager,
        [FromServices] ConnectRateLimiter rateLimiter,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
```

In `GetClientAccessToken`, insert immediately after the grant-type check's closing `}` and before the `// RFC 6749 section 3.2` comment:

```csharp
        // DMS-1482 client gate: after the request is known to be well formed and before the manager
        // verifies the secret. The budget is spent whether or not the client exists, so a 429 says
        // nothing about existence; a malformed request keeps its answer and spends nothing.
        using RateLimitLease clientLease = rateLimiter.AttemptClient(model.client_id);
        if (!clientLease.IsAcquired)
        {
            return ConnectRateLimitRejection.Create(
                httpContext,
                clientLease,
                ConnectRateLimitRejection.ClientGate,
                logger
            );
        }
```

Change the `RevokeToken` signature to:

```csharp
    private static async Task<IResult> RevokeToken(
        [FromServices] ILogger<IdentityModule> logger,
        [FromServices] ITokenRevocationManager revocationManager,
        [FromServices] ConnectRateLimiter rateLimiter,
        HttpContext httpContext
    )
```

In `RevokeToken`, insert immediately before `TokenRevocationRequest request = new(`:

```csharp
        // DMS-1482 client gate: after the D-03 shape checks and credential parsing, before the manager
        // authenticates the caller. Spent whether or not the client exists; answered in the OAuth format
        // because this route carries OAuthErrorContractMetadata.
        using RateLimitLease clientLease = rateLimiter.AttemptClient(clientId);
        if (!clientLease.IsAcquired)
        {
            return ConnectRateLimitRejection.Create(
                httpContext,
                clientLease,
                ConnectRateLimitRejection.ClientGate,
                logger
            );
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~IdentityModuleRateLimitTests|FullyQualifiedName~IdentityModuleTests"`
Expected: PASS. The new fixtures pass, and so does every existing `IdentityModuleTests` fixture: `TokenEndpointBasicAuthTests`, `OAuthEndpointErrorTests`, `RevocationOwnershipTests`, `RevocationRequestContractTests` and the token-limit fixture.

- [ ] **Step 5: Format and commit**

```bash
dotnet csharpier format src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/IdentityModuleRateLimitTests.cs
git add src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/IdentityModuleRateLimitTests.cs
git commit -m "[DMS-1482] Rate limit token and revocation per client before secret verification"
```

---

### Task 6: Compose, CI environment files and documentation

**Files:**
- Modify: `eng/docker-compose/local-config.yml` (after line 79), `eng/docker-compose/published-config.yml` (after line 83)
- Modify: `eng/docker-compose/.env.example` (after line 252)
- Modify: `eng/docker-compose/.env.e2e`, `.env.config.e2e`, `.env.config.mssql.e2e`, `.env.config.mssql.multitenant.e2e`, `.env.routeContext.e2e`, `.env.cursorpartitions.e2e`, `.env.smoke`, `.env.smoke.ds61`
- Modify: `docs/CONFIGURATION.md`, `reference/design/configuration-service/CS-AUTH.md`, `docs/changelog/8.1.0.md`

**Interfaces:**
- Consumes: setting names and defaults from Task 2, response contracts from Tasks 1 and 4.

- [ ] **Step 1: Map the compose variables**

In both `local-config.yml` and `published-config.yml`, insert directly after the `IdentitySettings__BearerTokenPerClientLimit:` line, at the same six-space indentation:

```yaml
      # /connect/* rate limiting (DMS-1482); see docs/CONFIGURATION.md#connectratelimit.
      ConnectRateLimit__Enabled: ${CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_ENABLED:-true}
      ConnectRateLimit__SourcePermitLimit: ${CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_SOURCE_PERMIT_LIMIT:-120}
      ConnectRateLimit__ClientPermitLimit: ${CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_CLIENT_PERMIT_LIMIT:-30}
      ConnectRateLimit__WindowSeconds: ${CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_WINDOW_SECONDS:-60}
```

In `.env.example`, insert directly after `CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=15`:

```
# /connect/* rate limiting: requests per source address (IPv6: per /64) across the four
# /connect/* routes, and token/revocation requests per client_id across all sources, in each
# fixed window. Per Configuration Service process. Traffic proxied by the Ed-Fi API arrives from
# one address and shares one source budget. See docs/CONFIGURATION.md#connectratelimit.
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_ENABLED=true
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_SOURCE_PERMIT_LIMIT=120
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_CLIENT_PERMIT_LIMIT=30
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_WINDOW_SECONDS=60
```

- [ ] **Step 2: Raise the limits in the CI environment files**

In each of `.env.e2e`, `.env.config.e2e`, `.env.config.mssql.e2e`, `.env.config.mssql.multitenant.e2e`, `.env.routeContext.e2e`, `.env.cursorpartitions.e2e`, `.env.smoke` and `.env.smoke.ds61`, insert directly after the `CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=` line:

```
# Test traffic reaches the Configuration Service from one address, so the /connect/* limits are
# raised far above test volume; limiting stays enabled (DMS-1482).
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_ENABLED=true
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_SOURCE_PERMIT_LIMIT=1000000
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_CLIENT_PERMIT_LIMIT=1000000
CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_WINDOW_SECONDS=60
```

Verify: `grep -c "CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_SOURCE_PERMIT_LIMIT" eng/docker-compose/.env.e2e eng/docker-compose/.env.config.e2e eng/docker-compose/.env.config.mssql.e2e eng/docker-compose/.env.config.mssql.multitenant.e2e eng/docker-compose/.env.routeContext.e2e eng/docker-compose/.env.cursorpartitions.e2e eng/docker-compose/.env.smoke eng/docker-compose/.env.smoke.ds61 eng/docker-compose/.env.example`
Expected: `1` for each of the nine files.

- [ ] **Step 3: Run the environment-file Pester tests**

Run: `pwsh -NoProfile -Command "Invoke-Pester eng/docker-compose/tests/DatabaseEngineEnvironmentFile.Tests.ps1, eng/docker-compose/tests/DataStandardEnvironmentFile.Tests.ps1, eng/docker-compose/tests/AzureVmDeployment.Tests.ps1 -Output Detailed"`
Expected: PASS. Fix any parity failure by keeping `.env.smoke` and `.env.smoke.ds61` identical in the new lines.

- [ ] **Step 4: Document `ConnectRateLimit` in `docs/CONFIGURATION.md`**

Insert this section immediately before `## Plugins`:

````markdown
## ConnectRateLimit

The Configuration Service rate limits its four anonymous OAuth endpoints, `POST /connect/token`,
`/connect/revoke`, `/connect/register` and `/connect/introspect` (route-context suffixes included),
to slow client-secret guessing and client enumeration. No other endpoint is limited. Two independent
budgets apply, and a request must fit both:

- **Per source address**, across all four routes. The address is the connection's remote address after
  [forwarded headers](#reverse-proxy-and-forwarded-headers) from trusted proxies are applied. A native
  IPv6 address counts against its /64. Requests whose address is unknown share one budget.
- **Per `client_id`**, across all sources, for `/connect/token` and `/connect/revoke` only, spent just
  before the client secret is verified. A request rejected earlier (malformed form, unsupported grant
  type, missing token) does not spend it. The comparison ignores case. The budget is spent whether or
  not the client exists, so a `429` reveals nothing about which clients exist.

| Parameter | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | `false` turns all `/connect/*` limiting off. |
| `SourcePermitLimit` | `120` | Requests per source address in each window. Must be > 0 when enabled. |
| `ClientPermitLimit` | `30` | Token and revocation requests per `client_id` in each window. Must be > 0 when enabled. |
| `WindowSeconds` | `60` | Fixed-window length in seconds. Must be > 0 when enabled. |

The Docker Compose stacks set these from `CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_ENABLED`,
`CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_SOURCE_PERMIT_LIMIT`,
`CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_CLIENT_PERMIT_LIMIT` and
`CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_WINDOW_SECONDS`. An invalid value is reported at startup and
the service answers every request with `500` until it is corrected.

Sizing notes:

- Limits are enforced **per Configuration Service process**. With several replicas behind a load
  balancer, a caller can reach up to replicas × limit requests per window.
- Callers behind one NAT, and all requests proxied by the Ed-Fi API's `/oauth/token`, share **one
  source budget**. Raise `SourcePermitLimit` if the Ed-Fi API serves many token requests.
- The Ed-Fi API's own Configuration Service client caches its token, so it stays far below the client
  limit. If it is ever refused, that request fails rather than retrying.
- The defaults come from expected traffic shape, not measured load. Real clients are already capped at
  15 active tokens (`IdentitySettings:BearerTokenPerClientLimit`) and should reuse them.

### Rejection response

A refused request receives `429 Too Many Requests` with a `Retry-After` header: the limiter's
recommended delay rounded up to whole seconds, omitted if the limiter supplies none. `/connect/token`,
`/connect/register` and `/connect/introspect` answer with an Ed-Fi problem-details body served as
`application/problem+json`. Its `correlationId` is the request's trace identifier:

```json
{
  "detail": "The number of allowed requests has been exceeded. Retry the request later.",
  "type": "urn:ed-fi:management-api:too-many-requests",
  "title": "Too Many Requests",
  "status": 429,
  "correlationId": "0HNCTN1IRQMDG:00000001",
  "validationErrors": {},
  "errors": []
}
```

`/connect/revoke` keeps its OAuth error format (see
[CS-AUTH](../reference/design/configuration-service/CS-AUTH.md#token-revocation)) and answers
`application/json`:

```json
{
  "error": "temporarily_unavailable",
  "error_description": "The number of allowed requests has been exceeded. Retry the request later."
}
```

This `429` is distinct from the active-token limit's `429`
(`urn:ed-fi:management-api:security:authentication:too-many-tokens`), which is unchanged.
````

- [ ] **Step 5: Add the CS-AUTH note**

In `reference/design/configuration-service/CS-AUTH.md`, insert after the `[^revoke]:` footnote paragraph in `## Token Endpoints` (before the paragraph that begins `` `/connect/token` accepts client credentials``):

```markdown
All four `/connect/*` endpoints are rate limited per source address, and `/connect/token` and
`/connect/revoke` also per `client_id`, immediately before the client secret is verified (DMS-1482).
A refusal is `429` with `Retry-After`: an Ed-Fi problem-details body of type
`urn:ed-fi:management-api:too-many-requests`, except on `/connect/revoke`, which answers in the OAuth
format with `error` `temporarily_unavailable`. Limits, defaults and sizing are in
[CONFIGURATION.md](../../../docs/CONFIGURATION.md#connectratelimit); the design is
[DMS-1482](./DMS-1482-cms-connect-rate-limiting.md).
```

- [ ] **Step 6: Add the changelog entry**

In `docs/changelog/8.1.0.md`, add as the first bullet under `## New features`:

```markdown
- **The Configuration Service rate limits `/connect/*`.** `/connect/token`, `/connect/revoke`,
  `/connect/register` and `/connect/introspect` now answer `429 Too Many Requests` with
  `Retry-After` once a source address exceeds 120 requests, or a `client_id` exceeds 30 token and
  revocation requests, in a 60-second window. The body is the Ed-Fi
  `urn:ed-fi:management-api:too-many-requests` problem details, or the OAuth error
  `temporarily_unavailable` on `/connect/revoke`. Clients that reuse their tokens are unaffected.
  Operators can tune or disable the limits through the new `ConnectRateLimit` section; deployments
  where many callers share one address, including the Ed-Fi API's `/oauth/token` proxy, may need a
  higher source limit. See [ConnectRateLimit](../CONFIGURATION.md#connectratelimit) and
  [DMS-1482](https://edfi.atlassian.net/browse/DMS-1482).
```

- [ ] **Step 7: Check the links and commit**

Run: `grep -n "connectratelimit\|## ConnectRateLimit" docs/CONFIGURATION.md docs/changelog/8.1.0.md reference/design/configuration-service/CS-AUTH.md eng/docker-compose/.env.example eng/docker-compose/local-config.yml eng/docker-compose/published-config.yml`
Expected: the heading appears once in `docs/CONFIGURATION.md`, and every other file's link targets `#connectratelimit`.

```bash
git add eng/docker-compose/local-config.yml eng/docker-compose/published-config.yml eng/docker-compose/.env.example eng/docker-compose/.env.e2e eng/docker-compose/.env.config.e2e eng/docker-compose/.env.config.mssql.e2e eng/docker-compose/.env.config.mssql.multitenant.e2e eng/docker-compose/.env.routeContext.e2e eng/docker-compose/.env.cursorpartitions.e2e eng/docker-compose/.env.smoke eng/docker-compose/.env.smoke.ds61 docs/CONFIGURATION.md reference/design/configuration-service/CS-AUTH.md docs/changelog/8.1.0.md
git commit -m "[DMS-1482] Document and configure Configuration Service connect rate limiting"
```

---

### Task 7: Full verification

**Files:** none modified, unless a step finds a defect. In that case fix it in the task that owns the file, and commit there.

- [ ] **Step 1: Build the Configuration Service solution**

Run: `dotnet build src/config/EdFi.DmsConfigurationService.sln -c Release`
Expected: build succeeded, 0 errors. Don't let this change add new warnings in files it touches.

- [ ] **Step 2: Run the CMS unit suites in full**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj -c Release`
Expected: PASS, 0 failed.

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj -c Release`
Expected: PASS, 0 failed.

- [ ] **Step 3: Confirm the DMS proxy contract is unchanged**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~OAuthManagerTests"`
Expected: PASS. `urn:ed-fi:management-api:too-many-requests` is already a near-miss case there and maps to DMS's generic 429.

- [ ] **Step 4: Check formatting**

Run: `dotnet csharpier check src/config`
Expected: no files need formatting.

- [ ] **Step 5: Run the CMS E2E suite against the Docker stack**

This catches environment-file mistakes that unit tests can't see. From `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E`:

```powershell
pwsh ./teardown-local-cms.ps1
pwsh ./setup-local-cms.ps1
dotnet test ./EdFi.DmsConfigurationService.Tests.E2E.csproj
```

Expected: PASS. In particular the `Connect`, `Token` and `Revocation` features pass and no response is 429. Then confirm the container received the generous values:

`docker inspect $(docker ps --filter "name=config" --format "{{.Names}}" | head -1) --format "{{range .Config.Env}}{{println .}}{{end}}" | grep ConnectRateLimit`

Expected: `ConnectRateLimit__SourcePermitLimit=1000000` and `ConnectRateLimit__ClientPermitLimit=1000000`.

Teardown afterwards: `pwsh ./teardown-local-cms.ps1`.

- [ ] **Step 6: Review the branch diff against the spec**

Run: `git diff 6bb7d328a --stat` and `git log --oneline 6bb7d328a..HEAD`
Expected: only the files listed in this plan's file structure, plus the spec and this plan. There should be no DMS source, backend, persistence or `SchemaHashConstants` changes. Check each spec §6 row against a passing test named in Tasks 1-5.

---

## Spec coverage map

| Spec item | Task |
|---|---|
| REQ-1 source partition, enumeration with a varying `client_id`, Host/Tenant/suffix/case variants, distinct IP, trusted vs untrusted forwarding, IPv6 /64, all four routes, non-connect routes | 3, 4 |
| REQ-1 client partition, case variants, across sources, Basic header parsing, shared across token and revoke | 3, 5 |
| REQ-2 Ed-Fi body on token, register and introspect (source and client); OAuth body on revoke (source and client); `Retry-After`; security headers; factory unit test; too-many-tokens unchanged | 1, 4, 5 |
| REQ-3 client gate before the manager; malformed requests keep their answers and spend nothing; a source-rejected malformed revoke gets 429 while an admitted one still gets 400 | 4, 5 |
| REQ-4 shipped values, model defaults, environment override, in-memory override, invalid values reported, `Enabled=false`, documentation and compose | 2, 4, 6 |
| REQ-5 all of the above, plus limiter disposal with the host | 3, 4 |
| §5 logging (gate and route only) | 4 |
| §4.5 CI env files, `.env.example`, `docs/CONFIGURATION.md`, CS-AUTH, changelog | 6 |
| §5 regression surfaces (`IdentityModuleTests`, `FailureResponseWriterTests`, `OAuthManagerTests`, CMS E2E) | 4, 5, 7 |

## Not in this plan (per spec)

- Backoff (D3).
- DMS-1490 enumeration code and timing fixes.
- DMS-1491 OAuth migration.
- DMS-1479 introspection authentication: whichever of DMS-1479 and DMS-1482 merges second adds the client gate to introspect.
- DMS-1484 headers.
- Distributed enforcement.
- DMS proxy changes.
- OpenAPI response documentation for the new 429.
- `eng/azure-vm/compose` mappings.
- The optional DMS-1491 Jira comment (spec §7), which is a manual step for the ticket owner and not part of execution.
