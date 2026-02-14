// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Constraints;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives the index inventory from primary key, unique, and foreign key constraints
/// across all schema-derived tables (concrete resources and abstract identity tables).
/// Descriptor resources using <see cref="ResourceStorageKind.SharedDescriptorTable"/> are skipped.
/// </summary>
public sealed class DeriveIndexInventoryPass : IRelationalModelSetPass
{
    private const string UnifiedStorageSuffix = "_Unified";
    private const string PresenceColumnSuffix = "_Present";

    /// <summary>
    /// Populates <see cref="RelationalModelSetBuilderContext.IndexInventory"/> with PK-implied,
    /// UK-implied, and FK-support indexes for each derived table.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var concreteResource in context.ConcreteResourcesInNameOrder)
        {
            if (concreteResource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            foreach (var table in concreteResource.RelationalModel.TablesInDependencyOrder)
            {
                DeriveIndexesForTable(table, context.IndexInventory);
            }
        }

        foreach (var abstractTable in context.AbstractIdentityTablesInNameOrder)
        {
            DeriveIndexesForTable(abstractTable.TableModel, context.IndexInventory);
        }
    }

    /// <summary>
    /// Derives PK-implied, UK-implied, and FK-support indexes for a single table and appends
    /// them to the inventory.
    /// </summary>
    private static void DeriveIndexesForTable(DbTableModel table, List<DbIndexInfo> inventory)
    {
        List<DbIndexInfo> tableIndexes = [];
        var tableColumnNames = table
            .Columns.Select(column => column.ColumnName.Value)
            .ToHashSet(StringComparer.Ordinal);

        // PK-implied index: one per table, reuses PK constraint name, unique.
        var pkIndexName = string.IsNullOrWhiteSpace(table.Key.ConstraintName)
            ? ConstraintNaming.BuildPrimaryKeyName(table.Table)
            : table.Key.ConstraintName;

        tableIndexes.Add(
            new DbIndexInfo(
                new DbIndexName(pkIndexName),
                table.Table,
                [.. table.Key.Columns.Select(c => c.ColumnName)],
                IsUnique: true,
                DbIndexKind.PrimaryKey
            )
        );

        // UK-implied indexes: one per UNIQUE constraint, reuses constraint name, unique.
        foreach (var unique in table.Constraints.OfType<TableConstraint.Unique>())
        {
            tableIndexes.Add(
                new DbIndexInfo(
                    new DbIndexName(unique.Name),
                    table.Table,
                    unique.Columns,
                    IsUnique: true,
                    DbIndexKind.UniqueConstraint
                )
            );
        }

        // FK-support indexes: one per FK, non-unique, suppressed when FK columns are
        // a leftmost prefix of any existing PK/UK/earlier-index key columns.
        foreach (var fk in table.Constraints.OfType<TableConstraint.ForeignKey>())
        {
            ValidateForeignKeyColumns(table, fk, tableColumnNames);

            if (IsLeftmostPrefixCovered(fk.Columns, tableIndexes))
            {
                continue;
            }

            var indexName = ConstraintNaming.BuildForeignKeySupportIndexName(table.Table, fk.Columns);
            tableIndexes.Add(
                new DbIndexInfo(
                    new DbIndexName(indexName),
                    table.Table,
                    fk.Columns,
                    IsUnique: false,
                    DbIndexKind.ForeignKeySupport
                )
            );
        }

        inventory.AddRange(tableIndexes);
    }

    /// <summary>
    /// Validates that FK columns target real table columns and avoid key-unification alias/presence columns.
    /// </summary>
    private static void ValidateForeignKeyColumns(
        DbTableModel table,
        TableConstraint.ForeignKey foreignKey,
        IReadOnlySet<string> tableColumnNames
    )
    {
        if (foreignKey.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' on table '{table.Table}' must define at least one local column."
            );
        }

        foreach (var column in foreignKey.Columns)
        {
            if (!tableColumnNames.Contains(column.Value))
            {
                throw new InvalidOperationException(
                    $"Foreign key '{foreignKey.Name}' on table '{table.Table}' references unknown column "
                        + $"'{column.Value}'."
                );
            }

            if (column.Value.EndsWith(PresenceColumnSuffix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Foreign key '{foreignKey.Name}' on table '{table.Table}' references synthetic presence "
                        + $"column '{column.Value}'. Foreign keys must target storage columns."
                );
            }

            if (ReferencesUnifiedAliasColumn(column, tableColumnNames))
            {
                var canonicalStorageColumn = $"{column.Value}{UnifiedStorageSuffix}";

                throw new InvalidOperationException(
                    $"Foreign key '{foreignKey.Name}' on table '{table.Table}' references binding/alias column "
                        + $"'{column.Value}' while canonical storage column '{canonicalStorageColumn}' is present. "
                        + "Foreign keys must target canonical storage columns."
                );
            }
        }
    }

    /// <summary>
    /// Returns true when the FK column appears to be an alias with a sibling canonical storage column.
    /// </summary>
    private static bool ReferencesUnifiedAliasColumn(
        DbColumnName column,
        IReadOnlySet<string> tableColumnNames
    )
    {
        if (column.Value.EndsWith(UnifiedStorageSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        return tableColumnNames.Contains($"{column.Value}{UnifiedStorageSuffix}");
    }

    /// <summary>
    /// Returns true when the FK columns form a leftmost prefix of any already-derived index's
    /// key columns on the same table.
    /// </summary>
    private static bool IsLeftmostPrefixCovered(
        IReadOnlyList<DbColumnName> fkColumns,
        IReadOnlyList<DbIndexInfo> existingIndexes
    )
    {
        foreach (var existing in existingIndexes)
        {
            if (existing.KeyColumns.Count < fkColumns.Count)
            {
                continue;
            }

            var isPrefix = true;

            for (var i = 0; i < fkColumns.Count; i++)
            {
                if (!fkColumns[i].Equals(existing.KeyColumns[i]))
                {
                    isPrefix = false;
                    break;
                }
            }

            if (isPrefix)
            {
                return true;
            }
        }

        return false;
    }
}
