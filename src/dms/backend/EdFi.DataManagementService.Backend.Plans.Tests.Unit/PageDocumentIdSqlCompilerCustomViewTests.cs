// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PageDocumentIdSqlCompiler_CustomView
{
    private static readonly DbTableName Root = new(new DbSchemaName("edfi"), "CourseTranscript");
    private static readonly DbTableName Intermediate = new(new DbSchemaName("edfi"), "StudentAcademicRecord");
    private static readonly DbColumnName DocumentId = new("DocumentId");

    private static PageDocumentIdAuthorizationCustomViewCheck Check(bool transitive = false) =>
        new(
            "StudentWithCustomAuth",
            0,
            new DbTableName(new DbSchemaName("auth"), "StudentWithCustomAuth"),
            new DbColumnName("Student_DocumentId"),
            transitive
                ?
                [
                    new ColumnPathStep(
                        Root,
                        new DbColumnName("StudentAcademicRecord_DocumentId"),
                        Intermediate,
                        DocumentId
                    ),
                    new ColumnPathStep(Intermediate, new DbColumnName("Student_DocumentId"), null, null),
                ]
                : [new ColumnPathStep(Root, new DbColumnName("Student_DocumentId"), null, null)],
            Root,
            DocumentId
        );

    private static PageDocumentIdQuerySpec Spec(PageDocumentIdAuthorizationSpec authorization) =>
        new(
            Root,
            [],
            new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            new PageCandidateMode.Traditional(IncludeTotalCountSql: true),
            authorization
        );

    [TestCase(
        SqlDialect.Pgsql,
        false,
        "r.\"Student_DocumentId\" IN (SELECT t0.\"Student_DocumentId\" FROM \"auth\".\"StudentWithCustomAuth\" t0)"
    )]
    [TestCase(
        SqlDialect.Mssql,
        false,
        "r.[Student_DocumentId] IN (SELECT t0.[Student_DocumentId] FROM [auth].[StudentWithCustomAuth] t0)"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        true,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"CourseTranscript\" t0 JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = t0.\"StudentAcademicRecord_DocumentId\" WHERE t1.\"Student_DocumentId\" IN (SELECT t2.\"Student_DocumentId\" FROM \"auth\".\"StudentWithCustomAuth\" t2))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        true,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[CourseTranscript] t0 JOIN [edfi].[StudentAcademicRecord] t1 ON t1.[DocumentId] = t0.[StudentAcademicRecord_DocumentId] WHERE t1.[Student_DocumentId] IN (SELECT t2.[Student_DocumentId] FROM [auth].[StudentWithCustomAuth] t2))"
    )]
    public void It_restricts_both_page_and_count_to_the_complete_custom_view_path(
        SqlDialect dialect,
        bool transitive,
        string expectedPredicate
    )
    {
        var plan = new PageDocumentIdSqlCompiler(dialect).Compile(
            Spec(new PageDocumentIdAuthorizationSpec([], CustomViewChecks: [Check(transitive)]))
        );

        string expectedPage =
            dialect is SqlDialect.Pgsql
                ? $"""
                    SELECT r."DocumentId"
                    FROM "edfi"."CourseTranscript" r
                    WHERE
                        ({expectedPredicate})
                    ORDER BY r."DocumentId" ASC
                    LIMIT @limit OFFSET @offset
                    ;
                    """
                : $"""
                    SELECT r.[DocumentId]
                    FROM [edfi].[CourseTranscript] r
                    WHERE
                        ({expectedPredicate})
                    ORDER BY r.[DocumentId] ASC
                    OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
                    ;
                    """;
        string expectedCount =
            dialect is SqlDialect.Pgsql
                ? $"""
                    SELECT COUNT(1)
                    FROM "edfi"."CourseTranscript" r
                    WHERE
                        ({expectedPredicate})
                    ;
                    """
                : $"""
                    SELECT COUNT(1)
                    FROM [edfi].[CourseTranscript] r
                    WHERE
                        ({expectedPredicate})
                    ;
                    """;

        plan.PageDocumentIdSql.ReplaceLineEndings().Trim().Should().Be(expectedPage.ReplaceLineEndings());
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql!.ReplaceLineEndings().Trim().Should().Be(expectedCount.ReplaceLineEndings());
        plan.PageParametersInOrder.Should()
            .Equal(
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit")
            );
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Should().BeEmpty();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_accepts_an_empty_custom_view_list_alongside_an_ownership_filter(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var authorization = new PageDocumentIdAuthorizationSpec(
            [],
            OwnershipTokenParameterization: OwnershipTokenParameterizationFactory.Create(
                dialect,
                [7],
                "ownershipTokenIds"
            )
        );

        var withoutCustomViews = compiler.Compile(Spec(authorization));
        var withEmptyCustomViews = compiler.Compile(Spec(authorization with { CustomViewChecks = [] }));

        withEmptyCustomViews
            .Should()
            .BeEquivalentTo(withoutCustomViews, options => options.WithStrictOrdering());
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_ignores_unused_namespace_parameters_when_all_authorization_checks_are_empty(
        SqlDialect dialect
    )
    {
        var authorization = new PageDocumentIdAuthorizationSpec(
            [],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                dialect,
                ["uri://example.org/"],
                "namespacePrefixes"
            ),
            CustomViewChecks: []
        );

        var plan = new PageDocumentIdSqlCompiler(dialect).Compile(Spec(authorization));

        plan.PageDocumentIdSql.Should().NotContain("WHERE");
        plan.TotalCountSql.Should().NotContain("WHERE");
        plan.PageParametersInOrder.Should()
            .Equal(
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit")
            );
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Should().BeEmpty();
    }

    [TestCase("root", "Custom view authorization check root table*does not match query root table*")]
    [TestCase("empty", "Custom view authorization check must include a path to the basis resource.")]
    [TestCase("direct", "Custom view authorization direct path table*does not match query root table*")]
    [TestCase("target-table", "Custom view authorization transitive path steps must include a target table*")]
    [TestCase(
        "target-column",
        "Custom view authorization transitive path steps must include a target column*"
    )]
    public void It_rejects_malformed_paths_before_returning_a_query(string malformedPart, string message)
    {
        var check = malformedPart switch
        {
            "root" => Check() with { RootTable = Intermediate },
            "empty" => Check() with { PathToBasisResource = [] },
            "direct" => Check() with
            {
                PathToBasisResource =
                [
                    new ColumnPathStep(Intermediate, new DbColumnName("Student_DocumentId"), null, null),
                ],
            },
            "target-table" => Check(true) with
            {
                PathToBasisResource =
                [
                    new ColumnPathStep(
                        Root,
                        new DbColumnName("StudentAcademicRecord_DocumentId"),
                        null,
                        DocumentId
                    ),
                    Check(true).PathToBasisResource[1],
                ],
            },
            "target-column" => Check(true) with
            {
                PathToBasisResource =
                [
                    new ColumnPathStep(
                        Root,
                        new DbColumnName("StudentAcademicRecord_DocumentId"),
                        Intermediate,
                        null
                    ),
                    Check(true).PathToBasisResource[1],
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(malformedPart)),
        };
        var act = () =>
            new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
                Spec(new PageDocumentIdAuthorizationSpec([], CustomViewChecks: [check]))
            );

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }
}
