// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;
using ExternalPlans = EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ExternalPlanContracts
{
    [Test]
    public void It_should_preserve_authoritative_column_binding_order_for_write_plans()
    {
        var rootPath = new JsonPathExpression("$", []);
        var schoolYearPath = new JsonPathExpression(
            "$.schoolYear",
            [new JsonPathSegment.Property("schoolYear")]
        );

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            rootPath,
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolYear"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    schoolYearPath,
                    TargetResource: null
                ),
            ],
            []
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "Student"),
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            tableModel,
            [tableModel],
            [],
            []
        );

        var tablePlan = new ExternalPlans.TableWritePlan(
            tableModel,
            "INSERT SQL",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new ExternalPlans.BulkInsertBatchingInfo(
                MaxRowsPerBatch: 700,
                ParametersPerRow: 3,
                MaxParametersPerCommand: 2100
            ),
            ColumnBindings:
            [
                new ExternalPlans.WriteColumnBinding(
                    tableModel.Columns[0],
                    new ExternalPlans.WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new ExternalPlans.WriteColumnBinding(
                    tableModel.Columns[1],
                    new ExternalPlans.WriteValueSource.Scalar(
                        schoolYearPath,
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    ParameterName: "schoolYear"
                ),
            ],
            KeyUnificationPlans:
            [
                new ExternalPlans.KeyUnificationWritePlan(
                    new DbColumnName("SchoolYear"),
                    CanonicalBindingIndex: 1,
                    MembersInOrder:
                    [
                        new ExternalPlans.KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: new DbColumnName("SchoolYear"),
                            RelativePath: schoolYearPath,
                            ScalarType: new RelationalScalarType(ScalarKind.Int32),
                            PresenceColumn: null,
                            PresenceBindingIndex: null,
                            PresenceIsSynthetic: false
                        ),
                    ]
                ),
            ]
        );

        var writePlan = new ExternalPlans.ResourceWritePlan(resourceModel, [tablePlan]);

        writePlan.TablePlansInDependencyOrder.Should().HaveCount(1);
        writePlan
            .TablePlansInDependencyOrder[0]
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal("documentId", "schoolYear");
        writePlan
            .TablePlansInDependencyOrder[0]
            .ColumnBindings[1]
            .Source.Should()
            .BeOfType<ExternalPlans.WriteValueSource.Scalar>();
        writePlan
            .TablePlansInDependencyOrder[0]
            .BulkInsertBatching.Should()
            .Be(
                new ExternalPlans.BulkInsertBatchingInfo(
                    MaxRowsPerBatch: 700,
                    ParametersPerRow: 3,
                    MaxParametersPerCommand: 2100
                )
            );
        writePlan.TablePlansInDependencyOrder[0].CollectionMergePlan.Should().BeNull();
    }

    [Test]
    public void It_should_expose_binding_index_first_collection_merge_metadata_for_collection_write_tables()
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create();
        var tablePlan = fixture.CreateTableWritePlan();

        tablePlan.DeleteByParentSql.Should().BeNull();
        tablePlan.CollectionMergePlan.Should().NotBeNull();
        tablePlan.CollectionKeyPreallocationPlan.Should().NotBeNull();
        tablePlan
            .CollectionKeyPreallocationPlan.Should()
            .Be(
                new ExternalPlans.CollectionKeyPreallocationPlan(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    BindingIndex: 1
                )
            );
        tablePlan
            .CollectionMergePlan!.SemanticIdentityBindings.Select(static binding => binding.BindingIndex)
            .Should()
            .Equal(3);
        tablePlan.CollectionMergePlan.UpdateByStableRowIdentitySql.Should().Be("UPDATE COLLECTION SQL");
        tablePlan.CollectionMergePlan.DeleteByStableRowIdentitySql.Should().Be("DELETE COLLECTION SQL");
        tablePlan
            .CollectionMergePlan.CompareBindingIndexesInOrder.Select(bindingIndex =>
                tablePlan.ColumnBindings[bindingIndex].ParameterName
            )
            .Should()
            .Equal("collectionItemId", "ordinal", "addressType", "streetNumberName");
        tablePlan
            .ColumnBindings[tablePlan.CollectionMergePlan.StableRowIdentityBindingIndex]
            .ParameterName.Should()
            .Be("collectionItemId");
        tablePlan
            .ColumnBindings[tablePlan.CollectionMergePlan.OrdinalBindingIndex]
            .ParameterName.Should()
            .Be("ordinal");
    }

    [Test]
    public void It_should_reject_collection_table_plans_that_mix_delete_by_parent_and_collection_merge_metadata()
    {
        var act = () => CreateCollectionTableWritePlan(deleteByParentSql: "DELETE SQL");

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be("DeleteByParentSql");
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.CollectionMergePlan));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.DeleteByParentSql));
    }

    [Test]
    public void It_should_reject_collection_table_plans_that_mix_update_sql_and_collection_merge_metadata()
    {
        var act = () => CreateCollectionTableWritePlan(updateSql: "UPDATE SQL");

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(ExternalPlans.TableWritePlan.UpdateSql));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.CollectionMergePlan));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.UpdateSql));
    }

    [TestCase(DbTableKind.RootExtension)]
    [TestCase(DbTableKind.CollectionExtensionScope)]
    public void It_should_reject_collection_merge_plans_for_non_collection_table_kinds(DbTableKind tableKind)
    {
        var act = () => CreateCollectionTableWritePlan(tableKind: tableKind);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be("TableModel.IdentityMetadata.TableKind");
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.CollectionMergePlan));
        exception.Message.Should().Contain(nameof(DbTableKind.Collection));
        exception.Message.Should().Contain(nameof(DbTableKind.ExtensionCollection));
        exception.Message.Should().Contain(tableKind.ToString());
    }

    [Test]
    public void It_should_reject_collection_merge_plans_without_semantic_identity_bindings()
    {
        var act = () =>
            new ExternalPlans.CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "UPDATE COLLECTION SQL",
                DeleteByStableRowIdentitySql: "DELETE COLLECTION SQL",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [1, 2, 3, 4]
            );

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be("SemanticIdentityBindings");
        exception.Message.Should().Contain("must be non-empty");
    }

    [Test]
    public void It_should_reject_collection_merge_plans_without_collection_key_preallocation_metadata()
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create();

        var act = () =>
            fixture.CreateTableWritePlan(
                updateSql: null,
                deleteByParentSql: null,
                collectionMergePlan: fixture.CollectionMergePlan,
                collectionKeyPreallocationPlan: null
            );

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(ExternalPlans.TableWritePlan.CollectionKeyPreallocationPlan));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.CollectionMergePlan));
        exception
            .Message.Should()
            .Contain(nameof(ExternalPlans.TableWritePlan.CollectionKeyPreallocationPlan));
    }

    [Test]
    public void It_should_reject_collection_merge_plans_when_stable_row_identity_binding_index_does_not_match_collection_key_preallocation_binding_index()
    {
        var act = () =>
            CreateCollectionTableWritePlan(
                collectionKeyPreallocationPlan: new ExternalPlans.CollectionKeyPreallocationPlan(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    BindingIndex: 2
                )
            );

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception
            .ParamName.Should()
            .Contain(nameof(ExternalPlans.CollectionKeyPreallocationPlan.BindingIndex));
        exception
            .Message.Should()
            .Contain(nameof(ExternalPlans.CollectionMergePlan.StableRowIdentityBindingIndex));
        exception.Message.Should().Contain(nameof(ExternalPlans.CollectionKeyPreallocationPlan.BindingIndex));
    }

    [Test]
    public void It_should_reject_collection_merge_plans_when_stable_row_identity_binding_column_does_not_match_collection_key_preallocation_column_name()
    {
        var act = () =>
            CreateCollectionTableWritePlan(
                collectionKeyPreallocationPlan: new ExternalPlans.CollectionKeyPreallocationPlan(
                    ColumnName: new DbColumnName("OtherCollectionItemId"),
                    BindingIndex: 1
                )
            );

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(ExternalPlans.CollectionKeyPreallocationPlan.ColumnName));
        exception
            .Message.Should()
            .Contain(nameof(ExternalPlans.CollectionMergePlan.StableRowIdentityBindingIndex));
        exception.Message.Should().Contain(nameof(ExternalPlans.CollectionKeyPreallocationPlan.ColumnName));
        exception.Message.Should().Contain("OtherCollectionItemId");
    }

    [Test]
    public void It_should_reject_collection_merge_semantic_identity_binding_indexes_outside_column_bindings()
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create();
        var semanticIdentityBindings = fixture.CollectionMergePlan.SemanticIdentityBindings.ToArray();
        semanticIdentityBindings[0] = semanticIdentityBindings[0] with { BindingIndex = 99 };

        var act = () =>
            CreateCollectionTableWritePlan(
                collectionMergePlan: fixture.CollectionMergePlan with
                {
                    SemanticIdentityBindings = [.. semanticIdentityBindings],
                }
            );

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception
            .ParamName.Should()
            .Contain(nameof(ExternalPlans.CollectionMergePlan.SemanticIdentityBindings));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.ColumnBindings));
    }

    [Test]
    public void It_should_reject_collection_merge_stable_row_identity_binding_indexes_outside_column_bindings()
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create();

        var act = () =>
            CreateCollectionTableWritePlan(
                collectionMergePlan: fixture.CollectionMergePlan with
                {
                    StableRowIdentityBindingIndex = 99,
                }
            );

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception
            .ParamName.Should()
            .Contain(nameof(ExternalPlans.CollectionMergePlan.StableRowIdentityBindingIndex));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.ColumnBindings));
    }

    [Test]
    public void It_should_reject_collection_merge_ordinal_binding_indexes_outside_column_bindings()
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create();

        var act = () =>
            CreateCollectionTableWritePlan(
                collectionMergePlan: fixture.CollectionMergePlan with
                {
                    OrdinalBindingIndex = 99,
                }
            );

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(ExternalPlans.CollectionMergePlan.OrdinalBindingIndex));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.ColumnBindings));
    }

    [Test]
    public void It_should_reject_collection_merge_compare_binding_indexes_outside_column_bindings()
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create();

        var act = () =>
            CreateCollectionTableWritePlan(
                collectionMergePlan: fixture.CollectionMergePlan with
                {
                    CompareBindingIndexesInOrder = [1, 99],
                }
            );

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception
            .ParamName.Should()
            .Contain(nameof(ExternalPlans.CollectionMergePlan.CompareBindingIndexesInOrder));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.ColumnBindings));
    }

    [Test]
    public void It_should_reject_collection_key_preallocation_binding_indexes_outside_column_bindings()
    {
        var act = () =>
            CreateCollectionTableWritePlan(
                collectionKeyPreallocationPlan: new ExternalPlans.CollectionKeyPreallocationPlan(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    BindingIndex: 99
                )
            );

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception
            .ParamName.Should()
            .Contain(nameof(ExternalPlans.CollectionKeyPreallocationPlan.BindingIndex));
        exception.Message.Should().Contain(nameof(ExternalPlans.TableWritePlan.ColumnBindings));
    }

    [Test]
    public void It_should_keep_delete_by_parent_contract_for_non_collection_non_root_tables()
    {
        var termPath = new JsonPathExpression("$.term", [new JsonPathSegment.Property("term")]);
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolSession"),
            new JsonPathExpression("$.session", [new JsonPathSegment.Property("session")]),
            new TableKey(
                "PK_SchoolSession",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Term"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    IsNullable: false,
                    SourceJsonPath: termPath,
                    TargetResource: null
                ),
            ],
            []
        );

        var tablePlan = new ExternalPlans.TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT SQL",
            UpdateSql: "UPDATE SQL",
            DeleteByParentSql: "DELETE SQL",
            BulkInsertBatching: new ExternalPlans.BulkInsertBatchingInfo(100, 2, 2100),
            ColumnBindings:
            [
                new ExternalPlans.WriteColumnBinding(
                    Column: tableModel.Columns[0],
                    Source: new ExternalPlans.WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new ExternalPlans.WriteColumnBinding(
                    Column: tableModel.Columns[1],
                    Source: new ExternalPlans.WriteValueSource.Scalar(
                        termPath,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    ParameterName: "term"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: null
        );

        tablePlan.DeleteByParentSql.Should().Be("DELETE SQL");
        tablePlan.CollectionMergePlan.Should().BeNull();
    }

    [Test]
    public void It_should_expose_keyset_table_contract_for_resource_read_plan()
    {
        var rootPath = new JsonPathExpression("$", []);
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            rootPath,
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "Student"),
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            tableModel,
            [tableModel],
            [],
            []
        );

        var readPlan = new ExternalPlans.ResourceReadPlan(
            resourceModel,
            ExternalPlans.KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new ExternalPlans.TableReadPlan(tableModel, "SELECT BY KEYSET SQL")],
            [],
            []
        );

        readPlan.KeysetTable.Table.Name.Should().Be("page");
        readPlan.KeysetTable.DocumentIdColumnName.Should().Be(new DbColumnName("DocumentId"));
        readPlan.TablePlansInDependencyOrder.Should().ContainSingle();
        readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        readPlan.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_capture_query_parameters_with_deterministic_order_and_roles()
    {
        var queryPlan = new ExternalPlans.PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT DocumentId FROM page",
            TotalCountSql: "SELECT COUNT(1) FROM page",
            PageParametersInOrder:
            [
                new ExternalPlans.QuerySqlParameter(
                    ExternalPlans.QuerySqlParameterRole.Filter,
                    ParameterName: "schoolYear"
                ),
                new ExternalPlans.QuerySqlParameter(
                    ExternalPlans.QuerySqlParameterRole.Offset,
                    ParameterName: "offset"
                ),
                new ExternalPlans.QuerySqlParameter(
                    ExternalPlans.QuerySqlParameterRole.Limit,
                    ParameterName: "limit"
                ),
            ],
            TotalCountParametersInOrder:
            [
                new ExternalPlans.QuerySqlParameter(
                    ExternalPlans.QuerySqlParameterRole.Filter,
                    ParameterName: "schoolYear"
                ),
            ]
        );

        queryPlan
            .PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(
                ExternalPlans.QuerySqlParameterRole.Filter,
                ExternalPlans.QuerySqlParameterRole.Offset,
                ExternalPlans.QuerySqlParameterRole.Limit
            );

        queryPlan
            .PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear", "offset", "limit");
        queryPlan.TotalCountParametersInOrder.Should().NotBeNull();
        queryPlan
            .TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear");
    }

    [Test]
    public void It_should_throw_actionable_not_supported_for_mapping_set_aot_payload_entry_point()
    {
        var act = () => MappingSet.FromPayload(new MappingPackPayload());

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "AOT mapping-pack decode is not implemented yet for MappingSet.FromPayload(MappingPackPayload). "
                    + "See E05-S05: reference/design/backend-redesign/epics/05-mpack-generation/05-pack-loader-validation.md."
            );
    }

    [Test]
    public void It_should_preserve_projection_contract_ordering_semantics()
    {
        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );
        var schoolIdPath = new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );
        var schoolYearPath = new JsonPathExpression(
            "$.schoolReference.schoolYear",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolYear")]
        );
        var descriptorPath = new JsonPathExpression(
            "$.gradeLevelDescriptor",
            [new JsonPathSegment.Property("gradeLevelDescriptor")]
        );

        var secondaryTable = new DbTableName(new DbSchemaName("edfi"), "StudentAddress");
        var primaryTable = new DbTableName(new DbSchemaName("edfi"), "Student");

        var readPlan = new ExternalPlans.ResourceReadPlan(
            Model: new RelationalResourceModel(
                new QualifiedResourceName("Ed-Fi", "Student"),
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                Root: new DbTableModel(
                    primaryTable,
                    new JsonPathExpression("$", []),
                    new TableKey(
                        "PK_Student",
                        [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                    ),
                    [
                        new DbColumnModel(
                            new DbColumnName("DocumentId"),
                            ColumnKind.ParentKeyPart,
                            new RelationalScalarType(ScalarKind.Int64),
                            IsNullable: false,
                            SourceJsonPath: null,
                            TargetResource: null
                        ),
                    ],
                    []
                ),
                TablesInDependencyOrder: [],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            KeysetTable: ExternalPlans.KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder: [],
            ReferenceIdentityProjectionPlansInDependencyOrder:
            [
                new ExternalPlans.ReferenceIdentityProjectionTablePlan(
                    secondaryTable,
                    [
                        new ExternalPlans.ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: false,
                            ReferenceObjectPath: schoolReferencePath,
                            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                            FkColumnOrdinal: 5,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ExternalPlans.ReferenceIdentityProjectionFieldOrdinal(
                                    schoolYearPath,
                                    ColumnOrdinal: 9
                                ),
                                new ExternalPlans.ReferenceIdentityProjectionFieldOrdinal(
                                    schoolIdPath,
                                    ColumnOrdinal: 8
                                ),
                            ]
                        ),
                    ]
                ),
                new ExternalPlans.ReferenceIdentityProjectionTablePlan(
                    primaryTable,
                    [
                        new ExternalPlans.ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: true,
                            ReferenceObjectPath: schoolReferencePath,
                            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                            FkColumnOrdinal: 2,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ExternalPlans.ReferenceIdentityProjectionFieldOrdinal(
                                    schoolIdPath,
                                    ColumnOrdinal: 4
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            DescriptorProjectionPlansInOrder:
            [
                new ExternalPlans.DescriptorProjectionPlan(
                    SelectByKeysetSql: "SELECT d.DocumentId, d.Uri FROM page JOIN dms.Descriptor d ON d.DocumentId = page.DocumentId",
                    ResultShape: new ExternalPlans.DescriptorProjectionResultShape(
                        DescriptorIdOrdinal: 0,
                        UriOrdinal: 1
                    ),
                    SourcesInOrder:
                    [
                        new ExternalPlans.DescriptorProjectionSource(
                            descriptorPath,
                            secondaryTable,
                            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                            DescriptorIdColumnOrdinal: 6
                        ),
                        new ExternalPlans.DescriptorProjectionSource(
                            descriptorPath,
                            primaryTable,
                            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                            DescriptorIdColumnOrdinal: 3
                        ),
                    ]
                ),
            ]
        );

        readPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder.Select(plan => plan.Table)
            .Should()
            .Equal(secondaryTable, primaryTable);
        readPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder[0]
            .BindingsInOrder[0]
            .IdentityFieldOrdinalsInOrder.Select(identity => identity.ColumnOrdinal)
            .Should()
            .Equal(9, 8);
        readPlan
            .DescriptorProjectionPlansInOrder[0]
            .SourcesInOrder.Select(source => source.Table)
            .Should()
            .Equal(secondaryTable, primaryTable);
        readPlan
            .DescriptorProjectionPlansInOrder[0]
            .ResultShape.Should()
            .Be(new ExternalPlans.DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1));
    }

    [Test]
    public void It_should_make_invalid_key_unification_member_combinations_unrepresentable_by_shape()
    {
        var relativePath = new JsonPathExpression(
            "$.schoolYear",
            [new JsonPathSegment.Property("schoolYear")]
        );

        var scalarMember = new ExternalPlans.KeyUnificationMemberWritePlan.ScalarMember(
            MemberPathColumn: new DbColumnName("SchoolYear"),
            RelativePath: relativePath,
            ScalarType: new RelationalScalarType(ScalarKind.Int32),
            PresenceColumn: null,
            PresenceBindingIndex: null,
            PresenceIsSynthetic: false
        );

        var descriptorMember = new ExternalPlans.KeyUnificationMemberWritePlan.DescriptorMember(
            MemberPathColumn: new DbColumnName("AcademicSubjectDescriptorId"),
            RelativePath: relativePath,
            DescriptorResource: new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor"),
            PresenceColumn: null,
            PresenceBindingIndex: null,
            PresenceIsSynthetic: false
        );

        typeof(ExternalPlans.KeyUnificationMemberWritePlan).IsAbstract.Should().BeTrue();

        var scalarType = scalarMember.GetType();
        scalarType
            .GetProperty(
                nameof(ExternalPlans.KeyUnificationMemberWritePlan.DescriptorMember.DescriptorResource)
            )
            .Should()
            .BeNull();

        var descriptorType = descriptorMember.GetType();
        descriptorType
            .GetProperty(nameof(ExternalPlans.KeyUnificationMemberWritePlan.ScalarMember.ScalarType))
            .Should()
            .BeNull();
    }

    [Test]
    public void It_should_defensively_copy_mutable_input_collections()
    {
        var rootPath = new JsonPathExpression("$", []);
        var schoolYearPath = new JsonPathExpression(
            "$.schoolYear",
            [new JsonPathSegment.Property("schoolYear")]
        );

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            rootPath,
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolYear"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    schoolYearPath,
                    TargetResource: null
                ),
            ],
            []
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "Student"),
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            tableModel,
            [tableModel],
            [],
            []
        );

        var membersInOrder = new List<ExternalPlans.KeyUnificationMemberWritePlan>
        {
            new ExternalPlans.KeyUnificationMemberWritePlan.ScalarMember(
                MemberPathColumn: new DbColumnName("SchoolYear"),
                RelativePath: schoolYearPath,
                ScalarType: new RelationalScalarType(ScalarKind.Int32),
                PresenceColumn: null,
                PresenceBindingIndex: null,
                PresenceIsSynthetic: false
            ),
        };

        var keyUnificationPlans = new List<ExternalPlans.KeyUnificationWritePlan>
        {
            new(
                CanonicalColumn: new DbColumnName("SchoolYear"),
                CanonicalBindingIndex: 1,
                MembersInOrder: membersInOrder
            ),
        };

        var columnBindings = new List<ExternalPlans.WriteColumnBinding>
        {
            new(
                tableModel.Columns[0],
                new ExternalPlans.WriteValueSource.DocumentId(),
                ParameterName: "documentId"
            ),
            new(
                tableModel.Columns[1],
                new ExternalPlans.WriteValueSource.Scalar(
                    schoolYearPath,
                    new RelationalScalarType(ScalarKind.Int32)
                ),
                ParameterName: "schoolYear"
            ),
        };

        var tablePlansInDependencyOrder = new List<ExternalPlans.TableWritePlan>
        {
            new(
                TableModel: tableModel,
                InsertSql: "INSERT SQL",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new ExternalPlans.BulkInsertBatchingInfo(
                    MaxRowsPerBatch: 700,
                    ParametersPerRow: 2,
                    MaxParametersPerCommand: 2100
                ),
                ColumnBindings: columnBindings,
                KeyUnificationPlans: keyUnificationPlans
            ),
        };

        var writePlan = new ExternalPlans.ResourceWritePlan(
            Model: resourceModel,
            TablePlansInDependencyOrder: tablePlansInDependencyOrder
        );

        membersInOrder.Clear();
        keyUnificationPlans.Clear();
        columnBindings.Clear();
        tablePlansInDependencyOrder.Clear();

        writePlan.TablePlansInDependencyOrder.Should().ContainSingle();
        writePlan.TablePlansInDependencyOrder[0].ColumnBindings.Should().HaveCount(2);
        writePlan.TablePlansInDependencyOrder[0].KeyUnificationPlans.Should().ContainSingle();
        writePlan
            .TablePlansInDependencyOrder[0]
            .KeyUnificationPlans[0]
            .MembersInOrder.Should()
            .ContainSingle();
    }

    private static ExternalPlans.TableWritePlan CreateCollectionTableWritePlan(
        string? updateSql = null,
        string? deleteByParentSql = null,
        ExternalPlans.CollectionMergePlan? collectionMergePlan = null,
        ExternalPlans.CollectionKeyPreallocationPlan? collectionKeyPreallocationPlan = null,
        DbTableKind tableKind = DbTableKind.Collection
    )
    {
        var fixture = SchoolAddressCollectionPlanFixture.Create(tableKind);

        return fixture.CreateTableWritePlan(
            updateSql: updateSql,
            deleteByParentSql: deleteByParentSql,
            collectionMergePlan: collectionMergePlan ?? fixture.CollectionMergePlan,
            collectionKeyPreallocationPlan: collectionKeyPreallocationPlan
                ?? fixture.CollectionKeyPreallocationPlan
        );
    }
}
