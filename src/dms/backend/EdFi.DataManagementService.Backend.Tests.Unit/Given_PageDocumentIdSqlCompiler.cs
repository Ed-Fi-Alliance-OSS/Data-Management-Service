// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PageDocumentIdSqlCompiler
{
    private PageDocumentIdSqlCompiler _compiler = null!;

    [SetUp]
    public void Setup()
    {
        _compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_rewrite_reference_site_unified_alias_predicate_to_canonical_with_documentid_presence_gate()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("Student_StudentUniqueId"),
                        QueryComparisonOperator.Equal,
                        "@studentUniqueId"
                    ),
                ],
                [
                    new UnifiedAliasColumnMapping(
                        new DbColumnName("Student_StudentUniqueId"),
                        new DbColumnName("StudentUniqueId_Unified"),
                        new DbColumnName("Student_DocumentId")
                    ),
                ]
            )
        );

        const string ExpectedPredicate =
            "r.\"Student_DocumentId\" IS NOT NULL AND r.\"StudentUniqueId_Unified\" = @studentUniqueId";

        plan.PageDocumentIdSql.Should().Contain(ExpectedPredicate);
        plan.TotalCountSql.Should().Contain(ExpectedPredicate);
        plan.PageDocumentIdSql.Should().NotContain("r.\"Student_StudentUniqueId\" = @studentUniqueId");
    }

    [Test]
    public void It_should_rewrite_optional_path_unified_alias_predicate_with_synthetic_presence_gate()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("GradingPeriodSchoolYear"),
                        QueryComparisonOperator.Equal,
                        "@schoolYear"
                    ),
                ],
                [
                    new UnifiedAliasColumnMapping(
                        new DbColumnName("GradingPeriodSchoolYear"),
                        new DbColumnName("SchoolYear_Unified"),
                        new DbColumnName("GradingPeriodSchoolYear_Present")
                    ),
                ]
            )
        );

        const string ExpectedPredicate =
            "r.\"GradingPeriodSchoolYear_Present\" IS NOT NULL AND r.\"SchoolYear_Unified\" = @schoolYear";

        plan.PageDocumentIdSql.Should().Contain(ExpectedPredicate);
        plan.TotalCountSql.Should().Contain(ExpectedPredicate);
        plan.PageDocumentIdSql.Should().NotContain("r.\"GradingPeriodSchoolYear\" = @schoolYear");
    }

    [Test]
    public void It_should_rewrite_ungated_unified_alias_predicate_to_canonical_column_only()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("SectionIdentifier"),
                        QueryComparisonOperator.Like,
                        "@sectionIdentifier"
                    ),
                ],
                [
                    new UnifiedAliasColumnMapping(
                        new DbColumnName("SectionIdentifier"),
                        new DbColumnName("SectionIdentifier_Unified"),
                        null
                    ),
                ]
            )
        );

        plan.PageDocumentIdSql.Should().Contain("r.\"SectionIdentifier_Unified\" LIKE @sectionIdentifier");
        plan.PageDocumentIdSql.Should().NotContain("IS NOT NULL");
        plan.PageDocumentIdSql.Should().NotContain("r.\"SectionIdentifier\" LIKE @sectionIdentifier");
    }

    [Test]
    public void It_should_keep_non_unified_predicates_on_their_bound_columns()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("SchoolId"),
                        QueryComparisonOperator.GreaterThanOrEqual,
                        "@schoolId"
                    ),
                ],
                []
            )
        );

        plan.PageDocumentIdSql.Should().Contain("r.\"SchoolId\" >= @schoolId");
        plan.TotalCountSql.Should().Contain("r.\"SchoolId\" >= @schoolId");
    }

    [Test]
    public void It_should_emit_mssql_paging_clause_with_offset_fetch()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(CreateSpec([], []));

        plan.PageDocumentIdSql.Should().Contain("OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY");
    }

    private static PageDocumentIdQuerySpec CreateSpec(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyList<UnifiedAliasColumnMapping> unifiedAliasMappings
    )
    {
        return new PageDocumentIdQuerySpec(
            RootTable: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            Predicates: predicates,
            UnifiedAliasMappings: unifiedAliasMappings
        );
    }
}
