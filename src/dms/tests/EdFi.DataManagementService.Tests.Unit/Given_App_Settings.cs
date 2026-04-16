// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_App_Settings
{
    [Test]
    public void It_uses_legacy_defaults_when_relational_settings_are_not_present()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.AuthenticationService),
                        "http://test-auth"
                    ),
                ])
                .Build()
        );

        settings.UseRelationalBackend.Should().BeFalse();
        settings.DmsInstanceDatabaseName.Should().Be(AppSettings.LegacyDmsInstanceDatabaseName);
    }

    [Test]
    public void It_prefers_environment_style_overrides_for_relational_settings()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>("AppSettings:UseRelationalBackend", "true"),
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DmsInstanceDatabaseName),
                        "edfi_datamanagementservice_relational"
                    ),
                ])
                .Build()
        );

        settings.UseRelationalBackend.Should().BeTrue();
        settings.DmsInstanceDatabaseName.Should().Be("edfi_datamanagementservice_relational");
    }

    [Test]
    public void It_reads_relational_settings_from_top_level_keys()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(nameof(AppSettings.UseRelationalBackend), "true"),
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DmsInstanceDatabaseName),
                        "edfi_datamanagementservice_relational_top_level"
                    ),
                ])
                .Build()
        );

        settings.UseRelationalBackend.Should().BeTrue();
        settings.DmsInstanceDatabaseName.Should().Be("edfi_datamanagementservice_relational_top_level");
    }
}
