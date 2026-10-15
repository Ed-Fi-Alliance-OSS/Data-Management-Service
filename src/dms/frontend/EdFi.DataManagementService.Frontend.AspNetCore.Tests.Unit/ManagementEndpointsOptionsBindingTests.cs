// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class Given_ManagementEndpointsOptionsBinding
{
    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) => configuration.AddInMemoryCollection(settings)
            );
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });
    }

    private static ManagementEndpointsOptions BoundOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<ManagementEndpointsOptions>>().Value;

    [Test]
    public void It_binds_the_configured_required_role()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new() { ["AppSettings:ManagementEndpoints:RequiredRole"] = "cms-client" }
        );

        BoundOptions(factory).RequiredRole.Should().Be("cms-client");
    }

    [Test]
    public void It_defaults_the_required_role_to_the_shipped_empty_value()
    {
        using WebApplicationFactory<Program> factory = CreateFactory([]);

        BoundOptions(factory)
            .TryGetRequiredRoleForEndpointMapping(out string? requiredRole)
            .Should()
            .BeFalse();
        requiredRole.Should().BeNull();
    }

    [Test]
    public void It_binds_the_enabled_switch_from_the_management_endpoints_section()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new() { ["AppSettings:ManagementEndpoints:Enabled"] = "true" }
        );

        BoundOptions(factory).Enabled.Should().BeTrue();
    }

    [Test]
    public void It_defaults_the_enabled_switch_to_the_shipped_false_value()
    {
        using WebApplicationFactory<Program> factory = CreateFactory([]);

        BoundOptions(factory).Enabled.Should().BeFalse();
    }

    [Test]
    public void It_ignores_the_removed_flat_switches()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new()
            {
                ["AppSettings:EnableManagementEndpoints"] = "true",
                ["AppSettings:EnableClaimsetReload"] = "true",
            }
        );

        BoundOptions(factory).Enabled.Should().BeFalse();
    }
}
