// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Integration;

[TestFixture]
public class APISchemaFileTests
{
    private JsonNode? _schemaContent;
    private IApiSchemaProvider _apiSchemaProvider;
    private StringContent _jsonContent;
    private Action<IWebHostBuilder> _webHostBuilder;

    [SetUp]
    public void Setup()
    {
        _schemaContent = JsonContentProvider.ReadContent("FakeSchemaContent.json");
        _apiSchemaProvider = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => _apiSchemaProvider.ApiSchemaRootNode).Returns(_schemaContent!);

        _jsonContent = new(
            JsonSerializer.Serialize(new { property1 = "property1", property2 = "property2", }),
            Encoding.UTF8,
            "application/json"
        );
        _webHostBuilder = (builder) =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiSchemaProvider);
                }
            );
        };
    }

    [Test]
    public async Task Should_throw_error_when_no_resourcename_element()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiSchemaProvider);
                }
            );
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/data/ed-fi/noresourcenames");

        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Should_throw_error_when_no_isshoolyearenumeration_element()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(_webHostBuilder);

        using StringContent jsonContent = _jsonContent;

        using var client = factory.CreateClient();
        var response = await client.PostAsync("/data/ed-fi/noIsSchoolYearEnumerations", jsonContent);

        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Should_throw_error_when_no_isdescriptor_element()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(_webHostBuilder);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/data/ed-fi/noIsDescriptors");

        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Should_throw_error_when_no_allowidentityupdates_element()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(_webHostBuilder);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/data/ed-fi/noallowidentityupdates");

        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
