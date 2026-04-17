// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteIdentityStability
{
    public static RelationalWriteExecutorResult? TryBuildFailureResult(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergeResult);

        if (request.TargetContext is not RelationalWriteTargetContext.ExistingDocument)
        {
            return null;
        }

        var rootTableState = mergeResult.TablesInDependencyOrder.SingleOrDefault(tableState =>
            tableState.TableWritePlan.TableModel.Table.Equals(request.WritePlan.Model.Root.Table)
        );

        if (rootTableState is null)
        {
            throw new InvalidOperationException(
                $"No-profile merge synthesis for resource '{RelationalWriteSupport.FormatResource(request.WritePlan.Model.Resource)}' "
                    + $"did not include root table '{request.WritePlan.Model.Root.Table}'. This indicates an internal merge-synthesis bug."
            );
        }

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

        var identityProjectionBindingIndexes = GetIdentityProjectionBindingIndexesOrThrow(
            request.MappingSet,
            rootTableState.TableWritePlan
        );
        var currentRootRow = rootTableState.CurrentRows[0];
        var mergedRootRow = rootTableState.MergedRows[0];

        if (
            Array.TrueForAll(
                identityProjectionBindingIndexes,
                bindingIndex =>
                    Equals(currentRootRow.Values[bindingIndex], mergedRootRow.Values[bindingIndex])
            )
        )
        {
            return null;
        }

        return request.AllowIdentityUpdates
            ? BuildNotYetSupportedFailure(request.OperationKind, request.WritePlan.Model.Resource)
            : BuildImmutableIdentityFailure(request.OperationKind, request.WritePlan.Model.Resource);
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

    private static RelationalWriteExecutorResult BuildNotYetSupportedFailure(
        RelationalWriteOperationKind operationKind,
        QualifiedResourceName resource
    )
    {
        var failureMessage = RelationalWriteSupport.BuildIdentityUpdatesNotYetSupportedMessage(resource);

        return operationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(failureMessage)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(failureMessage)
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
        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (tableWritePlan.ColumnBindings[bindingIndex].Column.ColumnName.Equals(columnName))
            {
                return bindingIndex;
            }
        }

        throw new InvalidOperationException(
            $"Root table '{tableWritePlan.TableModel.Table}' does not contain a write binding for identity-projection column '{columnName.Value}'."
        );
    }
}
