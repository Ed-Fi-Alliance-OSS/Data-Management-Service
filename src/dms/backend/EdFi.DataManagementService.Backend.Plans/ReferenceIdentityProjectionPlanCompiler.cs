// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles table-local reference identity projection metadata from
/// <see cref="RelationalResourceModel.DocumentReferenceBindings" />.
/// </summary>
internal sealed class ReferenceIdentityProjectionPlanCompiler
{
    public IReadOnlyList<ReferenceIdentityProjectionTablePlan> Compile(
        RelationalResourceModel resourceModel,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(columnOrdinalsByTable);

        if (resourceModel.DocumentReferenceBindings.Count == 0)
        {
            return [];
        }

        var plans = new List<ReferenceIdentityProjectionTablePlan>();

        foreach (var tableModel in resourceModel.TablesInDependencyOrder)
        {
            var bindingsInOrder = resourceModel
                .DocumentReferenceBindings.Where(binding => binding.Table.Equals(tableModel.Table))
                .Select(binding =>
                {
                    var columnOrdinals = ProjectionMetadataResolver.ResolveHydrationColumnOrdinalsOrThrow(
                        binding.Table,
                        columnOrdinalsByTable,
                        missingTable => new InvalidOperationException(
                            $"Cannot compile reference identity projection plan for '{missingTable}': owning table is not present in TablesInDependencyOrder."
                        )
                    );

                    return new ReferenceIdentityProjectionBinding(
                        IsIdentityComponent: binding.IsIdentityComponent,
                        ReferenceObjectPath: binding.ReferenceObjectPath,
                        TargetResource: binding.TargetResource,
                        FkColumnOrdinal: ProjectionMetadataResolver.ResolveHydrationColumnOrdinalOrThrow(
                            columnOrdinals,
                            binding.FkColumn,
                            missingColumn => new InvalidOperationException(
                                $"Cannot compile reference identity projection plan for '{binding.Table}': document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column '{missingColumn.Value}' does not exist in hydration select-list columns."
                            )
                        ),
                        IdentityFieldOrdinalsInOrder: binding.IdentityBindings.Select(
                            identityBinding => new ReferenceIdentityProjectionFieldOrdinal(
                                ReferenceJsonPath: identityBinding.ReferenceJsonPath,
                                ColumnOrdinal: ProjectionMetadataResolver.ResolveHydrationColumnOrdinalOrThrow(
                                    columnOrdinals,
                                    identityBinding.Column,
                                    missingColumn => new InvalidOperationException(
                                        $"Cannot compile reference identity projection plan for '{binding.Table}': reference-identity binding '{identityBinding.ReferenceJsonPath.Canonical}' for reference '{binding.ReferenceObjectPath.Canonical}' column '{missingColumn.Value}' does not exist in hydration select-list columns."
                                    )
                                )
                            )
                        )
                    );
                })
                .ToArray();

            if (bindingsInOrder.Length == 0)
            {
                continue;
            }

            plans.Add(
                new ReferenceIdentityProjectionTablePlan(
                    Table: tableModel.Table,
                    BindingsInOrder: bindingsInOrder
                )
            );
        }

        return plans;
    }
}
