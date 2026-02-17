// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Validates that foreign keys reference direct stored columns on both local and target sides.
/// </summary>
public sealed class ValidateForeignKeyStorageInvariantPass : IRelationalModelSetPass
{
    /// <summary>
    /// Applies storage-only foreign-key endpoint invariants across the derived model set.
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

            foreach (var foreignKey in table.Constraints.OfType<TableConstraint.ForeignKey>())
            {
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

    private static bool IsDocumentTable(DbTableName table)
    {
        return string.Equals(table.Schema.Value, "dms", StringComparison.Ordinal)
            && string.Equals(table.Name, "Document", StringComparison.Ordinal);
    }

    private static Dictionary<DbTableName, DbTableModel> BuildTablesByName(
        RelationalModelSetBuilderContext context
    )
    {
        Dictionary<DbTableName, DbTableModel> tablesByName = new();

        foreach (
            var table in context
                .ConcreteResourcesInNameOrder.SelectMany(resource =>
                    resource.RelationalModel.TablesInDependencyOrder
                )
                .Concat(context.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel))
        )
        {
            tablesByName.TryAdd(table.Table, table);
        }

        return tablesByName;
    }
}
