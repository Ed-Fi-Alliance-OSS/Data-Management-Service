// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal interface IRelationalWriteNonCollectionPersister
{
    Task<bool> TryPersistAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteNoProfileMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteNonCollectionPersister : IRelationalWriteNonCollectionPersister
{
    public async Task<bool> TryPersistAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteNoProfileMergeResult mergeResult,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);
        ArgumentNullException.ThrowIfNull(writeSession);

        var rootDocumentId = await ResolveRootDocumentIdAsync(
                request.MappingSet,
                request.WritePlan.Model.Resource,
                request.TargetContext,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds = [];

        await ExecuteDeletesAsync(
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

        return true;
    }

    private static async Task ExecuteDeletesAsync(
        RelationalWriteNoProfileMergeResult mergeResult,
        long rootDocumentId,
        IReadOnlyDictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        foreach (var tableState in mergeResult.TablesInDependencyOrder.Reverse())
        {
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
        RelationalWriteNoProfileMergeResult mergeResult,
        long rootDocumentId,
        Dictionary<FlattenedWriteValue.UnresolvedCollectionItemId, long> reservedCollectionItemIds,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        foreach (var tableState in mergeResult.TablesInDependencyOrder)
        {
            if (tableState.TableWritePlan.CollectionMergePlan is not null)
            {
                if (
                    tableState.TableWritePlan.TableModel.IdentityMetadata.TableKind
                    is not (DbTableKind.Collection or DbTableKind.ExtensionCollection)
                )
                {
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
        }
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
        RelationalWriteNoProfileTableState tableState,
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
        RelationalWriteNoProfileTableState tableState,
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

    private static async Task DeleteOmittedCollectionRowsAsync(
        RelationalWriteNoProfileTableState tableState,
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

            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableState.TableWritePlan,
                        mergePlan.DeleteByStableRowIdentitySql,
                        currentRow,
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task UpsertCollectionRowsAsync(
        SqlDialect dialect,
        RelationalWriteNoProfileTableState tableState,
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

        foreach (var mergedRow in tableState.MergedRows)
        {
            var stableRowIdentityValue = mergedRow.Values[mergePlan.StableRowIdentityBindingIndex];

            if (
                stableRowIdentityValue
                is FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
            )
            {
                await ReserveCollectionItemIdAsync(
                        dialect,
                        unresolvedCollectionItemId,
                        reservedCollectionItemIds,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

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

            await ExecuteNonQueryAsync(
                    writeSession,
                    BuildRowCommand(
                        tableState.TableWritePlan,
                        mergePlan.UpdateByStableRowIdentitySql,
                        mergedRow,
                        rootDocumentId,
                        reservedCollectionItemIds
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static HashSet<long> GetRetainedStableRowIdentities(RelationalWriteNoProfileTableState tableState)
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

    private static async Task ExecuteNonQueryAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await using var dbCommand = writeSession.CreateCommand(command);
        await dbCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RelationalWriteNoProfileTableRow? GetSingleRowOrThrow(
        IReadOnlyList<RelationalWriteNoProfileTableRow> rows,
        string rowKind,
        TableWritePlan tableWritePlan
    )
    {
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' produced {rows.Count} {rowKind} rows during non-collection persistence. "
                    + "Only zero or one row is supported before collection merge execution lands."
            ),
        };
    }

    private static RelationalCommand BuildRowCommand(
        TableWritePlan tableWritePlan,
        string sql,
        RelationalWriteNoProfileTableRow row,
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

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
}
