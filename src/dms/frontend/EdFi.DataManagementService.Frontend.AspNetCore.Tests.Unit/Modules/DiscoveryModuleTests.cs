// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
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
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");

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
        apiDetails?["urls"]?.AsObject().Count.Should().Be(6);
        apiDetails?["urls"]?["tokenInfo"].Should().NotBeNull();
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("/oauth/token_info");
        apiDetails?["applicationName"]?.GetValue<string>().Should().Be("DMS");
        apiDetails?["informationalVersion"]?.GetValue<string>().Should().Be("Release Candidate 1");
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
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");
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
        apiDetails?["urls"]?.AsObject().Count.Should().Be(6);
        var dependenciesUrl = apiDetails?["urls"]?["dependencies"];
        dependenciesUrl.Should().NotBeNull();
        dependenciesUrl?.GetValue<string>().Should().Contain(pathBase);
        var tokenInfoUrl = apiDetails?["urls"]?["tokenInfo"];
        tokenInfoUrl.Should().NotBeNull();
        tokenInfoUrl?.GetValue<string>().Should().Contain(pathBase);
        tokenInfoUrl?.GetValue<string>().Should().Contain("/oauth/token_info");
        apiDetails?["applicationName"]?.GetValue<string>().Should().Be("DMS");
        apiDetails?["informationalVersion"]?.GetValue<string>().Should().Be("Release Candidate 1");
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
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");

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
        A.CallTo(() => tenantValidator.ValidateTenantAsync("valid-tenant")).Returns(true);

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
        apiDetails.Should().NotBeNull();
        apiDetails?["urls"]?.AsObject().Count.Should().Be(6);
        // Verify URLs include tenant
        apiDetails?["urls"]?["dataManagementApi"]?.GetValue<string>().Should().Contain("valid-tenant");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("valid-tenant");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("/oauth/token_info");
    }

    [Test]
    public async Task When_MultiTenancy_Enabled_And_Invalid_Tenant_Provided_Returns_NotFound()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");

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
        A.CallTo(() => tenantValidator.ValidateTenantAsync("invalid-tenant")).Returns(false);

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
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("Release Candidate 1");

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
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("{tenant}");
        apiDetails?["urls"]?["tokenInfo"]?.GetValue<string>().Should().Contain("/oauth/token_info");
    }
}
