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
public class Given_RootOnlyReadPlanCompiler
{
    private RelationalResourceModel _supportedRootOnlyModel = null!;

    [SetUp]
    public void Setup()
    {
        _supportedRootOnlyModel = CreateSupportedRootOnlyModel();
    }

    [Test]
    public void It_should_compile_a_single_root_table_read_plan_with_expected_keyset_contract()
    {
        var readPlan = new RootOnlyReadPlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);

        readPlan.Model.Should().Be(_supportedRootOnlyModel);
        readPlan.KeysetTable.Should().Be(KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql));
        readPlan.TablePlansInDependencyOrder.Should().ContainSingle();
        readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        readPlan.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_emit_canonical_pgsql_select_by_keyset_sql_with_stable_aliases_and_ordering()
    {
        var readPlan = new RootOnlyReadPlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = readPlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .SelectByKeysetSql.Should()
            .Be(
                """
                SELECT
                    r."DocumentId",
                    r."SchoolYear",
                    r."LocalEducationAgencyId",
                    r."SchoolYearAlias"
                FROM "edfi"."Student" r
                INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
                ORDER BY
                    r."DocumentId" ASC,
                    r."SchoolYear" ASC
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_canonical_mssql_select_by_keyset_sql_with_stable_aliases_and_ordering()
    {
        var readPlan = new RootOnlyReadPlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
        var tablePlan = readPlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .SelectByKeysetSql.Should()
            .Be(
                """
                SELECT
                    r.[DocumentId],
                    r.[SchoolYear],
                    r.[LocalEducationAgencyId],
                    r.[SchoolYearAlias]
                FROM [edfi].[Student] r
                INNER JOIN [#page] k ON r.[DocumentId] = k.[DocumentId]
                ORDER BY
                    r.[DocumentId] ASC,
                    r.[SchoolYear] ASC
                ;

                """
            );
    }

    [Test]
    public void It_should_order_by_document_id_first_even_when_model_key_order_is_not_document_id_first()
    {
        var modelWithNonDocumentIdFirstKeyOrder = CreateRootOnlyModelWithNonDocumentIdFirstKeyOrder();

        var readPlan = new RootOnlyReadPlanCompiler(SqlDialect.Pgsql).Compile(
            modelWithNonDocumentIdFirstKeyOrder
        );

        readPlan
            .TablePlansInDependencyOrder.Single()
            .SelectByKeysetSql.Should()
            .Contain(
                """
                ORDER BY
                    r."DocumentId" ASC,
                    r."SchoolYear" ASC
                ;
                """
            );
    }

    [Test]
    public void It_should_mark_non_root_only_resources_as_unsupported()
    {
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
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
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
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var unsupportedModel = _supportedRootOnlyModel with
        {
            TablesInDependencyOrder = [_supportedRootOnlyModel.Root, childTable],
        };

        RootOnlyReadPlanCompiler.IsSupported(unsupportedModel).Should().BeFalse();
    }

    [Test]
    public void It_should_mark_non_relational_storage_resources_as_unsupported()
    {
        var unsupportedModel = _supportedRootOnlyModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        RootOnlyReadPlanCompiler.IsSupported(unsupportedModel).Should().BeFalse();
    }

    [Test]
    public void It_should_fail_fast_for_unsupported_resources()
    {
        var unsupportedModel = _supportedRootOnlyModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        var act = () => new RootOnlyReadPlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Only root-only relational-table resources are supported.*");
    }

    [Test]
    public void It_should_fail_fast_when_no_document_id_key_column_is_present()
    {
        var unsupportedModel = CreateRootOnlyModelWithoutDocumentIdInKey();
        var act = () => new RootOnlyReadPlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for '*': expected exactly one root document-id key column*"
            );
    }

    [Test]
    public void It_should_allow_try_compile_to_omit_unsupported_read_plan_without_throwing()
    {
        var unsupportedModel = _supportedRootOnlyModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        var wasCompiled = new RootOnlyReadPlanCompiler(SqlDialect.Pgsql).TryCompile(
            unsupportedModel,
            out var readPlan
        );

        wasCompiled.Should().BeFalse();
        readPlan.Should().BeNull();
    }

    private static RelationalResourceModel CreateSupportedRootOnlyModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar),
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
                new DbColumnModel(
                    ColumnName: new DbColumnName("LocalEducationAgencyId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.localEducationAgencyId",
                        [new JsonPathSegment.Property("localEducationAgencyId")]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYear"),
                        PresenceColumn: null
                    )
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

    private static RelationalResourceModel CreateRootOnlyModelWithoutDocumentIdInKey()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar)]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateRootOnlyModelWithNonDocumentIdFirstKeyOrder()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar),
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                ]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }
}
