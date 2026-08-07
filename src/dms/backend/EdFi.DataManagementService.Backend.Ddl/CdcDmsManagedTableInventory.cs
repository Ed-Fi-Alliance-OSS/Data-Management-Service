// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

internal static class CdcDmsManagedTableInventoryBuilder
{
    internal static IReadOnlyList<CdcDmsManagedTableInventory> Build(
        ISqlDialect dialect,
        DerivedRelationalModelSet modelSet,
        IReadOnlyList<CdcSourceTableInventory> cdcSourceInventory
    )
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(modelSet);
        ArgumentNullException.ThrowIfNull(cdcSourceInventory);

        List<CdcDmsManagedTableInventory> tables = [];

        AddCoreTables(tables, dialect, cdcSourceInventory);
        AddAuthorizationTables(tables, dialect, modelSet);
        AddResourceTables(tables, dialect, modelSet);
        AddTrackedChangeTables(tables, dialect, modelSet);

        return CdcDmsManagedTableInventoryContract.Normalize(tables, nameof(tables));
    }

    private static void AddCoreTables(
        List<CdcDmsManagedTableInventory> tables,
        ISqlDialect dialect,
        IReadOnlyList<CdcSourceTableInventory> cdcSourceInventory
    )
    {
        var sourceTablesByName = cdcSourceInventory.ToDictionary(table => table.TableName);

        foreach (var table in CoreTables())
        {
            var emittedQuotedTableName = sourceTablesByName.TryGetValue(table, out var sourceTable)
                ? sourceTable.EmittedQuotedTableName
                : dialect.QualifyTable(table);
            tables.Add(
                new CdcDmsManagedTableInventory(CdcDmsManagedTableKind.Core, table, emittedQuotedTableName)
            );
        }
    }

    private static void AddAuthorizationTables(
        List<CdcDmsManagedTableInventory> tables,
        ISqlDialect dialect,
        DerivedRelationalModelSet modelSet
    )
    {
        if (modelSet.AuthEdOrgHierarchy is not { EntitiesInNameOrder.Count: > 0 })
        {
            return;
        }

        var table = AuthObjectDefinitions.AuthEdOrgTable.Table;
        tables.Add(
            new CdcDmsManagedTableInventory(
                CdcDmsManagedTableKind.Authorization,
                table,
                dialect.QualifyTable(table)
            )
        );
    }

    private static void AddResourceTables(
        List<CdcDmsManagedTableInventory> tables,
        ISqlDialect dialect,
        DerivedRelationalModelSet modelSet
    )
    {
        foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
        {
            if (resource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            AddTables(
                tables,
                CdcDmsManagedTableKind.Resource,
                dialect,
                resource.RelationalModel.TablesInDependencyOrder.Select(table => table.Table)
            );
        }

        AddTables(
            tables,
            CdcDmsManagedTableKind.Resource,
            dialect,
            modelSet.AbstractIdentityTablesInNameOrder.Select(tableInfo => tableInfo.TableModel.Table)
        );
    }

    private static void AddTrackedChangeTables(
        List<CdcDmsManagedTableInventory> tables,
        ISqlDialect dialect,
        DerivedRelationalModelSet modelSet
    )
    {
        AddTables(
            tables,
            CdcDmsManagedTableKind.TrackedChange,
            dialect,
            modelSet.TrackedChangeTablesInNameOrder.Select(trackedTable => trackedTable.Table)
        );
    }

    private static void AddTables(
        List<CdcDmsManagedTableInventory> tables,
        CdcDmsManagedTableKind tableKind,
        ISqlDialect dialect,
        IEnumerable<DbTableName> tableNames
    )
    {
        foreach (var table in tableNames)
        {
            tables.Add(new CdcDmsManagedTableInventory(tableKind, table, dialect.QualifyTable(table)));
        }
    }

    private static IReadOnlyList<DbTableName> CoreTables() =>
        [
            DmsTableNames.DataStoreIdentity,
            DmsTableNames.CdcHeartbeat,
            DmsTableNames.Descriptor,
            DmsTableNames.Document,
            DmsTableNames.DocumentCache,
            DmsTableNames.DocumentCacheState,
            DmsTableNames.DocumentProjectionWork,
            EffectiveSchemaTableDefinition.Table,
            DmsTableNames.ReferentialIdentity,
            DmsTableNames.ResourceKey,
            DmsTableNames.SchemaComponent,
        ];
}
