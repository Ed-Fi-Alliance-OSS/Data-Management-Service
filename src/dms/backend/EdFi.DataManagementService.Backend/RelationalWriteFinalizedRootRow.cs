// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Reconstructs the finalized root-table row buffer from a merge result so proposed-value
/// authorization can read post-merge values aligned with the root table's
/// <see cref="TableWritePlan.ColumnBindings"/>, rather than re-reading the raw request body.
/// </summary>
internal static class RelationalWriteFinalizedRootRow
{
    public static RootWriteRowBuffer Build(
        RelationalWriteExecutorRequest request,
        RelationalWriteMergeResult mergeResult
    )
    {
        var rootTable = GetRootTableWritePlan(request.WritePlan);
        var rootTableState = mergeResult.TablesInDependencyOrder.SingleOrDefault(tableState =>
            tableState.TableWritePlan.TableModel.Table.Equals(rootTable.TableModel.Table)
        );

        if (rootTableState is null)
        {
            throw new InvalidOperationException(
                $"Relational write merge result did not include the root table '{rootTable.TableModel.Table}'."
            );
        }

        if (rootTableState.MergedRows.Length != 1)
        {
            throw new InvalidOperationException(
                $"Relational write merge result for root table '{rootTable.TableModel.Table}' "
                    + $"included {rootTableState.MergedRows.Length} merged rows; expected exactly one."
            );
        }

        return new RootWriteRowBuffer(rootTableState.TableWritePlan, rootTableState.MergedRows[0].Values);
    }

    private static TableWritePlan GetRootTableWritePlan(ResourceWritePlan writePlan)
    {
        var rootPlans = writePlan
            .TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind is DbTableKind.Root
            )
            .Take(2)
            .ToArray();

        return rootPlans.Length switch
        {
            1 => rootPlans[0],
            0 => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' does not contain a root table plan."
            ),
            _ => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' contains multiple root table plans."
            ),
        };
    }
}
