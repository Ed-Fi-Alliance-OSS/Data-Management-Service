// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class Given_ManagementEndpointModule
{
    private const string ValidRequiredRole = "dms-management-operator";
    private const string RoleClaimType = "operator_role";

    internal static WebApplicationFactory<Program> CreateFactory(
        IApiService apiService,
        string? requiredRole,
        bool multiTenancy = false,
        bool enableClaimsetReload = true,
        IJwtValidationService? jwtValidationService = null,
        string? roleClaimType = RoleClaimType,
        RecordingLoggerProvider? loggerProvider = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            }

            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    Dictionary<string, string?> settings = new()
                    {
                        ["AppSettings:MultiTenancy"] = multiTenancy ? "true" : "false",
                        ["AppSettings:EnableClaimsetReload"] = enableClaimsetReload ? "true" : "false",
                        ["JwtAuthentication:ClientRole"] = "legacy-service",
                    };

                    if (requiredRole is not null)
                    {
                        settings["AppSettings:ManagementEndpoints:RequiredRole"] = requiredRole;
                    }

                    if (roleClaimType is not null)
                    {
                        settings["JwtAuthentication:RoleClaimType"] = roleClaimType;
                    }

                    configuration.AddInMemoryCollection(settings);
                }
            );

            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.Replace(
                    ServiceDescriptor.Singleton<IJwtValidationService>(
                        jwtValidationService
                            ?? new ScriptedJwtValidationService(new Dictionary<string, ClaimsPrincipal?>())
                    )
                );
                services.Replace(ServiceDescriptor.Singleton(apiService));
            });
        });
    }

    internal static IApiService FakeApiService()
    {
        IApiService apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.ReloadClaimsetsAsync(A<string?>._))
            .Returns(Task.FromResult<IFrontendResponse>(OkResponse("reloaded")));
        A.CallTo(() => apiService.ViewClaimsetsAsync(A<string?>._))
            .Returns(Task.FromResult<IFrontendResponse>(OkResponse("viewed")));
        return apiService;
    }

    private static IFrontendResponse OkResponse(string marker) =>
        new StubFrontendResponse(200, JsonNode.Parse($"{{\"message\":\"{marker}\"}}"));

    internal static IEnumerable<string> MappedRoutePatterns(WebApplicationFactory<Program> factory) =>
        factory
            .Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToList();

    [Test]
    public void It_maps_the_single_tenant_claimset_routes_when_the_role_is_usable()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(FakeApiService(), ValidRequiredRole);

        IEnumerable<string> patterns = MappedRoutePatterns(factory);

        patterns.Should().Contain("/management/reload-claimsets");
        patterns.Should().Contain("/management/view-claimsets");
    }

    [Test]
    public void It_maps_the_tenant_scoped_claimset_routes_when_the_role_is_usable()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            ValidRequiredRole,
            multiTenancy: true
        );

        IEnumerable<string> patterns = MappedRoutePatterns(factory);

        patterns.Should().Contain("/management/{tenant}/reload-claimsets");
        patterns.Should().Contain("/management/{tenant}/view-claimsets");
    }

    [Test]
    public void It_maps_the_claimset_routes_even_when_claimset_reload_is_disabled()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            ValidRequiredRole,
            enableClaimsetReload: false
        );

        IEnumerable<string> patterns = MappedRoutePatterns(factory);

        patterns.Should().Contain("/management/reload-claimsets");
        patterns.Should().Contain("/management/view-claimsets");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("dms management operator")]
    public void It_does_not_map_the_single_tenant_claimset_routes_when_the_role_is_unusable(
        string? requiredRole
    )
    {
        using WebApplicationFactory<Program> factory = CreateFactory(FakeApiService(), requiredRole);

        IEnumerable<string> patterns = MappedRoutePatterns(factory);

        patterns.Should().NotContain("/management/reload-claimsets");
        patterns.Should().NotContain("/management/view-claimsets");
    }

    [Test]
    public void It_does_not_map_the_single_tenant_claimset_routes_when_the_role_is_over_length()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(FakeApiService(), new string('a', 257));

        MappedRoutePatterns(factory).Should().NotContain("/management/reload-claimsets");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("dms management operator")]
    public void It_does_not_map_the_tenant_scoped_claimset_routes_when_the_role_is_unusable(
        string? requiredRole
    )
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            requiredRole,
            multiTenancy: true
        );

        IEnumerable<string> patterns = MappedRoutePatterns(factory);

        patterns.Should().NotContain("/management/{tenant}/reload-claimsets");
        patterns.Should().NotContain("/management/{tenant}/view-claimsets");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void It_does_not_map_the_claimset_routes_when_the_role_claim_type_is_blank(string roleClaimType)
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            ValidRequiredRole,
            roleClaimType: roleClaimType
        );

        MappedRoutePatterns(factory).Should().NotContain("/management/reload-claimsets");
    }

    [Test]
    public void It_always_maps_the_unscoped_stubs_in_multi_tenant_mode()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            requiredRole: null,
            multiTenancy: true
        );

        IEnumerable<string> patterns = MappedRoutePatterns(factory);

        patterns.Should().Contain("/management/reload-claimsets");
        patterns.Should().Contain("/management/view-claimsets");
    }

    [Test]
    public async Task It_answers_404_on_the_unscoped_stubs_in_multi_tenant_mode()
    {
        IApiService apiService = FakeApiService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            apiService,
            requiredRole: null,
            multiTenancy: true
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/management/view-claimsets");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        A.CallTo(() => apiService.ViewClaimsetsAsync(A<string?>._)).MustNotHaveHappened();
    }

    [Test]
    public void It_warns_when_the_role_is_unusable_and_claimset_reload_is_enabled()
    {
        var loggerProvider = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            requiredRole: "dms management operator",
            enableClaimsetReload: true,
            loggerProvider: loggerProvider
        );
        _ = MappedRoutePatterns(factory);

        RecordingLogEntry warning = loggerProvider
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Category == typeof(ManagementEndpointModule).FullName && entry.Level == LogLevel.Warning
            )
            .Subject;
        warning.Message.Should().Contain("AppSettings:ManagementEndpoints:RequiredRole");
        warning.Message.Should().NotContain("dms management operator");
    }

    [Test]
    public void It_stays_silent_when_the_role_is_unusable_and_claimset_reload_is_disabled()
    {
        var loggerProvider = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            requiredRole: null,
            enableClaimsetReload: false,
            loggerProvider: loggerProvider
        );
        _ = MappedRoutePatterns(factory);

        loggerProvider
            .Entries.Should()
            .NotContain(entry =>
                entry.Category == typeof(ManagementEndpointModule).FullName && entry.Level == LogLevel.Warning
            );
    }

    [Test]
    public void It_stays_silent_when_the_role_is_usable()
    {
        var loggerProvider = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> factory = CreateFactory(
            FakeApiService(),
            ValidRequiredRole,
            loggerProvider: loggerProvider
        );
        _ = MappedRoutePatterns(factory);

        loggerProvider
            .Entries.Should()
            .NotContain(entry =>
                entry.Category == typeof(ManagementEndpointModule).FullName && entry.Level == LogLevel.Warning
            );
    }

    internal sealed class StubFrontendResponse(int statusCode, JsonNode? body) : IFrontendResponse
    {
        public int StatusCode { get; } = statusCode;
        public JsonNode? Body { get; } = body;
        public Dictionary<string, string> Headers { get; } = [];
        public string? LocationHeaderPath => null;
        public string? ContentType => null;
    }

    internal sealed class ScriptedJwtValidationService(
        IReadOnlyDictionary<string, ClaimsPrincipal?> principalsByToken
    ) : IJwtValidationService
    {
        public Task<(
            ClaimsPrincipal? Principal,
            ClientAuthorizations? ClientAuthorizations
        )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
        {
            principalsByToken.TryGetValue(token, out ClaimsPrincipal? principal);
            return Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>((principal, null));
        }
    }

    internal sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _sync = new();
        private readonly List<RecordingLogEntry> _entries = [];

        public IReadOnlyList<RecordingLogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, _sync, _entries);

        public void Dispose() { }

        private sealed class RecordingLogger(string categoryName, Lock sync, List<RecordingLogEntry> entries)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (sync)
                {
                    entries.Add(new RecordingLogEntry(categoryName, logLevel, formatter(state, exception)));
                }
            }
        }
    }

    internal sealed record RecordingLogEntry(string Category, LogLevel Level, string Message);
}
