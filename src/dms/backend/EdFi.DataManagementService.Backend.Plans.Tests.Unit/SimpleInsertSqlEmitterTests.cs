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
public class Given_SimpleInsertSqlEmitter
{
    [Test]
    public void It_should_emit_pgsql_insert_sql_with_canonical_multiline_format()
    {
        var sql = new SimpleInsertSqlEmitter(SqlDialect.Pgsql).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedColumns:
            [
                new DbColumnName("SchoolId"),
                new DbColumnName("SchoolYear"),
                new DbColumnName("StudentUniqueId"),
            ],
            orderedParameterNames: ["schoolId", "schoolYear", "studentUniqueId"]
        );

        sql.Should()
            .Be(
                """
                INSERT INTO "edfi"."StudentSchoolAssociation"
                (
                    "SchoolId",
                    "SchoolYear",
                    "StudentUniqueId"
                )
                VALUES
                (
                    @schoolId,
                    @schoolYear,
                    @studentUniqueId
                )
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_mssql_insert_sql_with_canonical_multiline_format()
    {
        var sql = new SimpleInsertSqlEmitter(SqlDialect.Mssql).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedColumns:
            [
                new DbColumnName("SchoolId"),
                new DbColumnName("SchoolYear"),
                new DbColumnName("StudentUniqueId"),
            ],
            orderedParameterNames: ["schoolId", "schoolYear", "studentUniqueId"]
        );

        sql.Should()
            .Be(
                """
                INSERT INTO [edfi].[StudentSchoolAssociation]
                (
                    [SchoolId],
                    [SchoolYear],
                    [StudentUniqueId]
                )
                VALUES
                (
                    @schoolId,
                    @schoolYear,
                    @studentUniqueId
                )
                ;

                """
            );
    }

    [Test]
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_multi_row_insert_sql_with_canonical_multiline_format(SqlDialect dialect)
    {
        var sql = new SimpleInsertSqlEmitter(dialect).EmitBatch(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedColumns:
            [
                new DbColumnName("SchoolId"),
                new DbColumnName("SchoolYear"),
                new DbColumnName("StudentUniqueId"),
            ],
            orderedParameterNamesByRow:
            [
                ["schoolId_0", "schoolYear_0", "studentUniqueId_0"],
                ["schoolId_1", "schoolYear_1", "studentUniqueId_1"],
            ]
        );

        sql.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    INSERT INTO "edfi"."StudentSchoolAssociation"
                    (
                        "SchoolId",
                        "SchoolYear",
                        "StudentUniqueId"
                    )
                    VALUES
                    (
                        @schoolId_0,
                        @schoolYear_0,
                        @studentUniqueId_0
                    ),
                    (
                        @schoolId_1,
                        @schoolYear_1,
                        @studentUniqueId_1
                    )
                    ;

                    """,
                    SqlDialect.Mssql => """
                    INSERT INTO [edfi].[StudentSchoolAssociation]
                    (
                        [SchoolId],
                        [SchoolYear],
                        [StudentUniqueId]
                    )
                    VALUES
                    (
                        @schoolId_0,
                        @schoolYear_0,
                        @studentUniqueId_0
                    ),
                    (
                        @schoolId_1,
                        @schoolYear_1,
                        @studentUniqueId_1
                    )
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );
    }

    [Test]
    public void It_should_fail_fast_when_column_and_parameter_counts_do_not_match()
    {
        var act = () =>
            new SimpleInsertSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedColumns: [new DbColumnName("SchoolId"), new DbColumnName("SchoolYear")],
                orderedParameterNames: ["schoolId"]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Column and parameter counts must match. Column count: 2. Parameter count: 1. (Parameter 'orderedParameterNames')"
            )
            .WithParameterName("orderedParameterNames");
    }

    [Test]
    public void It_should_reject_multi_row_batches_with_misaligned_parameter_counts()
    {
        var act = () =>
            new SimpleInsertSqlEmitter(SqlDialect.Pgsql).EmitBatch(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedColumns: [new DbColumnName("SchoolId"), new DbColumnName("SchoolYear")],
                orderedParameterNamesByRow:
                [
                    ["schoolId_0", "schoolYear_0"],
                    ["schoolId_1"],
                ]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Column and parameter counts must match for row 1. Column count: 2. Parameter count: 1. (Parameter 'orderedParameterNamesByRow')"
            )
            .WithParameterName("orderedParameterNamesByRow");
    }

    [Test]
    public void It_should_reject_invalid_bare_parameter_names()
    {
        var act = () =>
            new SimpleInsertSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedColumns: [new DbColumnName("SchoolId"), new DbColumnName("SchoolYear")],
                orderedParameterNames: ["schoolId", "@schoolYear"]
            );

        act.Should().Throw<ArgumentException>().WithParameterName("bareName");
    }
}
