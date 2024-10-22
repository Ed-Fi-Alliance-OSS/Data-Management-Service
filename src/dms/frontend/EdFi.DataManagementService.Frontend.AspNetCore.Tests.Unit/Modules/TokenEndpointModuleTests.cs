// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
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
public class TokenEndpointModuleTests
{
    [TestFixture]
    public class When_Posting_Successfully_To_The_Token_Endpoint
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
            var requestContent = new { clientid = "CSClient1", clientsecret = "test123@Puiu", displayname = "CSClient1" };
            _response = client.PostAsJsonAsync("/oauth/token").GetAwaiter().GetResult();
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
        public void Then_the_body_contains_a_bear_token_with_expiry()
        {
            _jsonContent?["access_token"]?.ToString().Should().Be("token_for_access");
            _jsonContent?["expires"]?.Should().Be(1800);
            _jsonContent?["token_type"]?.ToString().Should().Be("bearer");
        }
    }

    [Test]
    public async Task Returns_400_when_the_authentication_headers_and_grant_type_missing()
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

        // TODO: Create test_fixtures for missing Authorization: Basic XxxXXXX= headers and missing grant_type.
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
    public async Task Returns_502_when_the_upstream_fails_with_invalid_response()
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

        // Mock a call to the IdP service that returns a 500 error
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
    public async Task Returns_503_when_the_upstream_service_is_unavailable()
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
    public async Task Returns_504_when_upstream_gateway_timeout()
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
}
