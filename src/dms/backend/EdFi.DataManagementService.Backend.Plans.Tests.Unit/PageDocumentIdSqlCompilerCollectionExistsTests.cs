// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// A collection filter compiles to one correlated <c>EXISTS</c> emitted directly after the value predicates,
/// in every candidate mode and beside every authorization filter, and changes nothing else in the plan.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_PageDocumentIdSqlCompiler_with_a_collection_exists_filter(SqlDialect dialect)
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _authSchema = new("auth");
    private static readonly DbTableName _rootTable = new(_edfiSchema, "GradebookEntry");
    private static readonly DbTableName _collectionTable = new(
        _edfiSchema,
        "GradebookEntryIdentificationCode"
    );
    private static readonly DbColumnName _rootLocatorColumn = new("GradebookEntry_DocumentId");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    private static readonly QueryCollectionExistsPredicate _collectionFilter = new(
        _collectionTable,
        _rootLocatorColumn,
        [
            new QueryCollectionValuePredicate(
                new DbColumnName("SystemDescriptor_DescriptorId"),
                QueryComparisonOperator.Equal,
                "codeSystem"
            ),
            new QueryCollectionValuePredicate(
                new DbColumnName("IdentificationCode"),
                QueryComparisonOperator.Equal,
                "code",
                new QuerySqlStringContract(60)
            ),
        ]
    );

    private static readonly QuerySqlParameter[] _collectionFilterParametersInOrder =
    [
        new(QuerySqlParameterRole.Filter, "code", QuerySqlParameterBinding.CreateString(60)),
        new(QuerySqlParameterRole.Filter, "codeSystem"),
    ];

    private readonly PageDocumentIdSqlCompiler _compiler = new(dialect);

    private string ExistsSql =>
        dialect is SqlDialect.Pgsql
            ? "EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntryIdentificationCode\" ci WHERE ci.\"GradebookEntry_DocumentId\" = r.\"DocumentId\" AND ci.\"IdentificationCode\" = @code AND ci.\"SystemDescriptor_DescriptorId\" = @codeSystem)"
            : "EXISTS (SELECT 1 FROM [edfi].[GradebookEntryIdentificationCode] ci WHERE ci.[GradebookEntry_DocumentId] = r.[DocumentId] AND ci.[IdentificationCode] = @code AND ci.[SystemDescriptor_DescriptorId] = @codeSystem)";

    private static IEnumerable<TestCaseData> CompositionsByMode() =>
        from composition in new[]
        {
            "exists alone",
            "root predicate",
            "change-version window",
            "namespace",
            "custom view",
            "ownership",
            "relationship",
            "everything",
        }
        from mode in new[]
        {
            "offset page",
            "offset page with total count",
            "DocumentId cursor",
            "ContentVersion cursor",
            "DocumentId unpaged candidates",
            "ContentVersion unpaged candidates",
        }
        select new TestCaseData(composition, mode).SetArgDisplayNames(composition, mode);

    [TestCaseSource(nameof(CompositionsByMode))]
    public void It_inserts_one_exists_filter_after_the_value_predicates_and_changes_nothing_else(
        string composition,
        string mode
    )
    {
        var (spec, valuePredicateCount) = CreateSpec(composition, CreateMode(mode));

        var withoutFilter = _compiler.Compile(spec);
        var withFilter = _compiler.Compile(spec with { CollectionExistsPredicates = [_collectionFilter] });

        Normalize(withFilter.PageDocumentIdSql)
            .Should()
            .Be(InsertExistsFilter(withoutFilter.PageDocumentIdSql, valuePredicateCount));
        withFilter
            .PageParametersInOrder.Should()
            .Equal(InsertCollectionParameters(withoutFilter.PageParametersInOrder, valuePredicateCount));

        if (withoutFilter.TotalCountSql is null)
        {
            withFilter.TotalCountSql.Should().BeNull();
            withFilter.TotalCountParametersInOrder.Should().BeNull();
            return;
        }

        Normalize(withFilter.TotalCountSql!)
            .Should()
            .Be(InsertExistsFilter(withoutFilter.TotalCountSql, valuePredicateCount));
        withFilter
            .TotalCountParametersInOrder!.Value.Should()
            .Equal(
                InsertCollectionParameters(
                    withoutFilter.TotalCountParametersInOrder!.Value,
                    valuePredicateCount
                )
            );
    }

    [Test]
    public void It_compiles_a_null_or_empty_filter_list_exactly_as_a_spec_without_one()
    {
        var (spec, _) = CreateSpec(
            "everything",
            new PageCandidateMode.Traditional(IncludeTotalCountSql: true)
        );

        var omitted = _compiler.Compile(spec);

        _compiler.Compile(spec with { CollectionExistsPredicates = null }).Should().BeEquivalentTo(omitted);
        _compiler.Compile(spec with { CollectionExistsPredicates = [] }).Should().BeEquivalentTo(omitted);
    }

    [Test]
    public void It_emits_identical_sql_and_parameters_whatever_the_filter_and_predicate_order()
    {
        var secondFilter = new QueryCollectionExistsPredicate(
            new DbTableName(_edfiSchema, "GradebookEntryAlternateCode"),
            _rootLocatorColumn,
            [
                new QueryCollectionValuePredicate(
                    new DbColumnName("Code"),
                    QueryComparisonOperator.Equal,
                    "altCode"
                ),
            ]
        );
        var (spec, _) = CreateSpec(
            "root predicate",
            new PageCandidateMode.Traditional(IncludeTotalCountSql: true)
        );
        var reversedFilter = _collectionFilter with
        {
            Predicates = [.. _collectionFilter.Predicates.Reverse()],
        };

        var forward = _compiler.Compile(
            spec with
            {
                CollectionExistsPredicates = [_collectionFilter, secondFilter],
            }
        );
        var reversed = _compiler.Compile(
            spec with
            {
                CollectionExistsPredicates = [secondFilter, reversedFilter],
            }
        );

        reversed.Should().BeEquivalentTo(forward, options => options.WithStrictOrdering());

        var existsLines = Normalize(forward.PageDocumentIdSql)
            .Split('\n')
            .Where(static line => line.Contains("EXISTS (", StringComparison.Ordinal))
            .ToArray();

        existsLines.Should().HaveCount(2);
        existsLines[0].Should().Contain("GradebookEntryAlternateCode");
        existsLines[1].Should().Contain(ExistsSql);
        forward
            .PageParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolId", "altCode", "code", "codeSystem", "offset", "limit");
    }

    [Test]
    public void It_rejects_a_null_filter()
    {
        CompileWithFilters([null!])
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("CollectionExistsPredicates must not contain null entries.*");
    }

    [Test]
    public void It_rejects_a_filter_without_predicates()
    {
        CompileWithFilters([_collectionFilter with { Predicates = [] }])
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*'edfi.GradebookEntryIdentificationCode' must contain at least one value predicate.*"
            );
    }

    [Test]
    public void It_rejects_a_null_predicate()
    {
        CompileWithFilters([_collectionFilter with { Predicates = [null!] }])
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*'edfi.GradebookEntryIdentificationCode' must not contain null predicates.*");
    }

    [Test]
    public void It_rejects_an_invalid_parameter_name()
    {
        CompileWithFilters([
                _collectionFilter with
                {
                    Predicates =
                    [
                        new QueryCollectionValuePredicate(
                            new DbColumnName("IdentificationCode"),
                            QueryComparisonOperator.Equal,
                            "@code"
                        ),
                    ],
                },
            ])
            .Should()
            .Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_two_predicates_on_the_same_column_and_operator()
    {
        CompileWithFilters([
                _collectionFilter with
                {
                    Predicates =
                    [
                        .. _collectionFilter.Predicates,
                        new QueryCollectionValuePredicate(
                            new DbColumnName("IdentificationCode"),
                            QueryComparisonOperator.Equal,
                            "otherCode"
                        ),
                    ],
                },
            ])
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Duplicate collection predicate on 'edfi.GradebookEntryIdentificationCode' for semantic key "
                    + "(column='IdentificationCode', operator='Equal'). Colliding parameter names: ['code', 'otherCode']."
            );
    }

    [Test]
    public void It_rejects_two_filters_on_the_same_collection_table()
    {
        CompileWithFilters([_collectionFilter, _collectionFilter])
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "CollectionExistsPredicates must contain at most one filter per collection table; "
                    + "'edfi.GradebookEntryIdentificationCode' has 2.*"
            );
    }

    [Test]
    public void It_rejects_a_parameter_name_already_used_by_a_root_predicate()
    {
        var (spec, _) = CreateSpec("root predicate", new PageCandidateMode.Traditional());
        var colliding = _collectionFilter with
        {
            Predicates =
            [
                new QueryCollectionValuePredicate(
                    new DbColumnName("IdentificationCode"),
                    QueryComparisonOperator.Equal,
                    "SchoolId"
                ),
            ],
        };

        var compile = () => _compiler.Compile(spec with { CollectionExistsPredicates = [colliding] });

        compile.Should().Throw<ArgumentException>().WithMessage("Duplicate filter parameter names*");
    }

    [Test]
    public void It_rejects_a_parameter_name_owned_by_the_candidate_mode()
    {
        var colliding = _collectionFilter with
        {
            Predicates =
            [
                new QueryCollectionValuePredicate(
                    new DbColumnName("IdentificationCode"),
                    QueryComparisonOperator.Equal,
                    "limit"
                ),
            ],
        };

        CompileWithFilters([colliding])
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("Filter parameter name 'limit' collides with candidate mode parameter name*");
    }

    private Func<PageDocumentIdSqlPlan> CompileWithFilters(
        IReadOnlyList<QueryCollectionExistsPredicate> collectionFilters
    )
    {
        var (spec, _) = CreateSpec("exists alone", new PageCandidateMode.Traditional());

        return () => _compiler.Compile(spec with { CollectionExistsPredicates = collectionFilters });
    }

    /// <summary>
    /// The SQL expected once the collection filter is added: the same text with one more WHERE term, placed
    /// directly after the value predicates. When it becomes the first term, the term it displaces takes the
    /// <c>AND</c> connective; when the query had no WHERE clause, one is added before the ordering or the
    /// statement end.
    /// </summary>
    private string InsertExistsFilter(string sqlWithoutFilter, int valuePredicateCount)
    {
        var lines = Normalize(sqlWithoutFilter).Split('\n').ToList();
        var whereIndex = lines.IndexOf("WHERE");

        if (whereIndex < 0)
        {
            valuePredicateCount.Should().Be(0);

            var statementTailIndex = lines.FindIndex(static line =>
                line.StartsWith("ORDER BY", StringComparison.Ordinal) || line == ";"
            );
            lines.InsertRange(statementTailIndex, ["WHERE", $"    ({ExistsSql})"]);

            return string.Join('\n', lines);
        }

        var existsIndex = whereIndex + 1 + valuePredicateCount;

        if (valuePredicateCount == 0)
        {
            lines[existsIndex] = $"    AND {lines[existsIndex].TrimStart()}";
            lines.Insert(existsIndex, $"    ({ExistsSql})");
        }
        else
        {
            lines.Insert(existsIndex, $"    AND ({ExistsSql})");
        }

        return string.Join('\n', lines);
    }

    private static QuerySqlParameter[] InsertCollectionParameters(
        IEnumerable<QuerySqlParameter> parametersWithoutFilter,
        int valuePredicateCount
    )
    {
        var parameters = parametersWithoutFilter.ToList();
        parameters.InsertRange(valuePredicateCount, _collectionFilterParametersInOrder);

        return [.. parameters];
    }

    private static string Normalize(string sql) => sql.ReplaceLineEndings("\n");

    private static PageCandidateMode CreateMode(string mode) =>
        mode switch
        {
            "offset page" => new PageCandidateMode.Traditional(),
            "offset page with total count" => new PageCandidateMode.Traditional(IncludeTotalCountSql: true),
            "DocumentId cursor" => new PageCandidateMode.Cursor(),
            "ContentVersion cursor" => new PageCandidateMode.Cursor(
                OrderingMode: PageOrderingMode.ContentVersion
            ),
            "DocumentId unpaged candidates" => new PageCandidateMode.UnpagedCandidates(),
            "ContentVersion unpaged candidates" => new PageCandidateMode.UnpagedCandidates(
                OrderingMode: PageOrderingMode.ContentVersion
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    private (PageDocumentIdQuerySpec Spec, int ValuePredicateCount) CreateSpec(
        string composition,
        PageCandidateMode mode
    )
    {
        QueryValuePredicate[] rootPredicate =
        [
            new(new DbColumnName("SchoolId"), QueryComparisonOperator.Equal, "schoolId"),
        ];
        QueryValuePredicate[] changeVersionWindow =
        [
            new(
                new DbColumnName("ContentVersion"),
                QueryComparisonOperator.GreaterThanOrEqual,
                "minChangeVersion"
            ),
            new(
                new DbColumnName("ContentVersion"),
                QueryComparisonOperator.LessThanOrEqual,
                "maxChangeVersion"
            ),
        ];
        var everything = composition == "everything";

        QueryValuePredicate[] predicates = composition switch
        {
            "root predicate" or "everything" => rootPredicate,
            "change-version window" => changeVersionWindow,
            _ => [],
        };
        var authorization = composition switch
        {
            "exists alone" or "root predicate" or "change-version window" => null,
            _ => CreateAuthorization(
                includeNamespace: everything || composition == "namespace",
                includeCustomView: everything || composition == "custom view",
                includeOwnership: everything || composition == "ownership",
                includeRelationship: everything || composition == "relationship"
            ),
        };

        return (
            new PageDocumentIdQuerySpec(
                _rootTable,
                predicates,
                new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                mode,
                authorization
            ),
            predicates.Length
        );
    }

    private PageDocumentIdAuthorizationSpec CreateAuthorization(
        bool includeNamespace,
        bool includeCustomView,
        bool includeOwnership,
        bool includeRelationship
    ) =>
        new(
            Strategies: includeRelationship
                ?
                [
                    new PageDocumentIdAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        [
                            new PageDocumentIdAuthorizationEdOrgSubject(
                                _rootTable,
                                new DbColumnName("SchoolId"),
                                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                                    RelationshipAuthorizationHierarchyDirection.Normal
                                ),
                                [
                                    new RelationshipAuthorizationSubjectContributor(
                                        SecurableElementKind.EducationOrganization,
                                        "$.SchoolId",
                                        "SchoolId"
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
                : [],
            ClaimEducationOrganizationIdParameterization: includeRelationship
                ? AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    dialect,
                    [255901L],
                    RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
                )
                : null,
            NamespaceChecks: includeNamespace
                ?
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _rootTable,
                        new DbColumnName("Namespace")
                    ),
                ]
                : null,
            NamespacePrefixParameterization: includeNamespace
                ? NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
                : null,
            CustomViewChecks: includeCustomView
                ?
                [
                    new PageDocumentIdAuthorizationCustomViewCheck(
                        "GradebookEntryWithSection",
                        1,
                        new DbTableName(_authSchema, "GradebookEntryWithSection"),
                        _documentIdColumn,
                        [new ColumnPathStep(_rootTable, _documentIdColumn, _rootTable, _documentIdColumn)],
                        _rootTable,
                        _documentIdColumn
                    ),
                ]
                : null,
            OwnershipTokenParameterization: includeOwnership
                ? OwnershipTokenParameterizationFactory.Create(dialect, [7, 3, 11], "ownershipTokenIds")
                : null
        );
}
