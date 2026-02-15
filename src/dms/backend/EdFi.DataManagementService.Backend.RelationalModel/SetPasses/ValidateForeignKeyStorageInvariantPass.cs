// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;

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
            entry =>
                UnifiedAliasStorageResolver.BuildTableMetadata(
                    entry.Value,
                    new UnifiedAliasStorageResolver.PresenceGateMetadataOptions(
                        ThrowIfPresenceColumnMissing: false,
                        ThrowIfInvalidStrictSyntheticCandidate: false,
                        UnifiedAliasStorageResolver
                            .ScalarPresenceGateClassification
                            .StrictSyntheticPresenceFlag
                    )
                )
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
                        ValidateForeignKeyColumns(
                            foreignKey,
                            table.Table,
                            foreignKey.TargetTable,
                            "local",
                            foreignKey.Columns,
                            localTableMetadata
                        );
                        ValidateDocumentTableTargetColumns(
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

                ValidateForeignKeyColumns(
                    foreignKey,
                    table.Table,
                    targetTable.Table,
                    "local",
                    foreignKey.Columns,
                    localTableMetadata
                );
                ValidateForeignKeyColumns(
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

    private static void ValidateDocumentTableTargetColumns(
        TableConstraint.ForeignKey foreignKey,
        DbTableName referencingTable,
        DbTableName referencedTable,
        IReadOnlyList<DbColumnName> targetColumns
    )
    {
        if (targetColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table "
                    + $"'{referencedTable}' must define at least one target column."
            );
        }

        var invalidColumns = targetColumns
            .Where(column => !column.Equals(RelationalNameConventions.DocumentIdColumnName))
            .Select(column => $"'{column.Value}'")
            .ToArray();

        if (invalidColumns.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table '{referencedTable}' "
                + $"contains invalid target column(s): {string.Join(", ", invalidColumns)}. Foreign keys "
                + "targeting dms.Document must reference only DocumentId."
        );
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

    private static void ValidateForeignKeyColumns(
        TableConstraint.ForeignKey foreignKey,
        DbTableName referencingTable,
        DbTableName referencedTable,
        string columnRole,
        IReadOnlyList<DbColumnName> columns,
        UnifiedAliasStorageResolver.TableMetadata tableMetadata
    )
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table "
                    + $"'{referencedTable}' must define at least one {columnRole} column."
            );
        }

        List<string> offendingColumns = [];

        foreach (var column in columns)
        {
            if (!tableMetadata.ColumnsByName.TryGetValue(column, out var columnModel))
            {
                offendingColumns.Add($"'{column.Value}' (missing)");
                continue;
            }

            if (tableMetadata.SyntheticScalarPresenceColumns.Contains(columnModel.ColumnName))
            {
                offendingColumns.Add($"'{column.Value}' (synthetic presence column)");
                continue;
            }

            switch (columnModel.Storage)
            {
                case ColumnStorage.Stored:
                    continue;
                case ColumnStorage.UnifiedAlias unifiedAlias:
                    offendingColumns.Add(
                        $"'{column.Value}' (UnifiedAlias -> '{unifiedAlias.CanonicalColumn.Value}')"
                    );
                    continue;
                default:
                    offendingColumns.Add(
                        $"'{column.Value}' (unsupported storage '{columnModel.Storage.GetType().Name}')"
                    );
                    continue;
            }
        }

        if (offendingColumns.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table '{referencedTable}' "
                + $"contains invalid {columnRole} column(s): {string.Join(", ", offendingColumns)}. Foreign key "
                + "columns must reference stored columns and cannot use synthetic presence columns."
        );
    }
}
