// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.SetPasses.IdentityProjectionResolver;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives the explicit identity-text role (<see cref="DbColumnModel.UsesSqlServerIdentityCollation"/>)
/// for every string column that stores or copies an identity value, so DDL collation emission and
/// runtime comparer selection read one model flag instead of inferring coverage from names or SQL.
/// </summary>
/// <remarks>
/// <para>
/// Flagged string columns: root natural-key columns (via <see cref="IdentityProjectionResolver"/>),
/// <see cref="DocumentReferenceBinding.IdentityBindings"/> columns on any table, abstract identity table
/// columns with a <see cref="DbColumnModel.SourceJsonPath"/>, collection
/// <see cref="DbTableIdentityMetadata.SemanticIdentityBindings"/> columns, and the shared descriptor
/// columns named in <see cref="DescriptorIdentityTextColumns.All"/>.
/// </para>
/// <para>
/// The flag follows storage: a flagged unified alias flags its canonical stored column, and every alias
/// over a flagged canonical column is flagged too, because its effective collation is the canonical
/// column's. The pass is a no-op when the dialect declares no identity text collation (PostgreSQL).
/// </para>
/// </remarks>
public sealed class ApplySqlServerIdentityCollationPass : IRelationalModelSetPass
{
    /// <summary>
    /// Flags identity text columns on concrete resource tables and abstract identity tables.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.DialectRules.IdentityEquality.IdentityTextCollation is null)
        {
            return;
        }

        var rootIdentityColumnsByResource = BuildRootIdentityColumnsByResource(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var entry = context.ConcreteResourcesInNameOrder[index];
            var resourceModel = entry.RelationalModel;
            var identityColumnsByTable = CollectIdentityColumnsByTable(
                entry,
                rootIdentityColumnsByResource.GetValueOrDefault(entry.ResourceKey.Resource, [])
            );

            var updatedTables = resourceModel
                .TablesInDependencyOrder.Select(table =>
                    FlagColumns(table, identityColumnsByTable.GetValueOrDefault(table.Table, []))
                )
                .ToArray();
            var updatedRoot = updatedTables.Single(table =>
                table.JsonScope.Equals(resourceModel.Root.JsonScope)
            );

            context.ConcreteResourcesInNameOrder[index] = entry with
            {
                RelationalModel = resourceModel with
                {
                    Root = updatedRoot,
                    TablesInDependencyOrder = updatedTables,
                },
            };
        }

        for (var index = 0; index < context.AbstractIdentityTablesInNameOrder.Count; index++)
        {
            var entry = context.AbstractIdentityTablesInNameOrder[index];
            var identityColumns = entry
                .TableModel.Columns.Where(column => column.SourceJsonPath is not null)
                .Select(column => column.ColumnName)
                .ToHashSet();

            context.AbstractIdentityTablesInNameOrder[index] = entry with
            {
                TableModel = FlagColumns(entry.TableModel, identityColumns),
            };
        }
    }

    /// <summary>
    /// Resolves each relational-storage resource's root identity columns through the same identity
    /// projection used by trigger derivation. Extension resources contribute no root identity.
    /// </summary>
    private static Dictionary<
        QualifiedResourceName,
        IReadOnlyCollection<DbColumnName>
    > BuildRootIdentityColumnsByResource(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context.ConcreteResourcesInNameOrder.ToDictionary(model =>
            model.ResourceKey.Resource
        );
        Dictionary<QualifiedResourceName, IReadOnlyCollection<DbColumnName>> result = [];

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            if (
                !resourcesByKey.TryGetValue(resource, out var concreteModel)
                || concreteModel.StorageKind != ResourceStorageKind.RelationalTables
            )
            {
                continue;
            }

            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            result[resource] = BuildIdentityElementMappings(
                    concreteModel.RelationalModel,
                    builderContext,
                    resource
                )
                .Select(element => element.Column)
                .ToArray();
        }

        return result;
    }

    /// <summary>
    /// Collects the identity-bearing column names per table for one concrete resource, before string
    /// filtering and unified-alias resolution.
    /// </summary>
    private static Dictionary<DbTableName, HashSet<DbColumnName>> CollectIdentityColumnsByTable(
        ConcreteResourceModel entry,
        IReadOnlyCollection<DbColumnName> rootIdentityColumns
    )
    {
        var resourceModel = entry.RelationalModel;
        Dictionary<DbTableName, HashSet<DbColumnName>> columnsByTable = [];

        HashSet<DbColumnName> ColumnsFor(DbTableName table)
        {
            if (!columnsByTable.TryGetValue(table, out var columns))
            {
                columns = [];
                columnsByTable[table] = columns;
            }

            return columns;
        }

        if (entry.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            ColumnsFor(resourceModel.Root.Table).UnionWith(DescriptorIdentityTextColumns.All);
            return columnsByTable;
        }

        ColumnsFor(resourceModel.Root.Table).UnionWith(rootIdentityColumns);

        foreach (var binding in resourceModel.DocumentReferenceBindings)
        {
            ColumnsFor(binding.Table).UnionWith(binding.IdentityBindings.Select(identity => identity.Column));
        }

        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            ColumnsFor(table.Table)
                .UnionWith(
                    table.IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.ColumnName)
                );
        }

        return columnsByTable;
    }

    /// <summary>
    /// Flags the string columns among <paramref name="identityColumns"/>, resolving unified aliases to
    /// their canonical stored column and flagging every alias over a flagged canonical column.
    /// </summary>
    private static DbTableModel FlagColumns(DbTableModel table, IReadOnlySet<DbColumnName> identityColumns)
    {
        if (identityColumns.Count == 0)
        {
            return table;
        }

        var columnsByName = table.Columns.ToDictionary(column => column.ColumnName);
        HashSet<DbColumnName> flaggedStorage = [];

        foreach (var columnName in identityColumns)
        {
            if (!columnsByName.TryGetValue(columnName, out var column) || !IsString(column))
            {
                continue;
            }

            flaggedStorage.Add(
                column.Storage is ColumnStorage.UnifiedAlias alias ? alias.CanonicalColumn : columnName
            );
        }

        if (flaggedStorage.Count == 0)
        {
            return table;
        }

        var updatedColumns = table
            .Columns.Select(column =>
                IsString(column) && flaggedStorage.Contains(StorageColumnOf(column))
                    ? column with
                    {
                        UsesSqlServerIdentityCollation = true,
                    }
                    : column
            )
            .ToArray();

        return table with
        {
            Columns = updatedColumns,
        };
    }

    private static bool IsString(DbColumnModel column) => column.ScalarType?.Kind == ScalarKind.String;

    private static DbColumnName StorageColumnOf(DbColumnModel column) =>
        column.Storage is ColumnStorage.UnifiedAlias alias ? alias.CanonicalColumn : column.ColumnName;
}
