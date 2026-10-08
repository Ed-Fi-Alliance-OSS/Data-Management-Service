// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Fresh_Mssql_CompactDescriptor_Schema : CompactDescriptorSchemaTests
{
    private MssqlGeneratedDdlTestDatabase _database = default!;
    protected override string Dialect => "mssql";

    protected override DbConnection CreateConnection() => new SqlConnection(_database.ConnectionString);

    protected override async Task<IAsyncDisposable> ProvisionAsync()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "Compact descriptor catalog tests require a reachable SQL Server admin connection."
        );
        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            CompactDescriptorCatalogAssertions.FixtureRelativePath
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        return _database;
    }
}
