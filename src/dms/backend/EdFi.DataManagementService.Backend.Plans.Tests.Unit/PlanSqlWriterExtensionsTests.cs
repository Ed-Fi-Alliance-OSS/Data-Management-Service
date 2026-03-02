// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PlanSqlWriterExtensions
{
    private SqlWriter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _writer = new SqlWriter(SqlDialectFactory.Create(SqlDialect.Pgsql));
    }

    [TestCase("offset")]
    [TestCase("_offset")]
    [TestCase("offset1")]
    [TestCase("OffsetName")]
    public void It_should_append_prefixed_parameter_placeholders_for_valid_bare_parameter_names(
        string bareName
    )
    {
        _writer.Append("SELECT ").AppendParameter(bareName);

        _writer.ToString().Should().Be($"SELECT @{bareName}");
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("@offset")]
    [TestCase("1offset")]
    [TestCase("offset-name")]
    public void It_should_reject_invalid_bare_parameter_names(string bareName)
    {
        var act = () => _writer.AppendParameter(bareName);

        act.Should().Throw<ArgumentException>().WithParameterName("bareName");
    }

    [Test]
    public void It_should_emit_multiline_where_with_uppercase_keywords_and_canonical_indentation()
    {
        _writer
            .AppendLine("SELECT r.\"DocumentId\"")
            .AppendLine("FROM \"edfi\".\"StudentSchoolAssociation\" r")
            .AppendWhereClause(["  r.\"SchoolId\" = @schoolId  ", "r.\"SchoolYear\" >= @schoolYear"])
            .AppendLine("ORDER BY r.\"DocumentId\" ASC");

        var sql = _writer.ToString();

        sql.Should()
            .Be(
                """
                SELECT r."DocumentId"
                FROM "edfi"."StudentSchoolAssociation" r
                WHERE
                    (r."SchoolId" = @schoolId)
                    AND (r."SchoolYear" >= @schoolYear)
                ORDER BY r."DocumentId" ASC

                """
            );
        sql.Should().NotContain("\r");
        sql.Split('\n').Should().OnlyContain(line => line.Length == 0 || !line.EndsWith(' '));
    }

    [TestCase("r.\"SchoolId\" = @schoolId\nAND r.\"SchoolYear\" >= @schoolYear")]
    [TestCase("r.\"SchoolId\" = @schoolId\r\nAND r.\"SchoolYear\" >= @schoolYear")]
    [TestCase("\r")]
    [TestCase("\n")]
    public void It_should_reject_predicates_with_line_break_characters(string predicate)
    {
        var act = () => _writer.AppendWhereClause([predicate]);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*Predicate at index 0 cannot contain carriage return or newline characters.*")
            .WithParameterName("predicates");
    }

    [Test]
    public void It_should_not_append_where_when_no_predicates_are_supplied()
    {
        _writer.AppendLine("SELECT 1");
        _writer.AppendWhereClause([]);

        _writer.ToString().Should().Be("SELECT 1\n");
    }

    [TestCase(SqlDialect.Pgsql, "page", "\"page\"")]
    [TestCase(SqlDialect.Mssql, "#page", "[#page]")]
    public void It_should_emit_temp_table_relations_without_schema_qualification(
        SqlDialect dialect,
        string tempTableName,
        string expectedSql
    )
    {
        var writer = new SqlWriter(SqlDialectFactory.Create(dialect));

        writer.Append("FROM ").AppendRelation(new SqlRelationRef.TempTable(tempTableName));

        writer.ToString().Should().Be($"FROM {expectedSql}");
    }

    [TestCase(SqlDialect.Pgsql, "\"edfi\".\"Student\"")]
    [TestCase(SqlDialect.Mssql, "[edfi].[Student]")]
    public void It_should_emit_physical_tables_with_schema_qualification(
        SqlDialect dialect,
        string expectedSql
    )
    {
        var writer = new SqlWriter(SqlDialectFactory.Create(dialect));

        writer
            .Append("FROM ")
            .AppendRelation(
                new SqlRelationRef.PhysicalTable(new DbTableName(new DbSchemaName("edfi"), "Student"))
            );

        writer.ToString().Should().Be($"FROM {expectedSql}");
    }
}
