// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
[Category("OrdinaryDataStoreQuery")]
public class Given_ordinary_data_store_queries(bool multitenant) : OrdinaryDataStoreQueryTests(multitenant)
{
    private JobUpgradeTestDatabase _database = null!;
    protected override string Provider => "postgresql";
    protected override string ConnectionString => _database.ConnectionString;

    protected override Task DeployAsync()
    {
        _database = new();
        _database.DeployThrough(39);
        return Task.CompletedTask;
    }

    protected override Task DropAsync() => _database is null ? Task.CompletedTask : _database.DropAsync();

    protected override async Task ClearStoresAsync()
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.ExecuteAsync("""DELETE FROM "dmscs"."DataStore";""");
    }

    protected override async Task<long> InsertTenantAsync(string name)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = name }
        );
    }

    protected override async Task<int> InsertAsync(OrdinaryDataStoreQueryCases.Row row, long? tenantId)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            """INSERT INTO "dmscs"."DataStore" ("Name", "DataStoreType", "TenantId") VALUES (@Name, @Type, @TenantId) RETURNING "Id";""",
            new
            {
                row.Name,
                row.Type,
                TenantId = tenantId,
            }
        );
    }
}
