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
    public void It_uses_the_default_data_store_database_name_when_not_configured()
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

        settings.DataStoreDatabaseName.Should().Be(AppSettings.LegacyDataStoreDatabaseName);
    }

    [Test]
    public void It_prefers_environment_style_overrides_for_data_store_database_name()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreDatabaseName),
                        "edfi_datamanagementservice_relational"
                    ),
                ])
                .Build()
        );

        settings.DataStoreDatabaseName.Should().Be("edfi_datamanagementservice_relational");
    }

    [Test]
    public void It_reads_data_store_database_name_from_top_level_keys()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreDatabaseName),
                        "edfi_datamanagementservice_relational_top_level"
                    ),
                ])
                .Build()
        );

        settings.DataStoreDatabaseName.Should().Be("edfi_datamanagementservice_relational_top_level");
    }
}
