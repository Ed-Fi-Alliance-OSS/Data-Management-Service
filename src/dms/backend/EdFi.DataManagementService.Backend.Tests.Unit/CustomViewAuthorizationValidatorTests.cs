// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_CustomViewAuthorizationValidator
{
    [Test]
    public void It_builds_postgresql_validation_sql_for_each_custom_view()
    {
        PageDocumentIdAuthorizationCustomViewCheck[] checks =
        [
            CreateCheck("StudentEducationOrganizationAuthorization", "DocumentId"),
            CreateCheck("DescriptorAuthorization", "DocumentId"),
        ];

        string sql = CustomViewAuthorizationValidator.BuildCommandText(SqlDialect.Pgsql, checks);

        // Each check emits a catalog type guard (independent of view data) followed by a join that
        // surfaces missing-view / missing-column / operator-type errors at query time.
        sql.Should().Contain("FROM pg_catalog.pg_attribute a");
        sql.Should().Contain("n.nspname = 'auth'");
        sql.Should().Contain("c.relname = 'StudentEducationOrganizationAuthorization'");
        sql.Should().Contain("c.relname = 'DescriptorAuthorization'");
        sql.Should().Contain("a.attname = 'DocumentId'");
        sql.Should().Contain("a.atttypid <> 'pg_catalog.int8'::regtype");
        sql.Should().Contain("RAISE EXCEPTION 'Invalid custom authorization view DocumentId contract.';");
        sql.Should()
            .Contain(
                "SELECT cv.\"DocumentId\" FROM \"auth\".\"StudentEducationOrganizationAuthorization\" cv INNER JOIN \"edfi\".\"School\" root ON root.\"DocumentId\" = cv.\"DocumentId\" LIMIT 0"
            );
        sql.Should()
            .Contain(
                "SELECT cv.\"DocumentId\" FROM \"auth\".\"DescriptorAuthorization\" cv INNER JOIN \"edfi\".\"School\" root ON root.\"DocumentId\" = cv.\"DocumentId\" LIMIT 0"
            );
        // The bind probe must stay row-free: LIMIT 1 makes it scan until a match or exhaust an
        // empty/disjoint view, which is row work the catalog guards above already cover.
        sql.Should().NotContain("LIMIT 1");
    }

    [Test]
    public void It_accepts_a_materialized_view_as_the_postgresql_custom_authorization_object()
    {
        // A materialized view is a valid custom authorization object, and its columns are absent from
        // information_schema, so the DocumentId type guard must read pg_catalog.pg_attribute instead.
        string sql = CustomViewAuthorizationValidator.BuildCommandText(
            SqlDialect.Pgsql,
            [CreateCheck("StudentAuthorization", "DocumentId")]
        );

        sql.Should().Contain("c.relkind NOT IN ('v', 'm')");
        sql.Should().Contain("c.relkind IN ('v', 'm')");
        sql.Should().NotContain("information_schema");
    }

    [Test]
    public void It_escapes_postgresql_catalog_string_literals()
    {
        string sql = CustomViewAuthorizationValidator.BuildCommandText(
            SqlDialect.Pgsql,
            [CreateCheck("Student'Authorization", "Document'Id")]
        );

        sql.Should().Contain("c.relname = 'Student''Authorization'");
        sql.Should().Contain("a.attname = 'Document''Id'");
    }

    [Test]
    public void It_does_not_let_a_dollar_quoted_view_name_terminate_the_postgresql_do_block()
    {
        // The view name is embedded in the DO block as a string literal, so a name containing the
        // delimiter would close the block early and leave the remainder of the body parsed as top-level
        // SQL. The emitted delimiter must therefore avoid every embedded literal.
        string sql = CustomViewAuthorizationValidator.BuildCommandText(
            SqlDialect.Pgsql,
            [CreateCheck("Student$$Authorization", "DocumentId")]
        );

        sql.Should().NotContain("DO $$");
        sql.Should().Contain("c.relname = 'Student$$Authorization'");

        // Exactly one open and one close delimiter, and neither is the fixed $$ the name carries.
        string openTag = sql[sql.IndexOf("DO ", StringComparison.Ordinal)..]
            .Split('\n')[0]["DO ".Length..]
            .Trim();
        openTag.Should().NotBe("$$");
        CountOccurrences(sql, openTag).Should().Be(2);
        sql.Should().Contain($"END {openTag};");
        // The row-free bind probe is unchanged by the delimiter hardening.
        sql.Should().Contain("LIMIT 0");
        sql.Should().NotContain("LIMIT 1");
    }

    [Test]
    public void It_escalates_the_postgresql_dollar_quote_tag_until_it_is_collision_free()
    {
        // A name carrying the default tag forces the next candidate; a name carrying both forces another.
        CustomViewAuthorizationValidator.BuildPgsqlDollarQuoteTag("plain").Should().Be("$dmscv$");
        CustomViewAuthorizationValidator
            .BuildPgsqlDollarQuoteTag("carries $dmscv$ inline")
            .Should()
            .Be("$dmscvx$");
        CustomViewAuthorizationValidator
            .BuildPgsqlDollarQuoteTag("carries $dmscv$ and $dmscvx$ inline")
            .Should()
            .Be("$dmscvxx$");
    }

    [Test]
    public void It_emits_a_collision_free_delimiter_when_a_view_name_carries_the_default_tag()
    {
        string sql = CustomViewAuthorizationValidator.BuildCommandText(
            SqlDialect.Pgsql,
            [CreateCheck("Student$dmscv$Authorization", "DocumentId")]
        );

        sql.Should().Contain("DO $dmscvx$");
        sql.Should().Contain("END $dmscvx$;");
        sql.Should().Contain("c.relname = 'Student$dmscv$Authorization'");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;

        for (
            var index = value.IndexOf(token, StringComparison.Ordinal);
            index >= 0;
            index = value.IndexOf(token, index + token.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }

    [Test]
    public void It_builds_mssql_schema_validation_sql_that_is_independent_of_view_data()
    {
        PageDocumentIdAuthorizationCustomViewCheck[] checks =
        [
            CreateCheck(
                "StudentAuthorization",
                "DocumentId",
                [
                    new ColumnPathStep(
                        new DbTableName(new DbSchemaName("edfi"), "School"),
                        new DbColumnName("Student_DocumentId"),
                        new DbTableName(new DbSchemaName("edfi"), "Student"),
                        new DbColumnName("DocumentId")
                    ),
                    new ColumnPathStep(
                        new DbTableName(new DbSchemaName("edfi"), "Student"),
                        new DbColumnName("DocumentId"),
                        null,
                        null
                    ),
                ]
            ),
        ];

        string sql = CustomViewAuthorizationValidator.BuildCommandText(SqlDialect.Mssql, checks);

        sql.Should().Contain("FROM sys.views v");
        sql.Should().Contain("INNER JOIN sys.schemas s ON s.schema_id = v.schema_id");
        sql.Should().Contain("INNER JOIN sys.columns c ON c.object_id = v.object_id");
        // Binary collation on every catalog name comparison: sysname carries the database collation,
        // which is case-insensitive by default, and the bracketed bind probe resolves identifiers
        // case-insensitively too — so without this a mis-cased auth view or DocumentId column would
        // pass validation and filter the page against an object that is not the configured one.
        sql.Should().Contain("s.name COLLATE Latin1_General_100_BIN2 = N'auth'");
        sql.Should().Contain("v.name COLLATE Latin1_General_100_BIN2 = N'StudentAuthorization'");
        sql.Should().Contain("c.name COLLATE Latin1_General_100_BIN2 = N'DocumentId'");
        sql.Should().Contain("c.system_type_id = TYPE_ID(N'bigint')");
        sql.Should().Contain("THROW 50000");
        sql.Should()
            .Contain(
                "EXEC sys.sp_executesql N'SELECT TOP (0) cv.[DocumentId] FROM [auth].[StudentAuthorization] cv WHERE cv.[DocumentId] IS NOT NULL'"
            );
        sql.Should().NotContain("TOP (1)").And.NotContain("CONVERT(bigint, cv.");
        // The resolved root-to-basis path is exercised by the page query on both engines, so MSSQL
        // emits no extra root-path bind probe that PostgreSQL lacks.
        sql.Should().NotContain("[edfi].[School] root");
    }

    [Test]
    public void It_escapes_mssql_catalog_string_literals()
    {
        string sql = CustomViewAuthorizationValidator.BuildCommandText(
            SqlDialect.Mssql,
            [CreateCheck("Student'Authorization", "Document'Id")]
        );

        sql.Should().Contain("v.name COLLATE Latin1_General_100_BIN2 = N'Student''Authorization'");
        sql.Should().Contain("c.name COLLATE Latin1_General_100_BIN2 = N'Document''Id'");
    }

    [Test]
    public async Task It_skips_execution_when_no_custom_view_checks_are_configured()
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();

        await CustomViewAuthorizationValidator.ValidateAsync(commandExecutor, SqlDialect.Pgsql, []);
        await CustomViewAuthorizationValidator.ValidateAsync(commandExecutor, SqlDialect.Pgsql, null);

        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_consumes_all_validation_result_sets()
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var reader = A.Fake<IRelationalCommandReader>();
        A.CallTo(() => reader.ReadAsync(A<CancellationToken>._)).Returns(true);
        A.CallTo(() => reader.NextResultAsync(A<CancellationToken>._)).ReturnsNextFromSequence(true, false);

        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<IRelationalCommandReader, CancellationToken, Task<bool>> callback,
                    CancellationToken cancellationToken
                ) => callback(reader, cancellationToken)
            );

        await CustomViewAuthorizationValidator.ValidateAsync(
            commandExecutor,
            SqlDialect.Pgsql,
            [
                CreateCheck("FirstAuthorization", "DocumentId"),
                CreateCheck("SecondAuthorization", "DocumentId"),
            ]
        );

        A.CallTo(() => reader.NextResultAsync(A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_wraps_database_execution_failures()
    {
        var commandExecutor = A.Fake<IRelationalCommandExecutor>();
        var databaseException = new TestDbException("simulated database failure");
        A.CallTo(() =>
                commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromException<bool>(databaseException));

        Func<Task> action = () =>
            CustomViewAuthorizationValidator.ValidateAsync(
                commandExecutor,
                SqlDialect.Pgsql,
                [CreateCheck("StudentAuthorization", "DocumentId")]
            );

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    private static PageDocumentIdAuthorizationCustomViewCheck CreateCheck(
        string viewName,
        string documentIdColumn,
        IReadOnlyList<ColumnPathStep>? pathToBasisResource = null
    ) =>
        new(
            StrategyName: viewName,
            RawConfiguredIndex: 0,
            AuthView: new DbTableName(new DbSchemaName("auth"), viewName),
            AuthViewDocumentIdColumn: new DbColumnName(documentIdColumn),
            PathToBasisResource: pathToBasisResource ?? [],
            RootTable: new DbTableName(new DbSchemaName("edfi"), "School"),
            RootDocumentIdColumn: new DbColumnName("DocumentId")
        );

    private sealed class TestDbException(string message) : DbException(message);
}
