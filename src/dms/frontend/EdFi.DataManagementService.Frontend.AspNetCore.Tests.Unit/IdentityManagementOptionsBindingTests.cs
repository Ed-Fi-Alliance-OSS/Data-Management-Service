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
public class Given_IdentityManagementOptionsBinding
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

    private static IdentityManagementOptions BoundOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<IdentityManagementOptions>>().Value;

    [Test]
    public void It_binds_the_enabled_switch_from_the_identity_management_section()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new() { ["DataManagement:IdentityManagement:Enabled"] = "true" }
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
    public void It_ignores_the_removed_flat_switch()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new() { ["AppSettings:EnableIdentityManagement"] = "true" }
        );

        BoundOptions(factory).Enabled.Should().BeFalse();
    }
}
