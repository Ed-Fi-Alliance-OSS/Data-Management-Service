// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Postgresql_Natural_Key_Lookup_Database : NaturalKeyLookupDatabaseTests
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    protected override SqlDialect Dialect => SqlDialect.Pgsql;

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_database.ConnectionString);

    private protected override INaturalKeyLookupCommandBuilder CreateBuilder() =>
        new PostgresqlNaturalKeyLookupCommandBuilder();

    protected override async Task<(IAsyncDisposable Database, MappingSet MappingSet)> ProvisionAsync(
        string fixtureRelativePath
    )
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            fixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        return (_database, fixture.MappingSet);
    }
}
