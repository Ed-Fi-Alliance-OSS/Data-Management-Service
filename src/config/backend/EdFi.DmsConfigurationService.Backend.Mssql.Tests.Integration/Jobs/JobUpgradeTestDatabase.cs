// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

/// <summary>
/// An isolated database for the DMS-1437 upgrade fixtures. Every deploy is bounded by script number
/// through <see cref="Deploy.DatabaseDeploy.ScriptFilter"/>, so a fixture's exact journal assertions
/// describe the migrations it names and are not changed by scripts added later.
/// </summary>
internal sealed class JobUpgradeTestDatabase
{
    private const string ScriptsSegment = ".Deploy.Scripts.";
    private const string ScriptNamePrefix = "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.";

    public const int PreTicketScript = 31;
    public const int JobScheduleScript = 32;
    public const int JobScript = 33;

    public const string JobScheduleScriptName = ScriptNamePrefix + "0032_Create_JobSchedule_Table.sql";
    public const string JobScriptName = ScriptNamePrefix + "0033_Create_Job_Table.sql";

    private readonly string _databaseName;

    public JobUpgradeTestDatabase()
    {
        SqlConnectionStringBuilder builder = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = $"dms1437_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.InitialCatalog;
        ConnectionString = builder.ConnectionString;
    }

    public string ConnectionString { get; }

    public void DeployThrough(int lastScript)
    {
        DatabaseDeployResult result = new Deploy.DatabaseDeploy
        {
            ScriptFilter = scriptName => ScriptNumber(scriptName) <= lastScript,
        }.DeployDatabase(ConnectionString);

        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"Database deploy through script {lastScript} failed: {failure.Error}");
        }
    }

    public static int ScriptNumber(string scriptName)
    {
        int start = scriptName.IndexOf(ScriptsSegment, StringComparison.Ordinal) + ScriptsSegment.Length;
        return int.Parse(scriptName.AsSpan(start, 4), CultureInfo.InvariantCulture);
    }

    public async Task<string[]> JournalAsync()
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<string>(
                "SELECT ScriptName FROM dbo.dmscs_SchemaVersions ORDER BY Id;"
            )
        ).ToArray();
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT CAST(CASE WHEN OBJECT_ID(N'dmscs.' + @TableName, N'U') IS NULL THEN 0 ELSE 1 END AS bit);",
            new { TableName = tableName }
        );
    }

    /// <summary>
    /// The names of the key, foreign key, and check constraints and the indexes on a dmscs table.
    /// </summary>
    public async Task<string[]> ConstraintAndIndexNamesAsync(string tableName)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<string>(
                """
                SELECT name FROM sys.objects
                WHERE parent_object_id = OBJECT_ID(N'dmscs.' + @TableName) AND type IN ('PK', 'UQ', 'F', 'C')
                UNION
                SELECT name FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dmscs.' + @TableName) AND name IS NOT NULL
                ORDER BY 1;
                """,
                new { TableName = tableName }
            )
        ).ToArray();
    }

    public async Task DropAsync()
    {
        SqlConnectionStringBuilder master = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            IF DB_ID('{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END;
            """
        );
    }
}
