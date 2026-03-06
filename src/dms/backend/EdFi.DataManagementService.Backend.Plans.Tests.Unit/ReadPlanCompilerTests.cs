// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ReadPlanCompiler
{
    private RelationalResourceModel _resourceModel = null!;
    private ResourceReadPlan _pgsqlReadPlan = null!;
    private ResourceReadPlan _mssqlReadPlan = null!;

    [SetUp]
    public void Setup()
    {
        _resourceModel = CreateMultiTableResourceModel();
        _pgsqlReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(_resourceModel);
        _mssqlReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(_resourceModel);
    }

    [Test]
    public void It_should_compile_a_table_plan_for_every_table_in_dependency_order()
    {
        _pgsqlReadPlan
            .TablePlansInDependencyOrder.Select(static tablePlan => tablePlan.TableModel)
            .Should()
            .Equal(_resourceModel.TablesInDependencyOrder);
    }

    [Test]
    public void It_should_preserve_the_keyset_contract_and_empty_projection_arrays_for_story_05()
    {
        _pgsqlReadPlan.Model.Should().Be(_resourceModel);
        _pgsqlReadPlan
            .KeysetTable.Should()
            .Be(KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql));
        _pgsqlReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        _pgsqlReadPlan.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_emit_exact_pgsql_SelectByKeysetSql_for_root_child_nested_and_extension_tables()
    {
        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "Student",
            """
            SELECT
                r."DocumentId",
                r."StudentUniqueId"
            FROM "edfi"."Student" r
            INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
            ORDER BY
                r."DocumentId" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentAddress",
            """
            SELECT
                t."Student_DocumentId",
                t."Ordinal",
                t."City"
            FROM "edfi"."StudentAddress" t
            INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
            ORDER BY
                t."Student_DocumentId" ASC,
                t."Ordinal" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentAddressPeriod",
            """
            SELECT
                t."Student_DocumentId",
                t."AddressOrdinal",
                t."Ordinal",
                t."BeginDate"
            FROM "edfi"."StudentAddressPeriod" t
            INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
            ORDER BY
                t."Student_DocumentId" ASC,
                t."AddressOrdinal" ASC,
                t."Ordinal" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentExtension",
            """
            SELECT
                t."DocumentId",
                t."FavoriteColor"
            FROM "sample"."StudentExtension" t
            INNER JOIN "page" k ON t."DocumentId" = k."DocumentId"
            ORDER BY
                t."DocumentId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_emit_exact_mssql_SelectByKeysetSql_for_root_child_nested_and_extension_tables()
    {
        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "Student",
            """
            SELECT
                r.[DocumentId],
                r.[StudentUniqueId]
            FROM [edfi].[Student] r
            INNER JOIN [#page] k ON r.[DocumentId] = k.[DocumentId]
            ORDER BY
                r.[DocumentId] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentAddress",
            """
            SELECT
                t.[Student_DocumentId],
                t.[Ordinal],
                t.[City]
            FROM [edfi].[StudentAddress] t
            INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
            ORDER BY
                t.[Student_DocumentId] ASC,
                t.[Ordinal] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentAddressPeriod",
            """
            SELECT
                t.[Student_DocumentId],
                t.[AddressOrdinal],
                t.[Ordinal],
                t.[BeginDate]
            FROM [edfi].[StudentAddressPeriod] t
            INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
            ORDER BY
                t.[Student_DocumentId] ASC,
                t.[AddressOrdinal] ASC,
                t.[Ordinal] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentExtension",
            """
            SELECT
                t.[DocumentId],
                t.[FavoriteColor]
            FROM [sample].[StudentExtension] t
            INNER JOIN [#page] k ON t.[DocumentId] = k.[DocumentId]
            ORDER BY
                t.[DocumentId] ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_order_by_table_key_columns_exactly_as_modeled_without_special_casing_document_id()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateRootOnlyModelWithNonDocumentIdFirstKeyOrder()
        );

        readPlan
            .TablePlansInDependencyOrder.Single()
            .SelectByKeysetSql.Should()
            .Be(
                """
                SELECT
                    r."DocumentId",
                    r."SchoolYear"
                FROM "edfi"."Student" r
                INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
                ORDER BY
                    r."SchoolYear" ASC,
                    r."DocumentId" ASC
                ;

                """
            );
    }

    [Test]
    public void It_should_mark_shared_descriptor_storage_as_unsupported()
    {
        var unsupportedModel = _resourceModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        ReadPlanCompiler.IsSupported(unsupportedModel).Should().BeFalse();

        var wasCompiled = new ReadPlanCompiler(SqlDialect.Pgsql).TryCompile(
            unsupportedModel,
            out var readPlan
        );

        wasCompiled.Should().BeFalse();
        readPlan.Should().BeNull();
    }

    private static void AssertSelectByKeysetSql(
        ResourceReadPlan readPlan,
        string tableName,
        string expectedSql
    )
    {
        readPlan
            .TablePlansInDependencyOrder.Single(tablePlan => tablePlan.TableModel.Table.Name == tableName)
            .SelectByKeysetSql.Should()
            .Be(expectedSql);
    }

    private static RelationalResourceModel CreateMultiTableResourceModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("StudentUniqueId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.studentUniqueId",
                        [new JsonPathSegment.Property("studentUniqueId")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("Student_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("City"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].city",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("city"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var nestedTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressPeriod"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*].periods[*]",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement(),
                ]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressPeriod",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("Student_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("AddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AddressOrdinal"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("BeginDate"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].periods[*].beginDate",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("periods"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("beginDate"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var extensionTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
            JsonScope: new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtension",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("FavoriteColor"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$._ext.sample.favoriteColor",
                        [
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable, nestedTable, extensionTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateRootOnlyModelWithNonDocumentIdFirstKeyOrder()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar),
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }
}
