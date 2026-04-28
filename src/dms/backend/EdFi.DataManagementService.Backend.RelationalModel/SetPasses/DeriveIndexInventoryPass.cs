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
        var pkIndexName = ConstraintNaming.ResolvePrimaryKeyConstraintName(table.Table, table.Key);

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
        // ValidateForeignKeyStorageInvariantPass runs earlier in the default pass order and
        // guarantees FK endpoints map to direct stored columns before index derivation.
        // Process longer FKs first so that shorter prefixes are suppressed by the longer
        // index rather than the other way around.
        var orderedFks = table
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .OrderByDescending(fk => fk.Columns.Count)
            .ThenBy(fk => fk.Name, StringComparer.Ordinal);

        foreach (var columns in orderedFks.Select(fk => fk.Columns))
        {
            if (IsLeftmostPrefixCovered(columns, tableIndexes))
            {
                continue;
            }

            var indexName = ConstraintNaming.BuildForeignKeySupportIndexName(table.Table, columns);
            tableIndexes.Add(
                new DbIndexInfo(
                    new DbIndexName(indexName),
                    table.Table,
                    columns,
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
        foreach (var keyColumns in existingIndexes.Select(e => e.KeyColumns))
        {
            if (keyColumns.Count < fkColumns.Count)
            {
                continue;
            }

            var isPrefix = true;

            for (var i = 0; i < fkColumns.Count; i++)
            {
                if (!fkColumns[i].Equals(keyColumns[i]))
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
