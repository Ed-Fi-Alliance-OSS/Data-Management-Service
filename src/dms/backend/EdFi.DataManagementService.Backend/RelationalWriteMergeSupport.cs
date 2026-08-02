// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared, projection-focused helpers used by the relational write merge synthesizers
/// and the post-overlay key-unification resolver.
/// </summary>
internal static class RelationalWriteMergeSupport
{
    /// <summary>
    /// Slot filler for a hydration-exempt binding in a current-row projection. Never read: it exists
    /// only so the projected array stays indexable by binding index.
    /// </summary>
    private static readonly FlattenedWriteValue HydrationExemptPlaceholder = new FlattenedWriteValue.Literal(
        null
    );

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
    /// Normalizes a binding's canonical path to the scope-relative form Core publishes
    /// (e.g. <c>"$.addresses[*].streetNumber"</c> with scope <c>"$.addresses[*]"</c>
    /// becomes <c>"streetNumber"</c>). Falls back to stripping a leading <c>"$."</c> for
    /// paths that do not nest under the supplied scope.
    /// </summary>
    /// <remarks>
    /// Shared by the flattener, no-profile synthesizer, and profile collection walker so all
    /// three normalize identity / member paths the same way. The External assembly's
    /// <c>CompiledScopeAdapterFactory</c> carries an independent copy (External cannot
    /// reference Backend); keep the bodies in sync if either side changes.
    /// </remarks>
    internal static string ToScopeRelativePath(string canonicalPath, string scopeCanonical)
    {
        var scopePrefix = scopeCanonical + ".";
        if (canonicalPath.StartsWith(scopePrefix, StringComparison.Ordinal))
        {
            return canonicalPath[scopePrefix.Length..];
        }

        return canonicalPath.StartsWith("$.", StringComparison.Ordinal) ? canonicalPath[2..] : canonicalPath;
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

    /// <summary>
    /// Returns <see langword="true" /> when the binding's value is originated by the write path rather
    /// than read back from stored state, and the hydration (read) projection therefore does not carry
    /// its column. Such a binding can take no part in the current-row projection or in the no-op
    /// comparison: there is no stored value to project, and the merge always takes the request-side
    /// value for it (<see cref="RootBindingDisposition.StorageManaged" />), so a comparison could only
    /// ever compare the value against itself.
    /// </summary>
    /// <remarks>
    /// Keyed off the binding's <see cref="WriteValueSource" /> rather than a column name so the rule is
    /// a property of the compiled plan contract. Today the only such source is
    /// <see cref="WriteValueSource.DocumentUuid" />: the root row's public API id, which the flattener
    /// binds from the write target (a fresh uuid on create, the stored uuid on update) because no
    /// trigger originates it any more. The other storage-managed sources —
    /// <see cref="WriteValueSource.DocumentId" />, <see cref="WriteValueSource.ParentKeyPart" />,
    /// <see cref="WriteValueSource.Ordinal" />, <see cref="WriteValueSource.Precomputed" /> — all map to
    /// columns the hydration projection does carry, and their current-state values are load-bearing for
    /// collection row matching, so they stay in.
    /// </remarks>
    internal static bool IsHydrationExemptBinding(WriteColumnBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return binding.Source is WriteValueSource.DocumentUuid;
    }

    internal static ImmutableArray<FlattenedWriteValue> ProjectComparableValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        // Only resource root tables carry a hydration-exempt binding (DeriveDocumentMetadataColumnsPass
        // synthesizes DocumentUuid onto the model root alone), and a root table never has a collection
        // merge plan (UsesCollectionMergeContract requires a Collection / ExtensionCollection table
        // kind), so CompareBindingIndexesInOrder never has to filter.
        var bindingIndexes = tableWritePlan.CollectionMergePlan is null
            ? Enumerable
                .Range(0, tableWritePlan.ColumnBindings.Length)
                .Where(bindingIndex => !IsHydrationExemptBinding(tableWritePlan.ColumnBindings[bindingIndex]))
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

    /// <summary>
    /// Projects hydrated current-state rows into binding-indexed merged rows. The hydrated row is
    /// indexed by ordinals resolved against <paramref name="hydratedRowTableModel"/> — the model the
    /// row was materialized from — not the write plan's table model. These differ on resource root
    /// tables: the write-plan model carries the document-metadata columns, but the hydration (read)
    /// projection omits them, and column canonicalization leaves the remaining columns positioned after
    /// them, so write-plan ordinals would be shifted (and overshoot the shorter hydrated row).
    /// <para>
    /// Most of those columns are not bindings at all (they are non-writable, so plan compilation never
    /// binds them) and never reach this loop. <c>DocumentUuid</c> is the exception — the write path
    /// originates it, so it <em>is</em> a binding — and it is skipped here per
    /// <see cref="IsHydrationExemptBinding" />. The invariant this method now rests on is therefore:
    /// <b>every binding except a hydration-exempt one resolves to a column of the hydrated model</b>.
    /// The skipped slot is filled with a null placeholder purely to keep the array binding-indexed; no
    /// consumer reads it, because the merge takes the request-side value for such a binding and
    /// <see cref="ProjectComparableValues" /> leaves it out of the no-op comparison.
    /// </para>
    /// </summary>
    internal static ImmutableArray<RelationalWriteMergedTableRow> ProjectCurrentRows(
        TableWritePlan tableWritePlan,
        DbTableModel hydratedRowTableModel,
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
                if (IsHydrationExemptBinding(binding))
                {
                    bindingValues[bindingIndex] = HydrationExemptPlaceholder;
                    continue;
                }

                var columnOrdinal = FindColumnOrdinal(hydratedRowTableModel, binding.Column.ColumnName);
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

    internal static RelationalWriteMergedTableState BuildOrderedTableState(
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> currentRows,
        IReadOnlyList<RelationalWriteMergedTableRow> mergedRows
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(currentRows);
        ArgumentNullException.ThrowIfNull(mergedRows);

        IReadOnlyList<RelationalWriteMergedTableRow> currentRowsForComparison;
        IReadOnlyList<RelationalWriteMergedTableRow> mergedRowsForComparison;

        if (tableWritePlan.CollectionMergePlan is not null)
        {
            currentRowsForComparison = OrderCollectionRowsForComparisonIfFullyBound(
                tableWritePlan,
                currentRows
            );
            mergedRowsForComparison = OrderCollectionRowsForComparisonIfFullyBound(
                tableWritePlan,
                mergedRows
            );
        }
        else if (IsCollectionAlignedExtensionScope(tableWritePlan))
        {
            currentRowsForComparison = OrderCollectionAlignedExtensionScopeRowsForComparisonIfFullyBound(
                tableWritePlan,
                currentRows
            );
            mergedRowsForComparison = OrderCollectionAlignedExtensionScopeRowsForComparisonIfFullyBound(
                tableWritePlan,
                mergedRows
            );
        }
        else
        {
            currentRowsForComparison = currentRows;
            mergedRowsForComparison = mergedRows;
        }

        return new RelationalWriteMergedTableState(
            tableWritePlan,
            currentRowsForComparison,
            mergedRowsForComparison
        );
    }

    internal static IReadOnlyList<RelationalWriteMergedTableRow> OrderCollectionRowsForComparisonIfFullyBound(
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows
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

    internal static IReadOnlyList<RelationalWriteMergedTableRow> OrderCollectionAlignedExtensionScopeRowsForComparisonIfFullyBound(
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows
    )
    {
        var parentBindingIndexes = tableWritePlan
            .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(columnName =>
                FindBindingIndex(tableWritePlan, columnName)
            )
            .ToArray();

        return OrderRowsByBindingIndexesIfFullyBound(rows, parentBindingIndexes);
    }

    private static IReadOnlyList<RelationalWriteMergedTableRow> OrderRowsByBindingIndexesIfFullyBound(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
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

    private static bool CanProjectLiteralValues(
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<int> bindingIndexes
    ) => bindingIndexes.All(bindingIndex => values[bindingIndex] is FlattenedWriteValue.Literal);

    internal static bool IsCollectionAlignedExtensionScope(TableWritePlan tableWritePlan) =>
        tableWritePlan.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope;

    private sealed class BoundRowComparer(IReadOnlyList<int> bindingIndexes)
        : IComparer<RelationalWriteMergedTableRow>
    {
        public int Compare(RelationalWriteMergedTableRow? left, RelationalWriteMergedTableRow? right)
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

/// <summary>
/// Encodes the storage-collapsed-identity equality rule used by both write paths.
/// </summary>
internal static class StorageCollapsedIdentityHelpers
{
    private const char PartSeparator = '\u001E'; // RS - between parts
    private const char FieldSeparator = '\u001F'; // US - within a part
    private const string NullValueSentinel = "null";

    /// <summary>
    /// Builds a storage-collapsed key for a presence-aware identity sequence.
    /// Two sequences produce equal keys iff they collapse to the same DB shape.
    /// </summary>
    public static string BuildKey(ImmutableArray<SemanticIdentityPart> parts)
    {
        if (parts.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(PartSeparator);
            }

            var part = parts[i];
            var hasValue = part.IsPresent && part.Value is not null;
            builder.Append(part.RelativePath);
            builder.Append(FieldSeparator);
            builder.Append(hasValue ? '1' : '0');
            builder.Append(FieldSeparator);
            builder.Append(hasValue ? part.Value!.ToJsonString() : NullValueSentinel);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Equality comparer for raw <c>object?[]</c> identity values. Aliases
    /// <see cref="ObjectValueArrayComparer.Instance"/> so the no-profile flattener
    /// expresses the storage-collapsed-uniqueness rule by name.
    /// </summary>
    public static IEqualityComparer<object?[]> ObjectArrayComparer => ObjectValueArrayComparer.Instance;
}
