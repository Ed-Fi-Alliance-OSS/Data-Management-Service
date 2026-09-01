// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentProjectionWorkPager(
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<PostgresqlDocumentProjectionWorkPager> logger
) : IDocumentProjectionWorkPager
{
    private const string WorkAlias = "work";

    private static readonly string _workTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Pgsql,
        DocumentCacheInventoryDefinition.DocumentProjectionWork
    );
    private static readonly DbColumnName _documentIdColumn = DocumentCacheInventoryDefinition
        .DocumentProjectionWorkColumns
        .DocumentId;
    private static readonly DbColumnName _requiredContentVersionColumn = DocumentCacheInventoryDefinition
        .DocumentProjectionWorkColumns
        .RequiredContentVersion;
    private static readonly DbColumnName _firstEnqueuedAtColumn = DocumentCacheInventoryDefinition
        .DocumentProjectionWorkColumns
        .FirstEnqueuedAt;
    private static readonly DbColumnName _lastEnqueuedAtColumn = DocumentCacheInventoryDefinition
        .DocumentProjectionWorkColumns
        .LastEnqueuedAt;
    private static readonly string _documentId = Quote(_documentIdColumn);
    private static readonly string _requiredContentVersion = Quote(_requiredContentVersionColumn);
    private static readonly string _firstEnqueuedAt = Quote(_firstEnqueuedAtColumn);
    private static readonly string _lastEnqueuedAt = Quote(_lastEnqueuedAtColumn);

    internal static readonly string InitialPageSql = $"""
        SELECT
            {WorkAlias}.{_documentId},
            {WorkAlias}.{_requiredContentVersion},
            {WorkAlias}.{_firstEnqueuedAt},
            {WorkAlias}.{_lastEnqueuedAt}
        FROM {_workTable} AS {WorkAlias}
        ORDER BY {WorkAlias}.{_firstEnqueuedAt}, {WorkAlias}.{_documentId}
        LIMIT @pageSize;
        """;

    internal static readonly string CursorPageSql = $"""
        SELECT
            {WorkAlias}.{_documentId},
            {WorkAlias}.{_requiredContentVersion},
            {WorkAlias}.{_firstEnqueuedAt},
            {WorkAlias}.{_lastEnqueuedAt}
        FROM {_workTable} AS {WorkAlias}
        WHERE ({WorkAlias}.{_firstEnqueuedAt}, {WorkAlias}.{_documentId}) > (@lastFirstEnqueuedAt, @lastDocumentId)
        ORDER BY {WorkAlias}.{_firstEnqueuedAt}, {WorkAlias}.{_documentId}
        LIMIT @pageSize;
        """;

    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly ILogger<PostgresqlDocumentProjectionWorkPager> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    public async Task<DocumentProjectionWorkPage> ReadPageAsync(
        DocumentProjectionWorkPageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        string connectionString = DocumentProjectionWorkPagingGuards.RequireConnectionString(
            request,
            ProviderToken
        );

        _logger.LogDebug(
            "Paging PostgreSQL DocumentProjectionWork for target {TargetKey} with cursor {HasCursor}.",
            LoggingSanitizer.SanitizeForLogging(request.TargetExecutionContext.TargetKey.ToString()),
            request.Cursor.HasValue
        );

        // Declared before the connection so the connection is disposed first.
        await using NpgsqlDataSourceLease lease = _dataSourceCache.AcquireLease(connectionString);
        await using NpgsqlConnection connection = await lease
            .DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using NpgsqlCommand command = connection.CreateCommand();

        command.CommandText = request.Cursor.HasValue ? CursorPageSql : InitialPageSql;
        command.Parameters.Add(
            new NpgsqlParameter("pageSize", NpgsqlDbType.Integer) { Value = request.PageSize }
        );

        if (request.Cursor.HasValue)
        {
            command.Parameters.Add(
                new NpgsqlParameter("lastFirstEnqueuedAt", NpgsqlDbType.TimestampTz)
                {
                    Value = request.Cursor.LastFirstEnqueuedAt!.Value,
                }
            );
            command.Parameters.Add(
                new NpgsqlParameter("lastDocumentId", NpgsqlDbType.Bigint)
                {
                    Value = request.Cursor.LastDocumentId!.Value,
                }
            );
        }

        List<DocumentProjectionWorkPageItem> items = [];
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return new DocumentProjectionWorkPage(items, request.PageSize);
    }

    private static DocumentProjectionWorkPageItem ReadItem(NpgsqlDataReader reader) =>
        new(
            reader.GetInt64(reader.GetOrdinal(_documentIdColumn.Value)),
            reader.GetInt64(reader.GetOrdinal(_requiredContentVersionColumn.Value)),
            DocumentProjectionWorkPagingGuards.NormalizeUtcTimestamp(
                reader.GetValue(reader.GetOrdinal(_firstEnqueuedAtColumn.Value))
            ),
            DocumentProjectionWorkPagingGuards.NormalizeUtcTimestamp(
                reader.GetValue(reader.GetOrdinal(_lastEnqueuedAtColumn.Value))
            )
        );

    private static string Quote(DbColumnName column) =>
        SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Pgsql, column);
}
