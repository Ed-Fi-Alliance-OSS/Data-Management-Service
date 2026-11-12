// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Compares an existing document's finalized root identity-projection values with its stored
/// values. On SQL Server a value that differs from the stored one only under the identity
/// comparer (<see cref="IdentityEqualityContract.ComparerFor"/>) is not an identity change: the
/// stored value is copied back into the merged root row, per column, so proposed authorization,
/// guarded no-op detection and writer DML all see stored casing. Any other difference is a
/// genuine identity change and fails closed unless the resource allows identity updates.
/// </summary>
internal static class RelationalWriteIdentityStability
{
    public static (RelationalWriteMergeResult MergeResult, RelationalWriteExecutorResult? Failure) Apply(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);

        if (request.TargetContext is not RelationalWriteTargetContext.ExistingDocument)
        {
            return (mergeResult, null);
        }

        var rootTableIndex = -1;

        for (var index = 0; index < mergeResult.TablesInDependencyOrder.Length; index++)
        {
            if (
                mergeResult
                    .TablesInDependencyOrder[index]
                    .TableWritePlan.TableModel.Table.Equals(request.WritePlan.Model.Root.Table)
            )
            {
                rootTableIndex = index;
                break;
            }
        }

        if (rootTableIndex < 0)
        {
            throw new InvalidOperationException(
                $"No-profile merge synthesis for resource '{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}' "
                    + $"did not include root table '{request.WritePlan.Model.Root.Table}'. This indicates an internal merge-synthesis bug."
            );
        }

        var rootTableState = mergeResult.TablesInDependencyOrder[rootTableIndex];

        if (rootTableState.CurrentRows.Length != 1)
        {
            throw new InvalidOperationException(
                $"Existing-document write identity guard expected exactly 1 current root row for table '{rootTableState.TableWritePlan.TableModel.Table}', "
                    + $"but found {rootTableState.CurrentRows.Length}."
            );
        }

        if (rootTableState.MergedRows.Length != 1)
        {
            throw new InvalidOperationException(
                $"Existing-document write identity guard expected exactly 1 merged root row for table '{rootTableState.TableWritePlan.TableModel.Table}', "
                    + $"but found {rootTableState.MergedRows.Length}."
            );
        }

        var rootTableWritePlan = rootTableState.TableWritePlan;
        var identityProjectionBindingIndexes = GetIdentityProjectionBindingIndexesOrThrow(
            request.MappingSet,
            rootTableWritePlan
        );
        var currentRootRow = rootTableState.CurrentRows[0];
        var mergedRootRow = rootTableState.MergedRows[0];
        FlattenedWriteValue[]? reboundValues = null;
        var hasIdentityChange = false;

        foreach (var bindingIndex in identityProjectionBindingIndexes)
        {
            var currentValue = currentRootRow.Values[bindingIndex];
            var mergedValue = mergedRootRow.Values[bindingIndex];

            if (Equals(currentValue, mergedValue))
            {
                continue;
            }

            if (
                currentValue is FlattenedWriteValue.Literal { Value: string currentText }
                && mergedValue is FlattenedWriteValue.Literal { Value: string mergedText }
                && IdentityEqualityContract
                    .ComparerFor(rootTableWritePlan.ColumnBindings[bindingIndex].Column)
                    .Equals(currentText, mergedText)
            )
            {
                reboundValues ??= [.. mergedRootRow.Values];
                reboundValues[bindingIndex] = currentValue;
                continue;
            }

            hasIdentityChange = true;
        }

        if (hasIdentityChange && !request.AllowIdentityUpdates)
        {
            return (
                mergeResult,
                BuildImmutableIdentityFailure(request.OperationKind, request.WritePlan.Model.Resource)
            );
        }

        if (reboundValues is null)
        {
            return (mergeResult, null);
        }

        var reboundRootTableState = rootTableState with
        {
            MergedRows = [RelationalWriteRowHelpers.CreateMergedTableRow(rootTableWritePlan, reboundValues)],
        };

        return (
            mergeResult with
            {
                TablesInDependencyOrder = mergeResult.TablesInDependencyOrder.SetItem(
                    rootTableIndex,
                    reboundRootTableState
                ),
            },
            null
        );
    }

    private static RelationalWriteExecutorResult BuildImmutableIdentityFailure(
        RelationalWriteOperationKind operationKind,
        QualifiedResourceName resource
    )
    {
        var failureMessage = RelationalWriteSupport.BuildImmutableIdentityFailureMessage(resource);

        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureImmutableIdentity(failureMessage)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureImmutableIdentity(failureMessage)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
    }

    private static int[] GetIdentityProjectionBindingIndexesOrThrow(
        MappingSet mappingSet,
        TableWritePlan rootTableWritePlan
    )
    {
        var documentStampingTrigger = mappingSet.Model.TriggersInCreateOrder.SingleOrDefault(trigger =>
            trigger.Table.Equals(rootTableWritePlan.TableModel.Table)
            && trigger.Parameters is TriggerKindParameters.DocumentStamping
        );

        if (documentStampingTrigger is null)
        {
            throw new InvalidOperationException(
                $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' is missing a document-stamping trigger for root table "
                    + $"'{rootTableWritePlan.TableModel.Table}'. This indicates an internal mapping-set compilation bug."
            );
        }

        if (documentStampingTrigger.IdentityProjectionColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Document-stamping trigger '{documentStampingTrigger.Name.Value}' for root table '{rootTableWritePlan.TableModel.Table}' "
                    + "did not declare any identity-projection columns."
            );
        }

        return documentStampingTrigger
            .IdentityProjectionColumns.Select(columnName =>
                FindBindingIndexOrThrow(rootTableWritePlan, columnName)
            )
            .ToArray();
    }

    private static int FindBindingIndexOrThrow(TableWritePlan tableWritePlan, DbColumnName columnName)
    {
        try
        {
            return RelationalWriteMergeSupport.FindBindingIndex(tableWritePlan, columnName);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Root table '{tableWritePlan.TableModel.Table}' does not contain a write binding for identity-projection column '{columnName.Value}'.",
                ex
            );
        }
    }
}
