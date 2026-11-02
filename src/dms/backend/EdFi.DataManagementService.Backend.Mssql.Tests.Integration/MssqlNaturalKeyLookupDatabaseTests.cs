// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_Mssql_Natural_Key_Lookup_Database : NaturalKeyLookupDatabaseTests
{
    private MssqlGeneratedDdlTestDatabase _database = null!;

    protected override SqlDialect Dialect => SqlDialect.Mssql;

    protected override DbConnection CreateConnection() => new SqlConnection(_database.ConnectionString);

    private protected override INaturalKeyLookupCommandBuilder CreateBuilder() =>
        new MssqlNaturalKeyLookupCommandBuilder();

    protected override async Task<(IAsyncDisposable Database, MappingSet MappingSet)> ProvisionAsync(
        string fixtureRelativePath
    )
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "Natural-key lookup database tests require a reachable SQL Server admin connection."
        );
        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            fixtureRelativePath,
            strict: true
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        return (_database, fixture.MappingSet);
    }
}
