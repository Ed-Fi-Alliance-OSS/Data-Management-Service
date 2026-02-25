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
                        new ExternalPlans.KeyUnificationMemberWritePlan(
                            MemberPathColumn: new DbColumnName("SchoolYear"),
                            RelativePath: schoolYearPath,
                            Kind: ColumnKind.Scalar,
                            ScalarType: new RelationalScalarType(ScalarKind.Int32),
                            DescriptorResource: null,
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
            new ExternalPlans.KeysetTableContract(
                new DbTableName(new DbSchemaName("edfi"), "page"),
                new DbColumnName("DocumentId")
            ),
            [new ExternalPlans.TableReadPlan(tableModel, "SELECT BY KEYSET SQL")]
        );

        readPlan.KeysetTable.TableName.Should().Be(new DbTableName(new DbSchemaName("edfi"), "page"));
        readPlan.KeysetTable.DocumentIdColumn.Should().Be(new DbColumnName("DocumentId"));
        readPlan.TablePlansInDependencyOrder.Should().ContainSingle();
    }

    [Test]
    public void It_should_capture_query_parameters_with_deterministic_order_and_roles()
    {
        var queryPlan = new ExternalPlans.PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT DocumentId FROM page",
            TotalCountSql: "SELECT COUNT(1) FROM page",
            ParametersInOrder:
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
            ]
        );

        queryPlan
            .ParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(
                ExternalPlans.QuerySqlParameterRole.Filter,
                ExternalPlans.QuerySqlParameterRole.Offset,
                ExternalPlans.QuerySqlParameterRole.Limit
            );

        queryPlan
            .ParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear", "offset", "limit");
    }
}
