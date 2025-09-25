// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using Npgsql;
using Respawn;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public abstract class DatabaseTestBase
{
    private static readonly string _connectionString = Configuration.DatabaseOptions.Value.DatabaseConnection;
    private NpgsqlConnection? _respawnerConnection;
    private Respawner? _respawner;

    public NpgsqlDataSource? DataSource { get; set; }

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        new Deploy.DatabaseDeploy().DeployDatabase(_connectionString);
    }

    [SetUp]
    public async Task SetupDatabase()
    {
        DataSource = NpgsqlDataSource.Create(_connectionString);
        _respawnerConnection = await DataSource.OpenConnectionAsync();

        _respawner = await Respawner.CreateAsync(
            _respawnerConnection,
            new RespawnerOptions
            {
                TablesToInclude =
                [
                    new("dmscs", "vendor"),
                    new("dmscs", "vendornamespaceprefix"),
                    new("dmscs", "application"),
                    new("dmscs", "applicationeducationorganization"),
                    new("dmscs", "claimset"),
                    new("dmscs", "claimshierarchy"),
                    new("dmscs", "dmsinstance"),
                ],
                DbAdapter = DbAdapter.Postgres,
            }
        );
    }

    [TearDown]
    public async Task TeardownDatabase()
    {
        if (_respawnerConnection is not null)
        {
            await _respawner!.ResetAsync(_respawnerConnection);
            await _respawnerConnection.DisposeAsync();
        }
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// Helper method to clear claims-related tables
    /// </summary>
    protected async Task ClearClaimsTablesAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM dmscs.ClaimsHierarchy");
        await connection.ExecuteAsync("DELETE FROM dmscs.ClaimSet");
    }

    /// <summary>
    /// Helper method to get row counts from claims tables
    /// </summary>
    protected async Task<(int claimSetCount, int hierarchyCount)> GetClaimsTableCountsAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        var claimSetCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dmscs.ClaimSet");
        var hierarchyCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.ClaimsHierarchy"
        );
        return (claimSetCount, hierarchyCount);
    }

    /// <summary>
    /// Helper method to verify if claims tables are empty
    /// </summary>
    protected async Task<bool> AreClaimsTablesEmptyAsync()
    {
        var (claimSetCount, hierarchyCount) = await GetClaimsTableCountsAsync();
        return claimSetCount == 0 && hierarchyCount == 0;
    }

    /// <summary>
    /// Helper method to get all claim set names
    /// </summary>
    protected async Task<IEnumerable<string>> GetClaimSetNamesAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        return await connection.QueryAsync<string>(
            "SELECT ClaimSetName FROM dmscs.ClaimSet ORDER BY ClaimSetName"
        );
    }

    /// <summary>
    /// Helper method to check if a specific claim set exists
    /// </summary>
    protected async Task<bool> ClaimSetExistsAsync(string claimSetName)
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.ClaimSet WHERE ClaimSetName = @claimSetName",
            new { claimSetName }
        );
        return count > 0;
    }
}
