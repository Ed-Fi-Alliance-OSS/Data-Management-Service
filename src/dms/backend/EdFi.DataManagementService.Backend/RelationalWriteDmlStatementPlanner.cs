// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// One data-modifying statement a write owes: a table's rows for a single statement kind, with the
/// compiled plan SQL that applies them.
/// </summary>
/// <param name="Label">Diagnostic name, <c>kind:schema.table</c>, unique within one plan.</param>
/// <param name="TableWritePlan">The table the rows belong to.</param>
/// <param name="SingleRowSql">The compiled statement for exactly one row.</param>
/// <param name="EmitBatchSql">Emits the same statement for a given row count.</param>
/// <param name="Rows">
/// The rows to apply, in order. Never empty. Splitting them into round trips belongs to the caller: the
/// per-statement path splits at the table's bulk-insert row cap, the co-batched path packs against the
/// command's parameter budget.
/// </param>
internal sealed record RelationalWriteDmlStatement(
    string Label,
    TableWritePlan TableWritePlan,
    string SingleRowSql,
    Func<int, string> EmitBatchSql,
    IReadOnlyList<RelationalWriteMergedTableRow> Rows
);

/// <summary>
/// Every data-modifying statement a write owes, already in the order the correctness invariants require,
/// plus the collection keys that must be reserved before any of them is sent.
/// </summary>
internal sealed record RelationalWriteDmlStatementPlan(
    IReadOnlyList<RelationalWriteDmlStatement> StatementsInOrder,
    IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> CollectionItemIdsToReserve
);

/// <summary>
/// Resolves which data-modifying statements a merged write owes and the order they must be applied in.
/// </summary>
/// <remarks>
/// <para>
/// The order is the whole point of planning separately from executing. Deletes precede upserts, children
/// precede parents on delete, and parents precede children on insert. The last of those is not the compiled
/// dependency order: a collection-aligned extension scope's rows carry the parent collection row's
/// generated <c>CollectionItemId</c>, and the compiled order may place that scope ahead of the collection
/// it hangs off. A table is therefore held back while any of its rows still references a collection key
/// that some other table's statement produces, which puts the parent's insert first and keeps the child's
/// foreign key satisfiable.
/// </para>
/// <para>
/// Holding a table back is decided from the merged rows alone, never from whether a key has been reserved
/// yet. That is what lets one shared reservation command serve every table: were the ordering to depend on
/// reservation state, reserving up front would leave nothing to hold back and the child would be applied
/// before its parent.
/// </para>
/// </remarks>
internal static class RelationalWriteDmlStatementPlanner
{
    public static RelationalWriteDmlStatementPlan Plan(
        SqlDialect dialect,
        RelationalWriteMergeResult mergeResult,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(collectionItemIdBindings);

        var batchSqlEmitter = new WritePlanBatchSqlEmitter(dialect);
        List<RelationalWriteDmlStatement> statements = [];

        foreach (var tableState in mergeResult.TablesInDependencyOrder.Reverse())
        {
            AppendDeletes(statements, batchSqlEmitter, tableState);
        }

        foreach (var tableState in ResolveUpsertOrder(mergeResult, collectionItemIdBindings))
        {
            AppendUpserts(statements, batchSqlEmitter, tableState);
        }

        return new RelationalWriteDmlStatementPlan(
            statements,
            CollectFallbackCollectionItemIds(mergeResult, collectionItemIdBindings)
        );
    }

    /// <summary>
    /// Orders the upserted tables so that every collection key a table's rows reference is produced by an
    /// earlier statement. Fails loudly rather than reordering when no table can make progress.
    /// </summary>
    private static IReadOnlyList<RelationalWriteMergedTableState> ResolveUpsertOrder(
        RelationalWriteMergeResult mergeResult,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        List<RelationalWriteMergedTableState> ordered = new(mergeResult.TablesInDependencyOrder.Length);
        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> producedTokens = [];
        IReadOnlyList<RelationalWriteMergedTableState> pending = mergeResult.TablesInDependencyOrder;

        while (pending.Count > 0)
        {
            List<RelationalWriteMergedTableState> deferred = new(pending.Count);

            foreach (var tableState in pending)
            {
                if (HasUnproducedCollectionItemIds(tableState, collectionItemIdBindings, producedTokens))
                {
                    deferred.Add(tableState);
                    continue;
                }

                ordered.Add(tableState);
                AddOwnedCollectionItemIds(tableState, producedTokens);
            }

            if (deferred.Count == 0)
            {
                return ordered;
            }

            if (deferred.Count == pending.Count)
            {
                throw new InvalidOperationException(
                    "Relational write upserts could not resolve collection-id dependencies for tables: "
                        + string.Join(
                            ", ",
                            deferred.Select(tableState =>
                                RelationalWriteFlattener.FormatTable(tableState.TableWritePlan)
                            )
                        )
                );
            }

            pending = deferred;
        }

        return ordered;
    }

    private static bool HasUnproducedCollectionItemIds(
        RelationalWriteMergedTableState tableState,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings,
        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> producedTokens
    )
    {
        var ownedKeyBindingIndex = tableState.TableWritePlan.CollectionKeyPreallocationPlan?.BindingIndex;

        foreach (var values in tableState.MergedRows.Select(static mergedRow => mergedRow.Values))
        {
            for (var bindingIndex = 0; bindingIndex < values.Length; bindingIndex++)
            {
                if (bindingIndex == ownedKeyBindingIndex)
                {
                    continue;
                }

                // An inlined key is produced by the row that carries it, so it can never be the value a
                // dependent table is still waiting on.
                if (
                    values[bindingIndex] is FlattenedWriteValue.UnresolvedCollectionItemId token
                    && !collectionItemIdBindings.IsInlined(token)
                    && !producedTokens.Contains(token)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddOwnedCollectionItemIds(
        RelationalWriteMergedTableState tableState,
        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> producedTokens
    )
    {
        if (tableState.TableWritePlan.CollectionKeyPreallocationPlan is not { } preallocationPlan)
        {
            return;
        }

        foreach (var mergedRow in tableState.MergedRows)
        {
            if (
                mergedRow.Values[preallocationPlan.BindingIndex]
                is FlattenedWriteValue.UnresolvedCollectionItemId token
            )
            {
                producedTokens.Add(token);
            }
        }
    }

    /// <summary>
    /// The collection keys no statement can produce inline, in first-occurrence order. A key referenced by a
    /// table other than the one that owns it must be a client-side value, because a statement cannot read a
    /// value the sequence produced inside another statement's row.
    /// </summary>
    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> CollectFallbackCollectionItemIds(
        RelationalWriteMergeResult mergeResult,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> tokensInOrder = [];
        HashSet<FlattenedWriteValue.UnresolvedCollectionItemId> seen = [];

        var valuesInOrder = mergeResult.TablesInDependencyOrder.SelectMany(tableState =>
            tableState.MergedRows.SelectMany(mergedRow => mergedRow.Values)
        );

        foreach (var value in valuesInOrder)
        {
            if (
                value is FlattenedWriteValue.UnresolvedCollectionItemId token
                && !collectionItemIdBindings.IsInlined(token)
                && seen.Add(token)
            )
            {
                tokensInOrder.Add(token);
            }
        }

        return tokensInOrder;
    }

    private static void AppendDeletes(
        List<RelationalWriteDmlStatement> statements,
        WritePlanBatchSqlEmitter batchSqlEmitter,
        RelationalWriteMergedTableState tableState
    )
    {
        var tableWritePlan = tableState.TableWritePlan;

        if (RelationalWriteMergeSupport.IsCollectionAlignedExtensionScope(tableWritePlan))
        {
            var mergedRowsByPhysicalIdentity = GetRowsByPhysicalIdentityOrThrow(
                tableState.MergedRows,
                "merged",
                tableWritePlan
            );
            var deleteByParentSql =
                tableWritePlan.DeleteByParentSql
                ?? throw new InvalidOperationException(
                    $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' cannot delete an "
                        + "omitted aligned scope because no DeleteByParentSql was compiled."
                );

            AddStatement(
                statements,
                "delete-aligned-scope",
                tableWritePlan,
                deleteByParentSql,
                rowCount => batchSqlEmitter.EmitDeleteByParentBatch(tableWritePlan, rowCount),
                [
                    .. tableState.CurrentRows.Where(currentRow =>
                        !mergedRowsByPhysicalIdentity.ContainsKey(
                            ResolvePhysicalRowIdentityKey(tableWritePlan, currentRow)
                        )
                    ),
                ]
            );

            return;
        }

        if (tableWritePlan.CollectionMergePlan is { } collectionMergePlan)
        {
            if (
                tableWritePlan.TableModel.IdentityMetadata.TableKind
                is not (DbTableKind.Collection or DbTableKind.ExtensionCollection)
            )
            {
                return;
            }

            var retainedStableRowIdentities = GetRetainedStableRowIdentities(tableState);

            AddStatement(
                statements,
                "delete-collection",
                tableWritePlan,
                collectionMergePlan.DeleteByStableRowIdentitySql,
                rowCount =>
                    batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(tableWritePlan, rowCount),
                [
                    .. tableState.CurrentRows.Where(currentRow =>
                        !retainedStableRowIdentities.Contains(
                            ResolveStableRowIdentityLiteral(
                                tableWritePlan,
                                currentRow.Values[collectionMergePlan.StableRowIdentityBindingIndex]
                            )
                        )
                    ),
                ]
            );

            return;
        }

        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableWritePlan);

        if (
            currentRow is null
            || GetSingleRowOrThrow(tableState.MergedRows, "merged", tableWritePlan) is not null
        )
        {
            return;
        }

        AddStatement(
            statements,
            "delete-scope",
            tableWritePlan,
            tableWritePlan.DeleteByParentSql
                ?? throw new InvalidOperationException(
                    $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' cannot delete an "
                        + "omitted scope because no DeleteByParentSql was compiled."
                ),
            rowCount => batchSqlEmitter.EmitDeleteByParentBatch(tableWritePlan, rowCount),
            [currentRow]
        );
    }

    private static void AppendUpserts(
        List<RelationalWriteDmlStatement> statements,
        WritePlanBatchSqlEmitter batchSqlEmitter,
        RelationalWriteMergedTableState tableState
    )
    {
        var tableWritePlan = tableState.TableWritePlan;

        if (RelationalWriteMergeSupport.IsCollectionAlignedExtensionScope(tableWritePlan))
        {
            AppendAlignedScopeUpserts(statements, batchSqlEmitter, tableState);
            return;
        }

        if (tableWritePlan.CollectionMergePlan is not null)
        {
            if (
                tableWritePlan.TableModel.IdentityMetadata.TableKind
                is DbTableKind.Collection
                    or DbTableKind.ExtensionCollection
            )
            {
                AppendCollectionUpserts(statements, batchSqlEmitter, tableState);
            }

            return;
        }

        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableWritePlan);
        var mergedRow = GetSingleRowOrThrow(tableState.MergedRows, "merged", tableWritePlan);

        if (mergedRow is null)
        {
            return;
        }

        if (currentRow is null)
        {
            AddStatement(
                statements,
                "insert-scope",
                tableWritePlan,
                tableWritePlan.InsertSql,
                rowCount => batchSqlEmitter.EmitInsertBatch(tableWritePlan, rowCount),
                [mergedRow]
            );

            return;
        }

        if (currentRow.Values.SequenceEqual(mergedRow.Values))
        {
            return;
        }

        AddStatement(
            statements,
            "update-scope",
            tableWritePlan,
            tableWritePlan.UpdateSql
                ?? throw new InvalidOperationException(
                    $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' requires UpdateSql to "
                        + "persist a changed non-collection row."
                ),
            rowCount => batchSqlEmitter.EmitUpdateBatch(tableWritePlan, rowCount),
            [mergedRow]
        );
    }

    private static void AppendAlignedScopeUpserts(
        List<RelationalWriteDmlStatement> statements,
        WritePlanBatchSqlEmitter batchSqlEmitter,
        RelationalWriteMergedTableState tableState
    )
    {
        var tableWritePlan = tableState.TableWritePlan;
        var currentRowsByPhysicalIdentity = GetRowsByPhysicalIdentityOrThrow(
            tableState.CurrentRows,
            "current",
            tableWritePlan
        );
        List<RelationalWriteMergedTableRow> rowsToUpdate = new(tableState.MergedRows.Length);
        List<RelationalWriteMergedTableRow> rowsToInsert = new(tableState.MergedRows.Length);

        foreach (var mergedRow in tableState.MergedRows)
        {
            if (
                !currentRowsByPhysicalIdentity.TryGetValue(
                    ResolvePhysicalRowIdentityKey(tableWritePlan, mergedRow),
                    out var currentRow
                )
            )
            {
                rowsToInsert.Add(mergedRow);
                continue;
            }

            if (currentRow.Values.SequenceEqual(mergedRow.Values))
            {
                continue;
            }

            if (tableWritePlan.UpdateSql is null)
            {
                throw new InvalidOperationException(
                    $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' requires UpdateSql to "
                        + "persist a changed aligned scope row."
                );
            }

            rowsToUpdate.Add(mergedRow);
        }

        AddStatement(
            statements,
            "update-aligned-scope",
            tableWritePlan,
            tableWritePlan.UpdateSql!,
            rowCount => batchSqlEmitter.EmitUpdateBatch(tableWritePlan, rowCount),
            rowsToUpdate
        );
        AddStatement(
            statements,
            "insert-aligned-scope",
            tableWritePlan,
            tableWritePlan.InsertSql,
            rowCount => batchSqlEmitter.EmitInsertBatch(tableWritePlan, rowCount),
            rowsToInsert
        );
    }

    private static void AppendCollectionUpserts(
        List<RelationalWriteDmlStatement> statements,
        WritePlanBatchSqlEmitter batchSqlEmitter,
        RelationalWriteMergedTableState tableState
    )
    {
        var tableWritePlan = tableState.TableWritePlan;
        var collectionMergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' does not have a "
                    + "compiled collection merge plan."
            );
        Dictionary<long, RelationalWriteMergedTableRow> currentRowsByStableRowIdentity = new(
            tableState.CurrentRows.Length
        );

        foreach (var currentRow in tableState.CurrentRows)
        {
            currentRowsByStableRowIdentity.Add(
                ResolveStableRowIdentityLiteral(
                    tableWritePlan,
                    currentRow.Values[collectionMergePlan.StableRowIdentityBindingIndex]
                ),
                currentRow
            );
        }

        List<RelationalWriteMergedTableRow> rowsToUpdate = new(tableState.MergedRows.Length);
        List<RelationalWriteMergedTableRow> rowsToInsert = new(tableState.MergedRows.Length);
        var hasOrdinalReorder = false;

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[collectionMergePlan.StableRowIdentityBindingIndex];

            if (stableRowIdentityValue is FlattenedWriteValue.UnresolvedCollectionItemId)
            {
                rowsToInsert.Add(mergedRow);
                continue;
            }

            var stableRowIdentity = ResolveStableRowIdentityLiteral(tableWritePlan, stableRowIdentityValue);

            if (!currentRowsByStableRowIdentity.TryGetValue(stableRowIdentity, out var currentRow))
            {
                throw new InvalidOperationException(
                    $"Collection table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' produced a "
                        + $"merged row for stable identity '{stableRowIdentity}', but no current row with "
                        + "that identity was loaded."
                );
            }

            if (currentRow.Values.SequenceEqual(mergedRow.Values))
            {
                continue;
            }

            rowsToUpdate.Add(mergedRow);

            if (
                !Equals(
                    currentRow.Values[collectionMergePlan.OrdinalBindingIndex],
                    mergedRow.Values[collectionMergePlan.OrdinalBindingIndex]
                )
            )
            {
                hasOrdinalReorder = true;
            }
        }

        // Batched collection updates emit sequential UPDATE statements. For multi-row reorders, move the
        // affected siblings to temporary negative ordinals first so swaps do not trip the unique
        // (ParentScope, Ordinal) constraint before the final contiguous ordinals are applied.
        if (rowsToUpdate.Count > 1 && hasOrdinalReorder)
        {
            AddStatement(
                statements,
                "update-collection-temporary-ordinals",
                tableWritePlan,
                collectionMergePlan.UpdateByStableRowIdentitySql,
                rowCount =>
                    batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(tableWritePlan, rowCount),
                CreateTemporaryOrdinalRows(rowsToUpdate, collectionMergePlan.OrdinalBindingIndex)
            );
        }

        AddStatement(
            statements,
            "update-collection",
            tableWritePlan,
            collectionMergePlan.UpdateByStableRowIdentitySql,
            rowCount =>
                batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(tableWritePlan, rowCount),
            rowsToUpdate
        );
        AddStatement(
            statements,
            "insert-collection",
            tableWritePlan,
            tableWritePlan.InsertSql,
            rowCount => batchSqlEmitter.EmitInsertBatch(tableWritePlan, rowCount),
            rowsToInsert
        );
    }

    /// <summary>
    /// Records a statement unless it owes no rows, so a plan never carries a statement whose emitted SQL
    /// would apply to nothing.
    /// </summary>
    private static void AddStatement(
        List<RelationalWriteDmlStatement> statements,
        string kind,
        TableWritePlan tableWritePlan,
        string singleRowSql,
        Func<int, string> emitBatchSql,
        IReadOnlyList<RelationalWriteMergedTableRow> rows
    )
    {
        if (rows.Count == 0)
        {
            return;
        }

        statements.Add(
            new RelationalWriteDmlStatement(
                $"{kind}:{RelationalWriteFlattener.FormatTable(tableWritePlan)}",
                tableWritePlan,
                singleRowSql,
                emitBatchSql,
                rows
            )
        );
    }

    private static HashSet<long> GetRetainedStableRowIdentities(RelationalWriteMergedTableState tableState)
    {
        var collectionMergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                "Collection table "
                    + $"'{RelationalWriteFlattener.FormatTable(tableState.TableWritePlan)}' does not have a "
                    + "compiled collection merge plan."
            );
        HashSet<long> retainedStableRowIdentities = new(tableState.MergedRows.Length);

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[collectionMergePlan.StableRowIdentityBindingIndex];

            if (stableRowIdentityValue is FlattenedWriteValue.UnresolvedCollectionItemId)
            {
                continue;
            }

            retainedStableRowIdentities.Add(
                ResolveStableRowIdentityLiteral(tableState.TableWritePlan, stableRowIdentityValue)
            );
        }

        return retainedStableRowIdentities;
    }

    private static long ResolveStableRowIdentityLiteral(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue stableRowIdentityValue
    ) =>
        stableRowIdentityValue switch
        {
            FlattenedWriteValue.Literal(var value) => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Collection table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' expected a "
                    + "literal stable row identity during persistence."
            ),
        };

    private static RelationalWriteMergedTableRow? GetSingleRowOrThrow(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    ) =>
        rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' produced {rows.Count} "
                    + $"{rowKind} rows during no-profile persistence. Only zero or one row is supported "
                    + "before collection merge execution lands."
            ),
        };

    private static IReadOnlyDictionary<
        string,
        RelationalWriteMergedTableRow
    > GetRowsByPhysicalIdentityOrThrow(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    )
    {
        Dictionary<string, RelationalWriteMergedTableRow> rowsByPhysicalIdentity = new(
            rows.Count,
            StringComparer.Ordinal
        );

        foreach (var row in rows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableWritePlan, row);

            if (!rowsByPhysicalIdentity.TryAdd(physicalIdentity, row))
            {
                throw new InvalidOperationException(
                    $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' produced duplicate "
                        + $"{rowKind} rows for aligned scope physical identity '{physicalIdentity}'."
                );
            }
        }

        return rowsByPhysicalIdentity;
    }

    private static string ResolvePhysicalRowIdentityKey(
        TableWritePlan tableWritePlan,
        RelationalWriteMergedTableRow row
    )
    {
        var identityColumns = tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns;

        if (identityColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' does not define physical "
                    + "row identity metadata."
            );
        }

        StringBuilder builder = new(CalculatePhysicalRowIdentityKeyCapacity(identityColumns));

        for (var index = 0; index < identityColumns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('|');
            }

            var bindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
                tableWritePlan,
                identityColumns[index]
            );
            builder.Append(identityColumns[index].Value);
            builder.Append('=');
            builder.Append(FormatPhysicalRowIdentityValue(row.Values[bindingIndex]));
        }

        return builder.ToString();
    }

    private static string FormatPhysicalRowIdentityValue(FlattenedWriteValue value) =>
        value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue is null
                ? "literal:<null>"
                : $"literal:{literalValue.GetType().FullName}:{literalValue}",
            FlattenedWriteValue.UnresolvedRootDocumentId => "document:<unresolved>",
            FlattenedWriteValue.UnresolvedCollectionItemId(var token) => $"collection:{token}",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static int CalculatePhysicalRowIdentityKeyCapacity(IReadOnlyList<DbColumnName> identityColumns)
    {
        var capacity = Math.Max(0, identityColumns.Count - 1);

        foreach (var identityColumn in identityColumns)
        {
            capacity += identityColumn.Value.Length + 1 + 32;
        }

        return capacity;
    }

    private static IReadOnlyList<RelationalWriteMergedTableRow> CreateTemporaryOrdinalRows(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int ordinalBindingIndex
    )
    {
        var temporaryRows = new RelationalWriteMergedTableRow[rows.Count];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var temporaryValues = rows[rowIndex].Values.ToArray();
            temporaryValues[ordinalBindingIndex] = new FlattenedWriteValue.Literal(-1 - rowIndex);

            temporaryRows[rowIndex] = new RelationalWriteMergedTableRow(
                temporaryValues,
                rows[rowIndex].ComparableValues
            );
        }

        return temporaryRows;
    }
}
