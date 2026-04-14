// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal interface IRelationalWritePersister
{
    Task<RelationalWritePersistResult> PersistAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWritePersister : IRelationalWritePersister
{
    public async Task<RelationalWritePersistResult> PersistAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);
        var targetContext =
            request.TargetContext
            ?? throw new InvalidOperationException(
                "Relational write persistence requires an executor-resolved target context."
            );

        var rootDocumentId = await ResolveRootDocumentIdAsync(
                request.MappingSet,
                request.WritePlan.Model.Resource,
                targetContext,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds = [];

        await ExecuteDeletesAsync(
                request.MappingSet.Key.Dialect,
                mergeResult,
                rootDocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ExecuteInsertsAndUpdatesAsync(
                request.MappingSet.Key.Dialect,
                mergeResult,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new RelationalWritePersistResult(rootDocumentId, GetTargetDocumentUuid(targetContext));
    }

    private static DocumentUuid GetTargetDocumentUuid(RelationalWriteTargetContext targetContext) =>
        targetContext switch
        {
            RelationalWriteTargetContext.CreateNew(var documentUuid) => documentUuid,
            RelationalWriteTargetContext.ExistingDocument(_, var documentUuid, _) => documentUuid,
            _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
        };

    private static async Task ExecuteDeletesAsync(
        SqlDialect dialect,
        RelationalWriteMergeResult mergeResult,
        long rootDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        foreach (var tableState in mergeResult.TablesInDependencyOrder.Reverse())
        {
            if (IsCollectionAlignedExtensionScope(tableState.TableWritePlan))
            {
                await DeleteCollectionAlignedScopeRowsByPhysicalIdentityAsync(
                        dialect,
                        tableState,
                        rootDocumentId,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                continue;
            }

            if (tableState.TableWritePlan.CollectionMergePlan is not null)
            {
                await DeleteCollectionRowsByStableIdentityAsync(
                        dialect,
                        tableState,
                        rootDocumentId,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                continue;
            }

            if (tableState.Deletes.Length == 0)
            {
                continue;
            }

            await DeleteNonCollectionRowByParentAsync(
                    tableState,
                    rootDocumentId,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteInsertsAndUpdatesAsync(
        SqlDialect dialect,
        RelationalWriteMergeResult mergeResult,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var pendingTableStates = mergeResult.TablesInDependencyOrder.ToArray();

        while (pendingTableStates.Length > 0)
        {
            List<RelationalWriteMergeTableState> deferredTableStates = [];
            var persistedTableCount = 0;

            foreach (var tableState in pendingTableStates)
            {
                if (HasBlockingUnresolvedCollectionItemIds(tableState, reservedCollectionItemIds))
                {
                    deferredTableStates.Add(tableState);
                    continue;
                }

                if (IsCollectionAlignedExtensionScope(tableState.TableWritePlan))
                {
                    await UpsertCollectionAlignedScopeRowsAsync(
                            dialect,
                            tableState,
                            rootDocumentId,
                            reservedCollectionItemIds,
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    persistedTableCount++;
                    continue;
                }

                // Updates
                if (tableState.Updates.Length > 0)
                {
                    if (tableState.TableWritePlan.CollectionMergePlan is not null)
                    {
                        await UpdateCollectionRowsByStableIdentityAsync(
                                dialect,
                                tableState,
                                rootDocumentId,
                                reservedCollectionItemIds,
                                writeSession,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await UpdateNonCollectionRowAsync(
                                tableState,
                                rootDocumentId,
                                reservedCollectionItemIds,
                                writeSession,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                }

                // Inserts
                if (tableState.Inserts.Length > 0)
                {
                    await InsertRowsAsync(
                            dialect,
                            tableState,
                            rootDocumentId,
                            reservedCollectionItemIds,
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                persistedTableCount++;
            }

            if (deferredTableStates.Count == 0)
            {
                return;
            }

            if (persistedTableCount == 0)
            {
                throw new InvalidOperationException(
                    "Relational write could not resolve collection-id dependencies for tables: "
                        + string.Join(
                            ", ",
                            deferredTableStates.Select(tableState => FormatTable(tableState.TableWritePlan))
                        )
                );
            }

            pendingTableStates = [.. deferredTableStates];
        }
    }

    private static async Task UpsertCollectionAlignedScopeRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRowsByPhysicalIdentity = RelationalWritePersisterShared.GetRowsByPhysicalIdentityOrThrow(
            tableState.ComparableCurrentRowset,
            "current",
            tableState.TableWritePlan
        );
        var rowsToUpdate = tableState
            .Updates.Select(update => new MergeTableRow(
                update.Values,
                ImmutableArray<FlattenedWriteValue>.Empty
            ))
            .Where(updateRow =>
            {
                var physicalIdentity = RelationalWritePersisterShared.ResolvePhysicalRowIdentityKey(
                    tableState.TableWritePlan,
                    updateRow
                );

                return !currentRowsByPhysicalIdentity.TryGetValue(physicalIdentity, out var currentRow)
                    || !currentRow.Values.SequenceEqual(updateRow.Values);
            })
            .ToList();

        if (rowsToUpdate.Count > 0)
        {
            if (tableState.TableWritePlan.UpdateSql is null)
            {
                throw new InvalidOperationException(
                    $"Table '{FormatTable(tableState.TableWritePlan)}' requires UpdateSql to persist a changed aligned scope row."
                );
            }

            await ExecuteParameterizedBatchesAsync(
                    dialect,
                    tableState.TableWritePlan,
                    tableState.TableWritePlan.UpdateSql,
                    (batchSqlEmitter, rowCount) =>
                        batchSqlEmitter.EmitUpdateBatch(tableState.TableWritePlan, rowCount),
                    rowsToUpdate,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var rowsToInsert = ConvertInsertRowsToBatchRows(tableState.Inserts);

        foreach (
            var insertBatch in rowsToInsert.Chunk(
                tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch
            )
        )
        {
            await ReserveCollectionItemIdsAsync(
                    dialect,
                    GetUnresolvedCollectionItemIds(insertBatch),
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await ExecuteCollectionInsertBatchAsync(
                    dialect,
                    tableState.TableWritePlan,
                    insertBatch,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static bool HasBlockingUnresolvedCollectionItemIds(
        RelationalWriteMergeTableState tableState,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    ) =>
        RelationalWritePersisterShared.HasBlockingUnresolvedCollectionItemIds(
            tableState.Inserts.Select(i => i.Values).Concat(tableState.Updates.Select(u => u.Values)),
            tableState.TableWritePlan.CollectionKeyPreallocationPlan?.BindingIndex,
            reservedCollectionItemIds
        );

    private static async Task DeleteCollectionRowsByStableIdentityAsync(
        SqlDialect dialect,
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );
        var comparableCurrentRowsByStableId = tableState.ComparableCurrentRowset.ToDictionary(currentRow =>
            currentRow.Values[mergePlan.StableRowIdentityBindingIndex]
                is FlattenedWriteValue.Literal { Value: long stableId }
                ? stableId
                : throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableState.TableWritePlan)}' expected a literal stable row identity in ComparableCurrentRowset."
                )
        );
        HashSet<long> mergedStableRowIds = [];

        foreach (var mergedRow in tableState.ComparableMergedRowset)
        {
            if (
                mergedRow.Values[mergePlan.StableRowIdentityBindingIndex] is FlattenedWriteValue.Literal
                {
                    Value: long stableId
                }
            )
            {
                mergedStableRowIds.Add(stableId);
            }
        }

        List<MergeTableRow> rowsToDelete = [];
        HashSet<long> queuedStableRowIds = [];

        foreach (var currentRow in tableState.ComparableCurrentRowset)
        {
            var stableRowIdentityValue = currentRow.Values[mergePlan.StableRowIdentityBindingIndex]
                is FlattenedWriteValue.Literal { Value: long stableId }
                ? stableId
                : throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableState.TableWritePlan)}' expected a literal stable row identity in ComparableCurrentRowset."
                );

            if (mergedStableRowIds.Contains(stableRowIdentityValue))
            {
                continue;
            }

            rowsToDelete.Add(currentRow);
            queuedStableRowIds.Add(stableRowIdentityValue);
        }

        foreach (var delete in tableState.Deletes)
        {
            var stableRowIdentityValue =
                delete.StableRowIdentityValue
                ?? throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableState.TableWritePlan)}' received a profile delete without a StableRowIdentityValue."
                );

            if (queuedStableRowIds.Contains(stableRowIdentityValue))
            {
                continue;
            }

            if (comparableCurrentRowsByStableId.TryGetValue(stableRowIdentityValue, out var currentRow))
            {
                rowsToDelete.Add(currentRow);
                queuedStableRowIds.Add(stableRowIdentityValue);
                continue;
            }

            var values = new FlattenedWriteValue[tableState.TableWritePlan.ColumnBindings.Length];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] =
                    i == mergePlan.StableRowIdentityBindingIndex
                        ? new FlattenedWriteValue.Literal(stableRowIdentityValue)
                        : new FlattenedWriteValue.Literal(null);
            }

            rowsToDelete.Add(new MergeTableRow([.. values], ImmutableArray<FlattenedWriteValue>.Empty));
            queuedStableRowIds.Add(stableRowIdentityValue);
        }

        if (rowsToDelete.Count == 0)
        {
            return;
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                mergePlan.DeleteByStableRowIdentitySql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(
                        tableState.TableWritePlan,
                        rowCount
                    ),
                rowsToDelete,
                rootDocumentId,
                new Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long>(),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task DeleteNonCollectionRowByParentAsync(
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (tableState.TableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' cannot delete an omitted scope because no DeleteByParentSql was compiled."
            );
        }

        if (tableState.ComparableCurrentRowset.Length == 1)
        {
            Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> emptyReservedIds = [];

            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommandFromValues(
                        tableState.TableWritePlan,
                        tableState.TableWritePlan.DeleteByParentSql,
                        tableState.ComparableCurrentRowset[0].Values,
                        rootDocumentId,
                        emptyReservedIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return;
        }

        List<RelationalParameter> parameters = [];

        for (
            var bindingIndex = 0;
            bindingIndex < tableState.TableWritePlan.ColumnBindings.Length;
            bindingIndex++
        )
        {
            var binding = tableState.TableWritePlan.ColumnBindings[bindingIndex];
            var parameterName = NormalizeParameterName(binding.ParameterName);

            object? parameterValue = binding.Source switch
            {
                WriteValueSource.DocumentId => rootDocumentId,
                WriteValueSource.ParentKeyPart => rootDocumentId,
                _ => DBNull.Value,
            };

            parameters.Add(new RelationalParameter(parameterName, parameterValue));
        }

        await ExecuteNonQueryAsync(
                writeSession,
                new RelationalCommand(tableState.TableWritePlan.DeleteByParentSql, parameters),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes collection-aligned extension scope rows by comparing current vs merged rows
    /// using physical row identity. Unlike non-collection scopes, the parent key for these
    /// tables is a CollectionItemId (not the root DocumentId), so the actual row data must
    /// be used for parameter binding.
    /// </summary>
    private static async Task DeleteCollectionAlignedScopeRowsByPhysicalIdentityAsync(
        SqlDialect dialect,
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergedRowsByPhysicalIdentity = RelationalWritePersisterShared.GetRowsByPhysicalIdentityOrThrow(
            tableState.ComparableMergedRowset,
            "merged",
            tableState.TableWritePlan
        );

        if (tableState.TableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' cannot delete an omitted aligned scope because no DeleteByParentSql was compiled."
            );
        }

        List<MergeTableRow> rowsToDelete = [];

        foreach (var currentRow in tableState.ComparableCurrentRowset)
        {
            var physicalIdentity = RelationalWritePersisterShared.ResolvePhysicalRowIdentityKey(
                tableState.TableWritePlan,
                currentRow
            );

            if (mergedRowsByPhysicalIdentity.ContainsKey(physicalIdentity))
            {
                continue;
            }

            rowsToDelete.Add(currentRow);
        }

        if (rowsToDelete.Count == 0)
        {
            return;
        }

        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> emptyReservedIds = [];

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                tableState.TableWritePlan.DeleteByParentSql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitDeleteByParentBatch(tableState.TableWritePlan, rowCount),
                rowsToDelete,
                rootDocumentId,
                emptyReservedIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpdateCollectionRowsByStableIdentityAsync(
        SqlDialect dialect,
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );

        // Build current-row lookup by stable identity for no-op detection and ordinal comparison
        var currentRowsByStableId = new Dictionary<long, MergeTableRow>();
        foreach (var currentRow in tableState.ComparableCurrentRowset)
        {
            var stableValue = currentRow.Values[mergePlan.StableRowIdentityBindingIndex];
            if (stableValue is FlattenedWriteValue.Literal { Value: long stableId })
            {
                currentRowsByStableId[stableId] = currentRow;
            }
        }

        // Per-row no-op: filter out updates whose values are identical to current state
        List<MergeRowUpdate> changedUpdates = new(tableState.Updates.Length);
        foreach (var update in tableState.Updates)
        {
            if (
                update.StableRowIdentityValue is long sid
                && currentRowsByStableId.TryGetValue(sid, out var existingRow)
                && existingRow.Values.SequenceEqual(update.Values)
            )
            {
                continue;
            }
            changedUpdates.Add(update);
        }

        if (changedUpdates.Count == 0)
        {
            return;
        }

        // Detect ordinal reorders among actually changed rows
        var hasOrdinalReorder = false;
        if (changedUpdates.Count > 1)
        {
            foreach (var update in changedUpdates)
            {
                if (
                    update.StableRowIdentityValue is long updateStableId
                    && currentRowsByStableId.TryGetValue(updateStableId, out var currentRow)
                    && !Equals(
                        currentRow.Values[mergePlan.OrdinalBindingIndex],
                        update.Values[mergePlan.OrdinalBindingIndex]
                    )
                )
                {
                    hasOrdinalReorder = true;
                    break;
                }
            }
        }

        var rowsForBatch = ConvertUpdateRowsToBatchRows(changedUpdates);

        if (hasOrdinalReorder)
        {
            await ExecuteParameterizedBatchesAsync(
                    dialect,
                    tableState.TableWritePlan,
                    mergePlan.UpdateByStableRowIdentitySql,
                    (batchSqlEmitter, rowCount) =>
                        batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(
                            tableState.TableWritePlan,
                            rowCount
                        ),
                    CreateTemporaryOrdinalRows(rowsForBatch, mergePlan.OrdinalBindingIndex),
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                mergePlan.UpdateByStableRowIdentitySql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(
                        tableState.TableWritePlan,
                        rowCount
                    ),
                rowsForBatch,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpdateNonCollectionRowAsync(
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        // Per-row no-op: skip updates whose values are identical to current state
        var currentRow =
            tableState.ComparableCurrentRowset.Length == 1 ? tableState.ComparableCurrentRowset[0] : null;

        var updatesToApply = currentRow is not null
            ? tableState.Updates.Where(u => !currentRow.Values.SequenceEqual(u.Values)).ToList()
            : tableState.Updates.ToList();

        if (updatesToApply.Count == 0)
        {
            return;
        }

        if (tableState.TableWritePlan.UpdateSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' requires UpdateSql to persist a changed non-collection row."
            );
        }

        foreach (var update in updatesToApply)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommandFromValues(
                        tableState.TableWritePlan,
                        tableState.TableWritePlan.UpdateSql,
                        update.Values,
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task InsertRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergeTableState tableState,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var rowsForBatch = ConvertInsertRowsToBatchRows(tableState.Inserts);

        foreach (
            var insertBatch in rowsForBatch.Chunk(
                tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch
            )
        )
        {
            await ReserveCollectionItemIdsAsync(
                    dialect,
                    GetUnresolvedCollectionItemIds(insertBatch),
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await ExecuteCollectionInsertBatchAsync(
                    dialect,
                    tableState.TableWritePlan,
                    insertBatch,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    // ─── Conversion helpers ──────────────────────────────────────────────

    /// <summary>
    /// Converts merge insert rows to the batch-row format used by
    /// the shared batch execution and parameter-building infrastructure.
    /// </summary>
    private static List<MergeTableRow> ConvertInsertRowsToBatchRows(ImmutableArray<MergeRowInsert> inserts)
    {
        List<MergeTableRow> rows = new(inserts.Length);

        foreach (var insert in inserts)
        {
            rows.Add(new MergeTableRow(insert.Values, ImmutableArray<FlattenedWriteValue>.Empty));
        }

        return rows;
    }

    /// <summary>
    /// Converts merge update rows to the batch-row format used by
    /// the shared batch execution and parameter-building infrastructure.
    /// </summary>
    private static List<MergeTableRow> ConvertUpdateRowsToBatchRows(IReadOnlyList<MergeRowUpdate> updates) =>
        updates
            .Select(update => new MergeTableRow(update.Values, ImmutableArray<FlattenedWriteValue>.Empty))
            .ToList();

    // ─── Root DocumentId resolution ──────────────────────────────────────

    private static async Task<long> ResolveRootDocumentIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteTargetContext targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    ) =>
        await RelationalWritePersisterShared
            .ResolveRootDocumentIdAsync(mappingSet, resource, targetContext, writeSession, cancellationToken)
            .ConfigureAwait(false);

    // ─── CollectionItemId reservation ────────────────────────────────────

    private static async Task ReserveCollectionItemIdsAsync(
        SqlDialect dialect,
        IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds,
        IDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    ) =>
        await RelationalWritePersisterShared
            .ReserveCollectionItemIdsAsync(
                dialect,
                unresolvedCollectionItemIds,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

    // ─── Batch execution infrastructure ──────────────────────────────────

    private static async Task ExecuteNonQueryAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    ) =>
        await RelationalWritePersisterShared
            .ExecuteNonQueryAsync(writeSession, command, cancellationToken)
            .ConfigureAwait(false);

    private static async Task ExecuteParameterizedBatchesAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<MergeTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    ) =>
        await RelationalWritePersisterShared
            .ExecuteParameterizedBatchesAsync(
                dialect,
                tableWritePlan,
                sql,
                emitBatchSql,
                rows,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

    private static async Task ExecuteCollectionInsertBatchAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        IReadOnlyList<MergeTableRow> rows,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    ) =>
        await RelationalWritePersisterShared
            .ExecuteCollectionInsertBatchAsync(
                dialect,
                tableWritePlan,
                rows,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

    // ─── Temporary ordinal rows for safe multi-row reorders ──────────────

    private static IReadOnlyList<MergeTableRow> CreateTemporaryOrdinalRows(
        IReadOnlyList<MergeTableRow> rows,
        int ordinalBindingIndex
    ) => RelationalWritePersisterShared.CreateTemporaryOrdinalRows(rows, ordinalBindingIndex);

    // ─── Unresolved CollectionItemId extraction ──────────────────────────

    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetUnresolvedCollectionItemIds(
        IReadOnlyList<MergeTableRow> rows
    ) => RelationalWritePersisterShared.GetUnresolvedCollectionItemIds(rows);

    // ─── Command building ────────────────────────────────────────────────

    private static RelationalCommand BuildRowCommandFromValues(
        TableWritePlan tableWritePlan,
        string sql,
        ImmutableArray<FlattenedWriteValue> values,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    ) =>
        RelationalWritePersisterShared.BuildRowCommandFromValues(
            tableWritePlan,
            sql,
            values,
            rootDocumentId,
            reservedCollectionItemIds
        );

    private static string NormalizeParameterName(string parameterName) =>
        RelationalWritePersisterShared.NormalizeParameterName(parameterName);

    private static bool IsCollectionAlignedExtensionScope(TableWritePlan tableWritePlan) =>
        RelationalWriteMergeShared.IsCollectionAlignedExtensionScope(tableWritePlan);

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        RelationalWritePersisterShared.FormatTable(tableWritePlan);
}
