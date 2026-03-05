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
public class Given_WritePlanCompiler_InsertSql : WritePlanCompilerTestBase
{
    [Test]
    public void It_should_emit_canonical_pgsql_insert_sql_using_binding_column_and_parameter_order()
    {
        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
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
        var writePlan = new WritePlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
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

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_insert_sql_from_column_bindings_in_order(SqlDialect dialect)
    {
        var tablePlan = new WritePlanCompiler(dialect)
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single();

        var expectedInsertSql = new SimpleInsertSqlEmitter(dialect).Emit(
            tablePlan.TableModel.Table,
            tablePlan.ColumnBindings.Select(static binding => binding.Column.ColumnName).ToArray(),
            tablePlan.ColumnBindings.Select(static binding => binding.ParameterName).ToArray()
        );

        tablePlan.InsertSql.Should().Be(expectedInsertSql);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_identical_insert_sql_across_repeated_compilation_and_permuted_non_writable_column_order(
        SqlDialect dialect
    )
    {
        var compiler = new WritePlanCompiler(dialect);

        var firstInsertSql = compiler
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single()
            .InsertSql;

        var secondInsertSql = compiler
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single()
            .InsertSql;

        var permutedInsertSql = compiler
            .Compile(CreateSupportedRootOnlyModelWithUnifiedAliasColumnFirst())
            .TablePlansInDependencyOrder.Single()
            .InsertSql;

        firstInsertSql.Should().Be(secondInsertSql);
        permutedInsertSql.Should().Be(firstInsertSql);
    }
}
