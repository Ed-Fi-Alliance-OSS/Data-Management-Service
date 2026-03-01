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
public class Given_RootOnlyWritePlanCompiler
{
    private RelationalResourceModel _supportedRootOnlyModel = null!;

    [SetUp]
    public void Setup()
    {
        _supportedRootOnlyModel = CreateSupportedRootOnlyModel();
    }

    [Test]
    public void It_should_compile_stored_column_bindings_in_model_order_with_deterministic_parameter_names()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "SchoolYear", "LocalEducationAgencyId");

        tablePlan
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal("documentId", "schoolYear", "localEducationAgencyId");

        tablePlan.ColumnBindings[0].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan.ColumnBindings[1].Source.Should().BeOfType<WriteValueSource.Scalar>();
        tablePlan.ColumnBindings[2].Source.Should().BeOfType<WriteValueSource.Scalar>();

        tablePlan.UpdateSql.Should().NotBeNull();
        tablePlan.DeleteByParentSql.Should().BeNull();
        tablePlan.KeyUnificationPlans.Should().BeEmpty();

        tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(3);
        tablePlan.BulkInsertBatching.MaxRowsPerBatch.Should().Be(1000);
        tablePlan.BulkInsertBatching.MaxParametersPerCommand.Should().Be(65535);
    }

    [Test]
    public void It_should_emit_canonical_pgsql_insert_sql_using_binding_column_and_parameter_order()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .InsertSql.Should()
            .Be(
                """
                INSERT INTO "edfi"."Student"
                (
                    "DocumentId",
                    "SchoolYear",
                    "LocalEducationAgencyId"
                )
                VALUES
                (
                    @documentId,
                    @schoolYear,
                    @localEducationAgencyId
                )
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_canonical_mssql_insert_sql_using_binding_column_and_parameter_order()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .InsertSql.Should()
            .Be(
                """
                INSERT INTO [edfi].[Student]
                (
                    [DocumentId],
                    [SchoolYear],
                    [LocalEducationAgencyId]
                )
                VALUES
                (
                    @documentId,
                    @schoolYear,
                    @localEducationAgencyId
                )
                ;

                """
            );

        tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(3);
        tablePlan.BulkInsertBatching.MaxRowsPerBatch.Should().Be(700);
        tablePlan.BulkInsertBatching.MaxParametersPerCommand.Should().Be(2100);
    }

    [Test]
    public void It_should_emit_canonical_pgsql_update_sql_using_non_key_set_columns_and_key_where_columns()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .UpdateSql.Should()
            .Be(
                """
                UPDATE "edfi"."Student"
                SET
                    "SchoolYear" = @schoolYear,
                    "LocalEducationAgencyId" = @localEducationAgencyId
                WHERE
                    ("DocumentId" = @documentId)
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_canonical_mssql_update_sql_using_non_key_set_columns_and_key_where_columns()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .UpdateSql.Should()
            .Be(
                """
                UPDATE [edfi].[Student]
                SET
                    [SchoolYear] = @schoolYear,
                    [LocalEducationAgencyId] = @localEducationAgencyId
                WHERE
                    ([DocumentId] = @documentId)
                ;

                """
            );
    }

    [Test]
    public void It_should_leave_update_sql_null_when_no_stored_writable_non_key_columns_exist()
    {
        var keyOnlyModel = CreateRootOnlyKeyOnlyModel();

        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(keyOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan.UpdateSql.Should().BeNull();
    }

    [Test]
    public void It_should_fail_fast_for_non_root_only_resources()
    {
        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_StudentAddress",
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

        var act = () => new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Only root-only relational-table resources are supported.*");
    }

    [Test]
    public void It_should_fail_fast_for_non_relational_storage_resources()
    {
        var unsupportedModel = _supportedRootOnlyModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        var act = () => new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Only root-only relational-table resources are supported.*");
    }

    private static RelationalResourceModel CreateSupportedRootOnlyModel()
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

    private static RelationalResourceModel CreateRootOnlyKeyOnlyModel()
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
