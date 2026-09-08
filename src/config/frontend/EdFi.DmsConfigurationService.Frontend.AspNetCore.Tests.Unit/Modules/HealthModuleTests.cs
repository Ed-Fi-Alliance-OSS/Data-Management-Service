// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Globalization;
using System.Net;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class HealthTests
{
    [Test]
    public async Task TestPingEndpoint()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/health");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        DateTime.TryParse(content, CultureInfo.InvariantCulture, out DateTime dt).Should().Be(true);
    }
}

/// <summary>
/// Shared setup for full-pipeline health tests with CMS multi-tenancy enabled. Both the application
/// configuration (read by endpoint mapping) and IOptions&lt;AppSettings&gt; (read by
/// TenantResolutionMiddleware) must set MultiTenancy for the middleware to enforce tenant resolution.
/// </summary>
public abstract class MultiTenantPipelineTestBase
{
    protected static WebApplicationFactory<Program> CreateMultiTenantFactory(string? pathBase = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");

            builder.ConfigureAppConfiguration(
                (_, config) =>
                {
                    var settings = new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = "true" };
                    if (pathBase is not null)
                    {
                        settings["AppSettings:PathBase"] = pathBase;
                    }
                    config.AddInMemoryCollection(settings);
                }
            );

            builder.ConfigureServices(
                (_, services) => services.Configure<AppSettings>(s => s.MultiTenancy = true)
            );
        });
    }
}

[TestFixture]
public class Given_MultiTenancy_And_A_Health_Request : MultiTenantPipelineTestBase
{
    private HttpStatusCode _statusCode;
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = CreateMultiTenantFactory();
        using var client = factory.CreateClient();

        // No Tenant header
        var response = await client.GetAsync("/health");
        _statusCode = response.StatusCode;
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_returns_a_timestamp_body()
    {
        DateTime.TryParse(_body, CultureInfo.InvariantCulture, out _).Should().BeTrue();
    }
}

[TestFixture]
public class Given_MultiTenancy_With_PathBase_And_A_Health_Request : MultiTenantPipelineTestBase
{
    private HttpStatusCode _statusCode;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = CreateMultiTenantFactory(pathBase: "mt-config");
        using var client = factory.CreateClient();

        // No Tenant header
        var response = await client.GetAsync("/mt-config/health");
        _statusCode = response.StatusCode;
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }
}

[TestFixture]
public class Given_MultiTenancy_And_A_Protected_Request_Without_Tenant_Header : MultiTenantPipelineTestBase
{
    private HttpStatusCode _statusCode;
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = CreateMultiTenantFactory();
        using var client = factory.CreateClient();

        // No Tenant header
        var response = await client.GetAsync("/v3/vendors");
        _statusCode = response.StatusCode;
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_returns_400()
    {
        _statusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public void It_returns_the_tenant_required_message()
    {
        _body.Should().Contain("The 'Tenant' header is required when multi-tenancy is enabled");
    }
}
