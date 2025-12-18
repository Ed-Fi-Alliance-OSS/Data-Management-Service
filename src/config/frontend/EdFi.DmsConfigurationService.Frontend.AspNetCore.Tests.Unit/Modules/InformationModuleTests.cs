// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class InformationModuleTests
{
    [Test]
    public async Task Information_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Information_Endpoint_Returns_Expected_Structure()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonDoc.RootElement.TryGetProperty("version", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("applicationName", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("informationalVersion", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("urls", out var urls).Should().BeTrue();
        urls.TryGetProperty("openApiMetadata", out _).Should().BeTrue();
    }

    [Test]
    public async Task When_PathBase_Provided_Information_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var pathBase = "dms-config";
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
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/{pathBase}");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().NotBeNullOrEmpty();
    }
}
