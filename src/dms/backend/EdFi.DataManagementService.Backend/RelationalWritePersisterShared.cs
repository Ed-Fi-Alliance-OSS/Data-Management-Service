// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared static infrastructure methods used by relational write persistence.
/// </summary>
internal static class RelationalWritePersisterShared
{
    // ─── Command building ────────────────────────────────────────────────

    public static RelationalCommand BuildRowCommand(
        TableWritePlan tableWritePlan,
        string sql,
        MergeTableRow row,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        List<RelationalParameter> parameters = [];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            var parameterName = NormalizeParameterName(
                tableWritePlan.ColumnBindings[bindingIndex].ParameterName
            );
            var parameterValue = ResolveParameterValue(
                tableWritePlan,
                row.Values[bindingIndex],
                rootDocumentId,
                reservedCollectionItemIds
            );

            parameters.Add(new RelationalParameter(parameterName, parameterValue));
        }

        return new RelationalCommand(sql, parameters);
    }

    public static RelationalCommand BuildRowCommandFromValues(
        TableWritePlan tableWritePlan,
        string sql,
        IReadOnlyList<FlattenedWriteValue> values,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        List<RelationalParameter> parameters = [];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            var parameterName = NormalizeParameterName(
                tableWritePlan.ColumnBindings[bindingIndex].ParameterName
            );
            var parameterValue = ResolveParameterValue(
                tableWritePlan,
                values[bindingIndex],
                rootDocumentId,
                reservedCollectionItemIds
            );

            parameters.Add(new RelationalParameter(parameterName, parameterValue));
        }

        return new RelationalCommand(sql, parameters);
    }

    public static RelationalCommand BuildBatchCommand(
        string sql,
        TableWritePlan tableWritePlan,
        IReadOnlyList<MergeTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        List<RelationalParameter> parameters = [];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
            {
                var parameterName = NormalizeParameterName(
                    WriteBatchSqlSupport.BuildBatchParameterName(
                        tableWritePlan.ColumnBindings[bindingIndex].ParameterName,
                        rowIndex
                    )
                );
                var parameterValue = ResolveParameterValue(
                    tableWritePlan,
                    rows[rowIndex].Values[bindingIndex],
                    rootDocumentId,
                    reservedCollectionItemIds
                );

                parameters.Add(new RelationalParameter(parameterName, parameterValue));
            }
        }

        return new RelationalCommand(sql, parameters);
    }

    // ─── Parameter resolution ────────────────────────────────────────────

    public static object? ResolveParameterValue(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue value,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        return value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue,
            FlattenedWriteValue.UnresolvedRootDocumentId => rootDocumentId,
            FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
                when reservedCollectionItemIds.TryGetValue(
                    unresolvedCollectionItemId,
                    out var reservedCollectionItemId
                ) => reservedCollectionItemId,
            FlattenedWriteValue.UnresolvedCollectionItemId => throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' still contains an unresolved CollectionItemId. "
                    + "CollectionItemId reservation must complete before this row can be written."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }

    public static string NormalizeParameterName(string parameterName)
    {
        return parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";
    }

    // ─── Execution ───────────────────────────────────────────────────────

    public static async Task ExecuteNonQueryAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await dbCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task ExecuteParameterizedBatchAsync(
        WritePlanBatchSqlEmitter batchSqlEmitter,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<MergeTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (rows.Count == 1)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(tableWritePlan, sql, rows[0], rootDocumentId, reservedCollectionItemIds),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildBatchCommand(
                    emitBatchSql(batchSqlEmitter, rows.Count),
                    tableWritePlan,
                    rows,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static async Task ExecuteParameterizedBatchesAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<MergeTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(dialect);

        foreach (var updateBatch in rows.Chunk(tableWritePlan.BulkInsertBatching.MaxRowsPerBatch))
        {
            await ExecuteParameterizedBatchAsync(
                    batchSqlEmitter,
                    tableWritePlan,
                    sql,
                    emitBatchSql,
                    updateBatch,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    public static async Task ExecuteCollectionInsertBatchAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        IReadOnlyList<MergeTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (rows.Count == 1)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableWritePlan,
                        tableWritePlan.InsertSql,
                        rows[0],
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildBatchCommand(
                    new WritePlanBatchSqlEmitter(dialect).EmitInsertBatch(tableWritePlan, rows.Count),
                    tableWritePlan,
                    rows,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    // ─── Temporary ordinal rows for safe multi-row reorders ──────────────

    public static IReadOnlyList<MergeTableRow> CreateTemporaryOrdinalRows(
        IReadOnlyList<MergeTableRow> rows,
        int ordinalBindingIndex
    )
    {
        MergeTableRow[] temporaryRows = new MergeTableRow[rows.Count];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var temporaryValues = rows[rowIndex].Values.ToArray();
            // Persisted ordinals are non-negative contiguous values. Moving every row into
            // negative space first avoids transient collisions while a multi-row reorder
            // is applied in two passes.
            temporaryValues[ordinalBindingIndex] = new FlattenedWriteValue.Literal(-1 - rowIndex);

            temporaryRows[rowIndex] = new MergeTableRow(temporaryValues, rows[rowIndex].ComparableValues);
        }

        return temporaryRows;
    }

    // ─── Root DocumentId resolution ──────────────────────────────────────

    public static async Task<long> ResolveRootDocumentIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteTargetContext targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        return targetContext switch
        {
            RelationalWriteTargetContext.CreateNew(var documentUuid) => await InsertDocumentAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false),
            RelationalWriteTargetContext.ExistingDocument(var documentId, _, _) => documentId,
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };
    }

    public static async Task<long> InsertDocumentAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var command = BuildInsertDocumentCommand(mappingSet.Key.Dialect, documentUuid, resourceKeyId);

        await using var dbCommand = writeSession.CreateCommand(command);
        var scalarResult = await dbCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Document insert for resource '{RelationalWriteSupport.FormatResource(resource)}' did not return a DocumentId."
            );
        }

        return Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture);
    }

    public static RelationalCommand BuildInsertDocumentCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId";
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                VALUES (@documentUuid, @resourceKeyId);
                SELECT SCOPE_IDENTITY();
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    // ─── CollectionItemId reservation ────────────────────────────────────

    public static async Task ReserveCollectionItemIdAsync(
        SqlDialect dialect,
        FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId,
        IDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (reservedCollectionItemIds.ContainsKey(unresolvedCollectionItemId))
        {
            return;
        }

        var command = BuildReserveCollectionItemIdCommand(dialect);

        await using var dbCommand = writeSession.CreateCommand(command);
        var scalarResult = await dbCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            throw new InvalidOperationException(
                "CollectionItemId reservation did not return a value from dms.CollectionItemIdSequence."
            );
        }

        reservedCollectionItemIds.Add(
            unresolvedCollectionItemId,
            Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture)
        );
    }

    public static async Task ReserveCollectionItemIdsAsync(
        SqlDialect dialect,
        IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds,
        IDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(unresolvedCollectionItemIds);

        if (unresolvedCollectionItemIds.Count == 0)
        {
            return;
        }

        var missingCollectionItemIds = unresolvedCollectionItemIds
            .Where(unresolvedCollectionItemId =>
                !reservedCollectionItemIds.ContainsKey(unresolvedCollectionItemId)
            )
            .ToArray();

        if (missingCollectionItemIds.Length == 0)
        {
            return;
        }

        if (missingCollectionItemIds.Length == 1)
        {
            await ReserveCollectionItemIdAsync(
                    dialect,
                    missingCollectionItemIds[0],
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await using var dbCommand = writeSession.CreateCommand(
            BuildReserveCollectionItemIdsCommand(dialect, missingCollectionItemIds.Length)
        );
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var reservedValuesInOrder = await ReadReservedCollectionItemIdsAsync(
                reader,
                missingCollectionItemIds.Length,
                cancellationToken
            )
            .ConfigureAwait(false);

        for (var index = 0; index < missingCollectionItemIds.Length; index++)
        {
            reservedCollectionItemIds.Add(missingCollectionItemIds[index], reservedValuesInOrder[index]);
        }
    }

    public static RelationalCommand BuildReserveCollectionItemIdCommand(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT nextval('"dms"."CollectionItemIdSequence"');
                """,
                []
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT NEXT VALUE FOR [dms].[CollectionItemIdSequence];
                """,
                []
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    public static RelationalCommand BuildReserveCollectionItemIdsCommand(SqlDialect dialect, int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                "Reservation count must be at least 1."
            );
        }

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    series."Ordinal" AS "Ordinal",
                    nextval('"dms"."CollectionItemIdSequence"') AS "CollectionItemId"
                FROM generate_series(1, @count) AS series("Ordinal");
                """,
                [new RelationalParameter("@count", count)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                WITH [sequence_request] ([Ordinal]) AS (
                    SELECT 1
                    UNION ALL
                    SELECT [Ordinal] + 1
                    FROM [sequence_request]
                    WHERE [Ordinal] < @count
                )
                SELECT
                    [sequence_request].[Ordinal] AS [Ordinal],
                    NEXT VALUE FOR [dms].[CollectionItemIdSequence] OVER (ORDER BY [sequence_request].[Ordinal]) AS [CollectionItemId]
                FROM [sequence_request]
                OPTION (MAXRECURSION 0);
                """,
                [new RelationalParameter("@count", count)]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    public static async Task<long[]> ReadReservedCollectionItemIdsAsync(
        DbDataReader reader,
        int expectedCount,
        CancellationToken cancellationToken
    )
    {
        var ordinalColumnOrdinal = reader.GetOrdinal("Ordinal");
        var collectionItemIdColumnOrdinal = reader.GetOrdinal("CollectionItemId");
        var reservedCollectionItemIds = new long[expectedCount];
        var assignedOrdinals = new bool[expectedCount];
        var rowCount = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ordinal = await reader
                .GetFieldValueAsync<int>(ordinalColumnOrdinal, cancellationToken)
                .ConfigureAwait(false);

            if (ordinal < 1 || ordinal > expectedCount)
            {
                throw new InvalidOperationException(
                    $"CollectionItemId reservation returned an out-of-range ordinal value ({ordinal}) for batch size {expectedCount}."
                );
            }

            var index = ordinal - 1;

            if (assignedOrdinals[index])
            {
                throw new InvalidOperationException(
                    $"CollectionItemId reservation returned duplicate ordinal value {ordinal}."
                );
            }

            reservedCollectionItemIds[index] = await reader
                .GetFieldValueAsync<long>(collectionItemIdColumnOrdinal, cancellationToken)
                .ConfigureAwait(false);
            assignedOrdinals[index] = true;
            rowCount++;
        }

        if (rowCount != expectedCount || Array.Exists(assignedOrdinals, static assigned => !assigned))
        {
            throw new InvalidOperationException(
                $"CollectionItemId reservation returned {rowCount} rows for requested batch size {expectedCount}."
            );
        }

        return reservedCollectionItemIds;
    }

    // ─── Unresolved CollectionItemId extraction ──────────────────────────

    public static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetUnresolvedCollectionItemIds(
        IReadOnlyList<MergeTableRow> rows
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds = [];

        foreach (var row in rows)
        {
            unresolvedCollectionItemIds.AddRange(GetUnresolvedCollectionItemIds(row.Values));
        }

        return unresolvedCollectionItemIds;
    }

    public static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetUnresolvedCollectionItemIds(
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds = [];

        foreach (var value in values)
        {
            if (value is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId)
            {
                unresolvedCollectionItemIds.Add(unresolvedCollectionItemId);
            }
        }

        return unresolvedCollectionItemIds;
    }

    /// <summary>
    /// Scans value arrays for unresolved CollectionItemId references that have not yet been
    /// reserved, skipping the table's own self-reserved binding (if any). Used by both
    /// the no-profile and profile persisters to defer tables whose parent CollectionItemIds
    /// have not yet been allocated.
    /// </summary>
    public static bool HasBlockingUnresolvedCollectionItemIds(
        IEnumerable<ImmutableArray<FlattenedWriteValue>> valueArrays,
        int? selfReservedBindingIndex,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        foreach (var values in valueArrays)
        {
            foreach (
                var (value, bindingIndex) in values.Select(
                    static (value, bindingIndex) => (value, bindingIndex)
                )
            )
            {
                if (bindingIndex == selfReservedBindingIndex)
                {
                    continue;
                }

                if (
                    value is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
                    && !reservedCollectionItemIds.ContainsKey(unresolvedCollectionItemId)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ─── Formatting ─────────────────────────────────────────────────────

    public static string FormatTable(TableWritePlan tableWritePlan) =>
        RelationalWriteMergeShared.FormatTable(tableWritePlan);

    // ─── Physical row identity ──────────────────────────────────────────

    public static IReadOnlyDictionary<string, MergeTableRow> GetRowsByPhysicalIdentityOrThrow(
        IReadOnlyList<MergeTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    )
    {
        Dictionary<string, MergeTableRow> rowsByPhysicalIdentity = new(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableWritePlan, row);

            if (!rowsByPhysicalIdentity.TryAdd(physicalIdentity, row))
            {
                throw new InvalidOperationException(
                    $"Table '{FormatTable(tableWritePlan)}' produced duplicate {rowKind} rows for aligned scope physical identity '{physicalIdentity}'."
                );
            }
        }

        return rowsByPhysicalIdentity;
    }

    public static string ResolvePhysicalRowIdentityKey(TableWritePlan tableWritePlan, MergeTableRow row)
    {
        var identityColumns = tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns;

        if (identityColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' does not define physical row identity metadata."
            );
        }

        System.Text.StringBuilder builder = new();

        for (var index = 0; index < identityColumns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('|');
            }

            var bindingIndex = RelationalWriteMergeShared.FindBindingIndex(
                tableWritePlan,
                identityColumns[index]
            );
            builder.Append(identityColumns[index].Value);
            builder.Append('=');
            builder.Append(FormatPhysicalRowIdentityValue(row.Values[bindingIndex]));
        }

        return builder.ToString();
    }

    private static string FormatPhysicalRowIdentityValue(FlattenedWriteValue value)
    {
        return value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue is null
                ? "literal:<null>"
                : $"literal:{literalValue.GetType().FullName}:{literalValue}",
            FlattenedWriteValue.UnresolvedRootDocumentId => "document:<unresolved>",
            FlattenedWriteValue.UnresolvedCollectionItemId(var token) => $"collection:{token}",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }
}
