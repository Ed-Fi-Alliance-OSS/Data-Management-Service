// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.Unit.Authorization;

[TestFixture]
public class Given_DataStore_Connection_String_Provider
{
    [Test]
    public void It_returns_an_empty_string_when_no_connection_string_is_configured()
    {
        var settings = AppSettings.Create(new ConfigurationBuilder().Build());

        string connectionString = DataStoreConnectionStringProvider.Create(settings);

        connectionString.Should().BeEmpty();
    }

    [Test]
    public void It_returns_the_configured_postgresql_data_store_connection_string_verbatim()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreConnectionString),
                        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_e2e;"
                    ),
                ])
                .Build()
        );

        string connectionString = DataStoreConnectionStringProvider.Create(settings);

        connectionString
            .Should()
            .Be(
                "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_e2e;"
            );
    }

    [Test]
    public void It_returns_the_configured_sql_server_data_store_connection_string_verbatim()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DataStoreConnectionString),
                        "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"
                    ),
                ])
                .Build()
        );

        string connectionString = DataStoreConnectionStringProvider.Create(settings);

        connectionString
            .Should()
            .Be(
                "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"
            );
    }

    [Test]
    public void It_registers_the_default_postgresql_connection_string_from_the_e2e_appsettings_without_env_overrides()
    {
        // Regression for the default direct (dotnet-test) path: with no environment overrides, the E2E
        // appsettings.json supplies the default PostgreSQL opaque values so the provider registers a
        // non-empty Docker-network connection string with the Configuration Service.
        string appSettingsPath = Path.Combine(
            FindRepositoryRoot().FullName,
            "src",
            "dms",
            "tests",
            "EdFi.DataManagementService.Tests.E2E",
            "appsettings.json"
        );

        var settings = AppSettings.Create(
            new ConfigurationBuilder().AddJsonFile(appSettingsPath, optional: false).Build()
        );

        settings.DatabaseEngine.Should().Be("postgresql");
        settings
            .DataStoreAdminConnectionString.Should()
            .Be(
                "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_e2e;NoResetOnClose=true;"
            );

        DataStoreConnectionStringProvider
            .Create(settings)
            .Should()
            .Be(
                "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_e2e;"
            );
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (
            currentDirectory is not null && !File.Exists(Path.Combine(currentDirectory.FullName, "LICENSE"))
        )
        {
            currentDirectory = currentDirectory.Parent;
        }

        return currentDirectory
            ?? throw new InvalidOperationException(
                "Could not locate repository root from the test assembly output."
            );
    }
}
