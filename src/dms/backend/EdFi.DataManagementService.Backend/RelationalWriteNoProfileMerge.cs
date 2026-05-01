// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;

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
                collectionCandidate.SemanticIdentityInOrder
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

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";

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

        public RelationalWriteMergedTableState Build()
        {
            var currentRowsForComparison = IsCollectionAlignedExtensionScope(tableWritePlan)
                ? OrderCollectionAlignedExtensionScopeRowsIfFullyBound(tableWritePlan, currentRows)
                : currentRows;
            IReadOnlyList<RelationalWriteMergedTableRow> mergedRows;

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

            return new RelationalWriteMergedTableState(tableWritePlan, currentRowsForComparison, mergedRows);
        }

        private static IReadOnlyList<RelationalWriteMergedTableRow> OrderCollectionRowsIfFullyBound(
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
                    RelationalWriteMergeSupport.FindBindingIndex(tableWritePlan, columnName)
                )
                .Append(mergePlan.OrdinalBindingIndex)
                .ToArray();

            return OrderRowsByBindingIndexesIfFullyBound(rows, parentBindingIndexes);
        }

        private static IReadOnlyList<RelationalWriteMergedTableRow> OrderCollectionAlignedExtensionScopeRowsIfFullyBound(
            TableWritePlan tableWritePlan,
            IReadOnlyList<RelationalWriteMergedTableRow> rows
        )
        {
            var parentBindingIndexes = tableWritePlan
                .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(columnName =>
                    RelationalWriteMergeSupport.FindBindingIndex(tableWritePlan, columnName)
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

        private static bool IsCollectionAlignedExtensionScope(TableWritePlan tableWritePlan) =>
            tableWritePlan.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope;
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
            ImmutableArray<SemanticIdentityPart> requestSemanticIdentity
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
                requestSemanticIdentity
            );
        }
    }

    /// <summary>
    /// Per-table current-state index keyed by (parent-scope key, semantic identity key) for
    /// no-profile collection matching. Both the constructor and <see cref="TryMatch"/> route
    /// the identity through <see cref="SemanticIdentityKeys.BuildKey"/> deliberately — the
    /// presence-aware shared key path preserves missing-vs-explicit-null fidelity in the
    /// no-profile flow the same way the profile-aware merge does. Regression coverage for
    /// this end-to-end behavior lives in
    /// <c>RelationalWriteNoProfileMergeSynthesizerTests</c> (see the
    /// missing-vs-explicit-null collection-identity matching tests).
    /// </summary>
    private sealed class ProjectedCollectionTableState
    {
        private readonly Dictionary<
            object?[],
            Dictionary<string, RelationalWriteMergedTableRow>
        > _rowsByParentKey;

        public ProjectedCollectionTableState(
            TableWritePlan tableWritePlan,
            IReadOnlyList<RelationalWriteMergedTableRow> currentRows
        )
        {
            TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));

            _rowsByParentKey = new Dictionary<object?[], Dictionary<string, RelationalWriteMergedTableRow>>(
                ObjectValueArrayComparer.Instance
            );

            foreach (var currentRow in currentRows)
            {
                var parentScopeKey = ExtractParentScopeKey(tableWritePlan, currentRow.Values);
                var semanticIdentityKey = SemanticIdentityKeys.BuildKey(
                    BuildCurrentRowSemanticIdentityParts(tableWritePlan, currentRow.Values)
                );

                if (!_rowsByParentKey.TryGetValue(parentScopeKey, out var rowsBySemanticIdentity))
                {
                    rowsBySemanticIdentity = new Dictionary<string, RelationalWriteMergedTableRow>(
                        StringComparer.Ordinal
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
            ImmutableArray<SemanticIdentityPart> requestSemanticIdentity
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

            var semanticIdentityKey = SemanticIdentityKeys.BuildKey(requestSemanticIdentity);

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

        // Builds the SemanticIdentityPart sequence for a DB-projected current row using the
        // same scope-relative path convention as the flattener (and Core's
        // AddressDerivationEngine.ReadSemanticIdentity / ProfileCollectionWalker stored-row
        // projection). Stored rows always count as IsPresent: true: a row that exists in the
        // table had each bound column persisted, even when the persisted value is SQL NULL.
        // This mirrors the rule documented in ProfileCollectionWalker.cs and prevents a
        // request candidate with a missing identity part from matching a current row whose
        // persisted identity column happens to be NULL.
        //
        // Counterparts: RelationalWriteFlattener.MaterializeSemanticIdentityParts (request-
        // side, presence probed against the request JSON) and ProfileCollectionWalker's
        // inline current-row projection (DB-row, prefers Core-emitted identity paths from
        // semanticIdentityPathsByCollectionScope before falling back to scope-relative
        // normalization). The three remain separate because their presence and path-source
        // rules differ; only the shared scope-relative path normalization is centralized in
        // RelationalWriteMergeSupport.ToScopeRelativePath.
        private static ImmutableArray<SemanticIdentityPart> BuildCurrentRowSemanticIdentityParts(
            TableWritePlan tableWritePlan,
            IReadOnlyList<FlattenedWriteValue> values
        )
        {
            var mergePlan =
                tableWritePlan.CollectionMergePlan
                ?? throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
                );

            var scopeCanonical = tableWritePlan.TableModel.JsonScope.Canonical;
            var bindings = mergePlan.SemanticIdentityBindings;
            var parts = new SemanticIdentityPart[bindings.Length];

            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                var rawValue = ExtractLiteralValue(
                    tableWritePlan,
                    values[binding.BindingIndex],
                    tableWritePlan.ColumnBindings[binding.BindingIndex].Column.ColumnName
                );
                JsonNode? jsonValue = rawValue is null ? null : JsonValue.Create(rawValue);
                var relativePath = RelationalWriteMergeSupport.ToScopeRelativePath(
                    binding.RelativePath.Canonical,
                    scopeCanonical
                );
                parts[index] = new SemanticIdentityPart(relativePath, jsonValue, IsPresent: true);
            }

            return [.. parts];
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
