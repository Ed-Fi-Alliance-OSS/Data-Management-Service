// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared, projection-focused helpers used by the relational write merge synthesizers
/// and the post-overlay key-unification resolver.
/// </summary>
internal static class RelationalWriteMergeSupport
{
    internal static int FindBindingIndex(TableWritePlan tableWritePlan, DbColumnName columnName)
    {
        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (tableWritePlan.ColumnBindings[bindingIndex].Column.ColumnName.Equals(columnName))
            {
                return bindingIndex;
            }
        }

        throw new InvalidOperationException(
            $"Table '{FormatTable(tableWritePlan)}' does not contain a binding for column '{columnName.Value}'."
        );
    }

    /// <summary>
    /// Extracts the physical-row-identity slice of a row's binding-indexed values for use as
    /// the parent identity of nested-children recursion. Iterates the table's
    /// <see cref="DbTableModel.IdentityMetadata"/>.<c>PhysicalRowIdentityColumns</c> and pulls
    /// each column's value from <paramref name="values"/> via <see cref="FindBindingIndex"/>.
    /// Shared by the no-profile synthesizer, profile-merge synthesizer, and profile collection
    /// walker so all three keep the same parent-identity shape.
    /// </summary>
    internal static ImmutableArray<FlattenedWriteValue> ExtractPhysicalRowIdentityValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var physicalRowIdentityColumns = tableWritePlan
            .TableModel
            .IdentityMetadata
            .PhysicalRowIdentityColumns;
        var physicalRowIdentityValues = new FlattenedWriteValue[physicalRowIdentityColumns.Count];
        for (var index = 0; index < physicalRowIdentityColumns.Count; index++)
        {
            var columnName = physicalRowIdentityColumns[index];
            physicalRowIdentityValues[index] = values[FindBindingIndex(tableWritePlan, columnName)];
        }

        return [.. physicalRowIdentityValues];
    }

    internal static ImmutableArray<FlattenedWriteValue> ProjectComparableValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var bindingIndexes = tableWritePlan.CollectionMergePlan is null
            ? Enumerable.Range(0, tableWritePlan.ColumnBindings.Length)
            : tableWritePlan.CollectionMergePlan.CompareBindingIndexesInOrder;

        FlattenedWriteValue[] comparableValues = bindingIndexes
            .Select(bindingIndex => values[bindingIndex])
            .ToArray();

        return comparableValues.ToImmutableArray();
    }

    internal static object? NormalizeHydratedValue(DbColumnModel column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (value is null || column.ScalarType is null)
        {
            return value;
        }

        return column.ScalarType.Kind switch
        {
            ScalarKind.Date => NormalizeDateValue(value),
            ScalarKind.Time => NormalizeTimeValue(value),
            _ => value,
        };
    }

    internal static ImmutableArray<RelationalWriteMergedTableRow> ProjectCurrentRows(
        TableWritePlan tableWritePlan,
        IReadOnlyList<object?[]> hydratedRows
    )
    {
        List<RelationalWriteMergedTableRow> projectedRows = [];

        foreach (var hydratedRow in hydratedRows)
        {
            FlattenedWriteValue[] bindingValues = new FlattenedWriteValue[
                tableWritePlan.ColumnBindings.Length
            ];

            for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
            {
                var binding = tableWritePlan.ColumnBindings[bindingIndex];
                var columnOrdinal = FindColumnOrdinal(tableWritePlan.TableModel, binding.Column.ColumnName);
                bindingValues[bindingIndex] = new FlattenedWriteValue.Literal(
                    NormalizeHydratedValue(binding.Column, hydratedRow[columnOrdinal])
                );
            }

            projectedRows.Add(
                new RelationalWriteMergedTableRow(
                    bindingValues,
                    ProjectComparableValues(tableWritePlan, bindingValues)
                )
            );
        }

        return projectedRows.ToImmutableArray();
    }

    /// <summary>
    /// Builds a normalized-by-column projection of a single hydrated table row, keyed
    /// by column name. Includes every column in <paramref name="tableModel"/>.Columns,
    /// both binding-backed and alias-only. Each value is normalized via
    /// <see cref="NormalizeHydratedValue"/> so Date/Time comparisons in downstream
    /// key-unification agreement checks match the binding-indexed projection's
    /// normalization.
    /// </summary>
    internal static IReadOnlyDictionary<DbColumnName, object?> BuildCurrentRowByColumnName(
        DbTableModel tableModel,
        IReadOnlyList<object?> hydratedRow
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentNullException.ThrowIfNull(hydratedRow);

        var result = new Dictionary<DbColumnName, object?>();
        for (var columnOrdinal = 0; columnOrdinal < tableModel.Columns.Count; columnOrdinal++)
        {
            var column = tableModel.Columns[columnOrdinal];
            result[column.ColumnName] = NormalizeHydratedValue(column, hydratedRow[columnOrdinal]);
        }
        return result;
    }

    private static int FindColumnOrdinal(DbTableModel tableModel, DbColumnName columnName)
    {
        for (var columnOrdinal = 0; columnOrdinal < tableModel.Columns.Count; columnOrdinal++)
        {
            if (tableModel.Columns[columnOrdinal].ColumnName.Equals(columnName))
            {
                return columnOrdinal;
            }
        }

        throw new InvalidOperationException(
            $"Hydrated table '{tableModel.Table.Schema.Value}.{tableModel.Table.Name}' does not contain column '{columnName.Value}'."
        );
    }

    private static object NormalizeDateValue(object value) =>
        value switch
        {
            DateOnly => value,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => DateOnly.FromDateTime(dateTimeOffset.DateTime),
            _ => value,
        };

    private static object NormalizeTimeValue(object value) =>
        value switch
        {
            TimeOnly => value,
            TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
            DateTime dateTime => TimeOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => TimeOnly.FromDateTime(dateTimeOffset.DateTime),
            _ => value,
        };

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
}
