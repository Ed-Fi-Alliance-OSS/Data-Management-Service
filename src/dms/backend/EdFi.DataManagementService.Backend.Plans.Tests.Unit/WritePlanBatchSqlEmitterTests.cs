// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_WritePlanBatchSqlEmitter : WritePlanCompilerTestBase
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_keep_runtime_insert_batch_sql_aligned_with_the_plan_insert_emitter(
        SqlDialect dialect
    )
    {
        var tablePlan = new WritePlanCompiler(dialect)
            .Compile(CreateSupportedRootOnlyModel())
            .TablePlansInDependencyOrder.Single();
        var orderedColumns = tablePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName)
            .ToArray();
        var orderedParameterNames = tablePlan
            .ColumnBindings.Select(static binding => binding.ParameterName)
            .ToArray();
        var expectedSql = new SimpleInsertSqlEmitter(dialect).EmitBatch(
            tablePlan.TableModel.Table,
            orderedColumns,
            [
                orderedParameterNames.Select(static parameterName => $"{parameterName}_0").ToArray(),
                orderedParameterNames.Select(static parameterName => $"{parameterName}_1").ToArray(),
            ]
        );

        var sql = new WritePlanBatchSqlEmitter(dialect).EmitInsertBatch(tablePlan, 2);

        sql.Should().Be(expectedSql);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_repeated_update_sql_from_compiled_update_metadata(SqlDialect dialect)
    {
        var tablePlan = new WritePlanCompiler(dialect)
            .Compile(CreateSupportedRootOnlyModel())
            .TablePlansInDependencyOrder.Single();

        var sql = new WritePlanBatchSqlEmitter(dialect).EmitUpdateBatch(tablePlan, 2);

        sql.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    UPDATE "edfi"."Student"
                    SET
                        "SchoolYear" = @schoolYear_0,
                        "LocalEducationAgencyId" = @localEducationAgencyId_0
                    WHERE
                        ("DocumentId" = @documentId_0)
                    ;

                    UPDATE "edfi"."Student"
                    SET
                        "SchoolYear" = @schoolYear_1,
                        "LocalEducationAgencyId" = @localEducationAgencyId_1
                    WHERE
                        ("DocumentId" = @documentId_1)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    UPDATE [edfi].[Student]
                    SET
                        [SchoolYear] = @schoolYear_0,
                        [LocalEducationAgencyId] = @localEducationAgencyId_0
                    WHERE
                        ([DocumentId] = @documentId_0)
                    ;

                    UPDATE [edfi].[Student]
                    SET
                        [SchoolYear] = @schoolYear_1,
                        [LocalEducationAgencyId] = @localEducationAgencyId_1
                    WHERE
                        ([DocumentId] = @documentId_1)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_repeated_delete_by_parent_sql_from_compiled_parent_locator_metadata(
        SqlDialect dialect
    )
    {
        var writePlan = new WritePlanCompiler(dialect).Compile(CreateSupportedMultiTableModel());
        var rootExtensionPlan = writePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            string.Equals(tablePlan.TableModel.Table.Name, "StudentExtension", StringComparison.Ordinal)
        );

        var sql = new WritePlanBatchSqlEmitter(dialect).EmitDeleteByParentBatch(rootExtensionPlan, 2);

        sql.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    DELETE FROM "sample"."StudentExtension"
                    WHERE
                        ("DocumentId" = @documentId_0)
                    ;

                    DELETE FROM "sample"."StudentExtension"
                    WHERE
                        ("DocumentId" = @documentId_1)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    DELETE FROM [sample].[StudentExtension]
                    WHERE
                        ([DocumentId] = @documentId_0)
                    ;

                    DELETE FROM [sample].[StudentExtension]
                    WHERE
                        ([DocumentId] = @documentId_1)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_collection_update_and_delete_batches_from_compiled_collection_merge_metadata(
        SqlDialect dialect
    )
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(dialect, "SchoolAddress");
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(dialect);

        batchSqlEmitter
            .EmitCollectionUpdateByStableRowIdentityBatch(tablePlan, 2)
            .Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    UPDATE "edfi"."SchoolAddress"
                    SET
                        "Ordinal" = @ordinal_0,
                        "City" = @city_0
                    WHERE
                        ("CollectionItemId" = @collectionItemId_0)
                    ;

                    UPDATE "edfi"."SchoolAddress"
                    SET
                        "Ordinal" = @ordinal_1,
                        "City" = @city_1
                    WHERE
                        ("CollectionItemId" = @collectionItemId_1)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    UPDATE [edfi].[SchoolAddress]
                    SET
                        [Ordinal] = @ordinal_0,
                        [City] = @city_0
                    WHERE
                        ([CollectionItemId] = @collectionItemId_0)
                    ;

                    UPDATE [edfi].[SchoolAddress]
                    SET
                        [Ordinal] = @ordinal_1,
                        [City] = @city_1
                    WHERE
                        ([CollectionItemId] = @collectionItemId_1)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );

        batchSqlEmitter
            .EmitCollectionDeleteByStableRowIdentityBatch(tablePlan, 2)
            .Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    DELETE FROM "edfi"."SchoolAddress"
                    WHERE
                        ("CollectionItemId" = @collectionItemId_0)
                    ;

                    DELETE FROM "edfi"."SchoolAddress"
                    WHERE
                        ("CollectionItemId" = @collectionItemId_1)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    DELETE FROM [edfi].[SchoolAddress]
                    WHERE
                        ([CollectionItemId] = @collectionItemId_0)
                    ;

                    DELETE FROM [edfi].[SchoolAddress]
                    WHERE
                        ([CollectionItemId] = @collectionItemId_1)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );
    }
}
