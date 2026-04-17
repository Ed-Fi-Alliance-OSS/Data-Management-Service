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
public class Given_Dms_Instance_Connection_String_Provider
{
    [Test]
    public void It_uses_the_legacy_database_name_by_default()
    {
        var settings = AppSettings.Create(new ConfigurationBuilder().Build());

        string connectionString = DmsInstanceConnectionStringProvider.Create(settings);

        connectionString
            .Should()
            .Be(
                "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"
            );
    }

    [Test]
    public void It_uses_the_relational_database_name_from_shared_app_settings()
    {
        var settings = AppSettings.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    KeyValuePair.Create<string, string?>(
                        nameof(AppSettings.DmsInstanceDatabaseName),
                        "edfi_datamanagementservice_relational"
                    ),
                ])
                .Build()
        );

        string connectionString = DmsInstanceConnectionStringProvider.Create(settings);

        connectionString
            .Should()
            .Be(
                "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_relational;"
            );
    }
}
