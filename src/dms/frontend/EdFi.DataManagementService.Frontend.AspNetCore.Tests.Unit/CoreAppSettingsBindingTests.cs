// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
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

    [Test]
    public void It_binds_the_education_organization_projection_toggle_and_limits()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:EnableEducationOrganizationProjection"] = "false",
                    ["AppSettings:EducationOrganizationProjection:MaximumPageSize"] = "250",
                    ["AppSettings:EducationOrganizationProjection:MaxProjectionRows"] = "75000",
                    ["AppSettings:EducationOrganizationProjection:CursorLifetimeMinutes"] = "30",
                    ["AppSettings:EducationOrganizationProjection:ReadLockTimeoutSeconds"] = "7",
                    ["AppSettings:EducationOrganizationProjection:ReadCommandTimeoutSeconds"] = "90",
                }
            )
            .Build();

        CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
        configuration.GetSection("AppSettings").Bind(settings);

        settings.EnableEducationOrganizationProjection.Should().BeFalse();
        settings.EducationOrganizationProjection.MaximumPageSize.Should().Be(250);
        settings.EducationOrganizationProjection.MaxProjectionRows.Should().Be(75000);
        settings.EducationOrganizationProjection.CursorLifetimeMinutes.Should().Be(30);
        settings.EducationOrganizationProjection.ReadLockTimeoutSeconds.Should().Be(7);
        settings.EducationOrganizationProjection.ReadCommandTimeoutSeconds.Should().Be(90);
    }

    [Test]
    public void It_keeps_the_projection_defaults_for_keys_the_section_omits()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:EducationOrganizationProjection:CursorLifetimeMinutes"] = "30",
                }
            )
            .Build();

        CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
        configuration.GetSection("AppSettings").Bind(settings);

        settings.EnableEducationOrganizationProjection.Should().BeTrue();
        settings.EducationOrganizationProjection.CursorLifetimeMinutes.Should().Be(30);
        settings
            .EducationOrganizationProjection.MaximumPageSize.Should()
            .Be(EducationOrganizationProjectionSettings.MaximumPageSizeDefault);
        settings
            .EducationOrganizationProjection.MaxProjectionRows.Should()
            .Be(EducationOrganizationProjectionSettings.MaxProjectionRowsDefault);
        settings
            .EducationOrganizationProjection.ReadLockTimeoutSeconds.Should()
            .Be(EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsDefault);
        settings
            .EducationOrganizationProjection.ReadCommandTimeoutSeconds.Should()
            .Be(EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsDefault);
    }

    [Test]
    public void It_ships_the_projection_toggle_and_limits_explicitly_in_appsettings()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Read as raw strings, so the assertion fails if a key is missing from the file rather than
        // passing on the property defaults.
        configuration["AppSettings:EnableEducationOrganizationProjection"].Should().Be("True");
        IConfigurationSection projection = configuration.GetSection(
            "AppSettings:EducationOrganizationProjection"
        );
        projection["MaximumPageSize"].Should().Be("2000");
        projection["MaxProjectionRows"].Should().Be("50000");
        projection["CursorLifetimeMinutes"].Should().Be("60");
        projection["ReadLockTimeoutSeconds"].Should().Be("5");
        projection["ReadCommandTimeoutSeconds"].Should().Be("60");
    }
}

/// <summary>
/// The compose files map the projection toggle to <c>AppSettings__EnableEducationOrganizationProjection</c>,
/// and operators override the limits the same way; both must reach the bound options. Non-parallel
/// and restoring the exact prior process values, because the variable names are process-wide.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Education_Organization_Projection_Environment_Overrides
{
    private const string ToggleVariable = "AppSettings__EnableEducationOrganizationProjection";
    private const string PageSizeVariable = "AppSettings__EducationOrganizationProjection__MaximumPageSize";

    [Test]
    public void It_overrides_the_configuration_section_values()
    {
        string? priorToggle = Environment.GetEnvironmentVariable(ToggleVariable);
        string? priorPageSize = Environment.GetEnvironmentVariable(PageSizeVariable);

        try
        {
            Environment.SetEnvironmentVariable(ToggleVariable, "false");
            Environment.SetEnvironmentVariable(PageSizeVariable, "1500");

            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSettings:EnableEducationOrganizationProjection"] = "true",
                        ["AppSettings:EducationOrganizationProjection:MaximumPageSize"] = "2000",
                    }
                )
                .AddEnvironmentVariables()
                .Build();

            CoreAppSettings settings = new() { AllowIdentityUpdateOverrides = string.Empty };
            configuration.GetSection("AppSettings").Bind(settings);

            settings.EnableEducationOrganizationProjection.Should().BeFalse();
            settings.EducationOrganizationProjection.MaximumPageSize.Should().Be(1500);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ToggleVariable, priorToggle);
            Environment.SetEnvironmentVariable(PageSizeVariable, priorPageSize);
        }
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
