// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Executes the per-dialect loader SQL against a live database: guard (SQL Server), resource
/// key lookup, chunked document-then-student inserts, identity reseed, statistics refresh,
/// and the analytic verification queries. Any verification mismatch throws with every
/// mismatch listed.
/// </summary>
public static class PerfFixtureLoader
{
    public const long DefaultChunkSize = 50_000;

    private const int CommandTimeoutSeconds = 600;

    public static async Task LoadAndVerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfFixtureDefinition definition,
        long chunkSize = DefaultChunkSize
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        if (provider == PerfProvider.Mssql)
        {
            long generateSeriesAvailable = await ExecuteScalarAsync(
                connection,
                MssqlPerfFixtureLoaderSql.GenerateSeriesGuardSql
            );
            if (generateSeriesAvailable != 1)
            {
                throw new PerfFixtureLoadException([
                    "GENERATE_SERIES is unavailable: SQL Server "
                        + $"{MssqlPerfFixtureLoaderSql.MinimumProductMajorVersion}+ with database "
                        + $"compatibility level {MssqlPerfFixtureLoaderSql.MinimumCompatibilityLevel}+ "
                        + "is required.",
                ]);
            }
        }

        long resourceKeyId = await ExecuteScalarAsync(
            connection,
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.ResourceKeyLookupSql
                : MssqlPerfFixtureLoaderSql.ResourceKeyLookupSql
        );

        string documentInsertSql =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.DocumentInsertSql
                : MssqlPerfFixtureLoaderSql.DocumentInsertSql;
        string studentInsertSql =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.StudentInsertSql
                : MssqlPerfFixtureLoaderSql.StudentInsertSql;

        foreach ((long from, long to) in Chunks(definition.RowCount, chunkSize))
        {
            await ExecuteInsertAsync(connection, documentInsertSql, from, to, resourceKeyId);
            await ExecuteInsertAsync(connection, studentInsertSql, from, to, resourceKeyId: null);
        }

        await ExecuteNonQueryAsync(
            connection,
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.ReseedSql(definition)
                : MssqlPerfFixtureLoaderSql.ReseedSql(definition)
        );

        IReadOnlyList<string> statisticsSqls =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.StatisticsRefreshSqls
                : MssqlPerfFixtureLoaderSql.StatisticsRefreshSqls;
        foreach (string statisticsSql in statisticsSqls)
        {
            await ExecuteNonQueryAsync(connection, statisticsSql);
        }

        await VerifyAsync(connection, provider, definition);
    }

    public static async Task VerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfFixtureDefinition definition
    )
    {
        IReadOnlyList<PerfVerificationQuery> queries =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.VerificationQueries(definition)
                : MssqlPerfFixtureLoaderSql.VerificationQueries(definition);

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

    public static IEnumerable<(long From, long To)> Chunks(long rowCount, long chunkSize)
    {
        for (long from = 1; from <= rowCount; from += chunkSize)
        {
            yield return (from, Math.Min(from + chunkSize - 1, rowCount));
        }
    }

    private static async Task ExecuteInsertAsync(
        DbConnection connection,
        string sql,
        long fromOrdinal,
        long toOrdinal,
        long? resourceKeyId
    )
    {
        await using DbCommand command = CreateCommand(connection, sql);
        AddParameter(command, PerfFixtureLoaderParameters.FromOrdinal, fromOrdinal);
        AddParameter(command, PerfFixtureLoaderParameters.ToOrdinal, toOrdinal);
        if (resourceKeyId is not null)
        {
            AddParameter(command, PerfFixtureLoaderParameters.ResourceKeyId, resourceKeyId.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = CreateCommand(connection, sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = CreateCommand(connection, sql);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull
            ? throw new PerfFixtureLoadException([$"Scalar query returned no value: {sql}"])
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, long value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
