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
/// key lookups, the descriptor catalog (loaded first so student rows can reference it),
/// chunked document-then-student-then-child-collection inserts, identity reseed, statistics
/// refresh, and the analytic verification queries. Any verification mismatch throws with
/// every mismatch listed.
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

        IReadOnlyDictionary<string, int> descriptorIds = await LoadDescriptorsAsync(
            connection,
            provider,
            definition
        );
        IReadOnlyList<(string Name, object Value)> descriptorParameters = DescriptorParameters(descriptorIds)
            .Select(parameter => (parameter.Name, (object)parameter.Value))
            .ToArray();

        string documentInsertSql =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.DocumentInsertSql
                : MssqlPerfFixtureLoaderSql.DocumentInsertSql;
        string studentInsertSql =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.StudentInsertSql
                : MssqlPerfFixtureLoaderSql.StudentInsertSql;
        IReadOnlyList<string> childInsertSqls =
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFixtureLoaderSql.ChildCollectionInsertSqls
                : MssqlPerfFixtureLoaderSql.ChildCollectionInsertSqls;

        foreach ((long from, long to) in Chunks(definition.RowCount, chunkSize))
        {
            await ExecuteRangeInsertAsync(
                connection,
                documentInsertSql,
                from,
                to,
                [(PerfFixtureLoaderParameters.ResourceKeyId, resourceKeyId)]
            );
            await ExecuteRangeInsertAsync(connection, studentInsertSql, from, to, descriptorParameters);
            foreach (string childInsertSql in childInsertSqls)
            {
                await ExecuteRangeInsertAsync(connection, childInsertSql, from, to, descriptorParameters);
            }
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

    /// <summary>
    /// The descriptor-id parameter values student and child inserts bind, keyed by the
    /// loader parameter names. Values are native compact keys returned by the descriptor inserts.
    /// </summary>
    public static IReadOnlyList<(string Name, int Value)> DescriptorParameters(
        IReadOnlyDictionary<string, int> descriptorIds
    ) =>
        [
            (
                PerfFixtureLoaderParameters.BirthSexDescriptorId,
                descriptorIds[PerfFixtureDefinition.SexDescriptorResource]
            ),
            (
                PerfFixtureLoaderParameters.OtherNameTypeDescriptorId,
                descriptorIds[PerfFixtureDefinition.OtherNameTypeDescriptorResource]
            ),
            (
                PerfFixtureLoaderParameters.IdentificationDocumentUseDescriptorId,
                descriptorIds[PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource]
            ),
            (
                PerfFixtureLoaderParameters.PersonalInformationVerificationDescriptorId,
                descriptorIds[PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource]
            ),
            (
                PerfFixtureLoaderParameters.VisaDescriptorId,
                descriptorIds[PerfFixtureDefinition.VisaDescriptorResource]
            ),
        ];

    private static async Task<IReadOnlyDictionary<string, int>> LoadDescriptorsAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfFixtureDefinition definition
    )
    {
        Dictionary<string, int> descriptorIds = new(StringComparer.Ordinal);
        foreach (string resourceName in PerfFixtureDefinition.DescriptorResourceNames)
        {
            long descriptorResourceKeyId = await ExecuteScalarAsync(
                connection,
                provider == PerfProvider.Postgresql
                    ? PgsqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(resourceName)
                    : MssqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(resourceName)
            );
            long documentId = definition.DescriptorDocumentIdFor(resourceName);

            await using (
                DbCommand documentInsert = CreateCommand(
                    connection,
                    provider == PerfProvider.Postgresql
                        ? PgsqlPerfFixtureLoaderSql.DescriptorDocumentInsertSql
                        : MssqlPerfFixtureLoaderSql.DescriptorDocumentInsertSql
                )
            )
            {
                AddParameter(documentInsert, PerfFixtureLoaderParameters.DescriptorDocumentId, documentId);
                AddObjectParameter(
                    documentInsert,
                    PerfFixtureLoaderParameters.DescriptorDocumentUuid,
                    definition.DescriptorDocumentUuidFor(resourceName)
                );
                AddParameter(
                    documentInsert,
                    PerfFixtureLoaderParameters.ResourceKeyId,
                    descriptorResourceKeyId
                );
                await documentInsert.ExecuteNonQueryAsync();
            }

            await using (
                DbCommand descriptorInsert = CreateCommand(
                    connection,
                    provider == PerfProvider.Postgresql
                        ? PgsqlPerfFixtureLoaderSql.DescriptorInsertSql(resourceName)
                        : MssqlPerfFixtureLoaderSql.DescriptorInsertSql(resourceName)
                )
            )
            {
                AddParameter(descriptorInsert, PerfFixtureLoaderParameters.DescriptorDocumentId, documentId);
                AddParameter(
                    descriptorInsert,
                    PerfFixtureLoaderParameters.ResourceKeyId,
                    descriptorResourceKeyId
                );
                descriptorIds.Add(
                    resourceName,
                    await PerfSeederDatabase.ExecuteDescriptorInsertAsync(descriptorInsert)
                );
            }

            await using DbCommand referentialInsert = CreateCommand(
                connection,
                provider == PerfProvider.Postgresql
                    ? PgsqlPerfFixtureLoaderSql.DescriptorReferentialIdentityInsertSql
                    : MssqlPerfFixtureLoaderSql.DescriptorReferentialIdentityInsertSql
            );
            AddObjectParameter(
                referentialInsert,
                PerfFixtureLoaderParameters.DescriptorReferentialId,
                ReferentialIdentityDerivation.DescriptorReferentialId(
                    resourceName,
                    PerfFixtureDefinition.DescriptorUriFor(resourceName)
                )
            );
            AddParameter(referentialInsert, PerfFixtureLoaderParameters.DescriptorDocumentId, documentId);
            AddParameter(
                referentialInsert,
                PerfFixtureLoaderParameters.ResourceKeyId,
                descriptorResourceKeyId
            );
            await referentialInsert.ExecuteNonQueryAsync();
        }

        return descriptorIds;
    }

    private static async Task ExecuteRangeInsertAsync(
        DbConnection connection,
        string sql,
        long fromOrdinal,
        long toOrdinal,
        IReadOnlyList<(string Name, object Value)> extraParameters
    )
    {
        await using DbCommand command = CreateCommand(connection, sql);
        AddParameter(command, PerfFixtureLoaderParameters.FromOrdinal, fromOrdinal);
        AddParameter(command, PerfFixtureLoaderParameters.ToOrdinal, toOrdinal);
        foreach ((string name, object value) in extraParameters)
        {
            // Bind only the parameters the statement references; an unreferenced named
            // parameter is a driver error on PostgreSQL.
            if (sql.Contains("@" + name, StringComparison.Ordinal))
            {
                AddObjectParameter(command, name, value);
            }
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

    private static void AddObjectParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
