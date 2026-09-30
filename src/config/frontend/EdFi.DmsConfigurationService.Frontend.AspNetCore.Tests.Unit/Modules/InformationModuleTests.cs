// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        jsonDoc.RootElement.TryGetProperty("build", out var build).Should().BeTrue();
        build.GetString().Should().NotBeNullOrEmpty();
        jsonDoc.RootElement.TryGetProperty("urls", out var urls).Should().BeTrue();
        urls.TryGetProperty("openApiMetadata", out _).Should().BeTrue();
        jsonDoc
            .RootElement.TryGetProperty("specificationVersion", out var specificationVersion)
            .Should()
            .BeTrue();
        specificationVersion.GetString().Should().BeOneOf("v1", "v2", "v3");
    }

    [Test]
    public async Task It_should_return_a_four_part_build_version()
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
        jsonDoc.RootElement.TryGetProperty("build", out var build).Should().BeTrue();
        var buildValue = build.GetString();
        buildValue.Should().NotBeNullOrEmpty();
        Regex
            .IsMatch(buildValue!, @"^\d+\.\d+\.\d+\.\d+$")
            .Should()
            .BeTrue(
                because: $"build value '{buildValue}' should match the four-part numeric version pattern"
            );
    }

    [Test]
    public async Task Information_Endpoint_Returns_SpecificationVersion_From_Config()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:SpecificationVersion"] = "v2" }
                    );
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonDoc
            .RootElement.TryGetProperty("specificationVersion", out var specificationVersion)
            .Should()
            .BeTrue();
        specificationVersion.GetString().Should().Be("v2");
    }

    [Test]
    public async Task Information_Endpoint_Normalizes_SpecificationVersion_To_Lowercase()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:SpecificationVersion"] = "V2" }
                    );
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonDoc
            .RootElement.TryGetProperty("specificationVersion", out var specificationVersion)
            .Should()
            .BeTrue();
        specificationVersion.GetString().Should().Be("v2");
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

[TestFixture("", "/")]
[TestFixture("mt-config", "/mt-config")]
[TestFixture("mt-config", "/mt-config/")]
public class Given_MultiTenancy_Is_Enabled_And_An_Information_Discovery_Request_Without_A_Tenant_Header(
    string pathBase,
    string path
) : MultiTenantPipelineTestBase
{
    private HttpStatusCode _statusCode;

    [SetUp]
    public async Task Setup()
    {
        // UsePathBase leaves an empty path for "/mt-config" and "/" for "/mt-config/"; both are the root.
        await using var factory = CreateMultiTenantFactory(pathBase);
        using var client = factory.CreateClient();

        // No Tenant header and no credentials
        var response = await client.GetAsync(path);
        _statusCode = response.StatusCode;
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }
}
