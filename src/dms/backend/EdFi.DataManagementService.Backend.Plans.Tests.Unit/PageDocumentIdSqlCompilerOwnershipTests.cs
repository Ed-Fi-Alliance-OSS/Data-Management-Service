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
/// The <c>OwnershipBased</c> page filter: one <c>dms.Document</c> join, a null-guarded membership predicate
/// over the caller's ownership tokens, emitted as the last AND filter before the relationship OR group, on both
/// page and total-count SQL. The cursor-bound person auth-view behavior is pinned alongside, because the
/// ownership filter shares the WHERE clause with it and must not move it.
/// </summary>
[TestFixture]
public class Given_PageDocumentIdSqlCompiler_with_ownership_authorization
{
    private const string OwnershipParameterName = "ownershipTokenIds";
    private const string PgsqlDocumentJoin =
        "INNER JOIN \"dms\".\"Document\" doc ON doc.\"DocumentId\" = r.\"DocumentId\"";
    private const string MssqlDocumentJoin =
        "INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = r.[DocumentId]";
    private const string PgsqlOwnershipPredicate =
        "(doc.\"CreatedByOwnershipTokenId\" IS NOT NULL AND doc.\"CreatedByOwnershipTokenId\" = ANY(@ownershipTokenIds))";
    private const string MssqlOwnershipPredicate =
        "(doc.[CreatedByOwnershipTokenId] IS NOT NULL AND doc.[CreatedByOwnershipTokenId] IN (@ownershipTokenIds_0, @ownershipTokenIds_1, @ownershipTokenIds_2))";

    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _authSchema = new("auth");
    private static readonly DbTableName _rootTable = new(_edfiSchema, "GradebookEntry");
    private static readonly DbTableName _studentTable = new(_edfiSchema, "Student");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly IReadOnlyList<short> _tokens = [7, 3, 11];

    [Test]
    public void It_emits_a_pgsql_document_join_and_an_ANY_membership_predicate_for_an_ownership_only_spec()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(CreateOwnershipOnlySpec(SqlDialect.Pgsql, _tokens));

        plan.PageDocumentIdSql.Should().Contain(PgsqlDocumentJoin);
        plan.PageDocumentIdSql.Should().Contain(PgsqlOwnershipPredicate);
        plan.PageParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal(OwnershipParameterName, "offset", "limit");
        plan.PageParametersInOrder.Single(static p => p.ParameterName == OwnershipParameterName)
            .Binding.Kind.Should()
            .Be(QuerySqlParameterBindingKind.PgsqlArray);
    }

    [Test]
    public void It_emits_a_mssql_document_join_and_an_IN_list_predicate_for_an_ownership_only_spec()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(CreateOwnershipOnlySpec(SqlDialect.Mssql, _tokens));

        plan.PageDocumentIdSql.Should().Contain(MssqlDocumentJoin);
        plan.PageDocumentIdSql.Should().Contain(MssqlOwnershipPredicate);
        plan.PageParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("ownershipTokenIds_0", "ownershipTokenIds_1", "ownershipTokenIds_2", "offset", "limit");
        plan.PageParametersInOrder.Should()
            .OnlyContain(static p => p.Binding.Kind == QuerySqlParameterBindingKind.Scalar);
    }

    /// <summary>
    /// The authorization spec is normalized away when it carries nothing. Ownership alone must keep it, or an
    /// ownership-only configuration would compile to an unauthorized query that returns every row.
    /// </summary>
    [TestCase(SqlDialect.Pgsql, PgsqlOwnershipPredicate)]
    [TestCase(SqlDialect.Mssql, MssqlOwnershipPredicate)]
    public void It_retains_authorization_when_ownership_is_the_only_input(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var plan = compiler.Compile(CreateOwnershipOnlySpec(dialect, _tokens));

        plan.PageDocumentIdSql.Should().Contain("WHERE");
        plan.PageDocumentIdSql.Should().Contain(expectedPredicate);
    }

    [Test]
    public void It_emits_the_same_join_and_predicate_in_total_count_sql()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateOwnershipOnlySpec(SqlDialect.Pgsql, _tokens, includeTotalCountSql: true)
        );

        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql!.Should().Contain(PgsqlDocumentJoin);
        plan.TotalCountSql.Should().Contain(PgsqlOwnershipPredicate);
        plan.TotalCountParametersInOrder.Should().NotBeNull();
        plan.TotalCountParametersInOrder!.Value.Select(static p => p.ParameterName)
            .Should()
            .Equal(OwnershipParameterName);
    }

    [Test]
    public void It_joins_dms_Document_exactly_once_when_ownership_is_combined_with_an_id_filter()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = CreateOwnershipOnlySpec(SqlDialect.Pgsql, _tokens, includeTotalCountSql: true) with
        {
            Predicates =
            [
                new QueryValuePredicate(
                    new QueryPredicateTarget.DocumentUuid(),
                    QueryComparisonOperator.Equal,
                    "id"
                ),
            ],
        };

        var plan = compiler.Compile(spec);

        CountOrdinalOccurrences(plan.PageDocumentIdSql, "INNER JOIN").Should().Be(1);
        CountOrdinalOccurrences(plan.TotalCountSql!, "INNER JOIN").Should().Be(1);
        plan.PageDocumentIdSql.Should().Contain("doc.\"DocumentUuid\" = @id");
        plan.PageDocumentIdSql.Should().Contain(PgsqlOwnershipPredicate);
    }

    [Test]
    public void It_does_not_join_dms_Document_when_no_ownership_filter_or_id_filter_is_present()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateComposedSpec(SqlDialect.Pgsql, includeNamespace: true, includeOwnership: false)
        );

        plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        plan.PageDocumentIdSql.Should().NotContain("CreatedByOwnershipTokenId");
    }

    /// <summary>
    /// Ownership executes last among the AND strategies whatever position CMS gave it, so a custom view
    /// configured after <c>OwnershipBased</c> still precedes it in the WHERE clause.
    /// </summary>
    [Test]
    public void It_emits_the_ownership_filter_after_namespace_and_custom_view_filters_regardless_of_configured_index()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateComposedSpec(
                SqlDialect.Pgsql,
                includeNamespace: true,
                includeOwnership: true,
                customViewRawConfiguredIndex: 7
            )
        );

        var namespaceIndex = plan.PageDocumentIdSql.IndexOf(
            "r.\"Namespace\" IS NOT NULL",
            StringComparison.Ordinal
        );
        var customViewIndex = plan.PageDocumentIdSql.IndexOf(
            "\"auth\".\"GradebookEntryWithSection\"",
            StringComparison.Ordinal
        );
        var ownershipIndex = plan.PageDocumentIdSql.IndexOf(
            PgsqlOwnershipPredicate,
            StringComparison.Ordinal
        );

        namespaceIndex.Should().BeGreaterThan(-1);
        customViewIndex.Should().BeGreaterThan(namespaceIndex);
        ownershipIndex.Should().BeGreaterThan(customViewIndex);
    }

    [TestCase(SqlDialect.Pgsql, PgsqlOwnershipPredicate, "r.\"Namespace\" IS NOT NULL", "r.\"SchoolId\"")]
    [TestCase(SqlDialect.Mssql, MssqlOwnershipPredicate, "r.[Namespace] IS NOT NULL", "r.[SchoolId]")]
    public void It_emits_the_ownership_filter_after_the_namespace_filter_and_before_the_relationship_OR_group(
        SqlDialect dialect,
        string expectedOwnershipPredicate,
        string namespaceMarker,
        string relationshipMarker
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var plan = compiler.Compile(
            CreateComposedSpec(
                dialect,
                includeNamespace: true,
                includeOwnership: true,
                includeRelationshipStrategies: true
            )
        );

        var namespaceIndex = plan.PageDocumentIdSql.IndexOf(namespaceMarker, StringComparison.Ordinal);
        var ownershipIndex = plan.PageDocumentIdSql.IndexOf(
            expectedOwnershipPredicate,
            StringComparison.Ordinal
        );
        var relationshipIndex = plan.PageDocumentIdSql.IndexOf(relationshipMarker, StringComparison.Ordinal);

        namespaceIndex.Should().BeGreaterThan(-1);
        ownershipIndex.Should().BeGreaterThan(namespaceIndex);
        relationshipIndex.Should().BeGreaterThan(ownershipIndex);
    }

    [Test]
    public void It_keeps_the_relationship_OR_group_bracketed_when_the_ownership_filter_precedes_it()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateComposedSpec(
                SqlDialect.Pgsql,
                includeNamespace: false,
                includeOwnership: true,
                includeRelationshipStrategies: true,
                includeInvertedRelationshipStrategy: true
            )
        );

        // The OR group stays one outer-parens predicate with each strategy in its own inner parens. If the
        // ownership AND ever flattened it, the SQL would read "AND (strat1) OR (strat2)".
        plan.PageDocumentIdSql.Should().Contain("AND ((");
        plan.PageDocumentIdSql.Should().Contain(") OR (");
    }

    [Test]
    public void It_emits_the_ownership_filter_in_cursor_mode_before_the_cursor_bounds()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateOwnershipOnlySpec(SqlDialect.Pgsql, _tokens, mode: new PageCandidateMode.Cursor())
        );

        var ownershipIndex = plan.PageDocumentIdSql.IndexOf(
            PgsqlOwnershipPredicate,
            StringComparison.Ordinal
        );
        var lowerBoundIndex = plan.PageDocumentIdSql.IndexOf(
            "AND (r.\"DocumentId\" >= @cursorMin)",
            StringComparison.Ordinal
        );

        ownershipIndex.Should().BeGreaterThan(-1);
        lowerBoundIndex.Should().BeGreaterThan(ownershipIndex);
        plan.PageDocumentIdSql.Should().Contain("AND (r.\"DocumentId\" <= @cursorMax)");
        plan.PageParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal(OwnershipParameterName, "cursorMin", "cursorMax", "pageSize");
    }

    [Test]
    public void It_emits_the_ownership_filter_in_the_unpaged_candidate_relation_without_ordering()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateOwnershipOnlySpec(
                SqlDialect.Pgsql,
                _tokens,
                mode: new PageCandidateMode.UnpagedCandidates()
            )
        );

        plan.PageDocumentIdSql.Should().Contain(PgsqlDocumentJoin);
        plan.PageDocumentIdSql.Should().Contain(PgsqlOwnershipPredicate);
        plan.PageDocumentIdSql.Should().NotContain("ORDER BY");
        plan.PageParametersInOrder.Select(static p => p.ParameterName).Should().Equal(OwnershipParameterName);
    }

    /// <summary>
    /// The shared-candidate guarantee: every mode selects from the same authorized candidate relation. An
    /// ownership spec must keep the candidate root, join, and WHERE fragment byte-identical across traditional,
    /// cursor, and unpaged modes, exactly as the namespace and relationship specs do.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_keeps_the_shared_candidate_region_identical_across_modes_for_an_ownership_spec(
        SqlDialect dialect
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var authorization = CreateOwnershipAuthorization(dialect, _tokens);
        PageCandidateMode traditional = new PageCandidateMode.Traditional();
        PageCandidateMode cursor = new PageCandidateMode.Cursor();
        PageCandidateMode unpaged = new PageCandidateMode.UnpagedCandidates();

        var traditionalPlan = compiler.Compile(
            CandidateModeTestSpecs.CreateSpec(traditional, authorization: authorization)
        );
        var cursorPlan = compiler.Compile(
            CandidateModeTestSpecs.CreateSpec(cursor, authorization: authorization)
        );
        var unpagedPlan = compiler.Compile(
            CandidateModeTestSpecs.CreateSpec(unpaged, authorization: authorization)
        );

        var traditionalRegion = CandidateSqlRegions.SharedCandidateRegion(
            traditionalPlan.PageDocumentIdSql,
            traditional,
            dialect
        );

        traditionalRegion.Should().Contain("CreatedByOwnershipTokenId");
        CandidateSqlRegions
            .SharedCandidateRegion(cursorPlan.PageDocumentIdSql, cursor, dialect)
            .Should()
            .Be(traditionalRegion);
        CandidateSqlRegions
            .SharedCandidateRegion(unpagedPlan.PageDocumentIdSql, unpaged, dialect)
            .Should()
            .Be(traditionalRegion);
        CandidateSqlRegions
            .FilterParameters(cursorPlan)
            .Should()
            .Equal(CandidateSqlRegions.FilterParameters(traditionalPlan));
        CandidateSqlRegions
            .FilterParameters(unpagedPlan)
            .Should()
            .Equal(CandidateSqlRegions.FilterParameters(traditionalPlan));
    }

    // ── DMS-1392 regression guard: cursor-bound person auth-view subqueries ────────────────────

    /// <summary>
    /// Adding the ownership filter must not disturb the cursor-bounded self-anchored person auth-view subquery.
    /// The expected fragments are the ones the cursor fixture pins without ownership; the ownership predicate
    /// is asserted alongside, ahead of the OR group.
    /// </summary>
    [TestCase(
        SqlDialect.Pgsql,
        "r.\"DocumentId\" IN (SELECT t0.\"Student_DocumentId\" FROM \"auth\".\"EducationOrganizationIdToStudentDocumentId\" t0 WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds) AND t0.\"Student_DocumentId\" >= @cursorMin AND t0.\"Student_DocumentId\" <= @cursorMax)",
        PgsqlOwnershipPredicate
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[DocumentId] IN (SELECT t0.[Student_DocumentId] FROM [auth].[EducationOrganizationIdToStudentDocumentId] t0 WHERE t0.[SourceEducationOrganizationId] IN (@ClaimEducationOrganizationIds_0) AND t0.[Student_DocumentId] >= @cursorMin AND t0.[Student_DocumentId] <= @cursorMax)",
        MssqlOwnershipPredicate
    )]
    public void It_preserves_the_cursor_bounded_self_anchored_person_auth_view_subquery_when_ownership_is_added(
        SqlDialect dialect,
        string expectedAuthorizationFragment,
        string expectedOwnershipPredicate
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var plan = compiler.Compile(
            CreateSelfAnchoredPersonSpec(dialect, new PageCandidateMode.Cursor(), includeOwnership: true)
        );

        plan.PageDocumentIdSql.Should().Contain(expectedAuthorizationFragment);
        plan.PageDocumentIdSql.Should().Contain(expectedOwnershipPredicate);
        plan.PageDocumentIdSql.IndexOf(expectedOwnershipPredicate, StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                plan.PageDocumentIdSql.IndexOf(expectedAuthorizationFragment, StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// The person authorization fragment is byte-identical with and without the ownership filter, in cursor
    /// mode. The filter is an additional AND term, never a rewrite of the OR group.
    /// </summary>
    [TestCase(SqlDialect.Pgsql, "\"auth\".")]
    [TestCase(SqlDialect.Mssql, "[auth].")]
    public void It_does_not_change_the_cursor_mode_person_authorization_fragment_when_ownership_is_added(
        SqlDialect dialect,
        string authViewMarker
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var withoutOwnership = compiler.Compile(
            CreateSelfAnchoredPersonSpec(dialect, new PageCandidateMode.Cursor(), includeOwnership: false)
        );
        var withOwnership = compiler.Compile(
            CreateSelfAnchoredPersonSpec(dialect, new PageCandidateMode.Cursor(), includeOwnership: true)
        );

        ExtractLineContaining(withOwnership.PageDocumentIdSql, authViewMarker)
            .Should()
            .Be(ExtractLineContaining(withoutOwnership.PageDocumentIdSql, authViewMarker));
    }

    /// <summary>
    /// A <c>ContentVersion</c> cursor bounds a column the auth view does not expose, so the subquery stays
    /// unbounded. Ownership must not change that either way.
    /// </summary>
    [TestCase(SqlDialect.Pgsql, "\"auth\".", "AND (r.\"ContentVersion\" >= @cursorMin)")]
    [TestCase(SqlDialect.Mssql, "[auth].", "AND (r.[ContentVersion] >= @cursorMin)")]
    public void It_leaves_the_person_auth_view_subquery_unbounded_under_a_content_version_cursor_when_ownership_is_added(
        SqlDialect dialect,
        string authViewMarker,
        string expectedOuterLowerBound
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var contentVersionCursor = new PageCandidateMode.Cursor(
            OrderingMode: PageOrderingMode.ContentVersion
        );

        var withoutOwnership = compiler.Compile(
            CreateSelfAnchoredPersonSpec(dialect, contentVersionCursor, includeOwnership: false)
        );
        var withOwnership = compiler.Compile(
            CreateSelfAnchoredPersonSpec(dialect, contentVersionCursor, includeOwnership: true)
        );

        var authViewLine = ExtractLineContaining(withOwnership.PageDocumentIdSql, authViewMarker);

        authViewLine.Should().NotContain("@cursorMin");
        authViewLine.Should().Be(ExtractLineContaining(withoutOwnership.PageDocumentIdSql, authViewMarker));
        withOwnership.PageDocumentIdSql.Should().Contain(expectedOuterLowerBound);
    }

    // ── empty token list, dialect mismatch, and the SQL Server scalar ceiling ──────────────────

    /// <summary>
    /// The repository short-circuits an empty token list to an empty page before compiling. Should a spec with
    /// no tokens reach the compiler anyway, it must fail closed: a constant-false predicate that binds no
    /// parameter, never an empty <c>IN ()</c> or an unfiltered page.
    /// </summary>
    [TestCase(SqlDialect.Pgsql, "(doc.\"CreatedByOwnershipTokenId\" IS NOT NULL AND 1 = 0)")]
    [TestCase(SqlDialect.Mssql, "(doc.[CreatedByOwnershipTokenId] IS NOT NULL AND 1 = 0)")]
    public void It_emits_a_constant_false_predicate_and_binds_no_ownership_parameter_for_an_empty_token_list(
        SqlDialect dialect,
        string expectedPredicate
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        var plan = compiler.Compile(CreateOwnershipOnlySpec(dialect, [], includeTotalCountSql: true));

        plan.PageDocumentIdSql.Should().Contain(expectedPredicate);
        plan.PageDocumentIdSql.Should().NotContain("@ownershipTokenIds");
        plan.TotalCountSql!.Should().Contain(expectedPredicate);
        plan.PageParametersInOrder.Select(static p => p.ParameterName).Should().Equal("offset", "limit");
        plan.TotalCountParametersInOrder!.Value.Should().BeEmpty();
    }

    [Test]
    public void It_throws_when_a_pgsql_ownership_parameterization_is_handed_to_a_mssql_compiler()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var act = () => compiler.Compile(CreateOwnershipOnlySpec(SqlDialect.Pgsql, _tokens));

        act.Should().Throw<ArgumentException>().WithMessage("*not supported by SQL dialect 'Mssql'*");
    }

    [Test]
    public void It_throws_when_a_mssql_ownership_parameterization_is_handed_to_a_pgsql_compiler()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var act = () => compiler.Compile(CreateOwnershipOnlySpec(SqlDialect.Mssql, _tokens));

        act.Should().Throw<ArgumentException>().WithMessage("*not supported by SQL dialect 'Pgsql'*");
    }

    /// <summary>
    /// 1,999 is the largest token count CMS permits and the largest the factory accepts. SQL Server binds one
    /// scalar per token, so the compiler must emit all 1,999 placeholders; whether the whole command still fits
    /// the engine's parameter ceiling is the authorization parameter budget's decision, not this compiler's.
    /// </summary>
    [Test]
    public void It_emits_one_scalar_placeholder_per_token_for_1999_sql_server_tokens()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        IReadOnlyList<short> tokens = [.. Enumerable.Range(1, 1999).Select(static value => (short)value)];

        var plan = compiler.Compile(CreateOwnershipOnlySpec(SqlDialect.Mssql, tokens));

        plan.PageDocumentIdSql.Should().Contain("@ownershipTokenIds_0,");
        plan.PageDocumentIdSql.Should().Contain("@ownershipTokenIds_1998)");
        plan.PageDocumentIdSql.Should().NotContain("@ownershipTokenIds_1999");
        plan.PageParametersInOrder.Count(static p => p.Role == QuerySqlParameterRole.Filter)
            .Should()
            .Be(1999);
    }

    // ── spec builders ────────────────────────────────────────────────────────────────────────

    private static PageDocumentIdAuthorizationSpec CreateOwnershipAuthorization(
        SqlDialect dialect,
        IReadOnlyList<short> tokens
    ) =>
        new(
            Strategies: [],
            OwnershipTokenParameterization: OwnershipTokenParameterizationFactory.Create(
                dialect,
                tokens,
                OwnershipParameterName
            )
        );

    private static PageDocumentIdQuerySpec CreateOwnershipOnlySpec(
        SqlDialect dialect,
        IReadOnlyList<short> tokens,
        bool includeTotalCountSql = false,
        PageCandidateMode? mode = null
    ) =>
        new(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Mode: mode ?? new PageCandidateMode.Traditional(IncludeTotalCountSql: includeTotalCountSql),
            Authorization: CreateOwnershipAuthorization(dialect, tokens)
        );

    private static PageDocumentIdQuerySpec CreateComposedSpec(
        SqlDialect dialect,
        bool includeNamespace,
        bool includeOwnership,
        bool includeRelationshipStrategies = false,
        bool includeInvertedRelationshipStrategy = false,
        int? customViewRawConfiguredIndex = null
    )
    {
        List<PageDocumentIdAuthorizationStrategy> strategies = [];

        if (includeRelationshipStrategies)
        {
            strategies.Add(
                new PageDocumentIdAuthorizationStrategy(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    [CreateEdOrgSchoolSubject(RelationshipAuthorizationHierarchyDirection.Normal)]
                )
            );

            if (includeInvertedRelationshipStrategy)
            {
                strategies.Add(
                    new PageDocumentIdAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                        [CreateEdOrgSchoolSubject(RelationshipAuthorizationHierarchyDirection.Inverted)]
                    )
                );
            }
        }

        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks =
            customViewRawConfiguredIndex is { } rawConfiguredIndex
                ?
                [
                    new PageDocumentIdAuthorizationCustomViewCheck(
                        "GradebookEntryWithSection",
                        rawConfiguredIndex,
                        new DbTableName(_authSchema, "GradebookEntryWithSection"),
                        _documentIdColumn,
                        [new ColumnPathStep(_rootTable, _documentIdColumn, _rootTable, _documentIdColumn)],
                        _rootTable,
                        _documentIdColumn
                    ),
                ]
                : null;

        return new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: strategies,
                ClaimEducationOrganizationIdParameterization: includeRelationshipStrategies
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
                            _namespaceColumn
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
                CustomViewChecks: customViewChecks,
                OwnershipTokenParameterization: includeOwnership
                    ? OwnershipTokenParameterizationFactory.Create(dialect, _tokens, OwnershipParameterName)
                    : null
            )
        );
    }

    /// <summary>
    /// The self-anchored Student subject the cursor fixture uses: root <c>edfi.Student</c>, anchored on its own
    /// <c>DocumentId</c>, so a DocumentId cursor's bounds transfer into the auth-view subquery.
    /// </summary>
    private static PageDocumentIdQuerySpec CreateSelfAnchoredPersonSpec(
        SqlDialect dialect,
        PageCandidateMode.Cursor cursor,
        bool includeOwnership
    )
    {
        var subject = new PageDocumentIdAuthorizationPersonSubject(
            _studentTable,
            _documentIdColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                    []
                ),
                new RelationshipAuthorizationPersonStoredAnchor(_studentTable, _documentIdColumn),
                ProposedAnchor: null
            )
        );

        return new PageDocumentIdQuerySpec(
            RootTable: _studentTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Mode: cursor,
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies:
                [
                    new PageDocumentIdAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                        [subject]
                    ),
                ],
                ClaimEducationOrganizationIdParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    dialect,
                    [255901L],
                    RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
                ),
                OwnershipTokenParameterization: includeOwnership
                    ? OwnershipTokenParameterizationFactory.Create(dialect, _tokens, OwnershipParameterName)
                    : null
            )
        );
    }

    private static PageDocumentIdAuthorizationEdOrgSubject CreateEdOrgSchoolSubject(
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        new(
            _rootTable,
            new DbColumnName("SchoolId"),
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    "$.SchoolId",
                    "SchoolId"
                ),
            ]
        );

    /// <summary>
    /// Returns the single WHERE line referencing <paramref name="marker"/>, without its positional connective.
    /// The WHERE writer prefixes every predicate after the first with <c>AND </c>, so the same predicate reads
    /// differently depending on how many terms precede it; comparing predicates across specs has to subtract
    /// that prefix, as the shared candidate-region helper does for cursor bounds.
    /// </summary>
    private static string ExtractLineContaining(string sql, string marker)
    {
        var matchingLines = sql.Split('\n')
            .Where(line => line.Contains(marker, StringComparison.Ordinal))
            .ToArray();

        matchingLines.Should().ContainSingle($"exactly one WHERE line should reference '{marker}'");

        var predicate = matchingLines[0].Trim();

        return predicate.StartsWith("AND ", StringComparison.Ordinal)
            ? predicate["AND ".Length..]
            : predicate;
    }

    private static int CountOrdinalOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;
}
