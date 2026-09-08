// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_SimpleDeleteSqlEmitter
{
    [Test]
    public void It_should_emit_pgsql_delete_sql_with_canonical_multiline_format()
    {
        var sql = new SimpleDeleteSqlEmitter(SqlDialect.Pgsql).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedWhereColumns: [new DbColumnName("StudentUniqueId"), new DbColumnName("SchoolYear")],
            orderedWhereParameterNames: ["studentUniqueId", "schoolYear"]
        );

        sql.Should()
            .Be(
                """
                DELETE FROM "edfi"."StudentSchoolAssociation"
                WHERE
                    ("StudentUniqueId" = @studentUniqueId)
                    AND ("SchoolYear" = @schoolYear)
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_mssql_delete_sql_with_canonical_multiline_format()
    {
        var sql = new SimpleDeleteSqlEmitter(SqlDialect.Mssql).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedWhereColumns: [new DbColumnName("StudentUniqueId"), new DbColumnName("SchoolYear")],
            orderedWhereParameterNames: ["studentUniqueId", "schoolYear"]
        );

        sql.Should()
            .Be(
                """
                DELETE FROM [edfi].[StudentSchoolAssociation]
                WHERE
                    ([StudentUniqueId] = @studentUniqueId)
                    AND ([SchoolYear] = @schoolYear)
                ;

                """
            );
    }

    [Test]
    public void It_should_reject_empty_where_columns()
    {
        var act = () =>
            new SimpleDeleteSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedWhereColumns: [],
                orderedWhereParameterNames: []
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("At least one where column must be supplied. (Parameter 'orderedWhereColumns')")
            .WithParameterName("orderedWhereColumns");
    }

    [Test]
    public void It_should_reject_misaligned_where_column_and_parameter_counts()
    {
        var act = () =>
            new SimpleDeleteSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedWhereColumns: [new DbColumnName("StudentUniqueId"), new DbColumnName("SchoolYear")],
                orderedWhereParameterNames: ["studentUniqueId"]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Where-column and parameter counts must match. Where-column count: 2. Parameter count: 1. (Parameter 'orderedWhereParameterNames')"
            )
            .WithParameterName("orderedWhereParameterNames");
    }
}
