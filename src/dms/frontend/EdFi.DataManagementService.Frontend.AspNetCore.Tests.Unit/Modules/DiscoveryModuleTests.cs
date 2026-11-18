// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using ImpromptuInterface;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class DiscoveryModuleTests
{
    [Test]
    public async Task Discovery_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");

        IDataModelInfo expectedDataModelInfo = (
            new
            {
                ProjectName = "Ed-Fi",
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard 5.0.0",
            }
        ).ActLike<IDataModelInfo>();
        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([expectedDataModelInfo]);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDetails.Should().NotBeNull();
        apiDetails?["urls"]?.AsObject().Count.Should().Be(8);
        apiDetails?["urls"]?["tokenInfo"].Should().NotBeNull();
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("/oauth/token_info");
        GetUrl(apiDetails, "dataManagementApi").Should().Be("http://localhost/data");
        GetUrl(apiDetails, "changeQueries").Should().Be("http://localhost/changeQueries/v1/");
        apiDetails?["applicationName"]?.GetValue<string>().Should().Be("Ed-Fi API");
        apiDetails?["informationalVersion"]?.GetValue<string>().Should().Be("8.0.0");
        apiDetails?["dataModels"].Should().NotBeNull();
        apiDetails?["dataModels"]?.AsArray().Count.Should().Be(1);
        apiDetails?["dataModels"]?[0]?["name"]?.GetValue<string>().Should().Be("Ed-Fi");
    }

    [Test]
    public async Task When_PathBase_Provided_Discovery_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");
        IDataModelInfo expectedDataModelInfo = (
            new
            {
                ProjectName = "Ed-Fi",
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard 5.0.0",
            }
        ).ActLike<IDataModelInfo>();
        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([expectedDataModelInfo]);

        var pathBase = "dms-api";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:PathBase"] = pathBase }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/{pathBase}");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDetails.Should().NotBeNull();
        apiDetails?["urls"]?.AsObject().Count.Should().Be(8);
        var dependenciesUrl = apiDetails?["urls"]?["dependencies"];
        dependenciesUrl.Should().NotBeNull();
        dependenciesUrl?.GetValue<string>().Should().Contain(pathBase);
        var tokenInfoUrl = apiDetails?["urls"]?["tokenInfo"];
        tokenInfoUrl.Should().NotBeNull();
        tokenInfoUrl?.GetValue<string>().Should().Contain(pathBase);
        tokenInfoUrl?.GetValue<string>().Should().Contain("/oauth/token_info");
        GetUrl(apiDetails, "changeQueries").Should().Be($"http://localhost/{pathBase}/changeQueries/v1/");
        apiDetails?["applicationName"]?.GetValue<string>().Should().Be("Ed-Fi API");
        apiDetails?["informationalVersion"]?.GetValue<string>().Should().Be("8.0.0");
        apiDetails?["dataModels"].Should().NotBeNull();
        apiDetails?["dataModels"]?.AsArray().Count.Should().Be(1);
        apiDetails?["dataModels"]?[0]?["name"]?.GetValue<string>().Should().Be("Ed-Fi");
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Valid_Tenant_Provided_Returns_Ok_Response()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");

        IDataModelInfo expectedDataModelInfo = (
            new
            {
                ProjectName = "Ed-Fi",
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard 5.0.0",
            }
        ).ActLike<IDataModelInfo>();
        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([expectedDataModelInfo]);

        var tenantValidator = A.Fake<ITenantValidator>();
        A.CallTo(() => tenantValidator.DetermineTenantExistenceAsync("valid-tenant", A<CancellationToken>._))
            .Returns(TenantExistence.Exists);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                    collection.AddTransient((x) => tenantValidator);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/valid-tenant");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() =>
                tenantValidator.DetermineTenantExistenceAsync(
                    "valid-tenant",
                    A<CancellationToken>.That.Matches(token => token.CanBeCanceled)
                )
            )
            .MustHaveHappenedOnceExactly();
        apiDetails.Should().NotBeNull();
        apiDetails?["urls"]?.AsObject().Count.Should().Be(8);
        // Verify URLs include tenant
        apiDetails?["urls"]?["dataManagementApi"]?.GetValue<string>().Should().Contain("valid-tenant");
        GetUrl(apiDetails, "changeQueries").Should().Be("http://localhost/valid-tenant/changeQueries/v1/");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("valid-tenant");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("/oauth/token_info");
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Invalid_Tenant_Provided_Returns_NotFound()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");

        IDataModelInfo expectedDataModelInfo = (
            new
            {
                ProjectName = "Ed-Fi",
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard 5.0.0",
            }
        ).ActLike<IDataModelInfo>();
        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([expectedDataModelInfo]);

        var tenantValidator = A.Fake<ITenantValidator>();
        A.CallTo(() =>
                tenantValidator.DetermineTenantExistenceAsync("invalid-tenant", A<CancellationToken>._)
            )
            .Returns(TenantExistence.Absent);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                    collection.AddTransient((x) => tenantValidator);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/invalid-tenant");
        var content = await response.Content.ReadAsStringAsync();
        var errorDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        errorDetails?["title"]?.GetValue<string>().Should().Be("Not Found");
        errorDetails?["status"]?.GetValue<int>().Should().Be(404);
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Root_Url_Returns_Placeholders()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");

        IDataModelInfo expectedDataModelInfo = (
            new
            {
                ProjectName = "Ed-Fi",
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard 5.0.0",
            }
        ).ActLike<IDataModelInfo>();
        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([expectedDataModelInfo]);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDetails.Should().NotBeNull();
        // Verify URLs include tenant placeholder
        apiDetails?["urls"]?["dataManagementApi"]?.GetValue<string>().Should().Contain("{tenant}");
        GetUrl(apiDetails, "changeQueries").Should().Be("http://localhost/{tenant}/changeQueries/v1/");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("{tenant}");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("/oauth/token_info");
    }

    [Test]
    public async Task When_Route_Qualifier_Partially_Provided_Discovery_Endpoint_Returns_ChangeQueries_Url_With_Placeholder()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");

        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([]);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
                        }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/255901");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetUrl(apiDetails, "dataManagementApi").Should().Be("http://localhost/255901/{schoolYear}/data");
        GetUrl(apiDetails, "changeQueries")
            .Should()
            .Be("http://localhost/255901/{schoolYear}/changeQueries/v1/");
    }

    [Test]
    public async Task When_MultiTenancy_And_Route_Qualifiers_Provided_Discovery_Endpoint_Returns_ChangeQueries_Url_With_Route_Context()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");

        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([]);

        var tenantValidator = A.Fake<ITenantValidator>();
        A.CallTo(() => tenantValidator.DetermineTenantExistenceAsync("valid-tenant", A<CancellationToken>._))
            .Returns(TenantExistence.Exists);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:MultiTenancy"] = "true",
                            ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
                        }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelInfoProvider);
                    collection.AddTransient((x) => tenantValidator);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/valid-tenant/255901/2026");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetUrl(apiDetails, "dataManagementApi").Should().Be("http://localhost/valid-tenant/255901/2026/data");
        GetUrl(apiDetails, "changeQueries")
            .Should()
            .Be("http://localhost/valid-tenant/255901/2026/changeQueries/v1/");
    }

    private static string GetUrl(JsonNode? apiDetails, string name) =>
        apiDetails?["urls"]?[name]?.GetValue<string>() ?? string.Empty;

    /// <summary>
    /// Discovery for a tenant, through the real <see cref="TenantValidator" />, over a data-store provider
    /// whose loads fail after startup the way <see cref="ConfigurationServiceDataStoreProvider" />'s do: an
    /// <see cref="InvalidOperationException" /> wrapping the <see cref="HttpRequestException" />, here the 400
    /// the Configuration Service answers for an unknown tenant.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, string Content)> GetDiscoveryForTenant(
        bool cached,
        Func<IList<string>> tenantListAfterStartup
    )
    {
        bool started = false;
        DataStore dataStore = new(1, "Test", "TestInstance", "test-connection-string", []);
        IDataStoreProvider dataStoreProvider = A.Fake<IDataStoreProvider>();
        A.CallTo(() => dataStoreProvider.TenantExists(A<string>._)).ReturnsLazily(() => !started || cached);
        A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                started
                    ? throw new InvalidOperationException(
                        "Unable to connect to Configuration Service at https://cms.example.org/.",
                        new HttpRequestException(
                            HttpRequestError.Unknown,
                            "Configuration Service request failed with status code 400.",
                            inner: null,
                            HttpStatusCode.BadRequest
                        )
                    )
                    : (IList<DataStore>)[dataStore]
            );
        A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
            .ReturnsLazily(() => started ? tenantListAfterStartup() : new List<string> { "TestTenant" });
        A.CallTo(() => dataStoreProvider.GetAll(A<string?>._)).Returns([dataStore]);
        A.CallTo(() => dataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(dataStore);
        A.CallTo(() => dataStoreProvider.IsLoaded(A<string?>._)).Returns(true);
        A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys()).Returns(new List<string> { "" }.AsReadOnly());

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" }
                    );
                }
            );
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(_ => dataStoreProvider);
                    collection.AddTransient<ITenantValidator, TenantValidator>();
                }
            );
        });
        using var client = factory.CreateClient();
        started = true;

        HttpResponseMessage response = await client.GetAsync("/Tenant_255901");
        return (response, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Tenant_Is_Cached_Returns_Ok_Without_Asking_The_Configuration_Service()
    {
        (HttpResponseMessage response, _) = await GetDiscoveryForTenant(
            cached: true,
            () => throw new InvalidOperationException("The tenant list must not be requested.")
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Tenant_List_Lacks_The_Tenant_Returns_NotFound()
    {
        (HttpResponseMessage response, string content) = await GetDiscoveryForTenant(
            cached: false,
            () => ["Tenant_255902"]
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        JsonNode.Parse(content)!["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Tenant_List_Has_The_Tenant_Returns_Ok_Although_Its_Data_Stores_Failed()
    {
        (HttpResponseMessage response, _) = await GetDiscoveryForTenant(
            cached: false,
            () => ["tenant_255901"]
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A failed tenant lookup is not a missing tenant: Discovery answers 503, which the Configuration
    /// Service classifies as transient, rather than the permanent 404, and the body names nothing the
    /// failure carried.
    /// </summary>
    [Test]
    public async Task When_MultiTenancy_Enabled_And_Tenant_Existence_Cannot_Be_Determined_Returns_ServiceUnavailable()
    {
        (HttpResponseMessage response, string content) = await GetDiscoveryForTenant(
            cached: false,
            () =>
                throw new InvalidOperationException(
                    "Unable to connect to Configuration Service at https://cms.example.org/.",
                    new HttpRequestException("connection refused")
                )
        );

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        JsonNode body = JsonNode.Parse(content)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:service-unavailable");
        body["status"]!.GetValue<int>().Should().Be(503);
        content.Should().NotContain("cms.example.org").And.NotContain("Configuration Service");
        content.Should().NotContain("connection refused").And.NotContain("Exception");
    }
}
