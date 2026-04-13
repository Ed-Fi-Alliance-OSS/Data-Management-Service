// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared static utility methods used during relational write merge synthesis.
/// </summary>
internal static class RelationalWriteMergeShared
{
    public static ImmutableArray<FlattenedWriteValue> RewriteParentKeyPartValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
    )
    {
        FlattenedWriteValue[] rewrittenValues = [.. values];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (
                tableWritePlan.ColumnBindings[bindingIndex].Source
                is not WriteValueSource.ParentKeyPart parentKeyPart
            )
            {
                continue;
            }

            rewrittenValues[bindingIndex] = parentPhysicalRowIdentityValues[parentKeyPart.Index];
        }

        return rewrittenValues.ToImmutableArray();
    }

    public static ImmutableArray<FlattenedWriteValue> RewriteCollectionStableRowIdentity(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> currentValues
    )
    {
        var mergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
            );

        FlattenedWriteValue[] rewrittenValues = [.. values];
        rewrittenValues[mergePlan.StableRowIdentityBindingIndex] = currentValues[
            mergePlan.StableRowIdentityBindingIndex
        ];

        return rewrittenValues.ToImmutableArray();
    }

    public static ImmutableArray<FlattenedWriteValue> ExtractPhysicalRowIdentityValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        FlattenedWriteValue[] physicalRowIdentityValues = new FlattenedWriteValue[
            tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns.Count
        ];

        for (
            var index = 0;
            index < tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns.Count;
            index++
        )
        {
            var columnName = tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns[index];
            physicalRowIdentityValues[index] = values[FindBindingIndex(tableWritePlan, columnName)];
        }

        return physicalRowIdentityValues.ToImmutableArray();
    }

    public static ImmutableArray<FlattenedWriteValue> ProjectComparableValues(
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

    public static int FindBindingIndex(TableWritePlan tableWritePlan, DbColumnName columnName)
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

    public static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";

    // ─── Hydrated-row projection (shared by CurrentStateProjection in both merge paths) ──

    public static ImmutableArray<MergeTableRow> ProjectCurrentRows(
        TableWritePlan tableWritePlan,
        IReadOnlyList<object?[]> hydratedRows
    )
    {
        List<MergeTableRow> projectedRows = [];

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
                new MergeTableRow(bindingValues, ProjectComparableValues(tableWritePlan, bindingValues))
            );
        }

        return projectedRows.ToImmutableArray();
    }

    public static int FindColumnOrdinal(DbTableModel tableModel, DbColumnName columnName)
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

    public static object? NormalizeHydratedValue(DbColumnModel column, object? value)
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

    public static object NormalizeDateValue(object value) =>
        value switch
        {
            DateOnly => value,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => DateOnly.FromDateTime(dateTimeOffset.DateTime),
            _ => value,
        };

    public static object NormalizeTimeValue(object value) =>
        value switch
        {
            TimeOnly => value,
            TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
            DateTime dateTime => TimeOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => TimeOnly.FromDateTime(dateTimeOffset.DateTime),
            _ => value,
        };

    // ─── Row ordering and comparison (shared by TableStateBuilder in both merge paths) ──

    public static IReadOnlyList<MergeTableRow> OrderCollectionRowsIfFullyBound(
        TableWritePlan tableWritePlan,
        IReadOnlyList<MergeTableRow> rows
    )
    {
        var mergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
            );

        var parentBindingIndexes = tableWritePlan
            .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(columnName =>
                FindBindingIndex(tableWritePlan, columnName)
            )
            .Append(mergePlan.OrdinalBindingIndex)
            .ToArray();

        return OrderRowsByBindingIndexesIfFullyBound(rows, parentBindingIndexes);
    }

    public static IReadOnlyList<MergeTableRow> OrderCollectionAlignedExtensionScopeRowsIfFullyBound(
        TableWritePlan tableWritePlan,
        IReadOnlyList<MergeTableRow> rows
    )
    {
        var parentBindingIndexes = tableWritePlan
            .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(columnName =>
                FindBindingIndex(tableWritePlan, columnName)
            )
            .ToArray();

        return OrderRowsByBindingIndexesIfFullyBound(rows, parentBindingIndexes);
    }

    public static IReadOnlyList<MergeTableRow> OrderRowsByBindingIndexesIfFullyBound(
        IReadOnlyList<MergeTableRow> rows,
        IReadOnlyList<int> bindingIndexes
    )
    {
        if (
            bindingIndexes.Count == 0
            || rows.Any(row => !CanProjectLiteralValues(row.Values, bindingIndexes))
        )
        {
            return rows;
        }

        return rows.OrderBy(static row => row, new BoundRowComparer(bindingIndexes)).ToArray();
    }

    public static bool CanProjectLiteralValues(
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<int> bindingIndexes
    ) => bindingIndexes.All(bindingIndex => values[bindingIndex] is FlattenedWriteValue.Literal);

    public static bool IsCollectionAlignedExtensionScope(TableWritePlan tableWritePlan) =>
        tableWritePlan.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope;

    // ─── BoundRowComparer ────────────────────────────────────────────────

    public sealed class BoundRowComparer(IReadOnlyList<int> bindingIndexes) : IComparer<MergeTableRow>
    {
        public int Compare(MergeTableRow? left, MergeTableRow? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            foreach (var bindingIndex in bindingIndexes)
            {
                var leftValue = ((FlattenedWriteValue.Literal)left.Values[bindingIndex]).Value;
                var rightValue = ((FlattenedWriteValue.Literal)right.Values[bindingIndex]).Value;
                var compareResult = CompareLiteralValues(leftValue, rightValue);

                if (compareResult != 0)
                {
                    return compareResult;
                }
            }

            return 0;
        }

        private static int CompareLiteralValues(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return left switch
            {
                IComparable comparable => comparable.CompareTo(right),
                _ => string.CompareOrdinal(left.ToString(), right.ToString()),
            };
        }
    }
}
