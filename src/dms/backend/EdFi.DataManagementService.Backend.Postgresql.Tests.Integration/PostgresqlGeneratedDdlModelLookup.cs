// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal static class PostgresqlGeneratedDdlModelLookup
{
    public static DbTableModel RequireTable(
        DerivedRelationalModelSet modelSet,
        string schema,
        string tableName
    )
    {
        return EnumerateTables(modelSet)
            .Single(table =>
                table.Table.Schema.Value.Equals(schema, StringComparison.Ordinal)
                && table.Table.Name.Equals(tableName, StringComparison.Ordinal)
            );
    }

    public static DbTableModel RequireTableByScope(
        DerivedRelationalModelSet modelSet,
        string schema,
        string jsonScope
    )
    {
        return EnumerateTables(modelSet)
            .DistinctBy(static table =>
                (table.JsonScope.Canonical, table.Table.Schema.Value, table.Table.Name)
            )
            .Single(table =>
                table.Table.Schema.Value.Equals(schema, StringComparison.Ordinal)
                && table.JsonScope.Canonical.Equals(jsonScope, StringComparison.Ordinal)
            );
    }

    public static DbTableModel RequireTableByScopeAndColumns(
        DerivedRelationalModelSet modelSet,
        string schema,
        string jsonScope,
        params string[] requiredColumns
    )
    {
        return EnumerateTables(modelSet)
            .DistinctBy(static table =>
                (table.JsonScope.Canonical, table.Table.Schema.Value, table.Table.Name)
            )
            .Where(table =>
                table.Table.Schema.Value.Equals(schema, StringComparison.Ordinal)
                && table.JsonScope.Canonical.Equals(jsonScope, StringComparison.Ordinal)
            )
            .Single(table =>
                Array.TrueForAll(
                    requiredColumns,
                    requiredColumn =>
                        table.Columns.Any(column =>
                            column.ColumnName.Value.Equals(requiredColumn, StringComparison.Ordinal)
                        )
                )
            );
    }

    public static TableConstraint.ForeignKey RequireForeignKey(
        DbTableModel sourceTable,
        DbTableName targetTable,
        params string[] columns
    )
    {
        return sourceTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(foreignKey =>
                foreignKey.TargetTable == targetTable
                && foreignKey.Columns.Select(static column => column.Value).SequenceEqual(columns)
            );
    }

    private static IEnumerable<DbTableModel> EnumerateTables(DerivedRelationalModelSet modelSet)
    {
        return modelSet
            .ConcreteResourcesInNameOrder.SelectMany(static resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Concat(
                modelSet.AbstractIdentityTablesInNameOrder.Select(static tableInfo => tableInfo.TableModel)
            );
    }
}
