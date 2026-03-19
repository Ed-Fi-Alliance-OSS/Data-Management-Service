// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives stable-collection unique constraints needed for sibling ordering and composite parent/root FKs.
/// </summary>
public sealed class StableCollectionConstraintPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies stable-collection uniques for all concrete resources in the set.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var entry = context.ConcreteResourcesInNameOrder[index];
            var resource = entry.ResourceKey.Resource;
            var mutation = new ResourceMutation(new ResourceEntry(index, entry));

            ApplyStableCollectionConstraints(mutation, entry.RelationalModel, resource);

            if (!mutation.HasChanges)
            {
                continue;
            }

            var updatedModel = UpdateResourceModel(entry.RelationalModel, mutation);
            context.ConcreteResourcesInNameOrder[index] = entry with { RelationalModel = updatedModel };
        }
    }

    /// <summary>
    /// Adds sibling-order and parent/root-target uniques for one resource model.
    /// </summary>
    private static void ApplyStableCollectionConstraints(
        ResourceMutation mutation,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        var compositeParentTargets = ResolveCompositeParentTargetTables(
            resourceModel.TablesInDependencyOrder
        );

        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            if (!SupportsStableCollectionConstraints(table))
            {
                continue;
            }

            var tableAccumulator = mutation.GetTableAccumulator(table, resource);
            var mutated = false;
            var siblingOrderColumns = BuildSiblingOrderUniqueColumns(table);

            if (
                siblingOrderColumns.Length > 0
                && !ContainsUniqueConstraint(tableAccumulator.Constraints, table.Table, siblingOrderColumns)
            )
            {
                tableAccumulator.AddConstraint(
                    new TableConstraint.Unique(
                        ConstraintNaming.BuildColumnUniqueName(table.Table, siblingOrderColumns),
                        siblingOrderColumns
                    )
                );
                mutated = true;
            }

            if (compositeParentTargets.Contains(new TableScopeKey(table.Table, table.JsonScope.Canonical)))
            {
                var compositeTargetColumns = BuildCompositeParentTargetUniqueColumns(table);

                if (
                    compositeTargetColumns.Length > 0
                    && !ContainsUniqueConstraint(
                        tableAccumulator.Constraints,
                        table.Table,
                        compositeTargetColumns
                    )
                )
                {
                    tableAccumulator.AddConstraint(
                        new TableConstraint.Unique(
                            ConstraintNaming.BuildColumnUniqueName(table.Table, compositeTargetColumns),
                            compositeTargetColumns
                        )
                    );
                    mutated = true;
                }
            }

            if (mutated)
            {
                mutation.MarkTableMutated(table);
            }
        }
    }

    /// <summary>
    /// Determines which tables are targeted by composite parent/root-consistency foreign keys.
    /// </summary>
    private static HashSet<TableScopeKey> ResolveCompositeParentTargetTables(
        IReadOnlyList<DbTableModel> tables
    )
    {
        var tablesByName = tables.ToDictionary(table => table.Table);
        HashSet<TableScopeKey> targets = [];

        foreach (var table in tables)
        {
            foreach (var foreignKey in table.Constraints.OfType<TableConstraint.ForeignKey>())
            {
                if (!tablesByName.TryGetValue(foreignKey.TargetTable, out var targetTable))
                {
                    continue;
                }

                var expectedTargetColumns = BuildCompositeParentTargetUniqueColumns(targetTable);

                if (
                    expectedTargetColumns.Length == 0
                    || !foreignKey.TargetColumns.SequenceEqual(expectedTargetColumns)
                )
                {
                    continue;
                }

                targets.Add(new TableScopeKey(targetTable.Table, targetTable.JsonScope.Canonical));
            }
        }

        return targets;
    }

    /// <summary>
    /// Returns the ordered columns needed for sibling-order uniqueness on one stable collection table.
    /// </summary>
    private static DbColumnName[] BuildSiblingOrderUniqueColumns(DbTableModel table)
    {
        if (
            table.IdentityMetadata.ImmediateParentScopeLocatorColumns.Count == 0
            || !table.Columns.Any(column =>
                column.Kind == ColumnKind.Ordinal
                && column.ColumnName.Equals(RelationalNameConventions.OrdinalColumnName)
            )
        )
        {
            return [];
        }

        List<DbColumnName> columns = [];
        HashSet<string> seenColumns = new(StringComparer.Ordinal);

        foreach (var column in table.IdentityMetadata.ImmediateParentScopeLocatorColumns)
        {
            AddUniqueColumn(column, columns, seenColumns);
        }

        AddUniqueColumn(RelationalNameConventions.OrdinalColumnName, columns, seenColumns);

        return columns.ToArray();
    }

    /// <summary>
    /// Returns the ordered target columns that must be unique to support composite parent/root FKs.
    /// </summary>
    private static DbColumnName[] BuildCompositeParentTargetUniqueColumns(DbTableModel table)
    {
        if (
            table.IdentityMetadata.PhysicalRowIdentityColumns.Count == 0
            || table.IdentityMetadata.RootScopeLocatorColumns.Count == 0
        )
        {
            return [];
        }

        List<DbColumnName> columns = [];
        HashSet<string> seenColumns = new(StringComparer.Ordinal);

        foreach (var column in table.IdentityMetadata.PhysicalRowIdentityColumns)
        {
            AddUniqueColumn(column, columns, seenColumns);
        }

        foreach (var column in table.IdentityMetadata.RootScopeLocatorColumns)
        {
            AddUniqueColumn(column, columns, seenColumns);
        }

        return columns.Count > table.IdentityMetadata.PhysicalRowIdentityColumns.Count
            ? columns.ToArray()
            : [];
    }

    /// <summary>
    /// Returns whether the table participates in the stable-collection constraint surface for this story.
    /// </summary>
    private static bool SupportsStableCollectionConstraints(DbTableModel table)
    {
        return table.IdentityMetadata.TableKind
            is DbTableKind.Collection
                or DbTableKind.CollectionExtensionScope
                or DbTableKind.ExtensionCollection;
    }

    /// <summary>
    /// Stable key for locating one table within a resource model.
    /// </summary>
    private sealed record TableScopeKey(DbTableName Table, string Scope);
}
