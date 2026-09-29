// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// An isolated database for the DMS-1437 upgrade fixtures. Every deploy is bounded by script number
/// through <see cref="Deploy.DatabaseDeploy.ScriptFilter"/>, so a fixture's exact journal assertions
/// describe the migrations it names and are not changed by scripts added later.
/// </summary>
internal sealed class JobUpgradeTestDatabase
{
    private const string ScriptsSegment = ".Deploy.Scripts.";
    private const string ScriptNamePrefix = "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.";

    public const int PreTicketScript = 32;
    public const int JobScheduleScript = 33;
    public const int JobScript = 34;

    public const string JobScheduleScriptName = ScriptNamePrefix + "0033_Create_JobSchedule_Table.sql";
    public const string JobScriptName = ScriptNamePrefix + "0034_Create_Job_Table.sql";

    private readonly string _databaseName;

    public JobUpgradeTestDatabase()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = $"dms1437_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.Database!;
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
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<string>(
                """SELECT scriptname FROM public."dmscs_SchemaVersions" ORDER BY schemaversionsid;"""
            )
        ).ToArray();
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'dmscs' AND table_name = @TableName);
            """,
            new { TableName = tableName }
        );
    }

    /// <summary>The names of the constraints and indexes on a dmscs table.</summary>
    public async Task<string[]> ConstraintAndIndexNamesAsync(string tableName)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<string>(
                """
                SELECT conname FROM pg_constraint
                WHERE conrelid = format('"dmscs".%I', @TableName)::regclass
                UNION
                SELECT indexname FROM pg_indexes
                WHERE schemaname = 'dmscs' AND tablename = @TableName
                ORDER BY 1;
                """,
                new { TableName = tableName }
            )
        ).ToArray();
    }

    public async Task DropAsync()
    {
        NpgsqlConnectionStringBuilder maintenance = new(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        )
        {
            Database = "postgres",
            Pooling = false,
        };

        await using NpgsqlConnection connection = new(maintenance.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE);""");
    }
}
