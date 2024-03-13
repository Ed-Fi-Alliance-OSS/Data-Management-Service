// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Modules;

[TestFixture]
public class MetaDataModuleTests
{
    [Test]
    public async Task MetaData_Endpoint_Returns_Section_List()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var section1 = jsonContent?[0]?["name"]?.GetValue<string>();
        var section2 = jsonContent?[1]?["name"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        section1.Should().Contain("Resources");
        section2.Should().Contain("Descriptors");
    }

    [Test]
    public async Task MetaData_Returns_Resources_Content()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();
        Lazy<JsonNode> _resourcesJson =
            new(() =>
            {
                var json =
                    """{"openapi":"3.0.1", "info":"resources","servers":[{"url":"http://localhost:5000/data/v3"}]}""";
                return JsonNode.Parse(json)!;
            });

        A.CallTo(() => contentProvider.LoadJsonContent(A<string>.Ignored, A<string>.Ignored))
            .Returns(_resourcesJson);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => contentProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/resources/swagger.json");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var sectionInfo = jsonContent?["Value"]?["info"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        sectionInfo.Should().Contain("resources");
    }

    [Test]
    public async Task MetaData_Returns_Descriptors_Content()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();
        Lazy<JsonNode> _descriptorsJson =
            new(() =>
            {
                var json =
                    """{"openapi":"3.0.1", "info":"descriptors","servers":[{"url":"http://localhost:5000/data/v3"}]}""";
                return JsonNode.Parse(json)!;
            });

        A.CallTo(() => contentProvider.LoadJsonContent(A<string>.Ignored, A<string>.Ignored))
            .Returns(_descriptorsJson);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => contentProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/descriptors/swagger.json");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var sectionInfo = jsonContent?["Value"]?["info"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        sectionInfo.Should().Contain("descriptors");
    }

    [Test]
    public async Task MetaData_Returns_Invalid_Resource_Error()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/swagger.json");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        content.Should().Contain("Invalid resource");
    }
}
