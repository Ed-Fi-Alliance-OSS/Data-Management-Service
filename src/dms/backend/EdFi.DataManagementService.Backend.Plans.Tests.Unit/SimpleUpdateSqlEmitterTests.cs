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
public class Given_SimpleUpdateSqlEmitter
{
    [Test]
    public void It_should_emit_pgsql_update_sql_with_canonical_multiline_format()
    {
        var sql = new SimpleUpdateSqlEmitter(SqlDialect.Pgsql).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedSetColumns: [new DbColumnName("LastSurname"), new DbColumnName("BirthDate")],
            orderedSetParameterNames: ["lastSurname", "birthDate"],
            orderedKeyColumns: [new DbColumnName("StudentUniqueId"), new DbColumnName("SchoolYear")],
            orderedKeyParameterNames: ["studentUniqueId", "schoolYear"]
        );

        sql.Should()
            .Be(
                """
                UPDATE "edfi"."StudentSchoolAssociation"
                SET
                    "LastSurname" = @lastSurname,
                    "BirthDate" = @birthDate
                WHERE
                    ("StudentUniqueId" = @studentUniqueId)
                    AND ("SchoolYear" = @schoolYear)
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_mssql_update_sql_with_canonical_multiline_format()
    {
        var sql = new SimpleUpdateSqlEmitter(SqlDialect.Mssql).Emit(
            table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            orderedSetColumns: [new DbColumnName("LastSurname"), new DbColumnName("BirthDate")],
            orderedSetParameterNames: ["lastSurname", "birthDate"],
            orderedKeyColumns: [new DbColumnName("StudentUniqueId"), new DbColumnName("SchoolYear")],
            orderedKeyParameterNames: ["studentUniqueId", "schoolYear"]
        );

        sql.Should()
            .Be(
                """
                UPDATE [edfi].[StudentSchoolAssociation]
                SET
                    [LastSurname] = @lastSurname,
                    [BirthDate] = @birthDate
                WHERE
                    ([StudentUniqueId] = @studentUniqueId)
                    AND ([SchoolYear] = @schoolYear)
                ;

                """
            );
    }

    [Test]
    public void It_should_reject_empty_set_columns()
    {
        var act = () =>
            new SimpleUpdateSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedSetColumns: [],
                orderedSetParameterNames: [],
                orderedKeyColumns: [new DbColumnName("StudentUniqueId")],
                orderedKeyParameterNames: ["studentUniqueId"]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("At least one set column must be supplied. (Parameter 'orderedSetColumns')")
            .WithParameterName("orderedSetColumns");
    }

    [Test]
    public void It_should_reject_misaligned_set_column_and_parameter_counts()
    {
        var act = () =>
            new SimpleUpdateSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedSetColumns: [new DbColumnName("LastSurname"), new DbColumnName("BirthDate")],
                orderedSetParameterNames: ["lastSurname"],
                orderedKeyColumns: [new DbColumnName("StudentUniqueId")],
                orderedKeyParameterNames: ["studentUniqueId"]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Set-column and parameter counts must match. Set-column count: 2. Parameter count: 1. (Parameter 'orderedSetParameterNames')"
            )
            .WithParameterName("orderedSetParameterNames");
    }

    [Test]
    public void It_should_reject_empty_key_columns()
    {
        var act = () =>
            new SimpleUpdateSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedSetColumns: [new DbColumnName("LastSurname")],
                orderedSetParameterNames: ["lastSurname"],
                orderedKeyColumns: [],
                orderedKeyParameterNames: []
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("At least one key column must be supplied. (Parameter 'orderedKeyColumns')")
            .WithParameterName("orderedKeyColumns");
    }

    [Test]
    public void It_should_reject_misaligned_key_column_and_parameter_counts()
    {
        var act = () =>
            new SimpleUpdateSqlEmitter(SqlDialect.Pgsql).Emit(
                table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
                orderedSetColumns: [new DbColumnName("LastSurname")],
                orderedSetParameterNames: ["lastSurname"],
                orderedKeyColumns: [new DbColumnName("StudentUniqueId"), new DbColumnName("SchoolYear")],
                orderedKeyParameterNames: ["studentUniqueId"]
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Key-column and parameter counts must match. Key-column count: 2. Parameter count: 1. (Parameter 'orderedKeyParameterNames')"
            )
            .WithParameterName("orderedKeyParameterNames");
    }
}
