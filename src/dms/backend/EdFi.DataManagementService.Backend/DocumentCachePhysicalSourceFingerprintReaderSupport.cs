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

internal sealed record DocumentCachePhysicalSourceFingerprintReaderQuery(
    string ExistsCommandText,
    string ReadSourceIdentityCommandText,
    string TableDisplayName,
    string SourceIdentityColumnName,
    RelationalProviderToken ProviderToken
);

internal static class DocumentCachePhysicalSourceFingerprintReaderSupport
{
    private static readonly DocumentCachePhysicalSourceFingerprintReaderQuery _pgsqlQuery = CreateQuery(
        SqlDialect.Pgsql,
        RelationalProviderToken.Postgresql
    );

    private static readonly DocumentCachePhysicalSourceFingerprintReaderQuery _mssqlQuery = CreateQuery(
        SqlDialect.Mssql,
        RelationalProviderToken.SqlServer
    );

    public static DocumentCachePhysicalSourceFingerprintReaderQuery GetQuery(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _pgsqlQuery,
            SqlDialect.Mssql => _mssqlQuery,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    public static async Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadFingerprintAsync(
        Func<DbConnection> connectionFactory,
        DocumentCachePhysicalSourceFingerprintReaderQuery query,
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
                logger.LogDebug("{TableDisplayName} table does not exist", query.TableDisplayName);
                return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                    DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentityMissing,
                    "dms.DataStoreIdentity is missing."
                );
            }

            await using var command = connection.CreateCommand();
            command.CommandText = query.ReadSourceIdentityCommandText;

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ReadSourceIdentityAsync(reader, query).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable,
                "dms.DataStoreIdentity.SourceIdentity is unreadable."
            );
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string existsCommandText,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = existsCommandText;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadSourceIdentityAsync(
        DbDataReader reader,
        DocumentCachePhysicalSourceFingerprintReaderQuery query
    )
    {
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentitySingletonMissing,
                "dms.DataStoreIdentity singleton row is missing."
            );
        }

        string? sourceIdentityText = ReadOptionalString(reader, query.SourceIdentityColumnName);

        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable,
                "dms.DataStoreIdentity must contain exactly one singleton row."
            );
        }

        if (string.IsNullOrWhiteSpace(sourceIdentityText))
        {
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable,
                "dms.DataStoreIdentity.SourceIdentity is unreadable."
            );
        }

        if (!Guid.TryParseExact(sourceIdentityText, "D", out Guid sourceIdentity))
        {
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed,
                "dms.DataStoreIdentity.SourceIdentity is malformed."
            );
        }

        if (sourceIdentity == Guid.Empty)
        {
            return DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityAllZero,
                "dms.DataStoreIdentity.SourceIdentity is the zero UUID."
            );
        }

        return DocumentCachePhysicalSourceFingerprintReadResult.Success(
            DocumentCachePhysicalSourceFingerprintCalculator.Compute(query.ProviderToken, sourceIdentity)
        );
    }

    private static string? ReadOptionalString(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetString(ordinal);
    }

    private static DocumentCachePhysicalSourceFingerprintReaderQuery CreateQuery(
        SqlDialect dialect,
        RelationalProviderToken providerToken
    ) =>
        new(
            DataStoreIdentityTableDefinition.RenderExistsCommandText(dialect),
            DataStoreIdentityTableDefinition.RenderReadSourceIdentityCommandText(dialect),
            DataStoreIdentityTableDefinition.TableDisplayName,
            DataStoreIdentityTableDefinition.SourceIdentity.Value,
            providerToken
        );
}
