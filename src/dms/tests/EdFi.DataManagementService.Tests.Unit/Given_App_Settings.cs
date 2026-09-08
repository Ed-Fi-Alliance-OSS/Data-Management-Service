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
    public void It_uses_the_default_e2e_data_store_database_name_when_not_configured()
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

        settings.DataStoreDatabaseName.Should().Be(AppSettings.DefaultDataStoreDatabaseName);
    }

    [Test]
    public void It_prefers_environment_style_overrides_for_data_store_database_name()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreDatabaseName),
                        "edfi_datamanagementservice_e2e_override"
                    ),
                ])
                .Build()
        );

        settings.DataStoreDatabaseName.Should().Be("edfi_datamanagementservice_e2e_override");
    }

    [Test]
    public void It_reads_data_store_database_name_from_top_level_keys()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreDatabaseName),
                        "edfi_datamanagementservice_e2e_top_level"
                    ),
                ])
                .Build()
        );

        settings.DataStoreDatabaseName.Should().Be("edfi_datamanagementservice_e2e_top_level");
    }

    [Test]
    public void It_defaults_the_database_engine_to_postgresql_when_not_configured()
    {
        var settings = AppSettings.Create(new ConfigurationBuilder().Build());

        settings.DatabaseEngine.Should().Be(AppSettings.DefaultDatabaseEngine);
        settings.DatabaseEngine.Should().Be("postgresql");
    }

    [Test]
    public void It_reads_the_database_engine_override()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(nameof(AppSettings.DatabaseEngine), "mssql"),
                ])
                .Build()
        );

        settings.DatabaseEngine.Should().Be("mssql");
    }

    [Test]
    public void It_defaults_the_opaque_connection_strings_to_empty_when_not_configured()
    {
        var settings = AppSettings.Create(new ConfigurationBuilder().Build());

        settings.DataStoreAdminConnectionString.Should().BeEmpty();
        settings.DataStoreConnectionString.Should().BeEmpty();
    }

    [Test]
    public void It_reads_the_opaque_connection_strings_verbatim()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreAdminConnectionString),
                        "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
                    ),
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreConnectionString),
                        "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
                    ),
                ])
                .Build()
        );

        settings
            .DataStoreAdminConnectionString.Should()
            .Be(
                "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
            );
        settings
            .DataStoreConnectionString.Should()
            .Be(
                "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
            );
    }
}
