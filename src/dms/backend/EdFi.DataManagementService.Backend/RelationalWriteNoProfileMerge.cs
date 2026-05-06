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

internal interface IRelationalWriteNoProfileMergeSynthesizer
{
    RelationalWriteMergeResult Synthesize(RelationalWriteNoProfileMergeRequest request);
}

internal sealed class RelationalWriteNoProfileMergeSynthesizer : IRelationalWriteNoProfileMergeSynthesizer
{
    public RelationalWriteMergeResult Synthesize(RelationalWriteNoProfileMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentStateProjection = CurrentStateProjection.Create(request);
        var tableStateBuilders = CreateTableStateBuilders(request.WritePlan, currentStateProjection);

        var rootRow = CreateMergedRow(
            request.FlattenedWriteSet.RootRow.TableWritePlan,
            request.FlattenedWriteSet.RootRow.Values
        );
        tableStateBuilders[rootRow.TableWritePlan.TableModel.Table].AddMergedRow(rootRow.Row);

        var rootPhysicalRowIdentityValues = RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
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

        return new RelationalWriteMergeResult(
            request.WritePlan.TablePlansInDependencyOrder.Select(tableWritePlan =>
                tableStateBuilders[tableWritePlan.TableModel.Table].Build()
            ),
            supportsGuardedNoOp: true
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

            var scopePhysicalRowIdentityValues = RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
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

            var scopePhysicalRowIdentityValues = RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
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

            var collectionPhysicalRowIdentityValues =
                RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
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
        var row = RelationalWriteRowHelpers.CreateMergedTableRow(tableWritePlan, values);

        return new MergedRow(tableWritePlan, row);
    }

    private static ImmutableArray<FlattenedWriteValue> RewriteParentKeyPartValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
    ) =>
        RelationalWriteRowHelpers.RewriteParentKeyPartValues(
            tableWritePlan,
            values,
            parentPhysicalRowIdentityValues
        );

    private static ImmutableArray<FlattenedWriteValue> RewriteCollectionStableRowIdentity(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> matchedCurrentRowValues
    ) =>
        RelationalWriteRowHelpers.RewriteCollectionStableRowIdentity(
            tableWritePlan,
            values,
            matchedCurrentRowValues
        );

    private sealed record MergedRow(TableWritePlan TableWritePlan, RelationalWriteMergedTableRow Row);

    private sealed class TableStateBuilder(
        TableWritePlan tableWritePlan,
        ImmutableArray<RelationalWriteMergedTableRow> currentRows
    )
    {
        private readonly List<RelationalWriteMergedTableRow> _mergedRows = [];

        public void AddMergedRow(RelationalWriteMergedTableRow row)
        {
            ArgumentNullException.ThrowIfNull(row);
            _mergedRows.Add(row);
        }

        public RelationalWriteMergedTableState Build() =>
            RelationalWriteMergeSupport.BuildTableStateForComparison(
                tableWritePlan,
                currentRows,
                _mergedRows
            );
    }

    private sealed class CurrentStateProjection
    {
        private readonly IReadOnlyDictionary<
            DbTableName,
            ImmutableArray<RelationalWriteMergedTableRow>
        > _currentRowsByTable;

        private readonly IReadOnlyDictionary<
            DbTableName,
            ProjectedCollectionTableState
        > _collectionRowsByTable;

        private CurrentStateProjection(
            IReadOnlyDictionary<
                DbTableName,
                ImmutableArray<RelationalWriteMergedTableRow>
            > currentRowsByTable,
            IReadOnlyDictionary<DbTableName, ProjectedCollectionTableState> collectionRowsByTable
        )
        {
            _currentRowsByTable = currentRowsByTable;
            _collectionRowsByTable = collectionRowsByTable;
        }

        public static CurrentStateProjection Create(RelationalWriteNoProfileMergeRequest request)
        {
            Dictionary<DbTableName, ImmutableArray<RelationalWriteMergedTableRow>> currentRowsByTable = [];
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
                    ? RelationalWriteMergeSupport.ProjectCurrentRows(tableWritePlan, hydratedTableRows.Rows)
                    : ImmutableArray<RelationalWriteMergedTableRow>.Empty;

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

        public ImmutableArray<RelationalWriteMergedTableRow> GetCurrentRows(TableWritePlan tableWritePlan) =>
            _currentRowsByTable.TryGetValue(tableWritePlan.TableModel.Table, out var currentRows)
                ? currentRows
                : ImmutableArray<RelationalWriteMergedTableRow>.Empty;

        public RelationalWriteMergedTableRow? TryMatchCollectionRow(
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
    }

    private sealed class ProjectedCollectionTableState
    {
        private readonly Dictionary<
            object?[],
            Dictionary<object?[], RelationalWriteMergedTableRow>
        > _rowsByParentKey;

        public ProjectedCollectionTableState(
            TableWritePlan tableWritePlan,
            IReadOnlyList<RelationalWriteMergedTableRow> currentRows
        )
        {
            TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));

            _rowsByParentKey = new Dictionary<
                object?[],
                Dictionary<object?[], RelationalWriteMergedTableRow>
            >(ObjectValueArrayComparer.Instance);

            foreach (var currentRow in currentRows)
            {
                var parentScopeKey = ExtractParentScopeKey(tableWritePlan, currentRow.Values);
                var semanticIdentityKey = ExtractSemanticIdentityKey(tableWritePlan, currentRow.Values);

                if (!_rowsByParentKey.TryGetValue(parentScopeKey, out var rowsBySemanticIdentity))
                {
                    rowsBySemanticIdentity = new Dictionary<object?[], RelationalWriteMergedTableRow>(
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

        public RelationalWriteMergedTableRow? TryMatch(
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
                    values[
                        RelationalWriteMergeSupport.FindBindingIndex(
                            tableWritePlan,
                            immediateParentScopeColumns[index]
                        )
                    ],
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

        private static string FormatTable(TableWritePlan tableWritePlan) =>
            $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
    }
}
