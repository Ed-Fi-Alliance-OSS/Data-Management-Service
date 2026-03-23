// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Build.RelationalModelSystemColumnFactory;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Centralizes stable-row-identity metadata, seeded system columns, and parent/root FK shape derivation.
/// </summary>
internal static class RelationalModelStableIdentityHelper
{
    /// <summary>
    /// Builds the child-table PK column for stable collection row identity.
    /// </summary>
    internal static TableKey BuildChildTableKey(DbTableName tableName)
    {
        return new TableKey(
            ConstraintNaming.BuildPrimaryKeyName(tableName),
            [new DbKeyColumn(RelationalNameConventions.CollectionItemIdColumnName, ColumnKind.CollectionKey)]
        );
    }

    /// <summary>
    /// Builds root-table identity metadata.
    /// </summary>
    internal static DbTableIdentityMetadata BuildRootTableIdentityMetadata()
    {
        return CreateIdentityMetadata(
            DbTableKind.Root,
            [RelationalNameConventions.DocumentIdColumnName],
            [RelationalNameConventions.DocumentIdColumnName],
            []
        );
    }

    /// <summary>
    /// Builds collection-table identity metadata for the base resource graph.
    /// </summary>
    internal static DbTableIdentityMetadata BuildCollectionTableIdentityMetadata(
        string rootBaseName,
        bool isNestedCollection
    )
    {
        var rootDocumentIdColumn = RelationalNameConventions.RootDocumentIdColumnName(rootBaseName);
        IReadOnlyList<DbColumnName> immediateParentColumns = isNestedCollection
            ? [RelationalNameConventions.ParentCollectionItemIdColumnName]
            : [rootDocumentIdColumn];

        return CreateIdentityMetadata(
            DbTableKind.Collection,
            [RelationalNameConventions.CollectionItemIdColumnName],
            [rootDocumentIdColumn],
            immediateParentColumns
        );
    }

    /// <summary>
    /// Builds root-scope extension-table identity metadata.
    /// </summary>
    internal static DbTableIdentityMetadata BuildRootExtensionTableIdentityMetadata()
    {
        return CreateIdentityMetadata(
            DbTableKind.RootExtension,
            [RelationalNameConventions.DocumentIdColumnName],
            [RelationalNameConventions.DocumentIdColumnName],
            [RelationalNameConventions.DocumentIdColumnName]
        );
    }

    /// <summary>
    /// Builds collection-aligned extension-scope identity metadata.
    /// </summary>
    internal static DbTableIdentityMetadata BuildCollectionExtensionScopeIdentityMetadata(
        string baseRootBaseName
    )
    {
        var rootDocumentIdColumn = RelationalNameConventions.RootDocumentIdColumnName(baseRootBaseName);

        return CreateIdentityMetadata(
            DbTableKind.CollectionExtensionScope,
            [RelationalNameConventions.BaseCollectionItemIdColumnName],
            [rootDocumentIdColumn],
            [RelationalNameConventions.BaseCollectionItemIdColumnName]
        );
    }

    /// <summary>
    /// Builds extension child-collection identity metadata based on the owning parent table kind.
    /// </summary>
    internal static DbTableIdentityMetadata BuildExtensionChildTableIdentityMetadata(
        string baseRootBaseName,
        DbTableKind parentTableKind
    )
    {
        var rootDocumentIdColumn = RelationalNameConventions.RootDocumentIdColumnName(baseRootBaseName);
        IReadOnlyList<DbColumnName> immediateParentColumns = parentTableKind switch
        {
            DbTableKind.RootExtension => [rootDocumentIdColumn],
            DbTableKind.CollectionExtensionScope =>
            [
                RelationalNameConventions.BaseCollectionItemIdColumnName,
            ],
            DbTableKind.ExtensionCollection => [RelationalNameConventions.ParentCollectionItemIdColumnName],
            _ => throw new InvalidOperationException(
                $"Unsupported parent table kind '{parentTableKind}' for extension child collection derivation."
            ),
        };

        return CreateIdentityMetadata(
            DbTableKind.ExtensionCollection,
            [RelationalNameConventions.CollectionItemIdColumnName],
            [rootDocumentIdColumn],
            immediateParentColumns
        );
    }

    /// <summary>
    /// Builds the seeded system-column inventory implied by explicit identity metadata.
    /// </summary>
    internal static DbColumnModel[] BuildIdentityColumns(DbTableIdentityMetadata identityMetadata)
    {
        List<DbColumnModel> columns = [];

        AddColumns(
            columns,
            identityMetadata.PhysicalRowIdentityColumns,
            ResolvePhysicalIdentityColumnKind(identityMetadata.TableKind)
        );
        AddColumns(columns, identityMetadata.RootScopeLocatorColumns, ColumnKind.ParentKeyPart);
        AddColumns(columns, identityMetadata.ImmediateParentScopeLocatorColumns, ColumnKind.ParentKeyPart);

        if (UsesOrdinalColumn(identityMetadata.TableKind))
        {
            AddColumn(columns, RelationalNameConventions.OrdinalColumnName, ColumnKind.Ordinal);
        }

        return columns.ToArray();
    }

    /// <summary>
    /// Builds FK source columns from child identity metadata and parent table shape.
    /// </summary>
    internal static DbColumnName[] BuildParentScopeForeignKeyColumns(
        DbTableIdentityMetadata childIdentityMetadata,
        DbTableModel parentTable
    )
    {
        if (UsesSingleColumnParentScopeForeignKey(parentTable.IdentityMetadata.TableKind))
        {
            return childIdentityMetadata.ImmediateParentScopeLocatorColumns.ToArray();
        }

        return
        [
            .. childIdentityMetadata.ImmediateParentScopeLocatorColumns,
            .. childIdentityMetadata.RootScopeLocatorColumns,
        ];
    }

    /// <summary>
    /// Builds FK target columns from parent identity metadata.
    /// </summary>
    internal static DbColumnName[] BuildParentScopeForeignKeyTargetColumns(DbTableModel parentTable)
    {
        if (UsesSingleColumnParentScopeForeignKey(parentTable.IdentityMetadata.TableKind))
        {
            return parentTable.IdentityMetadata.PhysicalRowIdentityColumns.ToArray();
        }

        return
        [
            .. parentTable.IdentityMetadata.PhysicalRowIdentityColumns,
            .. parentTable.IdentityMetadata.RootScopeLocatorColumns,
        ];
    }

    private static DbTableIdentityMetadata CreateIdentityMetadata(
        DbTableKind tableKind,
        IReadOnlyList<DbColumnName> physicalRowIdentityColumns,
        IReadOnlyList<DbColumnName> rootScopeLocatorColumns,
        IReadOnlyList<DbColumnName> immediateParentScopeLocatorColumns
    )
    {
        return new DbTableIdentityMetadata(
            tableKind,
            physicalRowIdentityColumns,
            rootScopeLocatorColumns,
            immediateParentScopeLocatorColumns,
            []
        );
    }

    private static void AddColumns(
        List<DbColumnModel> columns,
        IReadOnlyList<DbColumnName> columnNames,
        ColumnKind columnKind
    )
    {
        foreach (var columnName in columnNames)
        {
            AddColumn(columns, columnName, columnKind);
        }
    }

    private static void AddColumn(List<DbColumnModel> columns, DbColumnName columnName, ColumnKind columnKind)
    {
        if (columns.Any(column => column.ColumnName.Equals(columnName)))
        {
            return;
        }

        columns.Add(CreateKeyColumn(columnName, columnKind));
    }

    private static ColumnKind ResolvePhysicalIdentityColumnKind(DbTableKind tableKind)
    {
        return tableKind switch
        {
            DbTableKind.Root => ColumnKind.ParentKeyPart,
            DbTableKind.Collection => ColumnKind.CollectionKey,
            DbTableKind.RootExtension => ColumnKind.ParentKeyPart,
            DbTableKind.CollectionExtensionScope => ColumnKind.ParentKeyPart,
            DbTableKind.ExtensionCollection => ColumnKind.CollectionKey,
            _ => throw new InvalidOperationException(
                $"Unsupported table kind '{tableKind}' for stable identity column derivation."
            ),
        };
    }

    private static bool UsesOrdinalColumn(DbTableKind tableKind)
    {
        return tableKind is DbTableKind.Collection or DbTableKind.ExtensionCollection;
    }

    private static bool UsesSingleColumnParentScopeForeignKey(DbTableKind parentTableKind)
    {
        return parentTableKind is DbTableKind.Root or DbTableKind.RootExtension;
    }
}
