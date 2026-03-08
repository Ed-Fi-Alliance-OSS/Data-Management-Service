// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Shared lookup helpers for deterministic projection metadata compilation and validation.
/// </summary>
internal static class ProjectionMetadataResolver
{
    public static IReadOnlyDictionary<DbColumnName, int> BuildHydrationColumnOrdinalMapOrThrow(
        DbTableModel tableModel,
        Func<DbColumnName, Exception> createDuplicateColumnException
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentNullException.ThrowIfNull(createDuplicateColumnException);

        Dictionary<DbColumnName, int> columnOrdinalByName = [];

        for (var index = 0; index < tableModel.Columns.Count; index++)
        {
            var columnName = tableModel.Columns[index].ColumnName;

            if (columnOrdinalByName.TryAdd(columnName, index))
            {
                continue;
            }

            throw createDuplicateColumnException(columnName);
        }

        return columnOrdinalByName;
    }

    public static DbTableModel ResolveTableModelOrThrow(
        DbTableName table,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        Func<DbTableName, Exception> createMissingTableException
    )
    {
        ArgumentNullException.ThrowIfNull(tablesByName);
        ArgumentNullException.ThrowIfNull(createMissingTableException);

        return ResolveDictionaryValueOrThrow(tablesByName, table, createMissingTableException);
    }

    public static TableReadPlan ResolveHydrationTablePlanOrThrow(
        DbTableName table,
        IReadOnlyDictionary<DbTableName, TableReadPlan> tablePlansByTable,
        Func<DbTableName, Exception> createMissingTableException
    )
    {
        ArgumentNullException.ThrowIfNull(tablePlansByTable);
        ArgumentNullException.ThrowIfNull(createMissingTableException);

        return ResolveDictionaryValueOrThrow(tablePlansByTable, table, createMissingTableException);
    }

    public static IReadOnlyDictionary<DbColumnName, int> ResolveHydrationColumnOrdinalsOrThrow(
        DbTableName table,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable,
        Func<DbTableName, Exception> createMissingTableException
    )
    {
        ArgumentNullException.ThrowIfNull(columnOrdinalsByTable);
        ArgumentNullException.ThrowIfNull(createMissingTableException);

        return ResolveDictionaryValueOrThrow(columnOrdinalsByTable, table, createMissingTableException);
    }

    public static DbColumnModel ResolveHydrationProjectionColumnOrThrow(
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, int> columnOrdinalByName,
        DbColumnName column,
        Func<DbColumnName, Exception> createMissingColumnException
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentNullException.ThrowIfNull(columnOrdinalByName);
        ArgumentNullException.ThrowIfNull(createMissingColumnException);

        return tableModel.Columns[
            ResolveHydrationColumnOrdinalOrThrow(columnOrdinalByName, column, createMissingColumnException)
        ];
    }

    public static int ResolveHydrationColumnOrdinalOrThrow(
        IReadOnlyDictionary<DbColumnName, int> columnOrdinalByName,
        DbColumnName column,
        Func<DbColumnName, Exception> createMissingColumnException
    )
    {
        ArgumentNullException.ThrowIfNull(columnOrdinalByName);
        ArgumentNullException.ThrowIfNull(createMissingColumnException);

        if (columnOrdinalByName.TryGetValue(column, out var columnOrdinal))
        {
            return columnOrdinal;
        }

        throw createMissingColumnException(column);
    }

    public static DbColumnModel ResolveTableColumnOrThrow(
        DbTableModel tableModel,
        DbColumnName column,
        Func<DbColumnName, Exception> createMissingColumnException
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentNullException.ThrowIfNull(createMissingColumnException);

        if (TryResolveTableColumn(tableModel, column, out _, out var columnModel))
        {
            return columnModel;
        }

        throw createMissingColumnException(column);
    }

    public static int ResolveTableColumnOrdinalOrThrow(
        DbTableModel tableModel,
        DbColumnName column,
        Func<DbColumnName, Exception> createMissingColumnException
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentNullException.ThrowIfNull(createMissingColumnException);

        if (TryResolveTableColumn(tableModel, column, out var columnOrdinal, out _))
        {
            return columnOrdinal;
        }

        throw createMissingColumnException(column);
    }

    private static TValue ResolveDictionaryValueOrThrow<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> valuesByKey,
        TKey key,
        Func<TKey, Exception> createMissingValueException
    )
        where TKey : notnull
    {
        if (valuesByKey.TryGetValue(key, out var value))
        {
            return value;
        }

        throw createMissingValueException(key);
    }

    private static bool TryResolveTableColumn(
        DbTableModel tableModel,
        DbColumnName column,
        out int columnOrdinal,
        out DbColumnModel columnModel
    )
    {
        for (var index = 0; index < tableModel.Columns.Count; index++)
        {
            var candidate = tableModel.Columns[index];

            if (!candidate.ColumnName.Equals(column))
            {
                continue;
            }

            columnOrdinal = index;
            columnModel = candidate;
            return true;
        }

        columnOrdinal = -1;
        columnModel = null!;
        return false;
    }
}
