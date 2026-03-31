// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalWriteContracts
{
    private ContractFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = ContractFixture.Create();
    }

    [Test]
    public void It_keeps_root_document_and_collection_item_keys_unresolved_for_create_flows()
    {
        var flatteningInput = new FlatteningInput(
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            _fixture.ResourceWritePlan,
            _fixture.SelectedBody,
            _fixture.ResolvedReferences
        );

        var topLevelCollectionCandidate = new CollectionWriteCandidate(
            _fixture.CollectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values:
            [
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Home"),
            ],
            semanticIdentityValues: ["Home"]
        );

        var writeSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                _fixture.RootPlan,
                values:
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ],
                collectionCandidates: [topLevelCollectionCandidate]
            )
        );

        flatteningInput.SelectedBody.Should().BeSameAs(_fixture.SelectedBody);
        flatteningInput.TargetContext.Should().BeOfType<RelationalWriteTargetContext.CreateNew>();
        writeSet.RootRow.Values[0].Should().BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        writeSet
            .RootRow.CollectionCandidates[0]
            .Values[0]
            .Should()
            .BeSameAs(FlattenedWriteValue.UnresolvedCollectionItemId.Instance);
        writeSet
            .RootRow.CollectionCandidates[0]
            .Values[1]
            .Should()
            .BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
    }

    [Test]
    public void It_keeps_collection_aligned_extension_scope_rows_attached_to_the_owning_collection_candidate()
    {
        var alignedScopeData = new CandidateAttachedAlignedScopeData(
            _fixture.CollectionExtensionScopePlan,
            values:
            [
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                new FlattenedWriteValue.Literal("Purple"),
            ]
        );

        var topLevelCollectionCandidate = new CollectionWriteCandidate(
            _fixture.CollectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values:
            [
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Home"),
            ],
            semanticIdentityValues: ["Home"],
            attachedAlignedScopeData: [alignedScopeData]
        );

        var writeSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                _fixture.RootPlan,
                values:
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ],
                collectionCandidates: [topLevelCollectionCandidate]
            )
        );

        writeSet.RootRow.RootExtensionRows.Should().BeEmpty();
        writeSet.RootRow.CollectionCandidates.Should().ContainSingle();
        writeSet.RootRow.CollectionCandidates[0].AttachedAlignedScopeData.Should().ContainSingle();
        writeSet
            .RootRow.CollectionCandidates[0]
            .AttachedAlignedScopeData[0]
            .TableWritePlan.TableModel.Table.Should()
            .Be(_fixture.CollectionExtensionScopePlan.TableModel.Table);
    }

    [Test]
    public void It_rejects_collection_aligned_extension_scope_rows_as_root_extension_rows()
    {
        var act = () =>
            new RootExtensionWriteRowBuffer(
                _fixture.CollectionExtensionScopePlan,
                values:
                [
                    FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                    new FlattenedWriteValue.Literal("Purple"),
                ]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*RootExtensionWriteRowBuffer*RootExtension*CollectionExtensionScope*");
    }

    private sealed record ContractFixture(
        DocumentUuid DocumentUuid,
        JsonNode SelectedBody,
        ResolvedReferenceSet ResolvedReferences,
        ResourceWritePlan ResourceWritePlan,
        TableWritePlan RootPlan,
        TableWritePlan CollectionPlan,
        TableWritePlan CollectionExtensionScopePlan
    )
    {
        public static ContractFixture Create()
        {
            var rootPlan = CreateRootPlan();
            var collectionPlan = CreateCollectionPlan();
            var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan();

            var resourceModel = new RelationalResourceModel(
                new QualifiedResourceName("Ed-Fi", "School"),
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                rootPlan.TableModel,
                [rootPlan.TableModel, collectionPlan.TableModel, collectionExtensionScopePlan.TableModel],
                [],
                []
            );

            return new ContractFixture(
                new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                JsonNode.Parse("""{"name":"Lincoln High","addresses":[{"addressType":"Home"}]}""")!,
                CreateEmptyResolvedReferenceSet(),
                new ResourceWritePlan(
                    resourceModel,
                    [rootPlan, collectionPlan, collectionExtensionScopePlan]
                ),
                rootPlan,
                collectionPlan,
                collectionExtensionScopePlan
            );
        }

        private static ResolvedReferenceSet CreateEmptyResolvedReferenceSet()
        {
            return new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            );
        }

        private static TableWritePlan CreateRootPlan()
        {
            var tableModel = new DbTableModel(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new JsonPathExpression("$", []),
                new TableKey(
                    "PK_School",
                    [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                [
                    new DbColumnModel(
                        new DbColumnName("DocumentId"),
                        ColumnKind.ParentKeyPart,
                        null,
                        false,
                        null,
                        null,
                        new ColumnStorage.Stored()
                    ),
                    new DbColumnModel(
                        new DbColumnName("Name"),
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                        false,
                        new JsonPathExpression("$.name", []),
                        null,
                        new ColumnStorage.Stored()
                    ),
                ],
                []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    DbTableKind.Root,
                    [new DbColumnName("DocumentId")],
                    [new DbColumnName("DocumentId")],
                    [],
                    []
                ),
            };

            return new TableWritePlan(
                tableModel,
                InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
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
                            new JsonPathExpression("$.name", []),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                        ),
                        "Name"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }

        private static TableWritePlan CreateCollectionPlan()
        {
            var tableModel = new DbTableModel(
                new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
                new JsonPathExpression("$.addresses[*]", []),
                new TableKey(
                    "PK_SchoolAddress",
                    [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
                ),
                [
                    new DbColumnModel(
                        new DbColumnName("CollectionItemId"),
                        ColumnKind.CollectionKey,
                        null,
                        false,
                        null,
                        null,
                        new ColumnStorage.Stored()
                    ),
                    new DbColumnModel(
                        new DbColumnName("School_DocumentId"),
                        ColumnKind.ParentKeyPart,
                        null,
                        false,
                        null,
                        null,
                        new ColumnStorage.Stored()
                    ),
                    new DbColumnModel(
                        new DbColumnName("Ordinal"),
                        ColumnKind.Ordinal,
                        null,
                        false,
                        null,
                        null,
                        new ColumnStorage.Stored()
                    ),
                    new DbColumnModel(
                        new DbColumnName("AddressType"),
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        false,
                        new JsonPathExpression("$.addressType", []),
                        null,
                        new ColumnStorage.Stored()
                    ),
                ],
                []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    DbTableKind.Collection,
                    [new DbColumnName("CollectionItemId")],
                    [new DbColumnName("School_DocumentId")],
                    [new DbColumnName("School_DocumentId")],
                    [
                        new CollectionSemanticIdentityBinding(
                            new JsonPathExpression("$.addressType", []),
                            new DbColumnName("AddressType")
                        ),
                    ]
                ),
            };

            return new TableWritePlan(
                tableModel,
                InsertSql: "insert into edfi.\"SchoolAddress\" values (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
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
                            new JsonPathExpression("$.addressType", []),
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
                            new JsonPathExpression("$.addressType", []),
                            3
                        ),
                    ],
                    StableRowIdentityBindingIndex: 0,
                    UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"AddressType\" = @AddressType where \"CollectionItemId\" = @CollectionItemId",
                    DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                    OrdinalBindingIndex: 2,
                    CompareBindingIndexesInOrder: [3, 2]
                ),
                CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                    new DbColumnName("CollectionItemId"),
                    0
                )
            );
        }

        private static TableWritePlan CreateCollectionExtensionScopePlan()
        {
            var tableModel = new DbTableModel(
                new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddress"),
                new JsonPathExpression("$.addresses[*]._ext.sample", []),
                new TableKey(
                    "PK_SchoolExtensionAddress",
                    [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
                ),
                [
                    new DbColumnModel(
                        new DbColumnName("BaseCollectionItemId"),
                        ColumnKind.ParentKeyPart,
                        null,
                        false,
                        null,
                        null,
                        new ColumnStorage.Stored()
                    ),
                    new DbColumnModel(
                        new DbColumnName("FavoriteColor"),
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        true,
                        new JsonPathExpression("$.favoriteColor", []),
                        null,
                        new ColumnStorage.Stored()
                    ),
                ],
                []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    DbTableKind.CollectionExtensionScope,
                    [new DbColumnName("BaseCollectionItemId")],
                    [new DbColumnName("BaseCollectionItemId")],
                    [new DbColumnName("BaseCollectionItemId")],
                    []
                ),
            };

            return new TableWritePlan(
                tableModel,
                InsertSql: "insert into sample.\"SchoolExtensionAddress\" values (@BaseCollectionItemId, @FavoriteColor)",
                UpdateSql: "update sample.\"SchoolExtensionAddress\" set \"FavoriteColor\" = @FavoriteColor where \"BaseCollectionItemId\" = @BaseCollectionItemId",
                DeleteByParentSql: "delete from sample.\"SchoolExtensionAddress\" where \"BaseCollectionItemId\" = @BaseCollectionItemId",
                BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.ParentKeyPart(0),
                        "BaseCollectionItemId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.Scalar(
                            new JsonPathExpression("$.favoriteColor", []),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "FavoriteColor"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }
    }
}
