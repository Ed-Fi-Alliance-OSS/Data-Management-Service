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
        apiDetails?["urls"]?.AsObject().Count.Should().Be(5);
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
        apiDetails?["urls"]?.AsObject().Count.Should().Be(5);
        var dependenciesUrl = apiDetails?["urls"]?["dependencies"];
        dependenciesUrl.Should().NotBeNull();
        dependenciesUrl?.GetValue<string>().Should().Contain(pathBase);
        apiDetails?["applicationName"]?.GetValue<string>().Should().Be("DMS");
        apiDetails?["informationalVersion"]?.GetValue<string>().Should().Be("Release Candidate 1");
        apiDetails?["dataModels"].Should().NotBeNull();
        apiDetails?["dataModels"]?.AsArray().Count.Should().Be(1);
        apiDetails?["dataModels"]?[0]?["name"]?.GetValue<string>().Should().Be("Ed-Fi");
    }
}
