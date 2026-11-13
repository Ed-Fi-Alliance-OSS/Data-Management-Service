// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
[Category("OrdinaryDataStoreQuery")]
[Category("MssqlIntegration")]
public class Given_ordinary_data_store_queries(bool multitenant) : OrdinaryDataStoreQueryTests(multitenant)
{
    private JobUpgradeTestDatabase _database = null!;
    protected override string Provider => "mssql";
    protected override string ConnectionString => _database.ConnectionString;

    protected override async Task DeployAsync()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require ConnectionStrings__MssqlAdmin."
        );
        _database = new();
        _database.DeployThrough(38);
        await using (SqlConnection connection = new(_database.ConnectionString))
        {
            string databaseCollation = await connection.QuerySingleAsync<string>(
                "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"
            );
            string displayCollation = databaseCollation.Equals(
                "Latin1_General_100_CI_AI",
                StringComparison.OrdinalIgnoreCase
            )
                ? "Latin1_General_CI_AI"
                : "Latin1_General_100_CI_AI";
            // Only fixed literals enter the DDL. Test the legacy accent-insensitive Name column
            // independently of the server/database default, retaining the native unique constraint.
            await connection.ExecuteAsync(
                $"""
                ALTER TABLE dmscs.DataStore DROP CONSTRAINT UX_DataStore_TenantId_Name;
                ALTER TABLE dmscs.DataStore ALTER COLUMN Name nvarchar(256) COLLATE {displayCollation} NOT NULL;
                ALTER TABLE dmscs.DataStore ADD CONSTRAINT UX_DataStore_TenantId_Name UNIQUE (TenantId, Name);
                """
            );
        }
        _database.DeployThrough(39);
    }

    protected override Task DropAsync() => _database is null ? Task.CompletedTask : _database.DropAsync();

    protected override async Task ClearStoresAsync()
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.ExecuteAsync("""DELETE FROM dmscs.DataStore;""");
    }

    protected override async Task<long> InsertTenantAsync(string name)
    {
        await using SqlConnection connection = new(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (@Name);""",
            new { Name = name }
        );
    }

    protected override async Task<int> InsertAsync(OrdinaryDataStoreQueryCases.Row row, long? tenantId)
    {
        await using SqlConnection connection = new(ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            """INSERT INTO dmscs.DataStore (Name, DataStoreType, TenantId) OUTPUT inserted.Id VALUES (@Name, @Type, @TenantId);""",
            new
            {
                row.Name,
                row.Type,
                TenantId = tenantId,
            }
        );
    }
}
