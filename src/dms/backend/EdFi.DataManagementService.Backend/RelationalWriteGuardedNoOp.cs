// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteGuardedNoOp
{
    public static bool IsNoOpCandidate(RelationalWriteMergeResult mergeResult)
    {
        ArgumentNullException.ThrowIfNull(mergeResult);

        foreach (var tableState in mergeResult.TablesInDependencyOrder)
        {
            if (tableState.CurrentRows.Length != tableState.MergedRows.Length)
            {
                return false;
            }

            for (var rowIndex = 0; rowIndex < tableState.CurrentRows.Length; rowIndex++)
            {
                if (
                    !RowsEqualForGuardedNoOp(
                        tableState.TableWritePlan,
                        tableState.CurrentRows[rowIndex],
                        tableState.MergedRows[rowIndex]
                    )
                )
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool RowsEqualForGuardedNoOp(
        TableWritePlan tableWritePlan,
        RelationalWriteMergedTableRow currentRow,
        RelationalWriteMergedTableRow mergedRow
    )
    {
        if (
            tableWritePlan.CollectionMergePlan is not null
            && !ProjectCollectionNoOpIdentityValues(tableWritePlan, currentRow.Values)
                .SequenceEqual(ProjectCollectionNoOpIdentityValues(tableWritePlan, mergedRow.Values))
        )
        {
            return false;
        }

        return currentRow.ComparableValues.SequenceEqual(mergedRow.ComparableValues);
    }

    private static ImmutableArray<FlattenedWriteValue> ProjectCollectionNoOpIdentityValues(
        TableWritePlan tableWritePlan,
        ImmutableArray<FlattenedWriteValue> values
    )
    {
        var identityMetadata = tableWritePlan.TableModel.IdentityMetadata;
        var seenColumnNames = new HashSet<DbColumnName>();
        var builder = ImmutableArray.CreateBuilder<FlattenedWriteValue>();

        AppendBindingValues(
            tableWritePlan,
            values,
            identityMetadata.PhysicalRowIdentityColumns,
            seenColumnNames,
            builder
        );
        AppendBindingValues(
            tableWritePlan,
            values,
            identityMetadata.RootScopeLocatorColumns,
            seenColumnNames,
            builder
        );
        AppendBindingValues(
            tableWritePlan,
            values,
            identityMetadata.ImmediateParentScopeLocatorColumns,
            seenColumnNames,
            builder
        );

        return builder.ToImmutable();
    }

    private static void AppendBindingValues(
        TableWritePlan tableWritePlan,
        ImmutableArray<FlattenedWriteValue> values,
        IReadOnlyList<DbColumnName> columnNames,
        HashSet<DbColumnName> seenColumnNames,
        ImmutableArray<FlattenedWriteValue>.Builder builder
    )
    {
        foreach (var columnName in columnNames)
        {
            if (!seenColumnNames.Add(columnName))
            {
                continue;
            }

            var bindingIndex = RelationalWriteMergeSupport.FindBindingIndex(tableWritePlan, columnName);
            builder.Add(values[bindingIndex]);
        }
    }
}

internal interface IRelationalWriteFreshnessChecker
{
    Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteFreshnessChecker : IRelationalWriteFreshnessChecker
{
    public async Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(writeSession);

        await using var command = writeSession.CreateCommand(
            RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(
                request.MappingSet.Key.Dialect,
                targetContext.DocumentId
            )
        );

        var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            return false;
        }

        var currentContentVersion = Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture);

        return currentContentVersion == targetContext.ObservedContentVersion;
    }
}
