// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Proves the Core AppSettings paging values are validated when the application starts, through the
/// real application entry point.
/// </summary>
/// <remarks>
/// The eager resolve in Program.cs records the failure against the ConfigureEndpoints phase, so an
/// operator gets an accurate status file rather than one reading Completed on a process that is about
/// to die. Host start then still fails, because eager options validation refuses to bring a host up on
/// invalid values. That is why these cases assert the recorded failure rather than a short-circuited
/// HTTP 500 response: the 500 path is reachable only for options that are not eagerly validated.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class CoreAppSettingsStartupValidationTests
{
    private WebApplicationFactory<Program>? _factory;
    private string _statusDirectory = null!;
    private string _statusFilePath = null!;

    private void CreateFactoryWith(string settingKey, string settingValue)
    {
        _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            [settingKey] = settingValue,
                        }
                    )
            );
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });
    }

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
        "AppSettings:DefaultPartitionCount",
        "201",
        nameof(CoreAppSettings.DefaultPartitionCount),
        TestName = "It_fails_startup_for_a_partition_count_above_the_supported_range"
    )]
    [TestCase(
        "AppSettings:MaximumPageSize",
        "0",
        nameof(CoreAppSettings.MaximumPageSize),
        TestName = "It_fails_startup_for_a_nonpositive_maximum_page_size"
    )]
    public async Task It_records_the_failed_startup_and_refuses_to_start(
        string settingKey,
        string settingValue,
        string expectedSettingName
    )
    {
        CreateFactoryWith(settingKey, settingValue);

        Action startHost = () => _factory!.CreateClient();
        startHost.Should().Throw<OptionsValidationException>();

        File.Exists(_statusFilePath).Should().BeTrue();
        var startupStatus = JsonNode.Parse(await File.ReadAllTextAsync(_statusFilePath))!.AsObject();

        startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
        startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureEndpoints);
        startupStatus["ErrorType"]!.GetValue<string>().Should().Be(nameof(OptionsValidationException));
        startupStatus["ErrorMessage"]!.GetValue<string>().Should().Contain(expectedSettingName);
    }
}

/// <summary>
/// Complements the host-start coverage above with a direct assertion on the eager validator's
/// failures, which the startup-status path reports only as a message.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Core_App_Settings_Startup_Validator
{
    private static ServiceProvider CreateServices(Dictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(configuration)
            {
                ["AppSettings:Datastore"] = "postgresql",
                ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
                ["ConfigurationServiceSettings:ClientId"] = "client-id",
                ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                ["ConfigurationServiceSettings:Scope"] = "scope",
                ["ConfigurationServiceSettings:EncryptionKey"] =
                    "TestEncryptionKey123456789012345678901234567890",
            }
        );

        builder.AddServices();

        return builder.Services.BuildServiceProvider();
    }

    [Test]
    public void It_reports_the_partition_count_failure_from_the_startup_validator()
    {
        using ServiceProvider serviceProvider = CreateServices(
            new Dictionary<string, string?>
            {
                ["AppSettings:MaximumPageSize"] = "500",
                ["AppSettings:DefaultPartitionCount"] = "201",
            }
        );

        Action validate = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        validate
            .Should()
            .Throw<OptionsValidationException>()
            .Which.Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(nameof(CoreAppSettings.DefaultPartitionCount));
    }

    [Test]
    public void It_reports_the_maximum_page_size_failure_from_the_startup_validator()
    {
        using ServiceProvider serviceProvider = CreateServices(
            new Dictionary<string, string?>
            {
                ["AppSettings:MaximumPageSize"] = "0",
                ["AppSettings:DefaultPartitionCount"] = "10",
            }
        );

        Action validate = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        validate
            .Should()
            .Throw<OptionsValidationException>()
            .Which.Failures.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(nameof(CoreAppSettings.MaximumPageSize));
    }

    [Test]
    public void It_passes_for_the_shipped_default_values()
    {
        using ServiceProvider serviceProvider = CreateServices(
            new Dictionary<string, string?>
            {
                ["AppSettings:MaximumPageSize"] = "500",
                ["AppSettings:DefaultPartitionCount"] = "10",
            }
        );

        Action validate = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
    }
}
