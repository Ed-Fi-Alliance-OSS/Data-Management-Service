// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DocumentCacheLifecycleReaderQuery(
    string ExistsCommandText,
    string ReadLifecycleCommandText,
    string LifecycleColumnName,
    string CacheAheadRecoveryRequiredColumnName,
    RelationalProviderToken ProviderToken
);

internal static class DocumentCacheLifecycleReaderSupport
{
    private static readonly DocumentCacheLifecycleReaderQuery _pgsqlQuery = CreateQuery(
        SqlDialect.Pgsql,
        RelationalProviderToken.Postgresql
    );

    private static readonly DocumentCacheLifecycleReaderQuery _mssqlQuery = CreateQuery(
        SqlDialect.Mssql,
        RelationalProviderToken.SqlServer
    );

    public static DocumentCacheLifecycleReaderQuery GetQuery(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _pgsqlQuery,
            SqlDialect.Mssql => _mssqlQuery,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    public static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        Func<DbConnection> connectionFactory,
        DocumentCacheLifecycleReaderQuery query,
        ILogger logger,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var connection = connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (!await TableExistsAsync(connection, query.ExistsCommandText, cancellationToken))
            {
                return DocumentCacheLifecycleReadResult.Failure(
                    DocumentCacheLifecycleReadStatus.Missing,
                    "dms.DocumentCacheState is missing."
                );
            }

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = query.ReadLifecycleCommandText;

            await using DbDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ReadLifecycleAsync(reader, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLifecycleReadFailure(logger, exception);
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "dms.DocumentCacheState is unreadable."
            );
        }
    }

    private static void LogLifecycleReadFailure(ILogger logger, Exception exception)
    {
        logger.LogDebug(
            "DocumentCache lifecycle read failed while reading provider metadata for category {FailureCategory}; exception type {ExceptionType}",
            DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
            exception.GetType().Name
        );
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        DbDataReader reader,
        DocumentCacheLifecycleReaderQuery query,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "dms.DocumentCacheState singleton row is missing."
            );
        }

        string? lifecycleText = ReadOptionalString(reader, query.LifecycleColumnName);
        bool? cacheAheadRecoveryRequired = ReadOptionalBoolean(
            reader,
            query.CacheAheadRecoveryRequiredColumnName
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "dms.DocumentCacheState must contain exactly one singleton row."
            );
        }

        if (
            lifecycleText is null
            || cacheAheadRecoveryRequired is null
            || !TryParseLifecycle(lifecycleText, out DocumentCacheLifecycleState lifecycle)
        )
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "dms.DocumentCacheState lifecycle row is invalid."
            );
        }

        return DocumentCacheLifecycleReadResult.Success(
            new DocumentCacheLifecycleObservation(lifecycle, cacheAheadRecoveryRequired.Value)
        );
    }

    private static bool TryParseLifecycle(string lifecycleText, out DocumentCacheLifecycleState lifecycle)
    {
        switch (lifecycleText)
        {
            case nameof(DocumentCacheLifecycleState.Disabled):
                lifecycle = DocumentCacheLifecycleState.Disabled;
                return true;
            case nameof(DocumentCacheLifecycleState.Resetting):
                lifecycle = DocumentCacheLifecycleState.Resetting;
                return true;
            case nameof(DocumentCacheLifecycleState.Rebuilding):
                lifecycle = DocumentCacheLifecycleState.Rebuilding;
                return true;
            case nameof(DocumentCacheLifecycleState.Tracking):
                lifecycle = DocumentCacheLifecycleState.Tracking;
                return true;
            default:
                lifecycle = default;
                return false;
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string existsCommandText,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = existsCommandText;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static string? ReadOptionalString(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? ReadOptionalBoolean(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static DocumentCacheLifecycleReaderQuery CreateQuery(
        SqlDialect dialect,
        RelationalProviderToken providerToken
    )
    {
        string lifecycleColumn = DocumentCacheInventoryDefinition
            .DocumentCacheStateColumns
            .ProjectionLifecycleState
            .Value;
        string cacheAheadRecoveryRequiredColumn = DocumentCacheInventoryDefinition
            .DocumentCacheStateColumns
            .CacheAheadRecoveryRequired
            .Value;

        return new DocumentCacheLifecycleReaderQuery(
            RenderExistsCommandText(dialect),
            RenderReadLifecycleCommandText(dialect),
            lifecycleColumn,
            cacheAheadRecoveryRequiredColumn,
            providerToken
        );
    }

    private static string RenderExistsCommandText(SqlDialect dialect)
    {
        var schemaLiteral = RenderSqlLiteral(DocumentCacheInventoryDefinition.DmsSchema.Value);
        var tableLiteral = RenderSqlLiteral(DocumentCacheInventoryDefinition.DocumentCacheState.Name);

        return dialect switch
        {
            SqlDialect.Pgsql =>
                $"SELECT 1 FROM information_schema.tables WHERE table_schema = {schemaLiteral} AND table_name = {tableLiteral}",
            SqlDialect.Mssql =>
                $"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = {schemaLiteral} AND TABLE_NAME = {tableLiteral}",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderReadLifecycleCommandText(SqlDialect dialect)
    {
        string qualifiedTable = SqlIdentifierQuoter.QuoteTableName(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheState
        );
        string stateIdColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId
        );
        string lifecycleColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState
        );
        string cacheAheadRecoveryRequiredColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired
        );

        return dialect switch
        {
            SqlDialect.Pgsql => $"SELECT {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}\n"
                + $"FROM {qualifiedTable}\n"
                + $"WHERE {stateIdColumn} = 1\n"
                + $"LIMIT 2",
            SqlDialect.Mssql => $"SELECT TOP (2) {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}\n"
                + $"FROM {qualifiedTable}\n"
                + $"WHERE {stateIdColumn} = 1",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderSqlLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
