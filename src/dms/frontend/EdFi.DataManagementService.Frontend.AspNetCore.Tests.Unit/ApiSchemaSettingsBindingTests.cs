// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using ApiSchemaSettings = EdFi.DataManagementService.Core.Configuration.ApiSchemaSettings;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class Given_ApiSchemaSettingsBinding
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

    private static ApiSchemaSettings BoundSettings(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<CoreAppSettings>>().Value.ApiSchema;

    [Test]
    public void It_binds_the_custom_path_settings_from_the_api_schema_section()
    {
        // The host loads the schema at startup, so the custom path must name a real workspace.
        string workspace = Path.Combine(AppContext.BaseDirectory, "ApiSchema");
        using WebApplicationFactory<Program> factory = CreateFactory(
            new()
            {
                ["AppSettings:ApiSchema:UseCustomPath"] = "true",
                ["AppSettings:ApiSchema:Path"] = workspace,
            }
        );

        ApiSchemaSettings settings = BoundSettings(factory);
        settings.UseCustomPath.Should().BeTrue();
        settings.Path.Should().Be(workspace);
    }

    [Test]
    public void It_defaults_to_the_bundled_workspace()
    {
        using WebApplicationFactory<Program> factory = CreateFactory([]);

        BoundSettings(factory).UseCustomPath.Should().BeFalse();
    }

    [Test]
    public void It_ignores_the_removed_flat_settings()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new()
            {
                ["AppSettings:UseApiSchemaPath"] = "true",
                ["AppSettings:ApiSchemaPath"] = "/legacy/ApiSchema",
            }
        );

        ApiSchemaSettings settings = BoundSettings(factory);
        settings.UseCustomPath.Should().BeFalse();
        settings.Path.Should().NotBe("/legacy/ApiSchema");
    }
}
