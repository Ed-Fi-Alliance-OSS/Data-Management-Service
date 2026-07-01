// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using Microsoft.Data.SqlClient;
using Respawn;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[Category("MssqlIntegration")]
public abstract class DatabaseTestBase
{
    private SqlConnection? _respawnerConnection;
    private Respawner? _respawner;

    protected static string ConnectionString => MssqlTestConfiguration.DatabaseConnectionString;

    public static async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );

        var result = new Deploy.DatabaseDeploy().DeployDatabase(ConnectionString);
        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"SQL Server schema deploy failed: {failure.Error.Message}");
        }
    }

    [SetUp]
    public async Task SetupDatabase()
    {
        _respawnerConnection = await OpenConnectionAsync();

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
                    new("dmscs", "apiclient"),
                    new("dmscs", "apiclientdatastore"),
                    new("dmscs", "claimset"),
                    new("dmscs", "claimshierarchy"),
                    new("dmscs", "datastore"),
                    new("dmscs", "datastorecontext"),
                    new("dmscs", "openiddictapplication"),
                    new("dmscs", "openiddictapplicationscope"),
                    new("dmscs", "openiddictscope"),
                    new("dmscs", "openiddictrole"),
                    new("dmscs", "openiddictclientrole"),
                    new("dmscs", "openiddicttoken"),
                    new("dmscs", "openiddictkey"),
                ],
                DbAdapter = DbAdapter.SqlServer,
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
    }

    /// <summary>
    /// Helper method to clear claims-related tables
    /// </summary>
    protected static async Task ClearClaimsTablesAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM dmscs.ClaimsHierarchy");
        await connection.ExecuteAsync("DELETE FROM dmscs.ClaimSet");
    }

    /// <summary>
    /// Helper method to get row counts from claims tables
    /// </summary>
    protected static async Task<(int claimSetCount, int hierarchyCount)> GetClaimsTableCountsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        var claimSetCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dmscs.ClaimSet");
        var hierarchyCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.ClaimsHierarchy"
        );
        return (claimSetCount, hierarchyCount);
    }

    /// <summary>
    /// Helper method to verify if claims tables are empty
    /// </summary>
    protected static async Task<bool> AreClaimsTablesEmptyAsync()
    {
        var (claimSetCount, hierarchyCount) = await GetClaimsTableCountsAsync();
        return claimSetCount == 0 && hierarchyCount == 0;
    }

    /// <summary>
    /// Helper method to get all claim set names
    /// </summary>
    protected static async Task<IEnumerable<string>> GetClaimSetNamesAsync()
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.QueryAsync<string>(
            "SELECT ClaimSetName FROM dmscs.ClaimSet ORDER BY ClaimSetName"
        );
    }

    /// <summary>
    /// Helper method to check if a specific claim set exists
    /// </summary>
    protected static async Task<bool> ClaimSetExistsAsync(string claimSetName)
    {
        await using var connection = await OpenConnectionAsync();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.ClaimSet WHERE ClaimSetName = @claimSetName",
            new { claimSetName }
        );
        return count > 0;
    }
}
