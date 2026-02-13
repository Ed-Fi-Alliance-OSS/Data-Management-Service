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
