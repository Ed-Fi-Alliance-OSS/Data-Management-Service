// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Executes the descriptor fixture load against a freshly leased database: guard
/// (SQL Server), resource key lookup, chunked document/descriptor/referential-identity
/// inserts, identity reseed, statistics refresh, and the analytic verification queries. Any
/// verification mismatch throws with every mismatch listed.
/// </summary>
public static class PerfDescriptorFixtureLoader
{
    public static async Task LoadAndVerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfDescriptorFixtureDefinition definition,
        long chunkSize = PerfFixtureLoader.DefaultChunkSize
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        bool postgresql = provider == PerfProvider.Postgresql;

        if (!postgresql)
        {
            long generateSeriesAvailable = await PerfSeederDatabase.ExecuteScalarAsync(
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

        long resourceKeyId = await PerfSeederDatabase.ExecuteScalarAsync(
            connection,
            postgresql
                ? PgsqlPerfDescriptorFixtureLoaderSql.ResourceKeyLookupSql
                : MssqlPerfDescriptorFixtureLoaderSql.ResourceKeyLookupSql
        );

        IReadOnlyList<string> rangeInsertSqls = postgresql
            ?
            [
                PgsqlPerfDescriptorFixtureLoaderSql.DocumentInsertSql,
                PgsqlPerfDescriptorFixtureLoaderSql.DescriptorInsertSql,
                PgsqlPerfDescriptorFixtureLoaderSql.ReferentialIdentityInsertSql,
            ]
            :
            [
                MssqlPerfDescriptorFixtureLoaderSql.DocumentInsertSql,
                MssqlPerfDescriptorFixtureLoaderSql.DescriptorInsertSql,
                MssqlPerfDescriptorFixtureLoaderSql.ReferentialIdentityInsertSql,
            ];

        foreach ((long from, long to) in PerfFixtureLoader.Chunks(definition.RowCount, chunkSize))
        {
            foreach (string sql in rangeInsertSqls)
            {
                await PerfSeederDatabase.ExecuteRangeAsync(
                    connection,
                    sql,
                    from,
                    to,
                    [(PerfFixtureLoaderParameters.ResourceKeyId, resourceKeyId)]
                );
            }
        }

        await PerfSeederDatabase.ExecuteNonQueryAsync(
            connection,
            postgresql
                ? PgsqlPerfDescriptorFixtureLoaderSql.ReseedSql(definition)
                : MssqlPerfDescriptorFixtureLoaderSql.ReseedSql(definition)
        );

        IReadOnlyList<string> statisticsSqls = postgresql
            ? PgsqlPerfDescriptorFixtureLoaderSql.StatisticsRefreshSqls
            : MssqlPerfDescriptorFixtureLoaderSql.StatisticsRefreshSqls;
        foreach (string statisticsSql in statisticsSqls)
        {
            await PerfSeederDatabase.ExecuteNonQueryAsync(connection, statisticsSql);
        }

        await VerifyAsync(connection, provider, definition);
    }

    public static async Task VerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfDescriptorFixtureDefinition definition
    ) =>
        await PerfSeederDatabase.VerifyAsync(
            connection,
            provider == PerfProvider.Postgresql
                ? PgsqlPerfDescriptorFixtureLoaderSql.VerificationQueries(definition)
                : MssqlPerfDescriptorFixtureLoaderSql.VerificationQueries(definition)
        );
}
