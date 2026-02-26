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
                        "studentUniqueId"
                    ),
                ],
                [
                    CreateUnifiedAliasMapping(
                        new DbColumnName("Student_StudentUniqueId"),
                        new DbColumnName("StudentUniqueId_Unified"),
                        new DbColumnName("Student_DocumentId")
                    ),
                ],
                includeTotalCountSql: true
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
                        "schoolYear"
                    ),
                ],
                [
                    CreateUnifiedAliasMapping(
                        new DbColumnName("GradingPeriodSchoolYear"),
                        new DbColumnName("SchoolYear_Unified"),
                        new DbColumnName("GradingPeriodSchoolYear_Present")
                    ),
                ],
                includeTotalCountSql: true
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
                        "sectionIdentifier"
                    ),
                ],
                [
                    CreateUnifiedAliasMapping(
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
                        "schoolId"
                    ),
                ],
                [],
                includeTotalCountSql: true
            )
        );

        plan.PageDocumentIdSql.Should().Contain("r.\"SchoolId\" >= @schoolId");
        plan.TotalCountSql.Should().Contain("r.\"SchoolId\" >= @schoolId");
    }

    [Test]
    public void It_should_emit_predicates_in_stable_sorted_order_after_unified_alias_rewrite()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("AliasB"),
                        QueryComparisonOperator.GreaterThan,
                        "zParam"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("AliasA"),
                        QueryComparisonOperator.Equal,
                        "aParam"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("AliasC"),
                        QueryComparisonOperator.LessThan,
                        "mParam"
                    ),
                ],
                [
                    CreateUnifiedAliasMapping(
                        new DbColumnName("AliasA"),
                        new DbColumnName("CanonicalA"),
                        new DbColumnName("PresenceA")
                    ),
                    CreateUnifiedAliasMapping(
                        new DbColumnName("AliasB"),
                        new DbColumnName("CanonicalB"),
                        null
                    ),
                ]
            )
        );

        var firstPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "(r.\"AliasC\" < @mParam)",
            StringComparison.Ordinal
        );
        var secondPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "(r.\"CanonicalB\" > @zParam)",
            StringComparison.Ordinal
        );
        var thirdPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "(r.\"PresenceA\" IS NOT NULL AND r.\"CanonicalA\" = @aParam)",
            StringComparison.Ordinal
        );

        firstPredicateIndex.Should().BeGreaterThan(-1);
        secondPredicateIndex.Should().BeGreaterThan(firstPredicateIndex);
        thirdPredicateIndex.Should().BeGreaterThan(secondPredicateIndex);

        plan.ParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("mParam", "zParam", "aParam", "offset", "limit");
        plan.ParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(
                QuerySqlParameterRole.Filter,
                QuerySqlParameterRole.Filter,
                QuerySqlParameterRole.Filter,
                QuerySqlParameterRole.Offset,
                QuerySqlParameterRole.Limit
            );
    }

    [Test]
    public void It_should_fail_fast_for_duplicate_semantic_predicates_after_unified_alias_rewrite()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("AliasTwo"),
                            QueryComparisonOperator.Equal,
                            "zParam"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("AliasOne"),
                            QueryComparisonOperator.Equal,
                            "aParam"
                        ),
                    ],
                    [
                        CreateUnifiedAliasMapping(
                            new DbColumnName("AliasTwo"),
                            new DbColumnName("CanonicalStudentUniqueId"),
                            new DbColumnName("Student_DocumentId")
                        ),
                        CreateUnifiedAliasMapping(
                            new DbColumnName("AliasOne"),
                            new DbColumnName("CanonicalStudentUniqueId"),
                            new DbColumnName("Student_DocumentId")
                        ),
                    ]
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Duplicate predicate after unified alias rewrite for semantic key (presenceColumn='Student_DocumentId', canonicalColumn='CanonicalStudentUniqueId', operator='Equal'). Colliding original columns: ['AliasOne', 'AliasTwo']. Colliding parameter names: ['aParam', 'zParam']."
            );
    }

    [Test]
    public void It_should_emit_identical_sql_and_parameter_metadata_when_compiling_the_same_query_twice()
    {
        var spec = CreateSpec(
            [
                new QueryValuePredicate(
                    new DbColumnName("SchoolId"),
                    QueryComparisonOperator.Equal,
                    "schoolId"
                ),
                new QueryValuePredicate(
                    new DbColumnName("Student_StudentUniqueId"),
                    QueryComparisonOperator.Equal,
                    "studentUniqueId"
                ),
                new QueryValuePredicate(
                    new DbColumnName("SchoolYear"),
                    QueryComparisonOperator.GreaterThanOrEqual,
                    "schoolYear"
                ),
            ],
            [
                CreateUnifiedAliasMapping(
                    new DbColumnName("Student_StudentUniqueId"),
                    new DbColumnName("StudentUniqueId_Unified"),
                    new DbColumnName("Student_DocumentId")
                ),
            ],
            includeTotalCountSql: true
        );

        var first = _compiler.Compile(spec);
        var second = _compiler.Compile(spec);

        second.PageDocumentIdSql.Should().Be(first.PageDocumentIdSql);
        second.TotalCountSql.Should().Be(first.TotalCountSql);
        second.ParametersInOrder.Should().Equal(first.ParametersInOrder);
    }

    [Test]
    public void It_should_emit_identical_sql_and_parameter_metadata_across_predicate_order_permutations()
    {
        var unifiedAliasMappings = new KeyValuePair<DbColumnName, ColumnStorage.UnifiedAlias>[]
        {
            CreateUnifiedAliasMapping(
                new DbColumnName("Student_StudentUniqueId"),
                new DbColumnName("StudentUniqueId_Unified"),
                new DbColumnName("Student_DocumentId")
            ),
            CreateUnifiedAliasMapping(
                new DbColumnName("SectionIdentifier"),
                new DbColumnName("SectionIdentifier_Unified"),
                null
            ),
        };

        var first = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("SectionIdentifier"),
                        QueryComparisonOperator.Equal,
                        "sectionIdentifier"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("SchoolYear"),
                        QueryComparisonOperator.GreaterThanOrEqual,
                        "schoolYear"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("Student_StudentUniqueId"),
                        QueryComparisonOperator.Equal,
                        "studentUniqueId"
                    ),
                ],
                unifiedAliasMappings,
                includeTotalCountSql: true
            )
        );
        var second = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("Student_StudentUniqueId"),
                        QueryComparisonOperator.Equal,
                        "studentUniqueId"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("SectionIdentifier"),
                        QueryComparisonOperator.Equal,
                        "sectionIdentifier"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("SchoolYear"),
                        QueryComparisonOperator.GreaterThanOrEqual,
                        "schoolYear"
                    ),
                ],
                unifiedAliasMappings,
                includeTotalCountSql: true
            )
        );

        second.PageDocumentIdSql.Should().Be(first.PageDocumentIdSql);
        second.TotalCountSql.Should().Be(first.TotalCountSql);
        second.ParametersInOrder.Should().Equal(first.ParametersInOrder);
    }

    [Test]
    public void It_should_fail_with_a_deterministic_invalid_parameter_name_error_across_predicate_order_permutations()
    {
        var firstException = Assert.Throws<ArgumentException>(() =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "schoolId"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("SchoolYear"),
                            QueryComparisonOperator.Equal,
                            "1; DROP TABLE foo--"
                        ),
                    ],
                    []
                )
            )
        );
        var secondException = Assert.Throws<ArgumentException>(() =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolYear"),
                            QueryComparisonOperator.Equal,
                            "1; DROP TABLE foo--"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "schoolId"
                        ),
                    ],
                    []
                )
            )
        );

        firstException.Should().NotBeNull();
        secondException.Should().NotBeNull();
        secondException!.ParamName.Should().Be(firstException!.ParamName);
        secondException.Message.Should().Be(firstException.Message);
    }

    [Test]
    public void It_should_fail_with_a_deterministic_paging_collision_error_across_predicate_order_permutations()
    {
        var firstException = Assert.Throws<ArgumentException>(() =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "OffSet"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("SchoolYear"),
                            QueryComparisonOperator.Equal,
                            "schoolYear"
                        ),
                    ],
                    []
                )
            )
        );
        var secondException = Assert.Throws<ArgumentException>(() =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolYear"),
                            QueryComparisonOperator.Equal,
                            "schoolYear"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "OffSet"
                        ),
                    ],
                    []
                )
            )
        );

        firstException.Should().NotBeNull();
        secondException.Should().NotBeNull();
        secondException!.ParamName.Should().Be(firstException!.ParamName);
        secondException.Message.Should().Be(firstException.Message);
        secondException.ParamName.Should().Be("Predicates");
        secondException
            .Message.Should()
            .Contain("Filter parameter name 'OffSet' collides with paging parameter name 'offset'");
    }

    [Test]
    public void It_should_reject_in_operator_until_supported()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.In,
                            "schoolIds"
                        ),
                    ],
                    []
                )
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Operator 'In' is not yet supported by ToSqlOperator.");
    }

    [Test]
    public void It_should_emit_pgsql_paging_clause_with_limit_offset()
    {
        var plan = _compiler.Compile(CreateSpec([], []));

        plan.PageDocumentIdSql.Should().Contain("LIMIT @limit OFFSET @offset");
        plan.PageDocumentIdSql.Should().NotContain("OFFSET @offset LIMIT @limit");
        plan.ParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(QuerySqlParameterRole.Offset, QuerySqlParameterRole.Limit);
        plan.ParametersInOrder.Select(parameter => parameter.ParameterName).Should().Equal("offset", "limit");
    }

    [Test]
    public void It_should_emit_mssql_paging_clause_with_offset_fetch()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(CreateSpec([], []));

        plan.PageDocumentIdSql.Should().Contain("OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY");
    }

    [Test]
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_terminate_compiled_statements_with_semicolon_newline(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(CreateSpec([], [], includeTotalCountSql: true));

        plan.PageDocumentIdSql.Should().EndWith(";\n");
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().EndWith(";\n");
    }

    [Test]
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_not_emit_total_count_sql_when_not_requested(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(CreateSpec([], []));

        plan.TotalCountSql.Should().BeNull();
    }

    [Test]
    public void It_should_emit_total_count_sql_when_requested()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("SchoolId"),
                        QueryComparisonOperator.Equal,
                        "schoolId"
                    ),
                ],
                [],
                includeTotalCountSql: true
            )
        );

        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain("SELECT COUNT(1)");
        plan.TotalCountSql.Should().Contain("(r.\"SchoolId\" = @schoolId)");
        plan.TotalCountSql.Should().NotContain("@offset");
        plan.TotalCountSql.Should().NotContain("@limit");
        plan.ParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(QuerySqlParameterRole.Filter, QuerySqlParameterRole.Offset, QuerySqlParameterRole.Limit);
        plan.ParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolId", "offset", "limit");
    }

    [Test]
    public void It_should_reject_predicate_parameter_names_that_are_not_safe_to_emit()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "1; DROP TABLE foo--"
                        ),
                    ],
                    []
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("ParameterName");
    }

    [Test]
    public void It_should_reject_paging_parameter_names_that_are_not_safe_to_emit()
    {
        var act = () => _compiler.Compile(CreateSpec([], [], offsetParameterName: "1; DROP TABLE foo--"));

        act.Should().Throw<ArgumentException>().WithParameterName("OffsetParameterName");
    }

    [Test]
    public void It_should_reject_limit_parameter_names_that_are_not_safe_to_emit()
    {
        var act = () => _compiler.Compile(CreateSpec([], [], limitParameterName: "1; DROP TABLE foo--"));

        act.Should().Throw<ArgumentException>().WithParameterName("LimitParameterName");
    }

    [Test]
    public void It_should_reject_filter_parameter_names_that_collide_with_offset_parameter_name_case_insensitively()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "OffSet"
                        ),
                    ],
                    []
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("Predicates")
            .WithMessage(
                "Filter parameter name 'OffSet' collides with paging parameter name 'offset' (case-insensitive). Rename the filter parameter or change OffsetParameterName.*"
            );
    }

    [Test]
    public void It_should_reject_filter_parameter_names_that_collide_with_limit_parameter_name_case_insensitively()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "LiMit"
                        ),
                    ],
                    []
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("Predicates")
            .WithMessage(
                "Filter parameter name 'LiMit' collides with paging parameter name 'limit' (case-insensitive). Rename the filter parameter or change LimitParameterName.*"
            );
    }

    private static PageDocumentIdQuerySpec CreateSpec(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyList<KeyValuePair<DbColumnName, ColumnStorage.UnifiedAlias>> unifiedAliasMappings,
        string offsetParameterName = "offset",
        string limitParameterName = "limit",
        bool includeTotalCountSql = false
    )
    {
        var unifiedAliasMappingsByColumn = new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>();

        foreach (var (aliasColumn, unifiedAlias) in unifiedAliasMappings)
        {
            unifiedAliasMappingsByColumn.Add(aliasColumn, unifiedAlias);
        }

        return new PageDocumentIdQuerySpec(
            RootTable: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            Predicates: predicates,
            UnifiedAliasMappingsByColumn: unifiedAliasMappingsByColumn,
            OffsetParameterName: offsetParameterName,
            LimitParameterName: limitParameterName,
            IncludeTotalCountSql: includeTotalCountSql
        );
    }

    private static KeyValuePair<DbColumnName, ColumnStorage.UnifiedAlias> CreateUnifiedAliasMapping(
        DbColumnName aliasColumn,
        DbColumnName canonicalColumn,
        DbColumnName? presenceColumn
    )
    {
        return new KeyValuePair<DbColumnName, ColumnStorage.UnifiedAlias>(
            aliasColumn,
            new ColumnStorage.UnifiedAlias(canonicalColumn, presenceColumn)
        );
    }
}
