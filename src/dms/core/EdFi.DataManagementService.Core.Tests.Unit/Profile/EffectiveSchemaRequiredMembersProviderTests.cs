// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
[Parallelizable]
internal class Given_Duplicate_Scopes_When_Resolving_EffectiveSchemaRequiredMembers
{
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _result;

    [SetUp]
    public void Setup()
    {
        var provider = new WritePlanEffectiveSchemaRequiredMembersProvider();

        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "TestResource"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_TestResource",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: null,
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
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

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "TestResource"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var writePlan = new ResourceWritePlan(
            resourceModel,
            [
                new TableWritePlan(
                    TableModel: rootTable,
                    InsertSql: "insert into edfi.\"TestResource\" values (...)",
                    UpdateSql: "update edfi.\"TestResource\" set ...",
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            rootTable.Columns[0],
                            new WriteValueSource.DocumentId(),
                            "DocumentId"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );

        // Scope catalog with duplicate JsonScope "$" entries
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalogWithDuplicates =
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["name"]
            ),
        ];

        _result = provider.Resolve(writePlan, scopeCatalogWithDuplicates);
    }

    [Test]
    public void It_returns_null_instead_of_throwing() => _result.Should().BeNull();
}

[TestFixture]
[Parallelizable]
internal class Given_Distinct_Scopes_When_Resolving_EffectiveSchemaRequiredMembers
{
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _result;

    [SetUp]
    public void Setup()
    {
        var provider = new WritePlanEffectiveSchemaRequiredMembersProvider();

        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "TestResource"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_TestResource",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: null,
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
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

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "TestResource"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var writePlan = new ResourceWritePlan(
            resourceModel,
            [
                new TableWritePlan(
                    TableModel: rootTable,
                    InsertSql: "insert into edfi.\"TestResource\" values (...)",
                    UpdateSql: "update edfi.\"TestResource\" set ...",
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            rootTable.Columns[0],
                            new WriteValueSource.DocumentId(),
                            "DocumentId"
                        ),
                    ],
                    KeyUnificationPlans: []
                ),
            ]
        );

        // Scope catalog with distinct JsonScope entries
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog =
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
        ];

        _result = provider.Resolve(writePlan, scopeCatalog);
    }

    [Test]
    public void It_returns_a_non_null_dictionary() => _result.Should().NotBeNull();
}
