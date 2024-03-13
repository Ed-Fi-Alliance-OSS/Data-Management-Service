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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Modules;

[TestFixture]
public class DependenciesModuleTests
{
    [Test]
    public async Task Dependencies_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();
        Lazy<JsonNode> _dependencyJson =
            new(() =>
            {
                var json = """[{"name": "dependency1"},{"name": "dependency2"}]""";
                return JsonNode.Parse(json)!;
            });

        A.CallTo(() => contentProvider.LoadJsonContent(A<string>.Ignored, null)).Returns(_dependencyJson);

        var httpContext = A.Fake<HttpContext>();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => contentProvider);
                    collection.AddTransient(x => httpContext);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/data/dependencies");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var name = jsonContent?["Value"]?[0]?["name"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        name.Should().Be("dependency1");
    }
}
