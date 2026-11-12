// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Validates stored FK endpoints and the compact descriptor-reference type and target contract.
/// </summary>
public sealed class ValidateForeignKeyStorageInvariantPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies foreign-key storage invariants across the derived model set.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tablesByName = BuildTablesByName(context);
        var tableMetadataByName = tablesByName.ToDictionary(
            entry => entry.Key,
            entry => UnifiedAliasStrictMetadataCache.GetOrBuild(context, entry.Value)
        );

        foreach (var table in tablesByName.Values)
        {
            var localTableMetadata = tableMetadataByName[table.Table];

            if (
                table.Columns.FirstOrDefault(column =>
                    column.Kind is ColumnKind.DescriptorFk
                    && column.ScalarType is not { Kind: ScalarKind.Int32 }
                ) is
                { } column
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor column '{table.Table}.{column.ColumnName.Value}' must use Int32 storage."
                );
            }

            foreach (var foreignKey in table.Constraints.OfType<TableConstraint.ForeignKey>())
            {
                if (IsDescriptorTable(foreignKey.TargetTable))
                {
                    ValidateDescriptorForeignKey(foreignKey, table, localTableMetadata);
                }

                if (!tablesByName.TryGetValue(foreignKey.TargetTable, out var targetTable))
                {
                    if (IsDocumentTable(foreignKey.TargetTable))
                    {
                        ForeignKeyStorageValidator.ValidateEndpointColumns(
                            foreignKey,
                            table.Table,
                            foreignKey.TargetTable,
                            "local",
                            foreignKey.Columns,
                            localTableMetadata
                        );
                        ForeignKeyStorageValidator.ValidateDocumentTargetColumns(
                            foreignKey,
                            table.Table,
                            foreignKey.TargetTable,
                            foreignKey.TargetColumns
                        );
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Foreign key '{foreignKey.Name}' from table '{table.Table}' references unknown "
                            + $"target table '{foreignKey.TargetTable}'."
                    );
                }

                var targetTableMetadata = tableMetadataByName[targetTable.Table];

                ForeignKeyStorageValidator.ValidateEndpointColumns(
                    foreignKey,
                    table.Table,
                    targetTable.Table,
                    "local",
                    foreignKey.Columns,
                    localTableMetadata
                );
                ForeignKeyStorageValidator.ValidateEndpointColumns(
                    foreignKey,
                    table.Table,
                    targetTable.Table,
                    "target",
                    foreignKey.TargetColumns,
                    targetTableMetadata
                );
            }
        }
    }

    /// <summary>
    /// Rejects descriptor references that confuse compact descriptor identity with owning document identity.
    /// </summary>
    private static void ValidateDescriptorForeignKey(
        TableConstraint.ForeignKey foreignKey,
        DbTableModel table,
        UnifiedAliasStorageResolver.TableMetadata tableMetadata
    )
    {
        if (
            foreignKey.TargetColumns.Count != 1
            || !foreignKey.TargetColumns[0].Equals(RelationalNameConventions.DescriptorKeyColumnName)
            || foreignKey.Columns.Count != 1
        )
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' from table '{table.Table}' must target only dms.Descriptor.DescriptorId."
            );
        }

        if (
            tableMetadata.ColumnsByName.TryGetValue(foreignKey.Columns[0], out var column)
            && column.ScalarType is not { Kind: ScalarKind.Int32 }
        )
        {
            throw new InvalidOperationException(
                $"Descriptor foreign key '{foreignKey.Name}' from table '{table.Table}' must use Int32 storage."
            );
        }
    }

    private static bool IsDescriptorTable(DbTableName table)
    {
        return table.Equals(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
    }

    /// <summary>
    /// Returns true when the referenced table is the shared core <c>dms.Document</c> table.
    /// </summary>
    private static bool IsDocumentTable(DbTableName table)
    {
        return string.Equals(table.Schema.Value, "dms", StringComparison.Ordinal)
            && string.Equals(table.Name, "Document", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a lookup of all derived tables by physical name across concrete resources and abstract identity tables.
    /// </summary>
    private static Dictionary<DbTableName, DbTableModel> BuildTablesByName(
        RelationalModelSetBuilderContext context
    )
    {
        Dictionary<DbTableName, DbTableModel> tablesByName = new();

        // Concrete resources may share a physical table (e.g., descriptors share dms.Descriptor),
        // so we use DistinctBy to avoid processing the same physical table more than once.
        foreach (
            var table in context
                .ConcreteResourcesInNameOrder.SelectMany(resource =>
                    resource.RelationalModel.TablesInDependencyOrder
                )
                .DistinctBy(t => t.Table)
        )
        {
            tablesByName.Add(table.Table, table);
        }

        foreach (var tableModel in context.AbstractIdentityTablesInNameOrder.Select(a => a.TableModel))
        {
            if (!tablesByName.TryAdd(tableModel.Table, tableModel))
            {
                throw new InvalidOperationException(
                    $"Duplicate table name '{tableModel.Table.Schema.Value}.{tableModel.Table.Name}' "
                        + "encountered during foreign key storage validation. "
                        + "This indicates a naming collision in the derived model set."
                );
            }
        }

        return tablesByName;
    }
}
