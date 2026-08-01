// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    internal const string InitialPageSql = """
        SELECT
            work."DocumentId",
            work."RequiredContentVersion",
            work."FirstEnqueuedAt",
            work."LastEnqueuedAt"
        FROM "dms"."DocumentProjectionWork" AS work
        ORDER BY work."FirstEnqueuedAt", work."DocumentId"
        LIMIT @pageSize;
        """;

    internal const string CursorPageSql = """
        SELECT
            work."DocumentId",
            work."RequiredContentVersion",
            work."FirstEnqueuedAt",
            work."LastEnqueuedAt"
        FROM "dms"."DocumentProjectionWork" AS work
        WHERE (work."FirstEnqueuedAt", work."DocumentId") > (@lastFirstEnqueuedAt, @lastDocumentId)
        ORDER BY work."FirstEnqueuedAt", work."DocumentId"
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

        NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
        await using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
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
            reader.GetInt64(reader.GetOrdinal("DocumentId")),
            reader.GetInt64(reader.GetOrdinal("RequiredContentVersion")),
            DocumentProjectionWorkPagingGuards.NormalizeUtcTimestamp(
                reader.GetValue(reader.GetOrdinal("FirstEnqueuedAt"))
            ),
            DocumentProjectionWorkPagingGuards.NormalizeUtcTimestamp(
                reader.GetValue(reader.GetOrdinal("LastEnqueuedAt"))
            )
        );
}
