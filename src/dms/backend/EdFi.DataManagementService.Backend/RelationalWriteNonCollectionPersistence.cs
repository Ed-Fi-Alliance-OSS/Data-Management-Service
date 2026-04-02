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

        if (HasPendingCollectionChanges(mergeResult))
        {
            return false;
        }

        var rootDocumentId = await ResolveRootDocumentIdAsync(
                request.MappingSet,
                request.WritePlan.Model.Resource,
                request.TargetContext,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        foreach (var tableState in mergeResult.TablesInDependencyOrder)
        {
            if (tableState.TableWritePlan.CollectionMergePlan is not null)
            {
                continue;
            }

            await PersistTableStateAsync(tableState, rootDocumentId, writeSession, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private static bool HasPendingCollectionChanges(RelationalWriteNoProfileMergeResult mergeResult)
    {
        foreach (var tableState in mergeResult.TablesInDependencyOrder)
        {
            if (tableState.TableWritePlan.CollectionMergePlan is null)
            {
                continue;
            }

            if (TableRequiresChange(tableState))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TableRequiresChange(RelationalWriteNoProfileTableState tableState)
    {
        if (tableState.CurrentRows.Length != tableState.MergedRows.Length)
        {
            return true;
        }

        for (var rowIndex = 0; rowIndex < tableState.CurrentRows.Length; rowIndex++)
        {
            if (
                !tableState.CurrentRows[rowIndex].Values.SequenceEqual(tableState.MergedRows[rowIndex].Values)
            )
            {
                return true;
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

    private static async Task PersistTableStateAsync(
        RelationalWriteNoProfileTableState tableState,
        long rootDocumentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var currentRow = GetSingleRowOrThrow(tableState.CurrentRows, "current", tableState.TableWritePlan);
        var mergedRow = GetSingleRowOrThrow(tableState.MergedRows, "merged", tableState.TableWritePlan);

        if (currentRow is null && mergedRow is null)
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
                        mergedRow!,
                        rootDocumentId
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return;
        }

        if (mergedRow is null)
        {
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
                        rootDocumentId
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
                    rootDocumentId
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
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
        long rootDocumentId
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
                rootDocumentId
            );

            parameters.Add(new RelationalParameter(parameterName, parameterValue));
        }

        return new RelationalCommand(sql, parameters);
    }

    private static object? ResolveParameterValue(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue value,
        long rootDocumentId
    )
    {
        return value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue,
            FlattenedWriteValue.UnresolvedRootDocumentId => rootDocumentId,
            FlattenedWriteValue.UnresolvedCollectionItemId => throw new InvalidOperationException(
                $"Table '{FormatTable(tableWritePlan)}' still contains an unresolved CollectionItemId. "
                    + "Collection persistence must complete before this row can be written."
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
