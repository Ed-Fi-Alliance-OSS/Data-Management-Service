// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives descriptor FK constraints from descriptor binding columns after key unification.
/// </summary>
public sealed class DescriptorForeignKeyConstraintPass : IRelationalModelSetPass
{
    private static readonly DbTableName _descriptorTableName = new(new DbSchemaName("dms"), "Descriptor");

    /// <summary>
    /// Executes descriptor FK derivation across all concrete resource tables.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[index];
            var updatedModel = ApplyToResource(
                context,
                concreteResource.RelationalModel,
                concreteResource.ResourceKey.Resource
            );

            context.ConcreteResourcesInNameOrder[index] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    private static RelationalResourceModel ApplyToResource(
        RelationalModelSetBuilderContext context,
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        List<DescriptorForeignKeyDeduplication> dedupDiagnostics = [];
        var updatedTables = new DbTableModel[resourceModel.TablesInDependencyOrder.Count];

        for (var tableIndex = 0; tableIndex < resourceModel.TablesInDependencyOrder.Count; tableIndex++)
        {
            var table = resourceModel.TablesInDependencyOrder[tableIndex];
            updatedTables[tableIndex] = ApplyToTable(context, table, resource, dedupDiagnostics);
        }

        var updatedRoot = updatedTables.Single(table => table.JsonScope.Equals(resourceModel.Root.JsonScope));
        var orderedDedupDiagnostics = dedupDiagnostics
            .OrderBy(entry => entry.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(entry => entry.Table.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.StorageColumn.Value, StringComparer.Ordinal)
            .ToArray();

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
            DescriptorForeignKeyDeduplications = orderedDedupDiagnostics,
        };
    }

    private static DbTableModel ApplyToTable(
        RelationalModelSetBuilderContext context,
        DbTableModel table,
        QualifiedResourceName resource,
        List<DescriptorForeignKeyDeduplication> dedupDiagnostics
    )
    {
        var tableMetadata = UnifiedAliasStrictMetadataCache.GetOrBuild(context, table);

        Dictionary<DbColumnName, DescriptorStorageGroup> storageGroupsByColumn = [];

        foreach (
            var descriptorColumn in table
                .Columns.Where(column => column.Kind == ColumnKind.DescriptorFk)
                .OrderBy(column => column.ColumnName.Value, StringComparer.Ordinal)
        )
        {
            var storageColumn = UnifiedAliasStorageResolver.ResolveStorageColumn(
                descriptorColumn.ColumnName,
                tableMetadata,
                UnifiedAliasStorageResolver.PresenceGateRejectionPolicy.RejectSyntheticScalarPresence,
                $"Descriptor FK derivation for resource '{FormatResource(resource)}' table '{table.Table}'",
                "descriptor column",
                "descriptor FK constraint derivation"
            );

            if (!storageGroupsByColumn.TryGetValue(storageColumn, out var storageGroup))
            {
                storageGroup = new DescriptorStorageGroup(storageColumn);
                storageGroupsByColumn.Add(storageColumn, storageGroup);
            }

            if (descriptorColumn.SourceJsonPath is not null)
            {
                storageGroup.BindingColumns.Add(descriptorColumn.ColumnName);
            }
        }

        var orderedStorageGroups = storageGroupsByColumn
            .Values.OrderBy(group => group.StorageColumn.Value, StringComparer.Ordinal)
            .ToArray();

        foreach (var storageGroup in orderedStorageGroups.Where(group => group.BindingColumns.Count > 1))
        {
            dedupDiagnostics.Add(
                new DescriptorForeignKeyDeduplication(
                    table.Table,
                    storageGroup.StorageColumn,
                    storageGroup
                        .BindingColumns.OrderBy(column => column.Value, StringComparer.Ordinal)
                        .ToArray()
                )
            );
        }

        var generatedDescriptorConstraints = orderedStorageGroups
            .Select(group => CreateDescriptorForeignKey(table.Table, group.StorageColumn))
            .ToArray();
        var nonDescriptorConstraints = table
            .Constraints.Where(constraint => !IsDescriptorForeignKeyConstraint(constraint))
            .ToArray();
        var updatedConstraints = nonDescriptorConstraints
            .Concat(generatedDescriptorConstraints)
            .Cast<TableConstraint>()
            .ToArray();

        return updatedConstraints.SequenceEqual(table.Constraints)
            ? table
            : table with
            {
                Constraints = updatedConstraints,
            };
    }

    private static TableConstraint.ForeignKey CreateDescriptorForeignKey(
        DbTableName table,
        DbColumnName storageColumn
    )
    {
        return new TableConstraint.ForeignKey(
            ConstraintNaming.BuildDescriptorForeignKeyName(table, storageColumn),
            [storageColumn],
            _descriptorTableName,
            [RelationalNameConventions.DocumentIdColumnName],
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.NoAction
        );
    }

    private static bool IsDescriptorForeignKeyConstraint(TableConstraint constraint)
    {
        if (constraint is not TableConstraint.ForeignKey foreignKey)
        {
            return false;
        }

        if (!foreignKey.TargetTable.Equals(_descriptorTableName))
        {
            return false;
        }

        return foreignKey.TargetColumns.Count == 1
            && foreignKey.TargetColumns[0].Equals(RelationalNameConventions.DocumentIdColumnName);
    }

    private sealed record DescriptorStorageGroup(DbColumnName StorageColumn)
    {
        public HashSet<DbColumnName> BindingColumns { get; } = [];
    }
}
