// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_ScopeTopologyIndex_for_root_table
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root)
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_RootInlined_for_root_scope()
    {
        _index.GetTopology("$").Should().Be(ScopeTopologyKind.RootInlined);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_root_extension_table
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
            ScopeTopologyIndexTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection_for_root_extension_scope()
    {
        _index.GetTopology("$._ext.sample").Should().Be(ScopeTopologyKind.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_collection_extension_scope
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            ScopeTopologyIndexTestHelpers.CreateTablePlan(
                "$.addresses[*]._ext.sample",
                "AddressExtension",
                DbTableKind.CollectionExtensionScope
            )
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection_for_collection_extension_scope()
    {
        _index
            .GetTopology("$.addresses[*]._ext.sample")
            .Should()
            .Be(ScopeTopologyKind.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_top_level_base_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            )
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_TopLevelBaseCollection_for_top_level_collection()
    {
        _index.GetTopology("$.addresses[*]").Should().Be(ScopeTopologyKind.TopLevelBaseCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_nested_base_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*].periods[*]",
                "AddressPeriods",
                DbTableKind.Collection
            )
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_nested_collection()
    {
        _index
            .GetTopology("$.addresses[*].periods[*]")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_root_level_extension_child_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
            ScopeTopologyIndexTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            ),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$._ext.sample.contacts[*]",
                "Contacts",
                DbTableKind.ExtensionCollection
            )
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_extension_child_collection_under_root_ext()
    {
        _index
            .GetTopology("$._ext.sample.contacts[*]")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_collection_aligned_extension_child_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            ScopeTopologyIndexTestHelpers.CreateTablePlan(
                "$.addresses[*]._ext.sample",
                "AddressExtension",
                DbTableKind.CollectionExtensionScope
            ),
            ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]._ext.sample.deliveryNotes[*]",
                "DeliveryNotes",
                DbTableKind.ExtensionCollection
            )
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_extension_child_collection_under_collection_ext()
    {
        _index
            .GetTopology("$.addresses[*]._ext.sample.deliveryNotes[*]")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_for_unknown_scope
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        var plan = ScopeTopologyIndexTestHelpers.CreateWritePlan(
            ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root)
        );
        _index = ScopeTopologyIndex.BuildFromWritePlan(plan);
    }

    [Test]
    public void It_returns_RootInlined_for_scope_not_in_write_plan()
    {
        _index.GetTopology("$.someInlinedObject").Should().Be(ScopeTopologyKind.RootInlined);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_non_collection_scope_under_root
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root)],
            ("$.address", ScopeKind.NonCollection)
        );
    }

    [Test]
    public void It_returns_RootInlined_for_inlined_scope_under_root()
    {
        _index.GetTopology("$.address").Should().Be(ScopeTopologyKind.RootInlined);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_non_collection_scope_under_top_level_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*]",
                    "Addresses",
                    DbTableKind.Collection
                ),
            ],
            ("$.addresses[*].mileInfo", ScopeKind.NonCollection)
        );
    }

    [Test]
    public void It_returns_TopLevelBaseCollection_for_inlined_scope_under_top_level_collection()
    {
        _index.GetTopology("$.addresses[*].mileInfo").Should().Be(ScopeTopologyKind.TopLevelBaseCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_non_collection_scope_under_nested_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*]",
                    "Addresses",
                    DbTableKind.Collection
                ),
                ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*].periods[*]",
                    "AddressPeriods",
                    DbTableKind.Collection
                ),
            ],
            ("$.addresses[*].periods[*].notes", ScopeKind.NonCollection)
        );
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_inlined_scope_under_nested_collection()
    {
        _index
            .GetTopology("$.addresses[*].periods[*].notes")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_non_collection_scope_under_root_extension
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateTablePlan(
                    "$._ext.sample",
                    "RootExtension",
                    DbTableKind.RootExtension
                ),
            ],
            ("$._ext.sample.locator", ScopeKind.NonCollection)
        );
    }

    [Test]
    public void It_returns_SeparateTableNonCollection_for_inlined_scope_under_root_extension()
    {
        _index.GetTopology("$._ext.sample.locator").Should().Be(ScopeTopologyKind.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_non_collection_scope_under_collection_extension
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*]",
                    "Addresses",
                    DbTableKind.Collection
                ),
                ScopeTopologyIndexTestHelpers.CreateTablePlan(
                    "$.addresses[*]._ext.sample",
                    "AddressExtension",
                    DbTableKind.CollectionExtensionScope
                ),
            ],
            ("$.addresses[*]._ext.sample.marker", ScopeKind.NonCollection)
        );
    }

    [Test]
    public void It_returns_SeparateTableNonCollection_for_inlined_scope_under_collection_extension()
    {
        _index
            .GetTopology("$.addresses[*]._ext.sample.marker")
            .Should()
            .Be(ScopeTopologyKind.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_non_collection_scope_under_extension_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateTablePlan(
                    "$._ext.sample",
                    "RootExtension",
                    DbTableKind.RootExtension
                ),
                ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                    "$._ext.sample.contacts[*]",
                    "Contacts",
                    DbTableKind.ExtensionCollection
                ),
            ],
            ("$._ext.sample.contacts[*].phone", ScopeKind.NonCollection)
        );
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_inlined_scope_under_extension_collection()
    {
        _index
            .GetTopology("$._ext.sample.contacts[*].phone")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_top_level_collection_scope
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root)],
            ("$.inlineItems[*]", ScopeKind.Collection)
        );
    }

    [Test]
    public void It_returns_TopLevelBaseCollection_for_inlined_top_level_collection()
    {
        _index.GetTopology("$.inlineItems[*]").Should().Be(ScopeTopologyKind.TopLevelBaseCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_extension_collection_scope
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateTablePlan(
                    "$._ext.sample",
                    "RootExtension",
                    DbTableKind.RootExtension
                ),
            ],
            ("$._ext.sample.contacts[*]", ScopeKind.Collection)
        );
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_inlined_extension_collection()
    {
        _index
            .GetTopology("$._ext.sample.contacts[*]")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

[TestFixture]
public class Given_ScopeTopologyIndex_with_inlined_nested_collection_under_table_backed_collection
{
    private ScopeTopologyIndex _index = null!;

    [SetUp]
    public void Setup()
    {
        _index = ScopeTopologyIndexTestHelpers.BuildIndexWithInlined(
            [
                ScopeTopologyIndexTestHelpers.CreateTablePlan("$", "Root", DbTableKind.Root),
                ScopeTopologyIndexTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*]",
                    "Addresses",
                    DbTableKind.Collection
                ),
            ],
            ("$.addresses[*].nested[*]", ScopeKind.Collection)
        );
    }

    [Test]
    public void It_returns_NestedOrExtensionCollection_for_inlined_nested_collection_with_collection_ancestor()
    {
        _index
            .GetTopology("$.addresses[*].nested[*]")
            .Should()
            .Be(ScopeTopologyKind.NestedOrExtensionCollection);
    }
}

// ── Shared test helpers ────────────────────────────────────────────────────

file static class ScopeTopologyIndexTestHelpers
{
    private static readonly QualifiedResourceName Resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName Schema = new("edfi");

    public static TableWritePlan CreateTablePlan(string jsonScope, string tableName, DbTableKind tableKind)
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var tableModel = new DbTableModel(
            Table: new DbTableName(Schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    public static TableWritePlan CreateCollectionTablePlan(
        string jsonScope,
        string tableName,
        DbTableKind tableKind
    )
    {
        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var nameColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Name"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
            TargetResource: null
        );

        var columns = new DbColumnModel[] { collectionKeyColumn, parentKeyColumn, ordinalColumn, nameColumn };

        var tableModel = new DbTableModel(
            Table: new DbTableName(Schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new DbColumnName("Name")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@CollectionItemId, @ParentDocumentId, @Ordinal, @Name)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    nameColumn,
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: $"UPDATE edfi.\"{tableName}\" SET \"Name\" = @Name WHERE \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: $"DELETE FROM edfi.\"{tableName}\" WHERE \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    public static ResourceWritePlan CreateWritePlan(params TableWritePlan[] tablePlans)
    {
        var rootModel = tablePlans[0].TableModel;

        var model = new RelationalResourceModel(
            Resource: Resource,
            PhysicalSchema: Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootModel,
            TablesInDependencyOrder: tablePlans.Select(tp => tp.TableModel).ToList(),
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(model, tablePlans);
    }

    public static ScopeTopologyIndex BuildIndexWithInlined(
        TableWritePlan[] tablePlans,
        params (string JsonScope, ScopeKind Kind)[] additionalScopes
    ) => ScopeTopologyIndex.BuildFromWritePlan(CreateWritePlan(tablePlans), additionalScopes);
}
