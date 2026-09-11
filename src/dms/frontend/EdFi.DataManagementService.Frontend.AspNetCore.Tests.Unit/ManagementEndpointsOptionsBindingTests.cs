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
    private static WebApplicationFactory<Program> CreateFactory(string? requiredRole)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    if (requiredRole is not null)
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:ManagementEndpoints:RequiredRole"] = requiredRole,
                            }
                        );
                    }
                }
            );
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });
    }

    [Test]
    public void It_binds_the_configured_required_role()
    {
        using WebApplicationFactory<Program> factory = CreateFactory("dms-management-operator");

        factory
            .Services.GetRequiredService<IOptions<ManagementEndpointsOptions>>()
            .Value.RequiredRole.Should()
            .Be("dms-management-operator");
    }

    [Test]
    public void It_defaults_the_required_role_to_the_shipped_empty_value()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(requiredRole: null);

        ManagementEndpointsOptions options = factory
            .Services.GetRequiredService<IOptions<ManagementEndpointsOptions>>()
            .Value;

        options.TryGetRequiredRoleForEndpointMapping(out string? requiredRole).Should().BeFalse();
        requiredRole.Should().BeNull();
    }
}
