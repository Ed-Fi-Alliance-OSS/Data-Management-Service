// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Database plumbing shared by the final-gate seeding executors: command creation with the
/// loader's long timeout, parameter binding, scalar/non-query execution, chunked range
/// execution, and analytic verification that throws with every mismatch listed.
/// </summary>
internal static class PerfSeederDatabase
{
    private const int CommandTimeoutSeconds = 600;

    public static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = CreateCommand(connection, sql);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> ExecuteScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = CreateCommand(connection, sql);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull
            ? throw new PerfFixtureLoadException([$"Scalar query returned no value: {sql}"])
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Executes one range-parameterized statement, binding the chunk bounds plus any extra
    /// parameters the statement text actually references — an unreferenced named parameter
    /// is a driver error on PostgreSQL.
    /// </summary>
    public static async Task ExecuteRangeAsync(
        DbConnection connection,
        string sql,
        long fromOrdinal,
        long toOrdinal,
        IReadOnlyList<(string Name, long Value)> extraParameters
    )
    {
        await using DbCommand command = CreateCommand(connection, sql);
        AddParameter(command, PerfFixtureLoaderParameters.FromOrdinal, fromOrdinal);
        AddParameter(command, PerfFixtureLoaderParameters.ToOrdinal, toOrdinal);
        foreach ((string name, long value) in extraParameters)
        {
            if (sql.Contains("@" + name, StringComparison.Ordinal))
            {
                AddParameter(command, name, value);
            }
        }

        await command.ExecuteNonQueryAsync();
    }

    public static async Task VerifyAsync(
        DbConnection connection,
        IReadOnlyList<PerfVerificationQuery> queries
    )
    {
        List<string> mismatches = [];
        foreach (PerfVerificationQuery query in queries)
        {
            long actual = await ExecuteScalarAsync(connection, query.Sql);
            if (actual != query.ExpectedValue)
            {
                mismatches.Add($"{query.Name}: expected {query.ExpectedValue}, got {actual}.");
            }
        }

        if (mismatches.Count > 0)
        {
            throw new PerfFixtureLoadException(mismatches);
        }
    }

    public static DbCommand CreateCommand(DbConnection connection, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        return command;
    }

    public static void AddParameter(DbCommand command, string name, long value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public static void AddObjectParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
