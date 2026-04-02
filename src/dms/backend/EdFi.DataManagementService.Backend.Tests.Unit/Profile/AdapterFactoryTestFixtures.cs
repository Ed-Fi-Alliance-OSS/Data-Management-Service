// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Shared fixture builders for CompiledScopeAdapterFactory tests.
/// </summary>
internal static class AdapterFactoryTestFixtures
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _schema = new("edfi");

    /// <summary>
    /// Builds a ResourceWritePlan containing only a root table with two scalar columns.
    /// </summary>
    public static ResourceWritePlan BuildRootOnlyPlan()
    {
        var rootTableModel = BuildRootTableModel();

        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var rootPlan = BuildRootTableWritePlan(rootTableModel);

        return new ResourceWritePlan(model, [rootPlan]);
    }

    /// <summary>
    /// Builds a ResourceWritePlan with a root table and one collection table (addresses).
    /// The collection table has a CollectionMergePlan with a single semantic identity binding.
    /// </summary>
    public static ResourceWritePlan BuildRootAndCollectionPlan()
    {
        var rootTableModel = BuildRootTableModel();
        var collectionTableModel = BuildCollectionTableModel();

        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, collectionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var rootPlan = BuildRootTableWritePlan(rootTableModel);
        var collectionPlan = BuildCollectionTableWritePlan(collectionTableModel);

        return new ResourceWritePlan(model, [rootPlan, collectionPlan]);
    }

    /// <summary>
    /// Builds a ResourceWritePlan with a root table and one RootExtension table (_ext.sample).
    /// </summary>
    public static ResourceWritePlan BuildRootAndExtensionPlan()
    {
        var rootTableModel = BuildRootTableModel();
        var extensionTableModel = BuildRootExtensionTableModel();

        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, extensionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var rootPlan = BuildRootTableWritePlan(rootTableModel);
        var extensionPlan = BuildRootExtensionTableWritePlan(extensionTableModel);

        return new ResourceWritePlan(model, [rootPlan, extensionPlan]);
    }

    // ── DbTableModel builders ──────────────────────────────────────────────

    public static DbTableModel BuildRootTableModel() =>
        new(
            Table: new DbTableName(_schema, "School"),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                Column(
                    "SchoolId",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    isNullable: false,
                    sourceJsonPath: Path("$.schoolId", new JsonPathSegment.Property("schoolId"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

    public static DbTableModel BuildCollectionTableModel() =>
        new(
            Table: new DbTableName(_schema, "SchoolAddress"),
            JsonScope: Path(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                Column("CollectionItemId", ColumnKind.CollectionKey, null, isNullable: false),
                Column("School_DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                Column("Ordinal", ColumnKind.Ordinal, null, isNullable: false),
                Column(
                    "AddressType",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    isNullable: false,
                    sourceJsonPath: Path("$.addressType", new JsonPathSegment.Property("addressType"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        Path("$.addressType", new JsonPathSegment.Property("addressType")),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

    public static DbTableModel BuildRootExtensionTableModel() =>
        new(
            Table: new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            JsonScope: Path(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            Key: new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                Column("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                Column(
                    "FavoriteColor",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    isNullable: true,
                    sourceJsonPath: Path(
                        "$._ext.sample.favoriteColor",
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("favoriteColor")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

    // ── TableWritePlan builders ────────────────────────────────────────────

    public static TableWritePlan BuildRootTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"School\" VALUES (@DocumentId, @SchoolId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, tableModel.Columns.Count, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        Path("$.schoolId", new JsonPathSegment.Property("schoolId")),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolId"
                ),
            ],
            KeyUnificationPlans: []
        );

    public static TableWritePlan BuildCollectionTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"SchoolAddress\" VALUES (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, tableModel.Columns.Count, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        Path("$.addressType", new JsonPathSegment.Property("addressType")),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "AddressType"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        Path("$.addressType", new JsonPathSegment.Property("addressType")),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"SchoolAddress\" SET \"AddressType\" = @AddressType WHERE \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"SchoolAddress\" WHERE \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

    public static TableWritePlan BuildRootExtensionTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "INSERT INTO sample.\"SchoolExtension\" VALUES (@DocumentId, @FavoriteColor)",
            UpdateSql: "UPDATE sample.\"SchoolExtension\" SET \"FavoriteColor\" = @FavoriteColor WHERE \"DocumentId\" = @DocumentId",
            DeleteByParentSql: "DELETE FROM sample.\"SchoolExtension\" WHERE \"DocumentId\" = @DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, tableModel.Columns.Count, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        Path(
                            "$._ext.sample.favoriteColor",
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor")
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );

    // ── Helpers ───────────────────────────────────────────────────────────

    private static JsonPathExpression Path(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);

    private static DbColumnModel Column(
        string columnName,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        bool isNullable,
        JsonPathExpression? sourceJsonPath = null,
        QualifiedResourceName? targetResource = null
    ) =>
        new(
            ColumnName: new DbColumnName(columnName),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: targetResource,
            Storage: new ColumnStorage.Stored()
        );
}
