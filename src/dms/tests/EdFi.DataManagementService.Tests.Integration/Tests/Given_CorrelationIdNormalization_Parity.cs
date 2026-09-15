// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Tests.Integration.Tests;

/// <summary>
/// Parity test covering three distinct in-process failure paths without leasing a real database.
/// </summary>
/// <remarks>
/// Carries the API CI-selection categories so both API integration lanes select it: each lane runs an
/// AND filter (<c>Category=ApiIntegration&amp;Category=PostgresqlIntegration</c> or
/// <c>Category=ApiIntegration&amp;Category=MssqlIntegration</c>). This fixture uses only a plain
/// <see cref="WebApplicationFactory{TEntryPoint}"/> plus faked providers, so it leases no database despite
/// carrying the engine categories. Dropping any one of the three categories silently removes this fixture
/// from a lane.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_CorrelationIdNormalization_Parity
{
    /// <summary>
    /// Over-length (80 characters against the configured cap of 64) and carrying control
    /// characters, so it exercises the allowlist and the length cap together. It has to stay
    /// longer than the cap, which <c>AppSettingsValidator</c> floors at 64: a shorter value
    /// would not be truncated at all and this fixture would stop covering truncation.
    /// </summary>
    private const string HostileCorrelationId =
        "12\r{34}\t567890567890567890567890567890567890567890567890567890567890567890567890";

    /// <summary>
    /// The lowest cap <c>AppSettingsValidator</c> accepts. Anything lower would fail validation
    /// and every request below would be answered with the generic short-circuit 500 instead of the response
    /// under test.
    /// </summary>
    private const int CorrelationIdMaxLength = 64;
    private const string CorrelationIdHeader = "correlationid";

    private WebApplicationFactory<Program> _generalFactory = null!;
    private WebApplicationFactory<Program> _rateLimitFactory = null!;
    private HttpClient _generalClient = null!;
    private HttpClient _rateLimitClient = null!;
    private HttpResponseMessage _unauthorizedResponse = null!;
    private HttpResponseMessage _notFoundResponse = null!;
    private HttpResponseMessage _rateLimitedResponse = null!;
    private JsonNode _unauthorizedBody = null!;
    private JsonNode _notFoundBody = null!;
    private JsonNode _rateLimitedBody = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _generalFactory = CreateFactory("Test", includeDocumentCacheStatusRole: true);
        _generalClient = _generalFactory.CreateClient();

        _unauthorizedResponse = await _generalClient.GetAsync("/health/document-cache");
        _notFoundResponse = await _generalClient.GetAsync("/missing-route");

        _unauthorizedBody = JsonNode.Parse(await _unauthorizedResponse.Content.ReadAsStringAsync())!;
        _notFoundBody = JsonNode.Parse(await _notFoundResponse.Content.ReadAsStringAsync())!;

        _rateLimitFactory = CreateFactory("TestRateLimit", includeDocumentCacheStatusRole: false);
        _rateLimitClient = _rateLimitFactory.CreateClient();

        using HttpResponseMessage _ = await _rateLimitClient.GetAsync("/health");
        _rateLimitedResponse = await _rateLimitClient.GetAsync("/health");

        _rateLimitedBody = JsonNode.Parse(await _rateLimitedResponse.Content.ReadAsStringAsync())!;
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _unauthorizedResponse.Dispose();
        _notFoundResponse.Dispose();
        _rateLimitedResponse.Dispose();
        _generalClient.Dispose();
        _rateLimitClient.Dispose();
        await _generalFactory.DisposeAsync();
        await _rateLimitFactory.DisposeAsync();
    }

    [Test]
    public void It_applies_the_same_normalized_correlation_id_to_401_404_and_429_responses()
    {
        // Truncate-to-64-then-filter: the first 64 characters of the hostile value are "12", CR,
        // "{34}", TAB and 56 digits; dropping the two control characters leaves 62.
        const string expected = "12{34}56789056789056789056789056789056789056789056789056789056";

        _unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _notFoundResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _rateLimitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _unauthorizedBody["correlationId"]!.GetValue<string>().Should().Be(expected);
        _notFoundBody["correlationId"]!.GetValue<string>().Should().Be(expected);
        _rateLimitedBody["correlationId"]!.GetValue<string>().Should().Be(expected);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        bool includeDocumentCacheStatusRole
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    Dictionary<string, string?> overrides = new()
                    {
                        ["AppSettings:CorrelationIdHeader"] = CorrelationIdHeader,
                        ["AppSettings:CorrelationIdMaxLength"] = CorrelationIdMaxLength.ToString(),
                    };

                    if (includeDocumentCacheStatusRole)
                    {
                        overrides["DataManagement:DocumentCache:Status:RequiredRole"] =
                            "dms-document-cache-operator";
                        overrides["JwtAuthentication:RoleClaimType"] = "operator_role";
                    }

                    configuration.AddInMemoryCollection(overrides);
                }
            );
            builder.ConfigureServices(services =>
            {
                AddEssentialMocks(services);
                services.AddSingleton<IStartupFilter>(
                    new CorrelationIdHeaderInjectionStartupFilter(CorrelationIdHeader, HostileCorrelationId)
                );
                services.Replace(ServiceDescriptor.Singleton(A.Fake<IDocumentCacheStatusService>()));
            });
        });
    }

    private sealed class CorrelationIdHeaderInjectionStartupFilter(string headerName, string headerValue)
        : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        context.Request.Headers[headerName] = headerValue;
                        await nextMiddleware();
                    }
                );
                next(app);
            };
        }
    }

    private static void AddEssentialMocks(IServiceCollection services)
    {
        IClaimSetProvider claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._)).Returns([]);
        services.AddTransient(_ => claimSetProvider);

        IDataStoreProvider dataStoreProvider = A.Fake<IDataStoreProvider>();
        DataStore dataStore = new(1, "Test", "TestInstance", "test-connection-string", []);
        A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .Returns([dataStore]);
        A.CallTo(() => dataStoreProvider.LoadTenants()).Returns(["TestTenant"]);
        A.CallTo(() => dataStoreProvider.GetAll(A<string?>._)).Returns([dataStore]);
        A.CallTo(() => dataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(dataStore);
        A.CallTo(() => dataStoreProvider.IsLoaded(A<string?>._)).Returns(true);
        A.CallTo(() => dataStoreProvider.TenantExists(A<string>.That.IsNotNull())).Returns(true);
        A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys()).Returns(new List<string> { "" }.AsReadOnly());
        services.AddTransient(_ => dataStoreProvider);

        ITenantValidator tenantValidator = A.Fake<ITenantValidator>();
        A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>.That.IsNotNull())).Returns(true);
        services.AddTransient(_ => tenantValidator);

        IConnectionStringProvider connectionStringProvider = A.Fake<IConnectionStringProvider>();
        A.CallTo(() => connectionStringProvider.GetConnectionString(A<long>._, A<string?>._))
            .Returns("test-connection-string");
        A.CallTo(() => connectionStringProvider.GetHealthCheckConnectionString())
            .Returns("test-connection-string");
        services.AddTransient(_ => connectionStringProvider);

        IBackendMappingInitializer backendMappingInitializer = A.Fake<IBackendMappingInitializer>();
        A.CallTo(() => backendMappingInitializer.InitializeAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        services.Replace(ServiceDescriptor.Singleton(backendMappingInitializer));
    }
}
