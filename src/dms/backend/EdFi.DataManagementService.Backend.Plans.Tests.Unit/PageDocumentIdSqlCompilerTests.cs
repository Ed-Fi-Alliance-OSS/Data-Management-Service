// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Security;
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
                        QueryComparisonOperator.Equal,
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

        plan.PageDocumentIdSql.Should().Contain("r.\"SectionIdentifier_Unified\" = @sectionIdentifier");
        plan.PageDocumentIdSql.Should().NotContain("IS NOT NULL");
        plan.PageDocumentIdSql.Should().NotContain("r.\"SectionIdentifier\" = @sectionIdentifier");
    }

    [Test]
    public void It_should_keep_non_unified_predicates_on_their_bound_columns()
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

        plan.PageDocumentIdSql.Should().Contain("r.\"SchoolId\" = @schoolId");
        plan.TotalCountSql.Should().Contain("r.\"SchoolId\" = @schoolId");
    }

    [Test]
    public void It_should_join_document_table_when_any_predicate_targets_document_uuid()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new QueryPredicateTarget.DocumentUuid(),
                        QueryComparisonOperator.Equal,
                        "documentUuid"
                    ),
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

        const string ExpectedDocumentJoin =
            "INNER JOIN \"dms\".\"Document\" doc ON doc.\"DocumentId\" = r.\"DocumentId\"";
        const string ExpectedDocumentUuidPredicate = "doc.\"DocumentUuid\" = @documentUuid";

        plan.PageDocumentIdSql.Should().Contain(ExpectedDocumentJoin);
        plan.PageDocumentIdSql.Should().Contain(ExpectedDocumentUuidPredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedDocumentJoin);
        plan.TotalCountSql.Should().Contain(ExpectedDocumentUuidPredicate);
    }

    [Test]
    public void It_should_join_descriptor_table_when_any_predicate_targets_descriptor_column()
    {
        var plan = _compiler.Compile(
            CreateDescriptorSpec(
                [
                    new QueryValuePredicate(
                        new QueryPredicateTarget.DescriptorColumn(new DbColumnName("Namespace")),
                        QueryComparisonOperator.Equal,
                        "namespace"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("ResourceKeyId"),
                        QueryComparisonOperator.Equal,
                        "resourceKeyId"
                    ),
                ],
                includeTotalCountSql: true
            )
        );

        const string ExpectedDescriptorJoin =
            "INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"";
        const string ExpectedDescriptorPredicate = "d.\"Namespace\" = @namespace";

        plan.PageDocumentIdSql.Should().Contain(ExpectedDescriptorJoin);
        plan.PageDocumentIdSql.Should().Contain(ExpectedDescriptorPredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedDescriptorJoin);
        plan.TotalCountSql.Should().Contain(ExpectedDescriptorPredicate);
    }

    [Test]
    public void It_should_join_descriptor_table_when_any_namespace_check_targets_descriptor_table()
    {
        var documentTable = new DbTableName(new DbSchemaName("dms"), "Document");
        var descriptorTable = new DbTableName(new DbSchemaName("dms"), "Descriptor");
        var namespaceColumn = new DbColumnName("Namespace");
        var plan = _compiler.Compile(
            new PageDocumentIdQuerySpec(
                RootTable: documentTable,
                Predicates: [],
                UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                IncludeTotalCountSql: true,
                Authorization: new PageDocumentIdAuthorizationSpec(
                    Strategies: [],
                    NamespaceChecks:
                    [
                        new NamespaceAuthorizationCheckSpec(
                            0,
                            NamespaceAuthorizationCheckValueSource.Stored,
                            documentTable,
                            namespaceColumn
                        ),
                        new NamespaceAuthorizationCheckSpec(
                            1,
                            NamespaceAuthorizationCheckValueSource.Stored,
                            descriptorTable,
                            namespaceColumn
                        ),
                    ],
                    NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                        SqlDialect.Pgsql,
                        ["uri://ed-fi.org/"],
                        "namespacePrefixes"
                    )
                )
            )
        );

        const string ExpectedDescriptorJoin =
            "INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"";
        const string ExpectedNamespaceAuthorizationGroup =
            "(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes) AND d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes))";

        plan.PageDocumentIdSql.Should().Contain(ExpectedDescriptorJoin);
        plan.PageDocumentIdSql.Should().Contain(ExpectedNamespaceAuthorizationGroup);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedDescriptorJoin);
        plan.TotalCountSql.Should().Contain(ExpectedNamespaceAuthorizationGroup);
    }

    [Test]
    public void It_should_reject_namespace_authorization_checks_for_unrelated_tables()
    {
        var documentTable = new DbTableName(new DbSchemaName("dms"), "Document");
        var unrelatedTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var namespaceColumn = new DbColumnName("Namespace");
        var authorization = new PageDocumentIdAuthorizationSpec(
            Strategies: [],
            NamespaceChecks:
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    unrelatedTable,
                    namespaceColumn
                ),
            ],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );

        var act = () =>
            _compiler.Compile(
                new PageDocumentIdQuerySpec(
                    RootTable: documentTable,
                    Predicates: [],
                    UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                    IncludeTotalCountSql: true,
                    Authorization: authorization
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"Namespace authorization check spec table '{unrelatedTable}' does not match query root table '{documentTable}'. Namespace authorization SQL emission supports only concrete root-table columns (or the shared dms.Descriptor join for descriptor queries)."
            );
    }

    [Test]
    public void It_should_emit_predicates_in_stable_sorted_order_after_unified_alias_rewrite()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("AliasB"),
                        QueryComparisonOperator.Equal,
                        "zParam"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("AliasA"),
                        QueryComparisonOperator.Equal,
                        "aParam"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("AliasC"),
                        QueryComparisonOperator.Equal,
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
            "(r.\"AliasC\" = @mParam)",
            StringComparison.Ordinal
        );
        var secondPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "(r.\"CanonicalB\" = @zParam)",
            StringComparison.Ordinal
        );
        var thirdPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "(r.\"PresenceA\" IS NOT NULL AND r.\"CanonicalA\" = @aParam)",
            StringComparison.Ordinal
        );

        firstPredicateIndex.Should().BeGreaterThan(-1);
        secondPredicateIndex.Should().BeGreaterThan(firstPredicateIndex);
        thirdPredicateIndex.Should().BeGreaterThan(secondPredicateIndex);

        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("mParam", "zParam", "aParam", "offset", "limit");
        plan.PageParametersInOrder.Select(parameter => parameter.Role)
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
    public void It_should_sort_predicates_by_operator_name_after_column_rewrite()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("Score"),
                        QueryComparisonOperator.Like,
                        "scorePattern"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("Score"),
                        QueryComparisonOperator.GreaterThan,
                        "minimumScore"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("Score"),
                        QueryComparisonOperator.Equal,
                        "exactScore"
                    ),
                ],
                []
            )
        );

        var equalityPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "r.\"Score\" = @exactScore",
            StringComparison.Ordinal
        );
        var greaterThanPredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "r.\"Score\" > @minimumScore",
            StringComparison.Ordinal
        );
        var likePredicateIndex = plan.PageDocumentIdSql.IndexOf(
            "r.\"Score\" LIKE @scorePattern",
            StringComparison.Ordinal
        );

        equalityPredicateIndex.Should().BeGreaterThan(-1);
        greaterThanPredicateIndex.Should().BeGreaterThan(equalityPredicateIndex);
        likePredicateIndex.Should().BeGreaterThan(greaterThanPredicateIndex);
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("exactScore", "minimumScore", "scorePattern", "offset", "limit");
    }

    [Test]
    public void It_should_fail_fast_for_duplicate_semantic_predicates_after_unified_alias_rewrite()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("AliasAlpha"),
                            QueryComparisonOperator.Equal,
                            "zParam"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("AliasZulu"),
                            QueryComparisonOperator.Equal,
                            "aParam"
                        ),
                    ],
                    [
                        CreateUnifiedAliasMapping(
                            new DbColumnName("AliasAlpha"),
                            new DbColumnName("CanonicalStudentUniqueId"),
                            new DbColumnName("Student_DocumentId")
                        ),
                        CreateUnifiedAliasMapping(
                            new DbColumnName("AliasZulu"),
                            new DbColumnName("CanonicalStudentUniqueId"),
                            new DbColumnName("Student_DocumentId")
                        ),
                    ]
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Duplicate predicate after unified alias rewrite for semantic key (presenceColumn='Student_DocumentId', canonicalColumn='CanonicalStudentUniqueId', operator='Equal'). Colliding original columns: ['AliasAlpha', 'AliasZulu']. Colliding parameter names: ['aParam', 'zParam']."
            );
    }

    [Test]
    public void It_should_allow_shared_canonical_columns_when_presence_gates_differ()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("StudentAlias"),
                        QueryComparisonOperator.Equal,
                        "studentUniqueId"
                    ),
                    new QueryValuePredicate(
                        new DbColumnName("ContactAlias"),
                        QueryComparisonOperator.Equal,
                        "contactUniqueId"
                    ),
                ],
                [
                    CreateUnifiedAliasMapping(
                        new DbColumnName("StudentAlias"),
                        new DbColumnName("PersonUniqueId_Unified"),
                        new DbColumnName("Student_DocumentId")
                    ),
                    CreateUnifiedAliasMapping(
                        new DbColumnName("ContactAlias"),
                        new DbColumnName("PersonUniqueId_Unified"),
                        new DbColumnName("Contact_DocumentId")
                    ),
                ]
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain(
                "r.\"Contact_DocumentId\" IS NOT NULL AND r.\"PersonUniqueId_Unified\" = @contactUniqueId"
            );
        plan.PageDocumentIdSql.Should()
            .Contain(
                "r.\"Student_DocumentId\" IS NOT NULL AND r.\"PersonUniqueId_Unified\" = @studentUniqueId"
            );
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("contactUniqueId", "studentUniqueId", "offset", "limit");
    }

    [Test]
    public void It_should_report_duplicate_ungated_predicates_with_none_presence_key()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "firstSchoolId"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "secondSchoolId"
                        ),
                    ],
                    []
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Duplicate predicate after unified alias rewrite for semantic key (presenceColumn='<none>', canonicalColumn='SchoolId', operator='Equal'). Colliding original columns: ['SchoolId', 'SchoolId']. Colliding parameter names: ['firstSchoolId', 'secondSchoolId']."
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
                    QueryComparisonOperator.Equal,
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
        second.PageParametersInOrder.Should().Equal(first.PageParametersInOrder);
        second.TotalCountParametersInOrder.Should().NotBeNull();
        first.TotalCountParametersInOrder.Should().NotBeNull();
        second.TotalCountParametersInOrder!.Value.Should().Equal(first.TotalCountParametersInOrder!.Value);
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
                        QueryComparisonOperator.Equal,
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
                        QueryComparisonOperator.Equal,
                        "schoolYear"
                    ),
                ],
                unifiedAliasMappings,
                includeTotalCountSql: true
            )
        );

        second.PageDocumentIdSql.Should().Be(first.PageDocumentIdSql);
        second.TotalCountSql.Should().Be(first.TotalCountSql);
        second.PageParametersInOrder.Should().Equal(first.PageParametersInOrder);
        second.TotalCountParametersInOrder.Should().NotBeNull();
        first.TotalCountParametersInOrder.Should().NotBeNull();
        second.TotalCountParametersInOrder!.Value.Should().Equal(first.TotalCountParametersInOrder!.Value);
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
    public void It_should_fail_with_a_deterministic_duplicate_filter_parameter_name_error_across_predicate_order_permutations()
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
                            "SchoolId"
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
                            "SchoolId"
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
        secondException.ParamName.Should().Be("Predicates");
        secondException
            .Message.Should()
            .Contain(
                "Duplicate filter parameter names are not allowed (case-insensitive). Colliding names: [['SchoolId', 'schoolId']]."
            );
    }

    [Test]
    public void It_should_report_duplicate_filter_parameter_name_groups_in_stable_order()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [
                        new QueryValuePredicate(
                            new DbColumnName("SchoolYear"),
                            QueryComparisonOperator.Equal,
                            "beta"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("CalendarYear"),
                            QueryComparisonOperator.Equal,
                            "Alpha"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("SchoolId"),
                            QueryComparisonOperator.Equal,
                            "alpha"
                        ),
                        new QueryValuePredicate(
                            new DbColumnName("LocalEducationAgencyId"),
                            QueryComparisonOperator.Equal,
                            "Beta"
                        ),
                    ],
                    []
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("Predicates")
            .WithMessage(
                "Duplicate filter parameter names are not allowed (case-insensitive). Colliding names: [['Alpha', 'alpha'], ['Beta', 'beta']]. Rename filter parameters so each name is unique.*"
            );
    }

    [Test]
    [TestCase(QueryComparisonOperator.NotEqual, "<>")]
    [TestCase(QueryComparisonOperator.LessThan, "<")]
    [TestCase(QueryComparisonOperator.LessThanOrEqual, "<=")]
    [TestCase(QueryComparisonOperator.GreaterThan, ">")]
    [TestCase(QueryComparisonOperator.GreaterThanOrEqual, ">=")]
    [TestCase(QueryComparisonOperator.Like, "LIKE")]
    public void It_should_emit_future_operator_sql_when_called_directly_by_lower_level_compiler(
        QueryComparisonOperator futureOperator,
        string expectedSqlOperator
    )
    {
        // Direct compiler coverage only; runtime planning rejects non-equality
        // operators until a future query-syntax story enables them end to end.
        var plan = _compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("NameOfInstitution"),
                        futureOperator,
                        "nameOfInstitution"
                    ),
                ],
                []
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain($"r.\"NameOfInstitution\" {expectedSqlOperator} @nameOfInstitution");
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
    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Document\" doc", "doc.\"DocumentUuid\" = @id")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document] doc", "doc.[DocumentUuid] = @id")]
    public void It_should_join_document_only_when_document_uuid_predicates_are_present(
        SqlDialect dialect,
        string expectedJoinFragment,
        string expectedPredicateFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new QueryPredicateTarget.DocumentUuid(),
                        QueryComparisonOperator.Equal,
                        "id"
                    ),
                ],
                [],
                includeTotalCountSql: true
            )
        );

        plan.PageDocumentIdSql.Should().Contain($"INNER JOIN {expectedJoinFragment} ON");
        plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        plan.TotalCountSql.Should().Contain($"INNER JOIN {expectedJoinFragment} ON");
        plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
    }

    [Test]
    public void It_should_apply_explicit_bin2_collation_for_mssql_string_equality_predicates()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("NameOfInstitution"),
                        QueryComparisonOperator.Equal,
                        "nameOfInstitution",
                        ScalarKind.String
                    ),
                ],
                []
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain("r.[NameOfInstitution] COLLATE Latin1_General_100_BIN2 = @nameOfInstitution");
    }

    [TestCase("Namespace", ScalarKind.String)]
    [TestCase("CodeValue", ScalarKind.String)]
    [TestCase("ShortDescription", ScalarKind.String)]
    [TestCase("Description", ScalarKind.String)]
    [TestCase("EffectiveBeginDate", ScalarKind.Date)]
    [TestCase("EffectiveEndDate", ScalarKind.Date)]
    public void It_should_target_descriptor_query_fields_against_the_shared_descriptor_table(
        string columnName,
        ScalarKind scalarKind
    )
    {
        var plan = _compiler.Compile(
            CreateDescriptorSpec([
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    "resourceKeyId"
                ),
                new QueryValuePredicate(
                    new QueryPredicateTarget.DescriptorColumn(new DbColumnName(columnName)),
                    QueryComparisonOperator.Equal,
                    "field",
                    scalarKind
                ),
            ])
        );

        plan.PageDocumentIdSql.Should().Contain("FROM \"dms\".\"Document\" r");
        plan.PageDocumentIdSql.Should()
            .Contain("INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"");
        plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
        plan.PageDocumentIdSql.Should().Contain($"d.\"{columnName}\" = @field");
        plan.PageDocumentIdSql.Should().NotContain("SchoolTypeDescriptor");
    }

    [Test]
    public void It_should_not_join_the_shared_descriptor_table_when_descriptor_page_filters_only_target_document_columns()
    {
        var plan = _compiler.Compile(
            CreateDescriptorSpec([
                new QueryValuePredicate(
                    new DbColumnName("DocumentUuid"),
                    QueryComparisonOperator.Equal,
                    "id"
                ),
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    "resourceKeyId"
                ),
            ])
        );

        plan.PageDocumentIdSql.Should().Contain("FROM \"dms\".\"Document\" r");
        plan.PageDocumentIdSql.Should().Contain("r.\"DocumentUuid\" = @id");
        plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
        plan.PageDocumentIdSql.Should().NotContain("\"dms\".\"Descriptor\"");
    }

    [Test]
    public void It_should_apply_binary_string_equality_to_mssql_descriptor_string_predicates()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateDescriptorSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("ResourceKeyId"),
                        QueryComparisonOperator.Equal,
                        "resourceKeyId"
                    ),
                    new QueryValuePredicate(
                        new QueryPredicateTarget.DescriptorColumn(new DbColumnName("Namespace")),
                        QueryComparisonOperator.Equal,
                        "namespace",
                        ScalarKind.String
                    ),
                ],
                includeTotalCountSql: true
            )
        );

        plan.PageDocumentIdSql.Should().Contain("d.[Namespace] COLLATE Latin1_General_100_BIN2 = @namespace");
        plan.TotalCountSql.Should().Contain("d.[Namespace] COLLATE Latin1_General_100_BIN2 = @namespace");
    }

    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Document\" r")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document] r")]
    public void It_should_emit_descriptor_total_count_sql_without_optional_joins_when_only_resource_key_discrimination_is_required(
        SqlDialect dialect,
        string expectedDocumentFromFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateDescriptorSpec(
                [
                    new QueryValuePredicate(
                        new DbColumnName("ResourceKeyId"),
                        QueryComparisonOperator.Equal,
                        "resourceKeyId"
                    ),
                ],
                includeTotalCountSql: true
            )
        );

        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        plan.TotalCountSql.Should().Contain("ResourceKeyId");
        plan.TotalCountSql.Should().NotContain("Descriptor");
        plan.TotalCountSql.Should().NotContain("doc.");
        plan.TotalCountSql.Should().NotContain("@offset");
        plan.TotalCountSql.Should().NotContain("@limit");
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("resourceKeyId");
    }

    [Test]
    public void It_should_emit_pgsql_paging_clause_with_limit_offset()
    {
        var plan = _compiler.Compile(CreateSpec([], []));

        plan.PageDocumentIdSql.Should().Contain("LIMIT @limit OFFSET @offset");
        plan.PageDocumentIdSql.Should().NotContain("OFFSET @offset LIMIT @limit");
        plan.PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(QuerySqlParameterRole.Offset, QuerySqlParameterRole.Limit);
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("offset", "limit");
        plan.TotalCountParametersInOrder.Should().BeNull();
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
        plan.TotalCountParametersInOrder.Should().BeNull();
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
        plan.PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .Equal(QuerySqlParameterRole.Filter, QuerySqlParameterRole.Offset, QuerySqlParameterRole.Limit);
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolId", "offset", "limit");
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.Role)
            .Should()
            .Equal(QuerySqlParameterRole.Filter);
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolId");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "\"auth\".\"EducationOrganizationIdToEducationOrganizationId\"",
        "r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\"",
        "WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "[auth].[EducationOrganizationIdToEducationOrganizationId]",
        "r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId]",
        "WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)"
    )]
    public void It_should_emit_normal_edorg_authorization_sql_for_page_and_total_count_queries(
        SqlDialect dialect,
        string expectedAuthTableFragment,
        string expectedSubjectFragment,
        string expectedClaimFilterFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedAuthTableFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedSubjectFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedClaimFilterFragment);
        plan.TotalCountSql.Should().Contain(expectedAuthTableFragment);
        plan.TotalCountSql.Should().Contain(expectedSubjectFragment);
        plan.TotalCountSql.Should().Contain(expectedClaimFilterFragment);
        var expectedPageParameterNames = dialect switch
        {
            SqlDialect.Mssql => new[] { "ClaimEducationOrganizationIds_0", "offset", "limit" },
            _ => new[] { "ClaimEducationOrganizationIds", "offset", "limit" },
        };

        var expectedTotalCountParameterNames = dialect switch
        {
            SqlDialect.Mssql => new[] { "ClaimEducationOrganizationIds_0" },
            _ => new[] { "ClaimEducationOrganizationIds" },
        };

        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal(expectedPageParameterNames);
        plan.PageParametersInOrder[0]
            .Binding.Kind.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Mssql => QuerySqlParameterBindingKind.Scalar,
                    _ => QuerySqlParameterBindingKind.PgsqlArray,
                }
            );
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal(expectedTotalCountParameterNames);
        plan.TotalCountParametersInOrder!.Value[0]
            .Binding.Kind.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Mssql => QuerySqlParameterBindingKind.Scalar,
                    _ => QuerySqlParameterBindingKind.PgsqlArray,
                }
            );
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\"",
        "WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[SchoolId] IN (@ClaimEducationOrganizationIds_0) OR r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId]",
        "WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_emit_direct_claim_match_or_normal_hierarchy_when_authorization_strategy_allows_it(
        SqlDialect dialect,
        string expectedDirectMatchFragment,
        string expectedHierarchyFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        true,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        var expectedDirectAndHierarchyFragment = dialect switch
        {
            SqlDialect.Mssql =>
                "(r.[SchoolId] IN (@ClaimEducationOrganizationIds_0) OR r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId] FROM [auth].[EducationOrganizationIdToEducationOrganizationId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)))))",
            _ =>
                "(r.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t0 WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)))))",
        };

        plan.PageDocumentIdSql.Should().Contain(expectedDirectAndHierarchyFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedDirectMatchFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedHierarchyFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedDirectAndHierarchyFragment);
        plan.TotalCountSql.Should().Contain(expectedDirectMatchFragment);
        plan.TotalCountSql.Should().Contain(expectedHierarchyFragment);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"SchoolId\" IN (SELECT t0.\"SourceEducationOrganizationId\"",
        "WHERE t0.\"TargetEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[SchoolId] IN (SELECT t0.[SourceEducationOrganizationId]",
        "WHERE t0.[TargetEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)"
    )]
    public void It_should_emit_inverted_edorg_authorization_sql_for_page_and_total_count_queries(
        SqlDialect dialect,
        string expectedSubjectFragment,
        string expectedClaimFilterFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedSubjectFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedClaimFilterFragment);
        plan.TotalCountSql.Should().Contain(expectedSubjectFragment);
        plan.TotalCountSql.Should().Contain(expectedClaimFilterFragment);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"SchoolId\" IN (SELECT t0.\"SourceEducationOrganizationId\"",
        "WHERE t0.\"TargetEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[SchoolId] IN (@ClaimEducationOrganizationIds_0) OR r.[SchoolId] IN (SELECT t0.[SourceEducationOrganizationId]",
        "WHERE t0.[TargetEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)"
    )]
    public void It_should_emit_direct_claim_match_or_inverted_hierarchy_when_authorization_strategy_allows_it(
        SqlDialect dialect,
        string expectedDirectMatchFragment,
        string expectedHierarchyFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                        true,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedDirectMatchFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedHierarchyFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedDirectMatchFragment);
        plan.TotalCountSql.Should().Contain(expectedHierarchyFragment);
    }

    [Test]
    public void It_should_use_edorg_subject_auth_object_metadata_instead_of_configured_strategy_name_for_people_involved_strategies()
    {
        var normalAuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );

        var plan = _compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Pgsql,
                    CreateAuthorizationStrategyPreservingSubjectAuthObjects(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
                        CreateAuthorizationSubject("SchoolId", authObject: normalAuthObject)
                    )
                )
            )
        );

        const string ExpectedNormalHierarchyFragment =
            "r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t0 WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))";
        const string UnexpectedInvertedHierarchyFragment =
            "r.\"SchoolId\" IN (SELECT t0.\"SourceEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t0 WHERE t0.\"TargetEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))";

        plan.PageDocumentIdSql.Should().Contain(ExpectedNormalHierarchyFragment);
        plan.PageDocumentIdSql.Should().NotContain(UnexpectedInvertedHierarchyFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedNormalHierarchyFragment);
        plan.TotalCountSql.Should().NotContain(UnexpectedInvertedHierarchyFragment);
    }

    [Test]
    public void It_should_use_custom_edorg_auth_object_table_and_columns_for_hierarchy_sql()
    {
        var authObject = new RelationshipAuthorizationAuthObject(
            new DbTableName(new DbSchemaName("auth"), "CustomEducationOrganizationAuthorization"),
            new DbColumnName("ResourceEducationOrganizationId"),
            new DbColumnName("ClaimEducationOrganizationId")
        );

        var plan = _compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Pgsql,
                    CreateAuthorizationStrategyPreservingSubjectAuthObjects(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject("SchoolId", authObject: authObject)
                    )
                )
            )
        );

        const string ExpectedCustomHierarchyFragment =
            "r.\"SchoolId\" IN (SELECT t0.\"ResourceEducationOrganizationId\" FROM \"auth\".\"CustomEducationOrganizationAuthorization\" t0 WHERE t0.\"ClaimEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))";

        plan.PageDocumentIdSql.Should().Contain(ExpectedCustomHierarchyFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedCustomHierarchyFragment);
    }

    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Student,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId",
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t0 WHERE t0.\"Student_DocumentId\" IN (SELECT t1.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t1 WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        RelationshipAuthorizationPersonKind.Contact,
        "Contact_DocumentId",
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t0 WHERE t0.\"Contact_DocumentId\" IN (SELECT t1.\"Contact_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToContactDocumentId\" t1 WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Pgsql,
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        RelationshipAuthorizationPersonKind.Staff,
        "Staff_DocumentId",
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t0 WHERE t0.\"Staff_DocumentId\" IN (SELECT t1.\"Staff_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStaffDocumentId\" t1 WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Student,
        RelationshipAuthorizationPersonKind.Student,
        "Student_DocumentId",
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[StudentSchoolAssociation] t0 WHERE t0.[Student_DocumentId] IN (SELECT t1.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t1 WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Contact,
        RelationshipAuthorizationPersonKind.Contact,
        "Contact_DocumentId",
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[StudentSchoolAssociation] t0 WHERE t0.[Contact_DocumentId] IN (SELECT t1.[Contact_DocumentId] FROM [auth].[EducationOrganizationIdToContactDocumentId] t1 WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        RelationshipAuthorizationPersonAuthViewKind.Staff,
        RelationshipAuthorizationPersonKind.Staff,
        "Staff_DocumentId",
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[StudentSchoolAssociation] t0 WHERE t0.[Staff_DocumentId] IN (SELECT t1.[Staff_DocumentId] FROM [auth].[EducationOrganizationIdToStaffDocumentId] t1 WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_emit_direct_root_column_people_authorization_sql_for_page_and_total_count_queries(
        SqlDialect dialect,
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        string rootPersonDocumentIdColumnName,
        string expectedPredicateFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            authViewKind,
                            personKind,
                            new DbColumnName(rootPersonDocumentIdColumnName),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
        plan.PageDocumentIdSql.Should().NotContain("UniqueId");
        plan.PageDocumentIdSql.Should().NotContain("USI");
        plan.PageDocumentIdSql.Should().NotContain("JOIN \"auth\"");
        plan.PageDocumentIdSql.Should().NotContain("JOIN [auth]");
    }

    [Test]
    public void It_should_close_direct_people_authorization_membership_and_root_subqueries()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Pgsql,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        const string ExpectedPeoplePredicate =
            "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t0 WHERE t0.\"Student_DocumentId\" IN (SELECT t1.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t1 WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)))))";

        plan.PageDocumentIdSql.Should().Contain(ExpectedPeoplePredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedPeoplePredicate);
    }

    [Test]
    public void It_should_emit_postgresql_array_claim_parameter_sql_for_people_authorization_once_per_query()
    {
        var plan = _compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Pgsql,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        const string ExpectedPeopleClaimFilter =
            "WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)";

        plan.PageDocumentIdSql.Should().Contain(ExpectedPeopleClaimFilter);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedPeopleClaimFilter);
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds", "offset", "limit");
        plan.PageParametersInOrder[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.PgsqlArray);
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds");
        plan.TotalCountParametersInOrder!.Value[0]
            .Binding.Kind.Should()
            .Be(QuerySqlParameterBindingKind.PgsqlArray);
    }

    [Test]
    public void It_should_emit_sql_server_scalar_claim_parameters_for_people_authorization_below_threshold()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Mssql,
                    CreateClaimEducationOrganizationIds(1999),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain(
                "WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1"
            );
        plan.PageDocumentIdSql.Should().Contain("@ClaimEducationOrganizationIds_1998");
        plan.PageDocumentIdSql.Should().NotContain("SELECT [Id] FROM @ClaimEducationOrganizationIds");
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should()
            .Contain(
                "WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1"
            );
        plan.TotalCountSql.Should().Contain("@ClaimEducationOrganizationIds_1998");
        plan.TotalCountSql.Should().NotContain("SELECT [Id] FROM @ClaimEducationOrganizationIds");
        plan.PageParametersInOrder.Should().HaveCount(2001);
        plan.PageParametersInOrder[0].ParameterName.Should().Be("ClaimEducationOrganizationIds_0");
        plan.PageParametersInOrder[1998].ParameterName.Should().Be("ClaimEducationOrganizationIds_1998");
        plan.PageParametersInOrder[1999].ParameterName.Should().Be("offset");
        plan.PageParametersInOrder[2000].ParameterName.Should().Be("limit");
        plan.PageParametersInOrder.Take(1999)
            .Should()
            .OnlyContain(parameter => parameter.Binding.Kind == QuerySqlParameterBindingKind.Scalar);
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Should().HaveCount(1999);
        plan.TotalCountParametersInOrder!.Value[0]
            .ParameterName.Should()
            .Be("ClaimEducationOrganizationIds_0");
        plan.TotalCountParametersInOrder!.Value[^1]
            .ParameterName.Should()
            .Be("ClaimEducationOrganizationIds_1998");
        plan.TotalCountParametersInOrder!.Value.Should()
            .OnlyContain(parameter => parameter.Binding.Kind == QuerySqlParameterBindingKind.Scalar);
    }

    [Test]
    public void It_should_emit_sql_server_structured_claim_parameter_sql_for_people_authorization_at_threshold()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Mssql,
                    CreateClaimEducationOrganizationIds(2000),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        const string ExpectedPeopleClaimFilter =
            "WHERE t1.[SourceEducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)";

        plan.PageDocumentIdSql.Should().Contain(ExpectedPeopleClaimFilter);
        plan.PageDocumentIdSql.Should().NotContain("@ClaimEducationOrganizationIds_0");
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedPeopleClaimFilter);
        plan.TotalCountSql.Should().NotContain("@ClaimEducationOrganizationIds_0");
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds", "offset", "limit");
        plan.PageParametersInOrder[0]
            .Binding.Should()
            .Be(QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id"));
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds");
        plan.TotalCountParametersInOrder!.Value[0]
            .Binding.Should()
            .Be(QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id"));
    }

    [Test]
    public void It_should_dedupe_people_claim_ids_before_sql_server_threshold_selection()
    {
        List<long> claimEducationOrganizationIds = [.. CreateClaimEducationOrganizationIds(1999)];
        claimEducationOrganizationIds.AddRange(CreateClaimEducationOrganizationIds(1999).Reverse());
        claimEducationOrganizationIds.Add(1999L);

        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Mssql,
                    claimEducationOrganizationIds,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain("@ClaimEducationOrganizationIds_1998");
        plan.PageDocumentIdSql.Should().NotContain("@ClaimEducationOrganizationIds_1999");
        plan.PageDocumentIdSql.Should().NotContain("SELECT [Id] FROM @ClaimEducationOrganizationIds");
        plan.PageParametersInOrder.Should().HaveCount(2001);
        plan.PageParametersInOrder[0].ParameterName.Should().Be("ClaimEducationOrganizationIds_0");
        plan.PageParametersInOrder[1998].ParameterName.Should().Be("ClaimEducationOrganizationIds_1998");
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Should().HaveCount(1999);
        plan.TotalCountParametersInOrder!.Value[^1]
            .ParameterName.Should()
            .Be("ClaimEducationOrganizationIds_1998");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"Student\" t0 WHERE t0.\"DocumentId\" IN (SELECT t1.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t1 WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[Student] t0 WHERE t0.[DocumentId] IN (SELECT t1.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t1 WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_emit_student_self_authorization_sql_from_root_document_id_without_unique_id_or_usi(
        SqlDialect dialect,
        string expectedPredicateFragment
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                            rootTable
                        )
                    )
                ),
                rootTable: rootTable
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
        plan.PageDocumentIdSql.Should().NotContain("UniqueId");
        plan.PageDocumentIdSql.Should().NotContain("USI");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"CourseTranscript\" t0 JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = t0.\"StudentAcademicRecord_DocumentId\" WHERE t1.\"Student_DocumentId\" IN (SELECT t2.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t2 WHERE t2.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[CourseTranscript] t0 JOIN [edfi].[StudentAcademicRecord] t1 ON t1.[DocumentId] = t0.[StudentAcademicRecord_DocumentId] WHERE t1.[Student_DocumentId] IN (SELECT t2.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t2 WHERE t2.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_emit_transitive_student_authorization_sql_with_ordered_path_joins_for_page_and_total_count_queries(
        SqlDialect dialect,
        string expectedPredicateFragment
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        CreateTransitivePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            rootTable,
                            [
                                new ColumnPathStep(
                                    rootTable,
                                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                                    studentAcademicRecordTable,
                                    new DbColumnName("DocumentId")
                                ),
                                new ColumnPathStep(
                                    studentAcademicRecordTable,
                                    new DbColumnName("Student_DocumentId"),
                                    studentTable,
                                    new DbColumnName("DocumentId")
                                ),
                            ]
                        )
                    )
                ),
                rootTable: rootTable
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
        plan.PageDocumentIdSql.Should().NotContain("UniqueId");
        plan.PageDocumentIdSql.Should().NotContain("USI");
        plan.PageDocumentIdSql.Should().NotContain("JOIN \"edfi\".\"Student\"");
        plan.PageDocumentIdSql.Should().NotContain("JOIN [edfi].[Student]");
    }

    [Test]
    public void It_should_close_transitive_people_authorization_path_and_membership_subqueries()
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var plan = _compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Pgsql,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                        CreateTransitivePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            rootTable,
                            [
                                new ColumnPathStep(
                                    rootTable,
                                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                                    studentAcademicRecordTable,
                                    new DbColumnName("DocumentId")
                                ),
                                new ColumnPathStep(
                                    studentAcademicRecordTable,
                                    new DbColumnName("Student_DocumentId"),
                                    studentTable,
                                    new DbColumnName("DocumentId")
                                ),
                            ]
                        )
                    )
                ),
                rootTable: rootTable
            )
        );

        const string ExpectedPeoplePredicate =
            "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"CourseTranscript\" t0 JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = t0.\"StudentAcademicRecord_DocumentId\" WHERE t1.\"Student_DocumentId\" IN (SELECT t2.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t2 WHERE t2.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)))))";

        plan.PageDocumentIdSql.Should().Contain(ExpectedPeoplePredicate);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedPeoplePredicate);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"CourseTranscript\" t0 JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = t0.\"StudentAcademicRecord_DocumentId\" WHERE t1.\"Contact_DocumentId\" IN (SELECT t2.\"Contact_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToContactDocumentId\" t2 WHERE t2.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[CourseTranscript] t0 JOIN [edfi].[StudentAcademicRecord] t1 ON t1.[DocumentId] = t0.[StudentAcademicRecord_DocumentId] WHERE t1.[Contact_DocumentId] IN (SELECT t2.[Contact_DocumentId] FROM [auth].[EducationOrganizationIdToContactDocumentId] t2 WHERE t2.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_emit_transitive_contact_authorization_sql_with_ordered_path_joins_for_page_and_total_count_queries(
        SqlDialect dialect,
        string expectedPredicateFragment
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "CourseTranscript");
        var studentAcademicRecordTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecord");
        var contactTable = new DbTableName(new DbSchemaName("edfi"), "Contact");
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreateTransitivePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Contact,
                            RelationshipAuthorizationPersonKind.Contact,
                            rootTable,
                            [
                                new ColumnPathStep(
                                    rootTable,
                                    new DbColumnName("StudentAcademicRecord_DocumentId"),
                                    studentAcademicRecordTable,
                                    new DbColumnName("DocumentId")
                                ),
                                new ColumnPathStep(
                                    studentAcademicRecordTable,
                                    new DbColumnName("Contact_DocumentId"),
                                    contactTable,
                                    new DbColumnName("DocumentId")
                                ),
                            ]
                        )
                    )
                ),
                rootTable: rootTable
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
        plan.PageDocumentIdSql.Should().NotContain("UniqueId");
        plan.PageDocumentIdSql.Should().NotContain("USI");
        plan.PageDocumentIdSql.Should().NotContain("JOIN \"edfi\".\"Contact\"");
        plan.PageDocumentIdSql.Should().NotContain("JOIN [edfi].[Contact]");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t0 WHERE t0.\"Student_DocumentId\" IN (SELECT t1.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentIdThroughResponsibility\" t1 WHERE t1.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[DocumentId] FROM [edfi].[StudentSchoolAssociation] t0 WHERE t0.[Student_DocumentId] IN (SELECT t1.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentIdThroughResponsibility] t1 WHERE t1.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_emit_students_only_through_responsibility_authorization_sql_for_page_and_total_count_queries(
        SqlDialect dialect,
        string expectedPredicateFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
        plan.PageDocumentIdSql.Should().NotContain("UniqueId");
        plan.PageDocumentIdSql.Should().NotContain("USI");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t0 WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)) AND r.\"DocumentId\" IN (SELECT t1.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t1 WHERE t1.\"Student_DocumentId\" IN (SELECT t2.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t2 WHERE t2.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId] FROM [auth].[EducationOrganizationIdToEducationOrganizationId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)) AND r.[DocumentId] IN (SELECT t1.[DocumentId] FROM [edfi].[StudentSchoolAssociation] t1 WHERE t1.[Student_DocumentId] IN (SELECT t2.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t2 WHERE t2.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_and_edorg_and_people_subjects_inside_one_mixed_relationship_strategy(
        SqlDialect dialect,
        string expectedAuthorizationFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                        CreateAuthorizationSubject("SchoolId"),
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedAuthorizationFragment);
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedAuthorizationFragment);
        AssertFragmentAppearsBefore(plan.PageDocumentIdSql, expectedAuthorizationFragment, "ORDER BY");
        plan.TotalCountSql.Should().NotContain("@offset");
        plan.TotalCountSql.Should().NotContain("@limit");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "FROM \"edfi\".\"StudentSchoolAssociation\"",
        "r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t0 WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))",
        "r.\"DocumentId\" IN (SELECT t1.\"DocumentId\" FROM \"edfi\".\"StudentSchoolAssociation\" t1 WHERE t1.\"Student_DocumentId\" IN (SELECT t2.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t2 WHERE t2.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds))"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "FROM [edfi].[StudentSchoolAssociation]",
        "r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId] FROM [auth].[EducationOrganizationIdToEducationOrganizationId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))",
        "r.[DocumentId] IN (SELECT t1.[DocumentId] FROM [edfi].[StudentSchoolAssociation] t1 WHERE t1.[Student_DocumentId] IN (SELECT t2.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t2 WHERE t2.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0))"
    )]
    public void It_should_or_edorg_only_and_people_involved_strategies_without_outer_authorization_joins(
        SqlDialect dialect,
        string expectedRootTableFromFragment,
        string expectedEdOrgPredicateFragment,
        string expectedPeoplePredicateFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject("SchoolId")
                    ),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                        CreatePersonAuthorizationSubject(
                            RelationshipAuthorizationPersonAuthViewKind.Student,
                            RelationshipAuthorizationPersonKind.Student,
                            new DbColumnName("Student_DocumentId"),
                            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                        )
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(expectedEdOrgPredicateFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedPeoplePredicateFragment);
        plan.PageDocumentIdSql.Should().Contain(" OR ");
        plan.PageDocumentIdSql.Should().NotContain("JOIN \"auth\"");
        plan.PageDocumentIdSql.Should().NotContain("JOIN [auth]");
        CountOrdinalOccurrences(plan.PageDocumentIdSql, expectedRootTableFromFragment).Should().Be(2);

        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(expectedEdOrgPredicateFragment);
        plan.TotalCountSql.Should().Contain(expectedPeoplePredicateFragment);
        plan.TotalCountSql.Should().Contain(" OR ");
        CountOrdinalOccurrences(plan.TotalCountSql!, expectedRootTableFromFragment).Should().Be(2);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_or_multiple_authorization_strategies(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject("SchoolId")
                    ),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                        CreateAuthorizationSubject("LocalEducationAgencyId")
                    )
                )
            )
        );

        var (expectedFirstStrategy, expectedSecondStrategy) = dialect switch
        {
            SqlDialect.Mssql => (
                "(r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId] FROM [auth].[EducationOrganizationIdToEducationOrganizationId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)))",
                "(r.[LocalEducationAgencyId] IN (SELECT t1.[SourceEducationOrganizationId] FROM [auth].[EducationOrganizationIdToEducationOrganizationId] t1 WHERE t1.[TargetEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0)))"
            ),
            _ => (
                "(r.\"SchoolId\" IN (SELECT t0.\"TargetEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t0 WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)))",
                "(r.\"LocalEducationAgencyId\" IN (SELECT t1.\"SourceEducationOrganizationId\" FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" t1 WHERE t1.\"TargetEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)))"
            ),
        };

        var expectedAuthorizationFragment = $"{expectedFirstStrategy} OR {expectedSecondStrategy}";

        plan.PageDocumentIdSql.Should().Contain(expectedAuthorizationFragment);
        plan.PageDocumentIdSql.Should().Contain(" OR ");
        plan.PageDocumentIdSql.Should().Contain("SchoolId");
        plan.PageDocumentIdSql.Should().Contain("LocalEducationAgencyId");
        plan.PageDocumentIdSql.Should().Contain("TargetEducationOrganizationId");
        plan.PageDocumentIdSql.Should().Contain("SourceEducationOrganizationId");
        plan.TotalCountSql.Should().Contain(" OR ");
        plan.TotalCountSql.Should().Contain(expectedAuthorizationFragment);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_and_multiple_subjects_within_one_authorization_strategy(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject("SchoolId"),
                        CreateAuthorizationSubject("LocalEducationAgencyId")
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should().Contain(" AND ");
        plan.PageDocumentIdSql.Should().Contain("SchoolId");
        plan.PageDocumentIdSql.Should().Contain("LocalEducationAgencyId");
        plan.PageDocumentIdSql.Should().Contain("t0");
        plan.PageDocumentIdSql.Should().Contain("t1");
        plan.TotalCountSql.Should().Contain(" AND ");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "\"edfi\".\"CourseTranscript\"",
        "r.\"StudentAcademicRecord_EducationOrganizationId\""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "[edfi].[CourseTranscript]",
        "r.[StudentAcademicRecord_EducationOrganizationId]"
    )]
    public void It_should_bind_reference_derived_authorization_subject_columns_to_the_root_alias(
        SqlDialect dialect,
        string expectedRootTableFragment,
        string expectedSubjectFragment
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    dialect,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject(
                            "StudentAcademicRecord_EducationOrganizationId",
                            new DbTableName(new DbSchemaName("edfi"), "CourseTranscript")
                        )
                    )
                ),
                rootTable: new DbTableName(new DbSchemaName("edfi"), "CourseTranscript")
            )
        );

        plan.PageDocumentIdSql.Should().Contain($"FROM {expectedRootTableFragment} r");
        plan.PageDocumentIdSql.Should().Contain(expectedSubjectFragment);
        plan.PageDocumentIdSql.Should().NotContain("StudentAcademicRecord\" t");
    }

    [Test]
    public void It_should_reject_authorization_subjects_from_a_different_root_table()
    {
        var subjectTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var queryRootTable = new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation");

        var act = () =>
            _compiler.Compile(
                CreateSpec(
                    [],
                    [],
                    includeTotalCountSql: true,
                    authorization: CreateAuthorizationSpec(
                        SqlDialect.Pgsql,
                        CreateAuthorizationStrategy(
                            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                            CreateAuthorizationSubject("SchoolId", subjectTable)
                        )
                    ),
                    rootTable: queryRootTable
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"Authorization subject table '{subjectTable}' does not match query root table '{queryRootTable}'. DMS-1055 query authorization currently supports only concrete root-table subjects in the page query compiler."
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_treat_an_empty_authorization_strategy_list_as_no_authorization(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            dialect,
            [255901001L, 255901002L],
            RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
        );
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: new PageDocumentIdAuthorizationSpec(
                    [],
                    ClaimEducationOrganizationIdParameterization: claimParameterization
                )
            )
        );

        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("offset", "limit");
        plan.PageDocumentIdSql.Should().NotContain("ClaimEducationOrganizationIds");
        plan.TotalCountSql.Should().NotContain("ClaimEducationOrganizationIds");
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_should_reject_authorization_strategy_without_subjects()
    {
        var authorization = CreateAuthorizationSpec(
            [255901001L],
            new PageDocumentIdAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                []
            )
        );

        Action act = () => _compiler.Compile(CreateSpec([], [], authorization: authorization));

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Authorization strategy 'RelationshipsWithEdOrgsOnly' requires at least one authorization subject.*"
            )
            .WithParameterName("authorization");
    }

    [Test]
    public void It_should_validate_claim_parameterization_when_authorization_strategies_are_present()
    {
        var invalidClaimParameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
            "claim-education-organization-ids",
            [255901001L],
            ["claim-education-organization-ids"]
        );
        var authorization = new PageDocumentIdAuthorizationSpec(
            [
                CreateAuthorizationStrategy(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    CreateAuthorizationSubject("SchoolId")
                ),
            ],
            invalidClaimParameterization
        );

        Action act = () => _compiler.Compile(CreateSpec([], [], authorization: authorization));

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName(
                $"{nameof(PageDocumentIdAuthorizationSpec.ClaimEducationOrganizationIdParameterization)}.{nameof(AuthorizationClaimEducationOrganizationIdParameterization.BaseParameterName)}"
            );
    }

    [Test]
    public void It_should_emit_mssql_structured_claim_parameter_sql_at_the_two_thousand_unique_id_threshold()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Mssql,
                    CreateClaimEducationOrganizationIds(2000),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain(
                "WHERE t0.[SourceEducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
        plan.TotalCountSql.Should()
            .Contain(
                "WHERE t0.[SourceEducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
        plan.PageDocumentIdSql.Should()
            .NotContain("r.[SchoolId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds) OR");
        plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds", "offset", "limit");
        plan.PageParametersInOrder[0]
            .Binding.Should()
            .Be(QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id"));
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds");
        plan.TotalCountParametersInOrder!.Value[0]
            .Binding.Should()
            .Be(QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id"));
    }

    [Test]
    public void It_should_emit_direct_claim_match_with_mssql_structured_claim_parameterization()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Mssql,
                    CreateClaimEducationOrganizationIds(2000),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        true,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        const string ExpectedDirectAndHierarchyFragment =
            "r.[SchoolId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds) OR r.[SchoolId] IN (SELECT t0.[TargetEducationOrganizationId]";

        plan.PageDocumentIdSql.Should().Contain(ExpectedDirectAndHierarchyFragment);
        plan.PageDocumentIdSql.Should()
            .Contain(
                "WHERE t0.[SourceEducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql.Should().Contain(ExpectedDirectAndHierarchyFragment);
        plan.TotalCountSql.Should()
            .Contain(
                "WHERE t0.[SourceEducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
    }

    [Test]
    public void It_should_preserve_duplicate_direct_match_strategies_as_distinct_or_branches()
    {
        static int CountOrdinalOccurrences(string value, string text) =>
            value.Split(text, StringSplitOptions.None).Length - 1;

        var plan = _compiler.Compile(
            CreateSpec(
                [],
                [],
                includeTotalCountSql: true,
                authorization: CreateAuthorizationSpec(
                    SqlDialect.Pgsql,
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        true,
                        CreateAuthorizationSubject("SchoolId")
                    ),
                    CreateAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        true,
                        CreateAuthorizationSubject("SchoolId")
                    )
                )
            )
        );

        CountOrdinalOccurrences(
                plan.PageDocumentIdSql,
                "r.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds)"
            )
            .Should()
            .Be(2);
        CountOrdinalOccurrences(
                plan.PageDocumentIdSql,
                "\"auth\".\"EducationOrganizationIdToEducationOrganizationId\""
            )
            .Should()
            .Be(2);
        plan.PageDocumentIdSql.Should().Contain(" OR ");
        plan.TotalCountSql.Should().NotBeNull();
        CountOrdinalOccurrences(plan.TotalCountSql!, "r.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds)")
            .Should()
            .Be(2);
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

    [Test]
    public void It_should_reject_offset_and_limit_parameter_names_that_are_identical()
    {
        var act = () =>
            _compiler.Compile(CreateSpec([], [], offsetParameterName: "page", limitParameterName: "page"));

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("OffsetParameterName")
            .WithMessage(
                "Paging parameter names must be distinct (case-insensitive). OffsetParameterName='page', LimitParameterName='page'. Rename either OffsetParameterName or LimitParameterName.*"
            );
    }

    [Test]
    public void It_should_reject_offset_and_limit_parameter_names_that_collide_case_insensitively()
    {
        var act = () =>
            _compiler.Compile(
                CreateSpec([], [], offsetParameterName: "OffSet", limitParameterName: "offset")
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("OffsetParameterName")
            .WithMessage(
                "Paging parameter names must be distinct (case-insensitive). OffsetParameterName='OffSet', LimitParameterName='offset'. Rename either OffsetParameterName or LimitParameterName.*"
            );
    }

    private static PageDocumentIdQuerySpec CreateSpec(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyList<KeyValuePair<DbColumnName, ColumnStorage.UnifiedAlias>> unifiedAliasMappings,
        string offsetParameterName = "offset",
        string limitParameterName = "limit",
        bool includeTotalCountSql = false,
        PageDocumentIdAuthorizationSpec? authorization = null,
        DbTableName? rootTable = null
    )
    {
        var unifiedAliasMappingsByColumn = new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>();

        foreach (var (aliasColumn, unifiedAlias) in unifiedAliasMappings)
        {
            unifiedAliasMappingsByColumn.Add(aliasColumn, unifiedAlias);
        }

        return new PageDocumentIdQuerySpec(
            RootTable: rootTable ?? new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            Predicates: predicates,
            UnifiedAliasMappingsByColumn: unifiedAliasMappingsByColumn,
            OffsetParameterName: offsetParameterName,
            LimitParameterName: limitParameterName,
            IncludeTotalCountSql: includeTotalCountSql,
            Authorization: authorization
        );
    }

    private static PageDocumentIdQuerySpec CreateDescriptorSpec(
        IReadOnlyList<QueryValuePredicate> predicates,
        string offsetParameterName = "offset",
        string limitParameterName = "limit",
        bool includeTotalCountSql = false
    )
    {
        return new PageDocumentIdQuerySpec(
            RootTable: new DbTableName(new DbSchemaName("dms"), "Document"),
            Predicates: predicates,
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            OffsetParameterName: offsetParameterName,
            LimitParameterName: limitParameterName,
            IncludeTotalCountSql: includeTotalCountSql
        );
    }

    private static PageDocumentIdAuthorizationSpec CreateAuthorizationSpec(
        SqlDialect dialect,
        params PageDocumentIdAuthorizationStrategy[] strategies
    ) => CreateAuthorizationSpec(dialect, [1L], strategies);

    private static PageDocumentIdAuthorizationSpec CreateAuthorizationSpec(
        IReadOnlyList<long> claimEducationOrganizationIds,
        params PageDocumentIdAuthorizationStrategy[] strategies
    ) => CreateAuthorizationSpec(SqlDialect.Pgsql, claimEducationOrganizationIds, strategies);

    private static PageDocumentIdAuthorizationSpec CreateAuthorizationSpec(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds,
        params PageDocumentIdAuthorizationStrategy[] strategies
    )
    {
        var parameterization =
            strategies.Length == 0
                ? null
                : AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    dialect,
                    claimEducationOrganizationIds,
                    RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
                );

        return new PageDocumentIdAuthorizationSpec(strategies, parameterization);
    }

    private static PageDocumentIdAuthorizationStrategy CreateAuthorizationStrategy(
        string strategyName,
        bool allowsDirectClaimMatch,
        params PageDocumentIdAuthorizationSubject[] subjects
    )
    {
        var direction = DefaultEdOrgAuthDirection(strategyName);
        var authObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction) with
        {
            AllowsDirectClaimMatch = allowsDirectClaimMatch,
        };
        var strategySubjects = subjects
            .Select(subject =>
                subject is PageDocumentIdAuthorizationEdOrgSubject edOrgSubject
                    ? edOrgSubject with
                    {
                        AuthObject = authObject,
                    }
                    : subject
            )
            .ToArray();

        return new PageDocumentIdAuthorizationStrategy(strategyName, strategySubjects);
    }

    private static PageDocumentIdAuthorizationStrategy CreateAuthorizationStrategy(
        string strategyName,
        params PageDocumentIdAuthorizationSubject[] subjects
    ) => CreateAuthorizationStrategy(strategyName, false, subjects);

    private static PageDocumentIdAuthorizationStrategy CreateAuthorizationStrategyPreservingSubjectAuthObjects(
        string strategyName,
        params PageDocumentIdAuthorizationSubject[] subjects
    )
    {
        return new PageDocumentIdAuthorizationStrategy(strategyName, subjects);
    }

    private static RelationshipAuthorizationHierarchyDirection DefaultEdOrgAuthDirection(
        string strategyName
    ) =>
        strategyName
            is AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
                or AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted
            ? RelationshipAuthorizationHierarchyDirection.Inverted
            : RelationshipAuthorizationHierarchyDirection.Normal;

    private static PageDocumentIdAuthorizationSubject CreateAuthorizationSubject(
        string columnName,
        DbTableName? table = null,
        RelationshipAuthorizationAuthObject? authObject = null
    )
    {
        return new PageDocumentIdAuthorizationEdOrgSubject(
            table ?? new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            new DbColumnName(columnName),
            authObject
                ?? RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    $"$.{columnName}",
                    columnName
                ),
            ]
        );
    }

    private static PageDocumentIdAuthorizationSubject CreatePersonAuthorizationSubject(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbColumnName rootBindingColumn,
        RelationshipAuthorizationPersonSubjectPathKind pathKind,
        DbTableName? rootTable = null
    )
    {
        var resolvedRootTable =
            rootTable ?? new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation");
        var documentIdColumn = new DbColumnName("DocumentId");
        IReadOnlyList<ColumnPathStep> pathSteps = pathKind switch
        {
            RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn =>
            [
                new ColumnPathStep(
                    resolvedRootTable,
                    rootBindingColumn,
                    new DbTableName(new DbSchemaName("edfi"), personKind.ToString()),
                    documentIdColumn
                ),
            ],
            RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId => [],
            _ => throw new ArgumentOutOfRangeException(nameof(pathKind), pathKind, "Unsupported path kind."),
        };

        return new PageDocumentIdAuthorizationPersonSubject(
            resolvedRootTable,
            rootBindingColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(authViewKind),
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapPersonKind(personKind),
                    $"$.{personKind.ToString().ToLowerInvariant()}Reference.{personKind.ToString().ToLowerInvariant()}UniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(pathKind, pathSteps),
                new RelationshipAuthorizationPersonStoredAnchor(resolvedRootTable, documentIdColumn),
                ProposedAnchor: null
            )
        );
    }

    private static PageDocumentIdAuthorizationSubject CreateTransitivePersonAuthorizationSubject(
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        DbTableName rootTable,
        IReadOnlyList<ColumnPathStep> pathSteps
    )
    {
        var documentIdColumn = new DbColumnName("DocumentId");
        var terminalStep = pathSteps[^1];

        return new PageDocumentIdAuthorizationPersonSubject(
            terminalStep.SourceTable,
            terminalStep.SourceColumnName,
            RelationshipAuthorizationAuthObject.CreatePerson(authViewKind),
            [
                new RelationshipAuthorizationSubjectContributor(
                    MapPersonKind(personKind),
                    $"$.{personKind.ToString().ToLowerInvariant()}Reference.{personKind.ToString().ToLowerInvariant()}UniqueId",
                    $"{personKind}UniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                    pathSteps
                ),
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, documentIdColumn),
                ProposedAnchor: null
            )
        );
    }

    private static SecurableElementKind MapPersonKind(RelationshipAuthorizationPersonKind personKind) =>
        personKind switch
        {
            RelationshipAuthorizationPersonKind.Student => SecurableElementKind.Student,
            RelationshipAuthorizationPersonKind.Contact => SecurableElementKind.Contact,
            RelationshipAuthorizationPersonKind.Staff => SecurableElementKind.Staff,
            _ => throw new ArgumentOutOfRangeException(
                nameof(personKind),
                personKind,
                "Unsupported person kind."
            ),
        };

    private static int CountOrdinalOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;

    private static void AssertFragmentAppearsBefore(string sql, string firstFragment, string secondFragment)
    {
        var firstIndex = sql.IndexOf(firstFragment, StringComparison.Ordinal);
        var secondIndex = sql.IndexOf(secondFragment, StringComparison.Ordinal);

        firstIndex.Should().BeGreaterThanOrEqualTo(0);
        secondIndex.Should().BeGreaterThanOrEqualTo(0);
        firstIndex.Should().BeLessThan(secondIndex);
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

    private static IReadOnlyList<long> CreateClaimEducationOrganizationIds(int count)
    {
        long[] claimEducationOrganizationIds = new long[count];

        for (var index = 0; index < count; index++)
        {
            claimEducationOrganizationIds[index] = index + 1L;
        }

        return claimEducationOrganizationIds;
    }
}
