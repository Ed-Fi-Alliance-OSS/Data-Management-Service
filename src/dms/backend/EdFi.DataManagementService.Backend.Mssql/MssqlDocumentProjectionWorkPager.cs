// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentProjectionWorkPager : IDocumentProjectionWorkPager
{
    private static readonly string _workAlias = SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, "work");
    private static readonly string _workTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Mssql,
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
            {_workAlias}.{_documentId},
            {_workAlias}.{_requiredContentVersion},
            {_workAlias}.{_firstEnqueuedAt},
            {_workAlias}.{_lastEnqueuedAt}
        FROM {_workTable} AS {_workAlias}
        ORDER BY {_workAlias}.{_firstEnqueuedAt}, {_workAlias}.{_documentId}
        OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY;
        """;

    internal static readonly string CursorPageSql = $"""
        SELECT
            {_workAlias}.{_documentId},
            {_workAlias}.{_requiredContentVersion},
            {_workAlias}.{_firstEnqueuedAt},
            {_workAlias}.{_lastEnqueuedAt}
        FROM {_workTable} AS {_workAlias}
        WHERE {_workAlias}.{_firstEnqueuedAt} > @lastFirstEnqueuedAt
           OR ({_workAlias}.{_firstEnqueuedAt} = @lastFirstEnqueuedAt AND {_workAlias}.{_documentId} > @lastDocumentId)
        ORDER BY {_workAlias}.{_firstEnqueuedAt}, {_workAlias}.{_documentId}
        OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY;
        """;

    private readonly Func<string, DbConnection> _createConnection;
    private readonly ILogger<MssqlDocumentProjectionWorkPager> _logger;

    public MssqlDocumentProjectionWorkPager(ILogger<MssqlDocumentProjectionWorkPager> logger)
        : this(connectionString => new SqlConnection(connectionString), logger) { }

    internal MssqlDocumentProjectionWorkPager(
        Func<string, DbConnection> createConnection,
        ILogger<MssqlDocumentProjectionWorkPager> logger
    )
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

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
            "Paging SQL Server DocumentProjectionWork for target {TargetKey} with cursor {HasCursor}.",
            LoggingSanitizer.SanitizeForLogging(request.TargetExecutionContext.TargetKey.ToString()),
            request.Cursor.HasValue
        );

        await using DbConnection connection = _createConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = request.Cursor.HasValue ? CursorPageSql : InitialPageSql;
        AddParameter(command, "@pageSize", request.PageSize, SqlDbType.Int);

        if (request.Cursor.HasValue)
        {
            AddParameter(
                command,
                "@lastFirstEnqueuedAt",
                request.Cursor.LastFirstEnqueuedAt!.Value.UtcDateTime,
                SqlDbType.DateTime2
            );
            AddParameter(command, "@lastDocumentId", request.Cursor.LastDocumentId!.Value, SqlDbType.BigInt);
        }

        List<DocumentProjectionWorkPageItem> items = [];
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return new DocumentProjectionWorkPage(items, request.PageSize);
    }

    private static void AddParameter(DbCommand command, string name, object value, SqlDbType sqlDbType)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        if (parameter is SqlParameter sqlParameter)
        {
            sqlParameter.SqlDbType = sqlDbType;
        }

        command.Parameters.Add(parameter);
    }

    private static DocumentProjectionWorkPageItem ReadItem(DbDataReader reader) =>
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
        SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, column);
}
