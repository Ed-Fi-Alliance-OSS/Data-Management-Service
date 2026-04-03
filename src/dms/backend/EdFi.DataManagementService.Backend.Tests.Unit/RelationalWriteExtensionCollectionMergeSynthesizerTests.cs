// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_No_Profile_Merge_Synthesizer_Extension_Collections
{
    private RelationalWriteNoProfileMergeSynthesizer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteNoProfileMergeSynthesizer();
    }

    [Test]
    public void It_preserves_stable_identity_for_root_extension_child_collections()
    {
        var fixture = CreateFixture();
        var mentorCollectionItemId = NewCollectionItemId();
        var coachCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("BLUE")],
                        collectionCandidates:
                        [
                            CreateRootExtensionChildCandidate(
                                fixture,
                                requestOrder: 0,
                                collectionItemId: mentorCollectionItemId,
                                interventionCode: "Mentor"
                            ),
                            CreateRootExtensionChildCandidate(
                                fixture,
                                requestOrder: 1,
                                collectionItemId: coachCollectionItemId,
                                interventionCode: "Coach"
                            ),
                        ]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            rootExtensionRows:
            [
                [345L, "BLUE"],
            ],
            rootExtensionChildRows:
            [
                [45L, 345L, 0, "Mentor"],
                [44L, 345L, 1, "Tutor"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var state = RequireState(result, fixture.RootExtensionChildPlan);

        state.CurrentRows.Should().HaveCount(2);
        state.MergedRows.Should().HaveCount(2);
        LiteralValue(state.MergedRows[0].Values[0]).Should().Be(45L);
        LiteralValue(state.MergedRows[0].Values[1]).Should().Be(345L);
        LiteralValue(state.MergedRows[0].Values[2]).Should().Be(0);
        LiteralValue(state.MergedRows[0].Values[3]).Should().Be("Mentor");
        state.MergedRows[0].ComparableValues.Select(LiteralValue).Should().Equal(0, "Mentor");

        state.MergedRows[1].Values[0].Should().BeSameAs(coachCollectionItemId);
        LiteralValue(state.MergedRows[1].Values[1]).Should().Be(345L);
        LiteralValue(state.MergedRows[1].Values[2]).Should().Be(1);
        LiteralValue(state.MergedRows[1].Values[3]).Should().Be("Coach");
        state.MergedRows[1].ComparableValues.Select(LiteralValue).Should().Equal(1, "Coach");
    }

    [Test]
    public void It_matches_collection_aligned_extension_child_collections_using_the_owning_base_row_identity()
    {
        var fixture = CreateFixture();
        var addressCollectionItemId = NewCollectionItemId();
        var childCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: addressCollectionItemId,
                        addressType: "Home",
                        attachedAlignedScopeData:
                        [
                            CreateAlignedExtensionScope(
                                fixture,
                                addressCollectionItemId,
                                "Purple",
                                [
                                    CreateCollectionAlignedExtensionChildCandidate(
                                        fixture,
                                        requestOrder: 0,
                                        collectionItemId: childCollectionItemId,
                                        baseCollectionItemId: addressCollectionItemId,
                                        serviceName: "Bus"
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [44L, 345L, 1, "Home"],
            ],
            collectionExtensionRows:
            [
                [44L, "Purple"],
            ],
            collectionAlignedExtensionChildRows:
            [
                [501L, 345L, 44L, 1, "Bus"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var addressState = RequireState(result, fixture.AddressPlan);
        var childState = RequireState(result, fixture.CollectionAlignedExtensionChildPlan);

        addressState.MergedRows.Should().ContainSingle();
        LiteralValue(addressState.MergedRows[0].Values[0]).Should().Be(44L);
        LiteralValue(addressState.MergedRows[0].Values[2]).Should().Be(0);
        LiteralValue(addressState.MergedRows[0].Values[3]).Should().Be("Home");

        childState.CurrentRows.Should().ContainSingle();
        childState.MergedRows.Should().ContainSingle();
        LiteralValue(childState.MergedRows[0].Values[0]).Should().Be(501L);
        LiteralValue(childState.MergedRows[0].Values[1]).Should().Be(345L);
        LiteralValue(childState.MergedRows[0].Values[2]).Should().Be(44L);
        LiteralValue(childState.MergedRows[0].Values[3]).Should().Be(0);
        LiteralValue(childState.MergedRows[0].Values[4]).Should().Be("Bus");
        childState.MergedRows[0].ComparableValues.Select(LiteralValue).Should().Equal(0, "Bus");
    }

    [Test]
    public void It_normalizes_collection_aligned_extension_scope_rows_for_guarded_no_op_compare_after_reorder()
    {
        var fixture = CreateFixture();
        var mailingCollectionItemId = NewCollectionItemId();
        var homeCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: mailingCollectionItemId,
                        addressType: "Mailing",
                        attachedAlignedScopeData:
                        [
                            CreateAlignedExtensionScope(fixture, mailingCollectionItemId, "Gold"),
                        ]
                    ),
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 1,
                        collectionItemId: homeCollectionItemId,
                        addressType: "Home",
                        attachedAlignedScopeData:
                        [
                            CreateAlignedExtensionScope(fixture, homeCollectionItemId, "Purple"),
                        ]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [45L, 345L, 0, "Mailing"],
                [44L, 345L, 1, "Home"],
            ],
            collectionExtensionRows:
            [
                [44L, "Purple"],
                [45L, "Gold"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var collectionExtensionState = RequireState(result, fixture.CollectionExtensionPlan);

        collectionExtensionState.CurrentRows.Should().HaveCount(2);
        collectionExtensionState.MergedRows.Should().HaveCount(2);
        collectionExtensionState
            .CurrentRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(44L, "Purple");
        collectionExtensionState
            .CurrentRows[1]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(45L, "Gold");
        collectionExtensionState
            .MergedRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(44L, "Purple");
        collectionExtensionState
            .MergedRows[1]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(45L, "Gold");
        RelationalWriteGuardedNoOp.IsNoOpCandidate(result).Should().BeTrue();
    }

    private static RelationalWriteNoProfileTableState RequireState(
        RelationalWriteNoProfileMergeResult result,
        TableWritePlan tableWritePlan
    )
    {
        return result.TablesInDependencyOrder.Single(tableState =>
            ReferenceEquals(tableState.TableWritePlan, tableWritePlan)
        );
    }

    private static ExtensionCollectionFixture CreateFixture()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var addressPlan = CreateAddressPlan();
        var collectionExtensionPlan = CreateCollectionExtensionPlan();
        var rootExtensionChildPlan = CreateRootExtensionChildPlan();
        var collectionAlignedExtensionChildPlan = CreateCollectionAlignedExtensionChildPlan();

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                rootExtensionPlan.TableModel,
                addressPlan.TableModel,
                collectionExtensionPlan.TableModel,
                rootExtensionChildPlan.TableModel,
                collectionAlignedExtensionChildPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ExtensionCollectionFixture(
            new ResourceWritePlan(
                resourceModel,
                [
                    rootPlan,
                    rootExtensionPlan,
                    addressPlan,
                    collectionExtensionPlan,
                    rootExtensionChildPlan,
                    collectionAlignedExtensionChildPlan,
                ]
            ),
            rootPlan,
            rootExtensionPlan,
            addressPlan,
            collectionExtensionPlan,
            rootExtensionChildPlan,
            collectionAlignedExtensionChildPlan
        );
    }

    private static RelationalWriteCurrentState CreateCurrentState(
        ExtensionCollectionFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? rootExtensionRows = null,
        IReadOnlyList<object?[]>? addressRows = null,
        IReadOnlyList<object?[]>? collectionExtensionRows = null,
        IReadOnlyList<object?[]>? rootExtensionChildRows = null,
        IReadOnlyList<object?[]>? collectionAlignedExtensionChildRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.RootExtensionPlan.TableModel, rootExtensionRows ?? []),
                new HydratedTableRows(fixture.AddressPlan.TableModel, addressRows ?? []),
                new HydratedTableRows(
                    fixture.CollectionExtensionPlan.TableModel,
                    collectionExtensionRows ?? []
                ),
                new HydratedTableRows(
                    fixture.RootExtensionChildPlan.TableModel,
                    rootExtensionChildRows ?? []
                ),
                new HydratedTableRows(
                    fixture.CollectionAlignedExtensionChildPlan.TableModel,
                    collectionAlignedExtensionChildRows ?? []
                ),
            ]
        );
    }

    private static FlattenedWriteValue.UnresolvedCollectionItemId NewCollectionItemId() =>
        FlattenedWriteValue.UnresolvedCollectionItemId.Create();

    private static CollectionWriteCandidate CreateRootExtensionChildCandidate(
        ExtensionCollectionFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        string interventionCode
    )
    {
        return new CollectionWriteCandidate(
            fixture.RootExtensionChildPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: [collectionItemId, Literal(345L), Literal(requestOrder), Literal(interventionCode)],
            semanticIdentityValues: [interventionCode]
        );
    }

    private static CollectionWriteCandidate CreateAddressCandidate(
        ExtensionCollectionFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        string addressType,
        IReadOnlyList<CandidateAttachedAlignedScopeData>? attachedAlignedScopeData = null
    )
    {
        return new CollectionWriteCandidate(
            fixture.AddressPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: [collectionItemId, Literal(345L), Literal(requestOrder), Literal(addressType)],
            semanticIdentityValues: [addressType],
            attachedAlignedScopeData: attachedAlignedScopeData ?? []
        );
    }

    private static CandidateAttachedAlignedScopeData CreateAlignedExtensionScope(
        ExtensionCollectionFixture fixture,
        FlattenedWriteValue baseCollectionItemId,
        string favoriteColor,
        IReadOnlyList<CollectionWriteCandidate>? collectionCandidates = null
    )
    {
        return new CandidateAttachedAlignedScopeData(
            fixture.CollectionExtensionPlan,
            values: [baseCollectionItemId, Literal(favoriteColor)],
            collectionCandidates: collectionCandidates ?? []
        );
    }

    private static CollectionWriteCandidate CreateCollectionAlignedExtensionChildCandidate(
        ExtensionCollectionFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        FlattenedWriteValue baseCollectionItemId,
        string serviceName
    )
    {
        return new CollectionWriteCandidate(
            fixture.CollectionAlignedExtensionChildPlan,
            ordinalPath: [0, requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                baseCollectionItemId,
                Literal(requestOrder),
                Literal(serviceName),
            ],
            semanticIdentityValues: [serviceName]
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
            [CreateColumn("DocumentId", ColumnKind.ParentKeyPart), CreateColumn("Name", ColumnKind.Scalar)],
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
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
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

    private static TableWritePlan CreateRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression("$._ext.sample", []),
            new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("ExtensionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtension\" values (@School_DocumentId, @ExtensionCode)",
            UpdateSql: "update sample.\"SchoolExtension\" set \"ExtensionCode\" = @ExtensionCode where \"School_DocumentId\" = @School_DocumentId",
            DeleteByParentSql: "delete from sample.\"SchoolExtension\" where \"School_DocumentId\" = @School_DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.extensionCode", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ExtensionCode"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateAddressPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("AddressType", ColumnKind.Scalar),
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
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.addressType", []), 3)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"Ordinal\" = @Ordinal, \"AddressType\" = @AddressType where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateCollectionExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddress"),
            new JsonPathExpression("$.addresses[*]._ext.sample", []),
            new TableKey(
                "PK_SchoolExtensionAddress",
                [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("FavoriteColor", ColumnKind.Scalar),
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

    private static TableWritePlan CreateRootExtensionChildPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionIntervention"),
            new JsonPathExpression("$._ext.sample.interventions[*]", []),
            new TableKey(
                "PK_SchoolExtensionIntervention",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("InterventionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.interventionCode", []),
                        new DbColumnName("InterventionCode")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtensionIntervention\" values (@CollectionItemId, @School_DocumentId, @Ordinal, @InterventionCode)",
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
                        new JsonPathExpression("$.interventionCode", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "InterventionCode"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.interventionCode", []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update sample.\"SchoolExtensionIntervention\" set \"Ordinal\" = @Ordinal, \"InterventionCode\" = @InterventionCode where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from sample.\"SchoolExtensionIntervention\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateCollectionAlignedExtensionChildPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddressService"),
            new JsonPathExpression("$.addresses[*]._ext.sample.services[*]", []),
            new TableKey(
                "PK_SchoolExtensionAddressService",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("ServiceName", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("BaseCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.serviceName", []),
                        new DbColumnName("ServiceName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtensionAddressService\" values (@CollectionItemId, @School_DocumentId, @BaseCollectionItemId, @Ordinal, @ServiceName)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
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
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.serviceName", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ServiceName"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.serviceName", []), 4)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update sample.\"SchoolExtensionAddressService\" set \"Ordinal\" = @Ordinal, \"ServiceName\" = @ServiceName where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from sample.\"SchoolExtensionAddressService\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static DbColumnModel CreateColumn(string name, ColumnKind kind)
    {
        return new DbColumnModel(
            new DbColumnName(name),
            kind,
            kind is ColumnKind.Scalar or ColumnKind.Ordinal
                ? new RelationalScalarType(ScalarKind.String)
                : null,
            false,
            null,
            null,
            new ColumnStorage.Stored()
        );
    }

    private static FlattenedWriteValue Literal(object? value) => new FlattenedWriteValue.Literal(value);

    private static object? LiteralValue(FlattenedWriteValue value) =>
        value is FlattenedWriteValue.Literal literalValue
            ? literalValue.Value
            : throw new AssertionException($"Expected a literal value but found '{value.GetType().Name}'.");

    private sealed record ExtensionCollectionFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan RootExtensionPlan,
        TableWritePlan AddressPlan,
        TableWritePlan CollectionExtensionPlan,
        TableWritePlan RootExtensionChildPlan,
        TableWritePlan CollectionAlignedExtensionChildPlan
    );
}
