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
                    var columnOrdinals = ResolveColumnOrdinalsOrThrow(columnOrdinalsByTable, binding.Table);

                    return new ReferenceIdentityProjectionBinding(
                        IsIdentityComponent: binding.IsIdentityComponent,
                        ReferenceObjectPath: binding.ReferenceObjectPath,
                        TargetResource: binding.TargetResource,
                        FkColumnOrdinal: ResolveColumnOrdinalOrThrow(
                            columnOrdinals,
                            binding.Table,
                            binding.FkColumn,
                            $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column"
                        ),
                        IdentityFieldOrdinalsInOrder: binding.IdentityBindings.Select(
                            identityBinding => new ReferenceIdentityProjectionFieldOrdinal(
                                ReferenceJsonPath: identityBinding.ReferenceJsonPath,
                                ColumnOrdinal: ResolveColumnOrdinalOrThrow(
                                    columnOrdinals,
                                    binding.Table,
                                    identityBinding.Column,
                                    $"reference-identity binding '{identityBinding.ReferenceJsonPath.Canonical}' for reference '{binding.ReferenceObjectPath.Canonical}' column"
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

    private static IReadOnlyDictionary<DbColumnName, int> ResolveColumnOrdinalsOrThrow(
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable,
        DbTableName table
    )
    {
        if (columnOrdinalsByTable.TryGetValue(table, out var columnOrdinals))
        {
            return columnOrdinals;
        }

        throw new InvalidOperationException(
            $"Cannot compile reference identity projection plan for '{table}': owning table is not present in TablesInDependencyOrder."
        );
    }

    private static int ResolveColumnOrdinalOrThrow(
        IReadOnlyDictionary<DbColumnName, int> columnOrdinals,
        DbTableName table,
        DbColumnName column,
        string dependencyDescription
    )
    {
        if (columnOrdinals.TryGetValue(column, out var columnOrdinal))
        {
            return columnOrdinal;
        }

        throw new InvalidOperationException(
            $"Cannot compile reference identity projection plan for '{table}': {dependencyDescription} '{column.Value}' does not exist in hydration select-list columns."
        );
    }
}
