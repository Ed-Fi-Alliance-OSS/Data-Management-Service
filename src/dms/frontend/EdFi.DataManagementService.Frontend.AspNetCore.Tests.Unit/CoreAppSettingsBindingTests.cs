// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class CoreAppSettingsBindingTests
{
    [Test]
    public void It_binds_the_partition_count_from_the_app_settings_section()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:MaximumPageSize"] = "250",
                    ["AppSettings:DefaultPartitionCount"] = "25",
                }
            )
            .Build();

        CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
        configuration.GetSection("AppSettings").Bind(settings);

        settings.MaximumPageSize.Should().Be(250);
        settings.DefaultPartitionCount.Should().Be(25);
    }

    [Test]
    public void It_keeps_the_property_default_when_the_section_omits_the_partition_count()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["AppSettings:MaximumPageSize"] = "500" }
            )
            .Build();

        CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
        configuration.GetSection("AppSettings").Bind(settings);

        settings.DefaultPartitionCount.Should().Be(CoreAppSettings.DefaultPartitionCountDefault);
    }

    [Test]
    public void It_binds_the_shipped_configured_defaults()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
        configuration.GetSection("AppSettings").Bind(settings);

        settings.MaximumPageSize.Should().Be(500);
        settings.DefaultPartitionCount.Should().Be(10);
    }
}

/// <summary>
/// The documented environment override must reach the bound options. Non-parallel and restoring the
/// exact prior process value, because the variable name is process-wide.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Partition_Count_Environment_Override
{
    private const string VariableName = "AppSettings__DefaultPartitionCount";

    [Test]
    public void It_overrides_the_configuration_section_value()
    {
        string? priorValue = Environment.GetEnvironmentVariable(VariableName);

        try
        {
            Environment.SetEnvironmentVariable(VariableName, "42");

            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:DefaultPartitionCount"] = "10" }
                )
                .AddEnvironmentVariables()
                .Build();

            CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
            configuration.GetSection("AppSettings").Bind(settings);

            settings.DefaultPartitionCount.Should().Be(42);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, priorValue);
        }
    }
}
