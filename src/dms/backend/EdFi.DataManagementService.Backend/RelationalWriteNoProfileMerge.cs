// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalWriteNoProfileMergeRequest
{
    public RelationalWriteNoProfileMergeRequest(
        ResourceWritePlan writePlan,
        FlattenedWriteSet flattenedWriteSet,
        RelationalWriteCurrentState? currentState
    )
    {
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        FlattenedWriteSet = flattenedWriteSet ?? throw new ArgumentNullException(nameof(flattenedWriteSet));
        CurrentState = currentState;

        if (
            !ReferenceEquals(
                WritePlan.TablePlansInDependencyOrder[0],
                FlattenedWriteSet.RootRow.TableWritePlan
            )
        )
        {
            throw new ArgumentException(
                $"{nameof(flattenedWriteSet)} must use the root table from the supplied {nameof(writePlan)}.",
                nameof(flattenedWriteSet)
            );
        }
    }

    public ResourceWritePlan WritePlan { get; init; }

    public FlattenedWriteSet FlattenedWriteSet { get; init; }

    public RelationalWriteCurrentState? CurrentState { get; init; }
}

internal sealed record RelationalWriteNoProfileTableRow
{
    public RelationalWriteNoProfileTableRow(
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<FlattenedWriteValue> comparableValues
    )
    {
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        ComparableValues = FlattenedWriteContractSupport.ToImmutableArray(
            comparableValues,
            nameof(comparableValues)
        );
    }

    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    public ImmutableArray<FlattenedWriteValue> ComparableValues { get; init; }
}

internal sealed record RelationalWriteNoProfileTableState
{
    public RelationalWriteNoProfileTableState(
        TableWritePlan tableWritePlan,
        IEnumerable<RelationalWriteNoProfileTableRow> currentRows,
        IEnumerable<RelationalWriteNoProfileTableRow> mergedRows
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        CurrentRows = FlattenedWriteContractSupport.ToImmutableArray(currentRows, nameof(currentRows));
        MergedRows = FlattenedWriteContractSupport.ToImmutableArray(mergedRows, nameof(mergedRows));
    }

    public TableWritePlan TableWritePlan { get; init; }

    public ImmutableArray<RelationalWriteNoProfileTableRow> CurrentRows { get; init; }

    public ImmutableArray<RelationalWriteNoProfileTableRow> MergedRows { get; init; }
}

internal sealed record RelationalWriteNoProfileMergeResult
{
    public RelationalWriteNoProfileMergeResult(
        IEnumerable<RelationalWriteNoProfileTableState> tablesInDependencyOrder
    )
    {
        TablesInDependencyOrder = FlattenedWriteContractSupport.ToImmutableArray(
            tablesInDependencyOrder,
            nameof(tablesInDependencyOrder)
        );
    }

    public ImmutableArray<RelationalWriteNoProfileTableState> TablesInDependencyOrder { get; init; }
}

internal interface IRelationalWriteNoProfileMergeSynthesizer
{
    RelationalWriteNoProfileMergeResult Synthesize(RelationalWriteNoProfileMergeRequest request);
}

internal sealed class RelationalWriteNoProfileMergeSynthesizer : IRelationalWriteNoProfileMergeSynthesizer
{
    public RelationalWriteNoProfileMergeResult Synthesize(RelationalWriteNoProfileMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentStateProjection = CurrentStateProjection.Create(request);
        var tableStateBuilders = CreateTableStateBuilders(request.WritePlan, currentStateProjection);

        var rootRow = CreateMergedRow(
            request.FlattenedWriteSet.RootRow.TableWritePlan,
            request.FlattenedWriteSet.RootRow.Values
        );
        tableStateBuilders[rootRow.TableWritePlan.TableModel.Table].AddMergedRow(rootRow.Row);

        var rootPhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(
            rootRow.TableWritePlan,
            rootRow.Row.Values
        );

        SynthesizeRootExtensionRows(
            request.FlattenedWriteSet.RootRow.RootExtensionRows,
            rootPhysicalRowIdentityValues,
            currentStateProjection,
            tableStateBuilders
        );
        SynthesizeCollectionCandidates(
            request.FlattenedWriteSet.RootRow.CollectionCandidates,
            rootPhysicalRowIdentityValues,
            currentStateProjection,
            tableStateBuilders
        );

        return new RelationalWriteNoProfileMergeResult(
            request.WritePlan.TablePlansInDependencyOrder.Select(tableWritePlan =>
                tableStateBuilders[tableWritePlan.TableModel.Table].Build()
            )
        );
    }

    private static Dictionary<DbTableName, TableStateBuilder> CreateTableStateBuilders(
        ResourceWritePlan writePlan,
        CurrentStateProjection currentStateProjection
    )
    {
        Dictionary<DbTableName, TableStateBuilder> tableStateBuilders = [];

        foreach (var tableWritePlan in writePlan.TablePlansInDependencyOrder)
        {
            tableStateBuilders.Add(
                tableWritePlan.TableModel.Table,
                new TableStateBuilder(tableWritePlan, currentStateProjection.GetCurrentRows(tableWritePlan))
            );
        }

        return tableStateBuilders;
    }

    private static void SynthesizeRootExtensionRows(
        IReadOnlyList<RootExtensionWriteRowBuffer> rootExtensionRows,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        CurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, TableStateBuilder> tableStateBuilders
    )
    {
        foreach (var rootExtensionRow in rootExtensionRows)
        {
            var mergedValues = RewriteParentKeyPartValues(
                rootExtensionRow.TableWritePlan,
                rootExtensionRow.Values,
                parentPhysicalRowIdentityValues
            );
            var mergedRow = CreateMergedRow(rootExtensionRow.TableWritePlan, mergedValues);

            tableStateBuilders[rootExtensionRow.TableWritePlan.TableModel.Table].AddMergedRow(mergedRow.Row);

            var scopePhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(
                rootExtensionRow.TableWritePlan,
                mergedValues
            );

            SynthesizeCollectionCandidates(
                rootExtensionRow.CollectionCandidates,
                scopePhysicalRowIdentityValues,
                currentStateProjection,
                tableStateBuilders
            );
        }
    }

    private static void SynthesizeAttachedAlignedScopeRows(
        IReadOnlyList<CandidateAttachedAlignedScopeData> attachedAlignedScopeData,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        CurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, TableStateBuilder> tableStateBuilders
    )
    {
        foreach (var alignedScopeData in attachedAlignedScopeData)
        {
            var mergedValues = RewriteParentKeyPartValues(
                alignedScopeData.TableWritePlan,
                alignedScopeData.Values,
                parentPhysicalRowIdentityValues
            );
            var mergedRow = CreateMergedRow(alignedScopeData.TableWritePlan, mergedValues);

            tableStateBuilders[alignedScopeData.TableWritePlan.TableModel.Table].AddMergedRow(mergedRow.Row);

            var scopePhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(
                alignedScopeData.TableWritePlan,
                mergedValues
            );

            SynthesizeCollectionCandidates(
                alignedScopeData.CollectionCandidates,
                scopePhysicalRowIdentityValues,
                currentStateProjection,
                tableStateBuilders
            );
        }
    }

    private static void SynthesizeCollectionCandidates(
        IReadOnlyList<CollectionWriteCandidate> collectionCandidates,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        CurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, TableStateBuilder> tableStateBuilders
    )
    {
        foreach (var collectionCandidate in collectionCandidates)
        {
            var matchedCurrentRow = currentStateProjection.TryMatchCollectionRow(
                collectionCandidate.TableWritePlan,
                parentPhysicalRowIdentityValues,
                collectionCandidate.SemanticIdentityValues
            );
            var mergedValues = RewriteParentKeyPartValues(
                collectionCandidate.TableWritePlan,
                collectionCandidate.Values,
                parentPhysicalRowIdentityValues
            );

            if (matchedCurrentRow is not null)
            {
                mergedValues = RewriteCollectionStableRowIdentity(
                    collectionCandidate.TableWritePlan,
                    mergedValues,
                    matchedCurrentRow.Values
                );
            }

            var mergedRow = CreateMergedRow(collectionCandidate.TableWritePlan, mergedValues);

            tableStateBuilders[collectionCandidate.TableWritePlan.TableModel.Table]
                .AddMergedRow(mergedRow.Row);

            var collectionPhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(
                collectionCandidate.TableWritePlan,
                mergedValues
            );

            SynthesizeAttachedAlignedScopeRows(
                collectionCandidate.AttachedAlignedScopeData,
                collectionPhysicalRowIdentityValues,
                currentStateProjection,
                tableStateBuilders
            );
            SynthesizeCollectionCandidates(
                collectionCandidate.CollectionCandidates,
                collectionPhysicalRowIdentityValues,
                currentStateProjection,
                tableStateBuilders
            );
        }
    }

    private static MergedRow CreateMergedRow(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var comparableValues = ProjectComparableValues(tableWritePlan, values);

        return new MergedRow(tableWritePlan, new RelationalWriteNoProfileTableRow(values, comparableValues));
    }

    private static ImmutableArray<FlattenedWriteValue> RewriteParentKeyPartValues(
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

    private static ImmutableArray<FlattenedWriteValue> RewriteCollectionStableRowIdentity(
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

    private static ImmutableArray<FlattenedWriteValue> ExtractPhysicalRowIdentityValues(
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

    private static ImmutableArray<FlattenedWriteValue> ProjectComparableValues(
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

    private static int FindBindingIndex(TableWritePlan tableWritePlan, DbColumnName columnName)
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

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";

    private sealed record MergedRow(TableWritePlan TableWritePlan, RelationalWriteNoProfileTableRow Row);

    private sealed class TableStateBuilder(
        TableWritePlan tableWritePlan,
        ImmutableArray<RelationalWriteNoProfileTableRow> currentRows
    )
    {
        private readonly List<RelationalWriteNoProfileTableRow> _mergedRows = [];

        public void AddMergedRow(RelationalWriteNoProfileTableRow row)
        {
            ArgumentNullException.ThrowIfNull(row);
            _mergedRows.Add(row);
        }

        public RelationalWriteNoProfileTableState Build()
        {
            var currentRowsForComparison = IsCollectionAlignedExtensionScope(tableWritePlan)
                ? OrderCollectionAlignedExtensionScopeRowsIfFullyBound(tableWritePlan, currentRows)
                : currentRows;
            IReadOnlyList<RelationalWriteNoProfileTableRow> mergedRows;

            if (tableWritePlan.CollectionMergePlan is not null)
            {
                mergedRows = OrderCollectionRowsIfFullyBound(tableWritePlan, _mergedRows);
            }
            else if (IsCollectionAlignedExtensionScope(tableWritePlan))
            {
                mergedRows = OrderCollectionAlignedExtensionScopeRowsIfFullyBound(
                    tableWritePlan,
                    _mergedRows
                );
            }
            else
            {
                mergedRows = _mergedRows;
            }

            return new RelationalWriteNoProfileTableState(
                tableWritePlan,
                currentRowsForComparison,
                mergedRows
            );
        }

        private static IReadOnlyList<RelationalWriteNoProfileTableRow> OrderCollectionRowsIfFullyBound(
            TableWritePlan tableWritePlan,
            IReadOnlyList<RelationalWriteNoProfileTableRow> rows
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

        private static IReadOnlyList<RelationalWriteNoProfileTableRow> OrderCollectionAlignedExtensionScopeRowsIfFullyBound(
            TableWritePlan tableWritePlan,
            IReadOnlyList<RelationalWriteNoProfileTableRow> rows
        )
        {
            var parentBindingIndexes = tableWritePlan
                .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(columnName =>
                    FindBindingIndex(tableWritePlan, columnName)
                )
                .ToArray();

            return OrderRowsByBindingIndexesIfFullyBound(rows, parentBindingIndexes);
        }

        private static IReadOnlyList<RelationalWriteNoProfileTableRow> OrderRowsByBindingIndexesIfFullyBound(
            IReadOnlyList<RelationalWriteNoProfileTableRow> rows,
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

        private static bool IsCollectionAlignedExtensionScope(TableWritePlan tableWritePlan) =>
            tableWritePlan.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope;
    }

    private sealed class CurrentStateProjection
    {
        private readonly IReadOnlyDictionary<
            DbTableName,
            ImmutableArray<RelationalWriteNoProfileTableRow>
        > _currentRowsByTable;

        private readonly IReadOnlyDictionary<
            DbTableName,
            ProjectedCollectionTableState
        > _collectionRowsByTable;

        private CurrentStateProjection(
            IReadOnlyDictionary<
                DbTableName,
                ImmutableArray<RelationalWriteNoProfileTableRow>
            > currentRowsByTable,
            IReadOnlyDictionary<DbTableName, ProjectedCollectionTableState> collectionRowsByTable
        )
        {
            _currentRowsByTable = currentRowsByTable;
            _collectionRowsByTable = collectionRowsByTable;
        }

        public static CurrentStateProjection Create(RelationalWriteNoProfileMergeRequest request)
        {
            Dictionary<DbTableName, ImmutableArray<RelationalWriteNoProfileTableRow>> currentRowsByTable = [];
            Dictionary<DbTableName, ProjectedCollectionTableState> collectionRowsByTable = [];

            var hydratedRowsByTable = request.CurrentState is null
                ? new Dictionary<DbTableName, HydratedTableRows>()
                : request.CurrentState.TableRowsInDependencyOrder.ToDictionary(hydratedTableRows =>
                    hydratedTableRows.TableModel.Table
                );

            foreach (var tableWritePlan in request.WritePlan.TablePlansInDependencyOrder)
            {
                var projectedRows = hydratedRowsByTable.TryGetValue(
                    tableWritePlan.TableModel.Table,
                    out var hydratedTableRows
                )
                    ? ProjectCurrentRows(tableWritePlan, hydratedTableRows.Rows)
                    : ImmutableArray<RelationalWriteNoProfileTableRow>.Empty;

                currentRowsByTable.Add(tableWritePlan.TableModel.Table, projectedRows);

                if (tableWritePlan.CollectionMergePlan is null)
                {
                    continue;
                }

                collectionRowsByTable.Add(
                    tableWritePlan.TableModel.Table,
                    new ProjectedCollectionTableState(tableWritePlan, projectedRows)
                );
            }

            return new CurrentStateProjection(currentRowsByTable, collectionRowsByTable);
        }

        public ImmutableArray<RelationalWriteNoProfileTableRow> GetCurrentRows(
            TableWritePlan tableWritePlan
        ) =>
            _currentRowsByTable.TryGetValue(tableWritePlan.TableModel.Table, out var currentRows)
                ? currentRows
                : ImmutableArray<RelationalWriteNoProfileTableRow>.Empty;

        public RelationalWriteNoProfileTableRow? TryMatchCollectionRow(
            TableWritePlan tableWritePlan,
            IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
            IReadOnlyList<object?> semanticIdentityValues
        )
        {
            if (
                !_collectionRowsByTable.TryGetValue(
                    tableWritePlan.TableModel.Table,
                    out var projectedCollectionTableState
                )
            )
            {
                return null;
            }

            return projectedCollectionTableState.TryMatch(
                parentPhysicalRowIdentityValues,
                semanticIdentityValues
            );
        }

        private static ImmutableArray<RelationalWriteNoProfileTableRow> ProjectCurrentRows(
            TableWritePlan tableWritePlan,
            IReadOnlyList<object?[]> hydratedRows
        )
        {
            List<RelationalWriteNoProfileTableRow> projectedRows = [];

            foreach (var hydratedRow in hydratedRows)
            {
                FlattenedWriteValue[] bindingValues = new FlattenedWriteValue[
                    tableWritePlan.ColumnBindings.Length
                ];

                for (
                    var bindingIndex = 0;
                    bindingIndex < tableWritePlan.ColumnBindings.Length;
                    bindingIndex++
                )
                {
                    var binding = tableWritePlan.ColumnBindings[bindingIndex];
                    var columnOrdinal = FindColumnOrdinal(
                        tableWritePlan.TableModel,
                        binding.Column.ColumnName
                    );
                    bindingValues[bindingIndex] = new FlattenedWriteValue.Literal(
                        NormalizeHydratedValue(binding.Column, hydratedRow[columnOrdinal])
                    );
                }

                projectedRows.Add(
                    new RelationalWriteNoProfileTableRow(
                        bindingValues,
                        ProjectComparableValues(tableWritePlan, bindingValues)
                    )
                );
            }

            return projectedRows.ToImmutableArray();
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

        private static object? NormalizeHydratedValue(DbColumnModel column, object? value)
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
    }

    private sealed class ProjectedCollectionTableState
    {
        private readonly Dictionary<
            object?[],
            Dictionary<object?[], RelationalWriteNoProfileTableRow>
        > _rowsByParentKey;

        public ProjectedCollectionTableState(
            TableWritePlan tableWritePlan,
            IReadOnlyList<RelationalWriteNoProfileTableRow> currentRows
        )
        {
            TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));

            _rowsByParentKey = new Dictionary<
                object?[],
                Dictionary<object?[], RelationalWriteNoProfileTableRow>
            >(ObjectValueArrayComparer.Instance);

            foreach (var currentRow in currentRows)
            {
                var parentScopeKey = ExtractParentScopeKey(tableWritePlan, currentRow.Values);
                var semanticIdentityKey = ExtractSemanticIdentityKey(tableWritePlan, currentRow.Values);

                if (!_rowsByParentKey.TryGetValue(parentScopeKey, out var rowsBySemanticIdentity))
                {
                    rowsBySemanticIdentity = new Dictionary<object?[], RelationalWriteNoProfileTableRow>(
                        ObjectValueArrayComparer.Instance
                    );
                    _rowsByParentKey.Add(parentScopeKey, rowsBySemanticIdentity);
                }

                if (!rowsBySemanticIdentity.TryAdd(semanticIdentityKey, currentRow))
                {
                    throw new InvalidOperationException(
                        $"Current-state rowset for collection table '{FormatTable(tableWritePlan)}' contains duplicate semantic identities under the same parent scope."
                    );
                }
            }
        }

        public TableWritePlan TableWritePlan { get; }

        public RelationalWriteNoProfileTableRow? TryMatch(
            IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
            IReadOnlyList<object?> semanticIdentityValues
        )
        {
            var parentScopeKey = TryProjectLiteralLookupKey(parentPhysicalRowIdentityValues);

            if (
                parentScopeKey is null
                || !_rowsByParentKey.TryGetValue(parentScopeKey, out var rowsBySemanticIdentity)
            )
            {
                return null;
            }

            var semanticIdentityKey = semanticIdentityValues.ToArray();

            return rowsBySemanticIdentity.TryGetValue(semanticIdentityKey, out var currentRow)
                ? currentRow
                : null;
        }

        private static object?[] ExtractParentScopeKey(
            TableWritePlan tableWritePlan,
            IReadOnlyList<FlattenedWriteValue> values
        )
        {
            var immediateParentScopeColumns = tableWritePlan
                .TableModel
                .IdentityMetadata
                .ImmediateParentScopeLocatorColumns;
            object?[] parentScopeKey = new object?[immediateParentScopeColumns.Count];

            for (var index = 0; index < immediateParentScopeColumns.Count; index++)
            {
                parentScopeKey[index] = ExtractLiteralValue(
                    tableWritePlan,
                    values[FindBindingIndex(tableWritePlan, immediateParentScopeColumns[index])],
                    immediateParentScopeColumns[index]
                );
            }

            return parentScopeKey;
        }

        private static object?[] ExtractSemanticIdentityKey(
            TableWritePlan tableWritePlan,
            IReadOnlyList<FlattenedWriteValue> values
        )
        {
            var mergePlan =
                tableWritePlan.CollectionMergePlan
                ?? throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
                );

            object?[] semanticIdentityKey = new object?[mergePlan.SemanticIdentityBindings.Length];

            for (var index = 0; index < mergePlan.SemanticIdentityBindings.Length; index++)
            {
                var semanticIdentityBinding = mergePlan.SemanticIdentityBindings[index];
                semanticIdentityKey[index] = ExtractLiteralValue(
                    tableWritePlan,
                    values[semanticIdentityBinding.BindingIndex],
                    tableWritePlan.ColumnBindings[semanticIdentityBinding.BindingIndex].Column.ColumnName
                );
            }

            return semanticIdentityKey;
        }

        private static object?[]? TryProjectLiteralLookupKey(IReadOnlyList<FlattenedWriteValue> values)
        {
            object?[] lookupKey = new object?[values.Count];

            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] is not FlattenedWriteValue.Literal literalValue)
                {
                    return null;
                }

                lookupKey[index] = literalValue.Value;
            }

            return lookupKey;
        }

        private static object? ExtractLiteralValue(
            TableWritePlan tableWritePlan,
            FlattenedWriteValue value,
            DbColumnName columnName
        ) =>
            value is FlattenedWriteValue.Literal literalValue
                ? literalValue.Value
                : throw new InvalidOperationException(
                    $"Table '{FormatTable(tableWritePlan)}' expected a literal value for column '{columnName.Value}' during current-state merge synthesis."
                );
    }

    private sealed class BoundRowComparer(IReadOnlyList<int> bindingIndexes)
        : IComparer<RelationalWriteNoProfileTableRow>
    {
        public int Compare(RelationalWriteNoProfileTableRow? left, RelationalWriteNoProfileTableRow? right)
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

    private sealed class ObjectValueArrayComparer : IEqualityComparer<object?[]>
    {
        public static ObjectValueArrayComparer Instance { get; } = new();

        public bool Equals(object?[]? left, object?[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (!Equals(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] values)
        {
            ArgumentNullException.ThrowIfNull(values);

            HashCode hashCode = new();
            hashCode.Add(values.Length);

            foreach (var value in values)
            {
                hashCode.Add(value);
            }

            return hashCode.ToHashCode();
        }
    }
}
