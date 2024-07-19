// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class MetadataModuleTests
{
    [TestFixture]
    public class When_Getting_The_Base_Metadata_Endpoint
    {
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        [SetUp]
        public void SetUp()
        {
            // Arrange
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
            });
            using var client = factory.CreateClient();

            // Act
            _response = client.GetAsync("/metadata").GetAwaiter().GetResult();
            var content = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _jsonContent = JsonNode.Parse(content) ?? throw new Exception("JSON parsing failed");
        }

        [TearDownAttribute]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_OK()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_the_body_contains_the_dependencies_url()
        {
            _jsonContent?["dependencies"]?.ToString().Should().Be("http://localhost/metadata/dependencies");
        }

        [Test]
        public void Then_the_body_contains_the_specifications_url()
        {
            _jsonContent
                ?["specifications"]?.ToString()
                .Should()
                .Be("http://localhost/metadata/specifications");
        }

        [Test]
        public void Then_the_body_contains_the_xsdFiles_url()
        {
            _jsonContent?["discovery"]?.ToString().Should().Be("http://localhost/metadata/xsdFiles");
        }
    }

    [Test]
    public async Task Metadata_Endpoint_Returns_Specifications_List()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/specifications");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var section1 = jsonContent?[0]?["name"]?.GetValue<string>();
        var section2 = jsonContent?[1]?["name"]?.GetValue<string>();
        var section3 = jsonContent?[2]?["name"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        section1.Should().Contain("Resources");
        section2.Should().Contain("Descriptors");
        section3.Should().Contain("Discovery");
    }

    [Test]
    public async Task Metadata_Returns_Resources_Content()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();

        var json =
            """{"openapi":"3.0.1", "info":"resources","servers":[{"url":"http://localhost:5000/data/v3"}]}""";
        JsonNode _resourcesJson = JsonNode.Parse(json)!;

        A.CallTo(
                () => contentProvider.LoadJsonContent(A<string>.Ignored, A<string>.Ignored, A<string>.Ignored)
            )
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
        var response = await client.GetAsync("/metadata/specifications/resources-spec.json");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var sectionInfo = jsonContent?["info"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        sectionInfo.Should().Contain("resources");
    }

    [Test]
    public async Task Metadata_Returns_Descriptors_Content()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();
        var json =
            """{"openapi":"3.0.1", "info":"descriptors","servers":[{"url":"http://localhost:5000/data/v3"}]}""";
        JsonNode _descriptorsJson = JsonNode.Parse(json)!;

        A.CallTo(
                () => contentProvider.LoadJsonContent(A<string>.Ignored, A<string>.Ignored, A<string>.Ignored)
            )
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
        var response = await client.GetAsync("/metadata/specifications/descriptors-spec.json");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var sectionInfo = jsonContent?["info"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        sectionInfo.Should().Contain("descriptors");
    }

    [Test]
    public async Task Metadata_Returns_Invalid_Resource_Error()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/swagger.json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Metadata_Returns_Dependencies()
    {
        // Arrange
        var httpContext = A.Fake<HttpContext>();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient(x => httpContext);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/dependencies");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        jsonContent?[0]!["resource"]?.GetValue<string>().Should().Be("/ed-fi/absenceEventCategoryDescriptors");
        jsonContent?[0]!["order"]?.GetValue<int>().Should().Be(1);
    }
}
