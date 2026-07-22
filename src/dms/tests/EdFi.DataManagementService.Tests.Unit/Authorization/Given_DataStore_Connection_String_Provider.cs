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
}
