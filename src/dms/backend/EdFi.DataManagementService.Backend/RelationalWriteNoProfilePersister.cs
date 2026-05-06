// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text;
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

internal sealed class RelationalWriteNoProfilePersister : IRelationalWritePersister
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
                "Relational no-profile persistence requires an executor-resolved target context."
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
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ExecuteUpsertsAsync(
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
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        foreach (var tableState in mergeResult.TablesInDependencyOrder.Reverse())
        {
            if (RelationalWriteMergeSupport.IsCollectionAlignedExtensionScope(tableState.TableWritePlan))
            {
                await DeleteOmittedCollectionAlignedScopeRowsAsync(
                        dialect,
                        tableState,
                        rootDocumentId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                continue;
            }

            if (tableState.TableWritePlan.CollectionMergePlan is not null)
            {
                if (
                    tableState.TableWritePlan.TableModel.IdentityMetadata.TableKind
                    is not (DbTableKind.Collection or DbTableKind.ExtensionCollection)
                )
                {
                    continue;
                }

                await DeleteOmittedCollectionRowsAsync(
                        dialect,
                        tableState,
                        rootDocumentId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                continue;
            }

            await DeleteOmittedNonCollectionRowAsync(
                    tableState,
                    rootDocumentId,
                    reservedCollectionItemIds,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteUpsertsAsync(
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
            List<RelationalWriteMergedTableState> deferredTableStates = [];
            var persistedTableCount = 0;

            foreach (var tableState in pendingTableStates)
            {
                if (HasBlockingUnresolvedCollectionItemIds(tableState, reservedCollectionItemIds))
                {
                    deferredTableStates.Add(tableState);
                    continue;
                }

                if (RelationalWriteMergeSupport.IsCollectionAlignedExtensionScope(tableState.TableWritePlan))
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

                if (tableState.TableWritePlan.CollectionMergePlan is not null)
                {
                    if (
                        tableState.TableWritePlan.TableModel.IdentityMetadata.TableKind
                        is not (DbTableKind.Collection or DbTableKind.ExtensionCollection)
                    )
                    {
                        persistedTableCount++;
                        continue;
                    }

                    await UpsertCollectionRowsAsync(
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

                await UpsertNonCollectionRowAsync(
                        tableState,
                        rootDocumentId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                persistedTableCount++;
            }

            if (deferredTableStates.Count == 0)
            {
                return;
            }

            if (persistedTableCount == 0)
            {
                throw new InvalidOperationException(
                    "Relational write upserts could not resolve collection-id dependencies for tables: "
                        + string.Join(
                            ", ",
                            deferredTableStates.Select(tableState => FormatTable(tableState.TableWritePlan))
                        )
                );
            }

            pendingTableStates = [.. deferredTableStates];
        }
    }

    private static bool HasBlockingUnresolvedCollectionItemIds(
        RelationalWriteMergedTableState tableState,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds
    )
    {
        var selfReservedBindingIndex = tableState.TableWritePlan.CollectionKeyPreallocationPlan?.BindingIndex;

        foreach (var mergedRow in tableState.MergedRows)
        {
            foreach (
                var (value, bindingIndex) in mergedRow.Values.Select(
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

    private static async Task<long> ResolveRootDocumentIdAsync(
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

    private static async Task<long> InsertDocumentAsync(
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

    private static RelationalCommand BuildInsertDocumentCommand(
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

    private static async Task DeleteOmittedNonCollectionRowAsync(
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableState.TableWritePlan);
        var mergedRow = GetSingleRowOrThrow(tableState.MergedRows, "merged", tableState.TableWritePlan);

        if (currentRow is null || mergedRow is not null)
        {
            return;
        }

        if (tableState.TableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' cannot delete an omitted scope because no DeleteByParentSql was compiled."
            );
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildRowCommand(
                    tableState.TableWritePlan,
                    tableState.TableWritePlan.DeleteByParentSql,
                    currentRow,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpsertNonCollectionRowAsync(
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableState.TableWritePlan);
        var mergedRow = GetSingleRowOrThrow(tableState.MergedRows, "merged", tableState.TableWritePlan);

        if (mergedRow is null)
        {
            return;
        }

        if (currentRow is null)
        {
            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableState.TableWritePlan,
                        tableState.TableWritePlan.InsertSql,
                        mergedRow,
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return;
        }

        if (currentRow.Values.SequenceEqual(mergedRow.Values))
        {
            return;
        }

        if (tableState.TableWritePlan.UpdateSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' requires UpdateSql to persist a changed non-collection row."
            );
        }

        await ExecuteNonQueryAsync(
                writeSession,
                BuildRowCommand(
                    tableState.TableWritePlan,
                    tableState.TableWritePlan.UpdateSql,
                    mergedRow,
                    rootDocumentId,
                    reservedCollectionItemIds
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task DeleteOmittedCollectionAlignedScopeRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergedRowsByPhysicalIdentity = GetRowsByPhysicalIdentityOrThrow(
            tableState.MergedRows,
            "merged",
            tableState.TableWritePlan
        );

        if (tableState.TableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableState.TableWritePlan)}' cannot delete an omitted aligned scope because no DeleteByParentSql was compiled."
            );
        }

        List<RelationalWriteMergedTableRow> rowsToDelete = [];

        foreach (var currentRow in tableState.CurrentRows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableState.TableWritePlan, currentRow);

            if (mergedRowsByPhysicalIdentity.ContainsKey(physicalIdentity))
            {
                continue;
            }

            rowsToDelete.Add(currentRow);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                tableState.TableWritePlan.DeleteByParentSql,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitDeleteByParentBatch(tableState.TableWritePlan, rowCount),
                rowsToDelete,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpsertCollectionAlignedScopeRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRowsByPhysicalIdentity = GetRowsByPhysicalIdentityOrThrow(
            tableState.CurrentRows,
            "current",
            tableState.TableWritePlan
        );
        List<RelationalWriteMergedTableRow> rowsToUpdate = [];
        List<RelationalWriteMergedTableRow> rowsToInsert = [];

        foreach (var mergedRow in tableState.MergedRows)
        {
            var physicalIdentity = ResolvePhysicalRowIdentityKey(tableState.TableWritePlan, mergedRow);

            if (!currentRowsByPhysicalIdentity.TryGetValue(physicalIdentity, out var currentRow))
            {
                rowsToInsert.Add(mergedRow);
                continue;
            }

            if (currentRow.Values.SequenceEqual(mergedRow.Values))
            {
                continue;
            }

            if (tableState.TableWritePlan.UpdateSql is null)
            {
                throw new InvalidOperationException(
                    $"Table '{FormatTable(tableState.TableWritePlan)}' requires UpdateSql to persist a changed aligned scope row."
                );
            }

            rowsToUpdate.Add(mergedRow);
        }

        await ExecuteParameterizedBatchesAsync(
                dialect,
                tableState.TableWritePlan,
                tableState.TableWritePlan.UpdateSql!,
                (batchSqlEmitter, rowCount) =>
                    batchSqlEmitter.EmitUpdateBatch(tableState.TableWritePlan, rowCount),
                rowsToUpdate,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

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

    private static async Task DeleteOmittedCollectionRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
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
        var retainedStableRowIdentities = GetRetainedStableRowIdentities(tableState);
        List<RelationalWriteMergedTableRow> rowsToDelete = [];

        foreach (var currentRow in tableState.CurrentRows)
        {
            var stableRowIdentity = ResolveStableRowIdentityLiteral(
                tableState.TableWritePlan,
                currentRow.Values[mergePlan.StableRowIdentityBindingIndex]
            );

            if (retainedStableRowIdentities.Contains(stableRowIdentity))
            {
                continue;
            }

            rowsToDelete.Add(currentRow);
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
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task UpsertCollectionRowsAsync(
        SqlDialect dialect,
        RelationalWriteMergedTableState tableState,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );
        var currentRowsByStableRowIdentity = tableState.CurrentRows.ToDictionary(currentRow =>
            ResolveStableRowIdentityLiteral(
                tableState.TableWritePlan,
                currentRow.Values[mergePlan.StableRowIdentityBindingIndex]
            )
        );
        List<RelationalWriteMergedTableRow> rowsToUpdate = [];
        List<RelationalWriteMergedTableRow> rowsToInsert = [];
        var hasOrdinalReorder = false;

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[mergePlan.StableRowIdentityBindingIndex];

            if (stableRowIdentityValue is FlattenedWriteValue.UnresolvedCollectionItemId)
            {
                rowsToInsert.Add(mergedRow);
                continue;
            }

            var stableRowIdentity = ResolveStableRowIdentityLiteral(
                tableState.TableWritePlan,
                stableRowIdentityValue
            );

            if (!currentRowsByStableRowIdentity.TryGetValue(stableRowIdentity, out var currentRow))
            {
                throw new InvalidOperationException(
                    $"Collection table '{FormatTable(tableState.TableWritePlan)}' produced a merged row for stable identity "
                        + $"'{stableRowIdentity}', but no current row with that identity was loaded."
                );
            }

            if (currentRow.Values.SequenceEqual(mergedRow.Values))
            {
                continue;
            }

            rowsToUpdate.Add(mergedRow);

            if (
                !Equals(
                    currentRow.Values[mergePlan.OrdinalBindingIndex],
                    mergedRow.Values[mergePlan.OrdinalBindingIndex]
                )
            )
            {
                hasOrdinalReorder = true;
            }
        }

        // Batched collection updates emit sequential UPDATE statements. For multi-row reorders, move the affected
        // siblings to temporary negative ordinals first so swaps do not trip the unique (ParentScope, Ordinal)
        // constraint before the final contiguous ordinals are applied.
        if (rowsToUpdate.Count > 1 && hasOrdinalReorder)
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
                    CreateTemporaryOrdinalRows(rowsToUpdate, mergePlan.OrdinalBindingIndex),
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
                rowsToUpdate,
                rootDocumentId,
                reservedCollectionItemIds,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        foreach (
            var insertBatch in rowsToInsert.Chunk(
                tableState.TableWritePlan.BulkInsertBatching.MaxRowsPerBatch
            )
        )
        {
            await ReserveCollectionItemIdsAsync(
                    dialect,
                    GetStableRowIdentityTokens(insertBatch, mergePlan.StableRowIdentityBindingIndex),
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

    private static HashSet<long> GetRetainedStableRowIdentities(RelationalWriteMergedTableState tableState)
    {
        var mergePlan =
            tableState.TableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableState.TableWritePlan)}' does not have a compiled collection merge plan."
            );
        HashSet<long> retainedStableRowIdentities = [];

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[mergePlan.StableRowIdentityBindingIndex];

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
    )
    {
        return stableRowIdentityValue switch
        {
            FlattenedWriteValue.Literal(var value) => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' expected a literal stable row identity during persistence."
            ),
        };
    }

    private static async Task ReserveCollectionItemIdAsync(
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

    private static async Task ReserveCollectionItemIdsAsync(
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

    private static RelationalCommand BuildReserveCollectionItemIdCommand(SqlDialect dialect)
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

    private static RelationalCommand BuildReserveCollectionItemIdsCommand(SqlDialect dialect, int count)
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

    private static async Task<long[]> ReadReservedCollectionItemIdsAsync(
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

    private static async Task ExecuteNonQueryAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await dbCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteParameterizedBatchAsync(
        WritePlanBatchSqlEmitter batchSqlEmitter,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
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

    private static async Task ExecuteParameterizedBatchesAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        string sql,
        Func<WritePlanBatchSqlEmitter, int, string> emitBatchSql,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
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

    private static async Task ExecuteCollectionInsertBatchAsync(
        SqlDialect dialect,
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
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

    private static RelationalWriteMergedTableRow? GetSingleRowOrThrow(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    )
    {
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' produced {rows.Count} {rowKind} rows during no-profile persistence. "
                    + "Only zero or one row is supported before collection merge execution lands."
            ),
        };
    }

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
            StringComparer.Ordinal
        );

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

    private static string ResolvePhysicalRowIdentityKey(
        TableWritePlan tableWritePlan,
        RelationalWriteMergedTableRow row
    )
    {
        var identityColumns = tableWritePlan.TableModel.IdentityMetadata.PhysicalRowIdentityColumns;

        if (identityColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' does not define physical row identity metadata."
            );
        }

        StringBuilder builder = new();

        for (var index = 0; index < identityColumns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('|');
            }

            var bindingIndex = FindBindingIndex(tableWritePlan, identityColumns[index]);
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

    private static RelationalCommand BuildRowCommand(
        TableWritePlan tableWritePlan,
        string sql,
        RelationalWriteMergedTableRow row,
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

    private static RelationalCommand BuildBatchCommand(
        string sql,
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
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

    private static IReadOnlyList<RelationalWriteMergedTableRow> CreateTemporaryOrdinalRows(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int ordinalBindingIndex
    )
    {
        RelationalWriteMergedTableRow[] temporaryRows = new RelationalWriteMergedTableRow[rows.Count];

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

    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetStableRowIdentityTokens(
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int stableRowIdentityBindingIndex
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds = [];

        foreach (var row in rows)
        {
            if (
                row.Values[stableRowIdentityBindingIndex]
                is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
            )
            {
                unresolvedCollectionItemIds.Add(unresolvedCollectionItemId);
            }
        }

        return unresolvedCollectionItemIds;
    }

    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetUnresolvedCollectionItemIds(
        IReadOnlyList<RelationalWriteMergedTableRow> rows
    )
    {
        List<FlattenedWriteValue.UnresolvedCollectionItemId> unresolvedCollectionItemIds = [];

        foreach (var row in rows)
        {
            unresolvedCollectionItemIds.AddRange(GetUnresolvedCollectionItemIds(row.Values));
        }

        return unresolvedCollectionItemIds;
    }

    private static IReadOnlyList<FlattenedWriteValue.UnresolvedCollectionItemId> GetUnresolvedCollectionItemIds(
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

    private static object? ResolveParameterValue(
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

    private static string NormalizeParameterName(string parameterName)
    {
        return parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";
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
}
