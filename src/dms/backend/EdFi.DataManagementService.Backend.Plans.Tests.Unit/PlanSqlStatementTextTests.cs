// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PlanSqlStatementText
{
    [TestCase("SELECT 1;", "SELECT 1;")]
    [TestCase("SELECT 1", "SELECT 1;")]
    [TestCase("SELECT 1\n", "SELECT 1;")]
    [TestCase("SELECT 1;\n", "SELECT 1;\n")]
    public void It_should_terminate_a_statement_without_doubling_an_existing_terminator(
        string sql,
        string expected
    )
    {
        PlanSqlStatementText.AsTerminatedStatement(sql).Should().Be(expected);
    }

    [TestCase("SELECT 1;", "SELECT 1")]
    [TestCase("SELECT 1;\n", "SELECT 1")]
    [TestCase("SELECT 1\n", "SELECT 1")]
    [TestCase("SELECT 1", "SELECT 1")]
    public void It_should_strip_the_terminator_and_trailing_whitespace_for_an_embeddable_body(
        string sql,
        string expected
    )
    {
        PlanSqlStatementText.AsEmbeddableBody(sql).Should().Be(expected);
    }

    [TestCase(SqlDialect.Pgsql, "\"edfi\".\"School\" r")]
    [TestCase(SqlDialect.Mssql, "[edfi].[School] r")]
    public void It_should_make_compiled_unpaged_candidate_sql_embeddable_in_a_common_table_expression(
        SqlDialect dialect,
        string expectedFinalFragment
    )
    {
        // The unpaged candidate relation is emitted as a terminated statement, and its consumers nest it
        // in a common table expression, where a terminator is a syntax error.
        var plan = new PageDocumentIdSqlCompiler(dialect).Compile(
            new PageDocumentIdQuerySpec(
                new DbTableName(new DbSchemaName("edfi"), "School"),
                [],
                new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                new PageCandidateMode.UnpagedCandidates()
            )
        );

        plan.PageDocumentIdSql.TrimEnd().Should().EndWith(";");

        var embeddableBody = PlanSqlStatementText.AsEmbeddableBody(plan.PageDocumentIdSql);

        // The compiler emits the terminator on its own line, so an embeddable body has to lose the
        // newline ahead of it as well and end on the last real SQL token.
        embeddableBody.Should().NotContain(";");
        embeddableBody.Should().EndWith(expectedFinalFragment);
    }
}
