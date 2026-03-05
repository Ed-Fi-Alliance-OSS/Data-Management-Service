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
public class Given_WritePlanCompiler_UpdateSql : WritePlanCompilerTestBase
{
    [Test]
    public void It_should_emit_canonical_pgsql_update_sql_using_non_key_set_columns_and_key_where_columns()
    {
        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
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
        var writePlan = new WritePlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
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
    public void It_should_reuse_column_binding_parameter_names_for_update_where_predicates_in_key_order()
    {
        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(
            CreateRootOnlyModelWithUpdateKeyParameterNameCollision()
        );
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(static binding => binding.ParameterName)
            .Should()
            .Equal("documentId", "documentId_2", "schoolYear", "gradeLevel");

        tablePlan
            .UpdateSql.Should()
            .Be(
                """
                UPDATE "edfi"."StudentCollision"
                SET
                    "DocumentId" = @documentId,
                    "GradeLevel" = @gradeLevel
                WHERE
                    ("documentId" = @documentId_2)
                    AND ("SchoolYear" = @schoolYear)
                ;

                """
            );
    }

    [Test]
    public void It_should_leave_update_sql_null_when_no_stored_writable_non_key_columns_exist()
    {
        var keyOnlyModel = CreateRootOnlyKeyOnlyModel();

        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(keyOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan.UpdateSql.Should().BeNull();
    }
}
