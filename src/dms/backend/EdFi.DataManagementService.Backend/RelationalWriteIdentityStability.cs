// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
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
/// When no genuine identity change remains, the root's identity-projection bindings are marked as
/// omitted from its in-place update: their stored values already match, and leaving them out of the
/// SET list keeps the database from evaluating cascades on foreign keys that reference them. An
/// allowed identity change keeps the full SET list so those cascades propagate it.
/// </summary>
internal static class RelationalWriteIdentityStability
{
    private static readonly ConditionalWeakTable<
        TableWritePlan,
        RelationalWriteReducedRootUpdate
    > _reducedRootUpdateByPlan = new();

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
        var reducedRootUpdate = GetReducedRootUpdate(request.MappingSet, rootTableWritePlan);
        var currentRootRow = rootTableState.CurrentRows[0];
        var mergedRootRow = rootTableState.MergedRows[0];
        FlattenedWriteValue[]? reboundValues = null;
        var hasIdentityChange = false;

        foreach (var bindingIndex in reducedRootUpdate.OmittedBindingIndexes)
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

        if (hasIdentityChange && reboundValues is null)
        {
            return (mergeResult, null);
        }

        var finalizedRootTableState = rootTableState;

        if (reboundValues is not null)
        {
            finalizedRootTableState = finalizedRootTableState with
            {
                MergedRows =
                [
                    RelationalWriteRowHelpers.CreateMergedTableRow(rootTableWritePlan, reboundValues),
                ],
            };
        }

        if (!hasIdentityChange)
        {
            finalizedRootTableState = finalizedRootTableState with { ReducedRootUpdate = reducedRootUpdate };
        }

        return (
            mergeResult with
            {
                TablesInDependencyOrder = mergeResult.TablesInDependencyOrder.SetItem(
                    rootTableIndex,
                    finalizedRootTableState
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

    /// <summary>
    /// The root plan's one reduced update, resolved on the first write against the plan and reused for the
    /// plan's lifetime. Its omitted bindings are the identity projection the guard compares.
    /// </summary>
    private static RelationalWriteReducedRootUpdate GetReducedRootUpdate(
        MappingSet mappingSet,
        TableWritePlan rootTableWritePlan
    )
    {
        if (_reducedRootUpdateByPlan.TryGetValue(rootTableWritePlan, out var reducedRootUpdate))
        {
            return reducedRootUpdate;
        }

        ImmutableArray<int> identityProjectionBindingIndexes =
        [
            .. GetIdentityProjectionBindingIndexesOrThrow(mappingSet, rootTableWritePlan),
        ];

        return _reducedRootUpdateByPlan.GetValue(
            rootTableWritePlan,
            plan =>
                RelationalWriteReducedRootUpdate.Create(
                    plan,
                    mappingSet.Key.Dialect,
                    identityProjectionBindingIndexes
                )
        );
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
