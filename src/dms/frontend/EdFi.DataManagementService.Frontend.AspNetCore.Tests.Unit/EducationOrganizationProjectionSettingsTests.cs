// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The projection switch and limits as the host registers them: bound from
/// <c>DataManagement:EducationOrganizationProjection</c> and validated at startup.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Registered_Education_Organization_Projection_Settings
{
    private const string Section = "DataManagement:EducationOrganizationProjection";

    private static ServiceProvider CreateServices(Dictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(configuration)
            {
                ["AppSettings:Datastore"] = "postgresql",
                ["AppSettings:MaximumPageSize"] = "500",
                ["AppSettings:DefaultPartitionCount"] = "10",
                ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
                ["ConfigurationServiceSettings:ClientId"] = "client-id",
                ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                ["ConfigurationServiceSettings:Scope"] = "scope",
                ["ConfigurationServiceSettings:EncryptionKey"] =
                    "TestEncryptionKey123456789012345678901234567890",
            }
        );

        builder.AddServices(LoadedPlugins.Empty);

        return builder.Services.BuildServiceProvider();
    }

    private static EducationOrganizationProjectionSettings Bound(Dictionary<string, string?> configuration)
    {
        using ServiceProvider serviceProvider = CreateServices(configuration);
        return serviceProvider.GetRequiredService<IOptions<EducationOrganizationProjectionSettings>>().Value;
    }

    [Test]
    public void It_binds_the_switch_and_limits_from_the_data_management_section()
    {
        EducationOrganizationProjectionSettings settings = Bound(
            new Dictionary<string, string?>
            {
                [$"{Section}:Enabled"] = "false",
                [$"{Section}:MaximumPageSize"] = "250",
                [$"{Section}:MaxProjectionRows"] = "75000",
                [$"{Section}:CursorLifetimeMinutes"] = "30",
                [$"{Section}:ReadLockTimeoutSeconds"] = "7",
                [$"{Section}:ReadCommandTimeoutSeconds"] = "90",
            }
        );

        settings.Enabled.Should().BeFalse();
        settings.MaximumPageSize.Should().Be(250);
        settings.MaxProjectionRows.Should().Be(75000);
        settings.CursorLifetimeMinutes.Should().Be(30);
        settings.ReadLockTimeoutSeconds.Should().Be(7);
        settings.ReadCommandTimeoutSeconds.Should().Be(90);
    }

    [Test]
    public void It_keeps_the_defaults_for_keys_the_section_omits()
    {
        EducationOrganizationProjectionSettings settings = Bound(
            new Dictionary<string, string?> { [$"{Section}:CursorLifetimeMinutes"] = "30" }
        );

        settings.Should().Be(new EducationOrganizationProjectionSettings { CursorLifetimeMinutes = 30 });
    }

    [Test]
    public void It_ignores_the_former_app_settings_keys()
    {
        EducationOrganizationProjectionSettings settings = Bound(
            new Dictionary<string, string?>
            {
                ["AppSettings:EnableEducationOrganizationProjection"] = "false",
                ["AppSettings:EducationOrganizationProjection:MaximumPageSize"] = "250",
            }
        );

        settings.Should().Be(new EducationOrganizationProjectionSettings());
    }

    [Test]
    public void It_passes_startup_validation_with_the_defaults()
    {
        using ServiceProvider serviceProvider = CreateServices([]);

        Action validate = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
    }

    [Test]
    public void It_fails_startup_validation_for_a_limit_out_of_range_when_the_endpoint_is_disabled()
    {
        using ServiceProvider serviceProvider = CreateServices(
            new Dictionary<string, string?>
            {
                [$"{Section}:Enabled"] = "false",
                [$"{Section}:MaxProjectionRows"] = "1000001",
            }
        );

        Action validate = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        validate
            .Should()
            .Throw<OptionsValidationException>()
            .Which.Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Be($"Setting {Section}:MaxProjectionRows must be between 1000 and 1000000");
    }

    [Test]
    public void It_ships_the_switch_and_limits_explicitly_in_appsettings()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Read as raw strings, so the assertion fails if a key is missing from the file rather than
        // passing on the property defaults.
        IConfigurationSection projection = configuration.GetSection(Section);
        projection["Enabled"].Should().Be("True");
        projection["MaximumPageSize"].Should().Be("2000");
        projection["MaxProjectionRows"].Should().Be("50000");
        projection["CursorLifetimeMinutes"].Should().Be("60");
        projection["ReadLockTimeoutSeconds"].Should().Be("5");
        projection["ReadCommandTimeoutSeconds"].Should().Be("60");
        configuration["AppSettings:EnableEducationOrganizationProjection"].Should().BeNull();
        configuration.GetSection("AppSettings:EducationOrganizationProjection").Exists().Should().BeFalse();
    }
}

/// <summary>
/// The compose files map the projection switch to <c>DataManagement__EducationOrganizationProjection__Enabled</c>,
/// and operators override the limits the same way; both must reach the bound settings. Non-parallel
/// and restoring the exact prior process values, because the variable names are process-wide.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Education_Organization_Projection_Environment_Overrides
{
    private const string SwitchVariable = "DataManagement__EducationOrganizationProjection__Enabled";
    private const string PageSizeVariable =
        "DataManagement__EducationOrganizationProjection__MaximumPageSize";

    [Test]
    public void It_overrides_the_configuration_section_values()
    {
        string? priorSwitch = Environment.GetEnvironmentVariable(SwitchVariable);
        string? priorPageSize = Environment.GetEnvironmentVariable(PageSizeVariable);

        try
        {
            Environment.SetEnvironmentVariable(SwitchVariable, "false");
            Environment.SetEnvironmentVariable(PageSizeVariable, "1500");

            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["DataManagement:EducationOrganizationProjection:Enabled"] = "true",
                        ["DataManagement:EducationOrganizationProjection:MaximumPageSize"] = "2000",
                    }
                )
                .AddEnvironmentVariables()
                .Build();

            EducationOrganizationProjectionSettings settings = configuration
                .GetSection(EducationOrganizationProjectionSettings.SectionName)
                .Get<EducationOrganizationProjectionSettings>()!;

            settings.Enabled.Should().BeFalse();
            settings.MaximumPageSize.Should().Be(1500);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SwitchVariable, priorSwitch);
            Environment.SetEnvironmentVariable(PageSizeVariable, priorPageSize);
        }
    }
}

/// <summary>
/// An out-of-range projection limit stops the real application entry point, whether or not the
/// endpoint is enabled; the startup status file records the failing setting.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_An_Out_Of_Range_Education_Organization_Projection_Limit_At_Startup
{
    private WebApplicationFactory<Program>? _factory;
    private string _statusDirectory = null!;
    private string _statusFilePath = null!;

    [TearDown]
    public void Teardown()
    {
        _factory?.Dispose();

        if (Directory.Exists(_statusDirectory))
        {
            Directory.Delete(_statusDirectory, recursive: true);
        }
    }

    [TestCase(
        "MaximumPageSize",
        "10001",
        TestName = "It_fails_startup_for_a_page_size_above_the_supported_range"
    )]
    [TestCase(
        "MaxProjectionRows",
        "999",
        TestName = "It_fails_startup_for_a_row_cap_below_the_supported_range"
    )]
    [TestCase(
        "CursorLifetimeMinutes",
        "1441",
        TestName = "It_fails_startup_for_a_cursor_lifetime_above_the_supported_range"
    )]
    [TestCase(
        "ReadLockTimeoutSeconds",
        "0",
        TestName = "It_fails_startup_for_a_lock_timeout_below_the_supported_range"
    )]
    [TestCase(
        "ReadCommandTimeoutSeconds",
        "601",
        TestName = "It_fails_startup_for_a_command_timeout_above_the_supported_range"
    )]
    public async Task It_records_the_failed_startup_and_refuses_to_start(string settingName, string value)
    {
        _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
        string key = $"{EducationOrganizationProjectionSettings.SectionName}:{settingName}";

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            [$"{EducationOrganizationProjectionSettings.SectionName}:Enabled"] = "false",
                            [key] = value,
                        }
                    )
            );
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });

        // The host must refuse to start; which exception WebApplicationFactory surfaces is a race in
        // its deferred host, so the cause is asserted through the status file Program writes first.
        Action startHost = () => _factory!.CreateClient();
        startHost.Should().Throw<Exception>();

        File.Exists(_statusFilePath).Should().BeTrue();
        var startupStatus = JsonNode.Parse(await File.ReadAllTextAsync(_statusFilePath))!.AsObject();

        startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
        startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureEndpoints);
        startupStatus["ErrorType"]!.GetValue<string>().Should().Be(nameof(OptionsValidationException));
        startupStatus["ErrorMessage"]!.GetValue<string>().Should().Contain(key);
    }
}
