// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Real-PostgreSQL execution evidence for the shared candidate relation: that the compiled traditional,
/// cursor, and unpaged candidate SQL runs, returns the expected ordered identifiers, and yields exactly
/// one row per <c>DocumentId</c> for every authorization shape the candidate compiler emits and for
/// every planner consumer that reaches it.
/// </summary>
/// <remarks>
/// The underlying authorization rows are deliberately seeded to fan out: two hierarchy edges reach one
/// subject in each hierarchy direction, two auth-view rows reach one person, several intermediate join rows match one root, and
/// several custom-view rows match one document. A join-based authorization plan would multiply the
/// candidate row under each of those seeds, which would corrupt the row numbering and candidate count
/// that partition boundaries are derived from. The candidate relation must stay unique by construction,
/// and that is asserted here against real results rather than enforced by a runtime guard or concealed
/// by an unconditional <c>DISTINCT</c>.
/// <para>
/// The fan-out is produced only from data the production schema can actually hold. The authorization
/// tables are created with their production constraints, and the person auth object is the production
/// <c>SELECT DISTINCT</c> view over its real source tables rather than a base-table stand-in, so a
/// duplicate the production schema forbids can never be what makes this probe pass.
/// </para>
/// <para>
/// These probes execute the compiled SQL directly rather than through <c>QueryDocuments</c>, which still
/// rejects cursor paging: cursor hydration and execution arrive with the cursor execution story.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Compiled_Candidate_Relation
{
    private const long DirectClaimEducationOrganizationId = 900L;
    private const long IndirectClaimEducationOrganizationId = 901L;
    private const string AuthorizedNamespacePrefix = "uri://ed-fi.org/";
    private const string AuthorizedNamespace = "uri://ed-fi.org/Probe";
    private const string UnauthorizedNamespace = "uri://other.org/Probe";
    private const string MatchingDescriptorCodeValue = "Probe";
    private const string NonMatchingDescriptorCodeValue = "Other";
    private const long MinimumChangeVersion = 30L;

    /// <summary>
    /// Both claim EducationOrganization ids the probe binds. Three distinct hierarchy tuples fan into
    /// the single subject value the root rows carry, two per direction: <c>(900, 900)</c> and
    /// <c>(901, 900)</c> for the normal direction, which reads the target column as its subject, and
    /// <c>(900, 900)</c> and <c>(900, 901)</c> for the inverted direction, which reads the source column.
    /// All three are legal under the production composite primary key, so the fan-out under test is
    /// reachable in production rather than an artifact of a relaxed test schema.
    /// </summary>
    private static readonly long[] _claimEducationOrganizationIds =
    [
        DirectClaimEducationOrganizationId,
        IndirectClaimEducationOrganizationId,
    ];

    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "CandidateProbeRoot");
    private static readonly DbTableName _childTable = new(new DbSchemaName("edfi"), "CandidateProbeChild");
    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");
    private static readonly DbTableName _studentSchoolAssociationTable = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );
    private static readonly DbTableName _customViewTable = new(
        new DbSchemaName("auth"),
        "CandidateProbeView"
    );

    /// <summary>
    /// The descriptor consumer's custom auth view. It is a separate relation from
    /// <see cref="_customViewTable" /> because that one is seeded with regular-resource root
    /// <c>DocumentId</c>s, which no descriptor row carries.
    /// </summary>
    private static readonly DbTableName _descriptorCustomViewTable = new(
        new DbSchemaName("auth"),
        "CandidateProbeDescriptorView"
    );
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");
    private static readonly DbColumnName _schoolIdColumn = new("SchoolId");
    private static readonly DbColumnName _studentDocumentIdColumn = new("Student_DocumentId");

    /// <summary>Every seeded root <c>DocumentId</c>, ascending.</summary>
    private static readonly long[] _allDocumentIds = [10L, 20L, 30L, 40L, 50L];

    /// <summary>
    /// The root <c>DocumentId</c>s inside the change-version window the planner probes request. A strict
    /// subset of <see cref="_allDocumentIds" />, so a planner that dropped the window would be caught.
    /// </summary>
    private static readonly long[] _documentIdsInChangeVersionWindow = [30L, 40L, 50L];

    /// <summary>
    /// Descriptor <c>DocumentId</c>s carrying the requested <c>ResourceKeyId</c>, the matching code
    /// value, and an authorized namespace.
    /// </summary>
    private static readonly long[] _authorizedDescriptorDocumentIds = [110L, 120L, 130L, 140L, 150L];

    /// <summary>
    /// <see cref="_authorizedDescriptorDocumentIds" /> as a SQL literal list, so the descriptor auth
    /// view seed cannot drift from the set the descriptor assertions expect.
    /// </summary>
    private static string AuthorizedDescriptorDocumentIdList =>
        string.Join(", ", _authorizedDescriptorDocumentIds);

    /// <summary>
    /// Descriptor rows the requested query must exclude, each for exactly one reason so the exclusion is
    /// attributable: an unrequested <c>ResourceKeyId</c>, an unauthorized namespace, and a non-matching
    /// code value.
    /// </summary>
    private const long UnrelatedResourceKeyDescriptorDocumentId = 160L;
    private const long UnauthorizedNamespaceDescriptorDocumentId = 170L;
    private const long NonMatchingCodeValueDescriptorDocumentId = 180L;

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // An isolated per-run database. The probe creates production-named authorization relations, so
        // it must never create them in a database another fixture or a later run can observe.
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        foreach (var statement in BuildSchemaStatements())
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // Both fields are guarded because one-time setup can fail between the two assignments. An
        // unguarded dispose would then throw over the real setup failure and hide it.
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_should_seed_authorization_sources_that_actually_expose_multiple_matches_per_candidate()
    {
        // Without this, every uniqueness assertion below could pass simply because nothing duplicates.
        // The two hierarchy directions read opposite columns of the same table, so each needs its own
        // count: a seed that fans out one direction can leave the other matching a single row.
        var normalHierarchyEdges = await ScalarAsync(
            $"""
            SELECT COUNT(*) FROM {Quote(CandidateProbeAuthorizationSpecs.EdOrgAuthObject.Name)}
            WHERE "TargetEducationOrganizationId" = {DirectClaimEducationOrganizationId};
            """
        );
        var invertedHierarchyEdges = await ScalarAsync(
            $"""
            SELECT COUNT(*) FROM {Quote(CandidateProbeAuthorizationSpecs.EdOrgAuthObject.Name)}
            WHERE "SourceEducationOrganizationId" = {DirectClaimEducationOrganizationId};
            """
        );
        var personViewRows = await ScalarAsync(
            $"""
            SELECT COUNT(*) FROM {Quote(CandidateProbeAuthorizationSpecs.SelfPersonAuthObject.Name)}
            WHERE "Student_DocumentId" = {_allDocumentIds[0]};
            """
        );
        var childRows = await ScalarAsync(
            $"""
            SELECT COUNT(*) FROM {Quote(_childTable)} WHERE "DocumentId" = {_allDocumentIds[0]};
            """
        );
        var customViewRows = await ScalarAsync(
            $"""
            SELECT COUNT(*) FROM {Quote(_customViewTable)} WHERE "DocumentId" = {_allDocumentIds[0]};
            """
        );
        var descriptorCustomViewRows = await ScalarAsync(
            $"""
            SELECT COUNT(*) FROM {Quote(_descriptorCustomViewTable)}
            WHERE "DocumentId" = {_authorizedDescriptorDocumentIds[0]};
            """
        );

        normalHierarchyEdges
            .Should()
            .BeGreaterThan(
                1,
                "the normal hierarchy direction reads TargetEducationOrganizationId as its subject, so two production-valid tuples must fan into the one subject value"
            );
        invertedHierarchyEdges
            .Should()
            .BeGreaterThan(
                1,
                "the inverted hierarchy direction reads SourceEducationOrganizationId as its subject, so it needs its own fan-out rather than inheriting the normal direction's"
            );
        personViewRows
            .Should()
            .BeGreaterThan(1, "the DISTINCT person auth view must still expose two rows for one person");
        childRows.Should().BeGreaterThan(1, "the intermediate join table must duplicate per root");
        customViewRows.Should().BeGreaterThan(1, "the custom auth view must duplicate per document");
        descriptorCustomViewRows
            .Should()
            .BeGreaterThan(
                1,
                "the descriptor custom auth view must duplicate per descriptor, or the descriptor custom-view cases would prove uniqueness against a single-row seed"
            );
    }

    [Test]
    public async Task It_should_execute_traditional_cursor_and_unpaged_candidate_sql_for_every_authorization_shape()
    {
        foreach (var (description, authorization) in BuildAuthorizationMatrix())
        {
            foreach (var mode in BuildEveryCandidateMode())
            {
                var ids = await SelectCandidateIdsAsync(mode, authorization);

                ids.Should()
                    .OnlyHaveUniqueItems(
                        $"the candidate relation must produce one row per DocumentId for {description} in {mode.GetType().Name} mode"
                    );
                ids.Should()
                    .BeEquivalentTo(
                        _allDocumentIds,
                        $"every seeded row must be reachable under {description}"
                    );

                // Only the paged modes order their output. The unpaged candidate relation is
                // deliberately unordered because its consumer supplies the ordering it needs.
                if (mode is not PageCandidateMode.UnpagedCandidates)
                {
                    ids.Should()
                        .BeInAscendingOrder(
                            $"paged candidate selection must be ordered by DocumentId for {description}"
                        );
                }
            }
        }
    }

    [Test]
    public async Task It_should_yield_one_row_per_document_id_for_every_regular_resource_planner_consumer()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var rootTableModel = CandidateProbePlannerInputs.CreateRootTableModel(_rootTable);
        var changeVersionRange = new ChangeVersionRange(MinimumChangeVersion, null);

        foreach (var (description, authorization) in BuildRegularResourceAuthorizationMatrix())
        {
            foreach (var paging in BuildEveryPagingChoice())
            {
                var planned = planner.Plan(
                    rootTableModel,
                    CandidateProbePlannerInputs.CreateRootSchoolIdFilter(DirectClaimEducationOrganizationId),
                    paging,
                    comparisonOperatorResolver: null,
                    authorization,
                    changeVersionRange
                );

                var ids = await SelectPlannedIdsAsync(planned.Plan, planned.ParameterValues);

                ids.Should()
                    .OnlyHaveUniqueItems(
                        $"the regular-resource consumer must produce one row per DocumentId for {description} in {paging.GetType().Name} paging"
                    );
                ids.Should()
                    .BeEquivalentTo(
                        _documentIdsInChangeVersionWindow,
                        $"the resource filter and change-version window must both restrict the candidate relation for {description}"
                    );
            }

            var planCandidatesSucceeded = planner.TryPlanCandidates(
                rootTableModel,
                CandidateProbePlannerInputs.CreateRootSchoolIdFilter(DirectClaimEducationOrganizationId),
                out var unpagedCandidates,
                out var emptyPageReason,
                comparisonOperatorResolver: null,
                authorization,
                changeVersionRange
            );

            planCandidatesSucceeded
                .Should()
                .BeTrue($"the unpaged candidate relation must plan for {description}: {emptyPageReason}");

            var unpagedIds = await SelectPlannedIdsAsync(
                unpagedCandidates!.Plan,
                unpagedCandidates.ParameterValues
            );

            unpagedIds
                .Should()
                .OnlyHaveUniqueItems(
                    $"the unpaged regular-resource candidate relation must produce one row per DocumentId for {description}"
                );
            unpagedIds.Should().BeEquivalentTo(_documentIdsInChangeVersionWindow);
        }
    }

    [Test]
    public async Task It_should_yield_one_row_per_document_id_for_every_descriptor_planner_consumer()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var mappingSet = CandidateProbePlannerInputs.CreateDescriptorMappingSet(SqlDialect.Pgsql);

        foreach (var (description, authorization, expectedIds) in BuildDescriptorAuthorizationMatrix())
        {
            foreach (var paging in BuildEveryPagingChoice())
            {
                var planned = planner.Plan(
                    mappingSet,
                    CandidateProbePlannerInputs.DescriptorResource,
                    CandidateProbePlannerInputs.CreateDescriptorCodeValueFilter(MatchingDescriptorCodeValue),
                    paging,
                    authorization
                );

                var ids = await SelectPlannedIdsAsync(planned.Plan, planned.ParameterValues);

                ids.Should()
                    .OnlyHaveUniqueItems(
                        $"the descriptor consumer must produce one row per DocumentId for {description} in {paging.GetType().Name} paging"
                    );
                ids.Should()
                    .BeEquivalentTo(
                        expectedIds,
                        $"the ResourceKeyId discriminator and descriptor filter must both restrict the candidate relation for {description}"
                    );
                ids.Should()
                    .NotContain(
                        UnrelatedResourceKeyDescriptorDocumentId,
                        "the mandatory ResourceKeyId discriminator must exclude an otherwise matching descriptor row"
                    );
                ids.Should()
                    .NotContain(
                        NonMatchingCodeValueDescriptorDocumentId,
                        "the descriptor filter must exclude a row whose code value does not match"
                    );
            }

            var unpaged = planner.PlanCandidates(
                mappingSet,
                CandidateProbePlannerInputs.DescriptorResource,
                CandidateProbePlannerInputs.CreateDescriptorCodeValueFilter(MatchingDescriptorCodeValue),
                authorization
            );

            var unpagedIds = await SelectPlannedIdsAsync(unpaged.Plan, unpaged.ParameterValues);

            unpagedIds
                .Should()
                .OnlyHaveUniqueItems(
                    $"the unpaged descriptor candidate relation must produce one row per DocumentId for {description}"
                );
            unpagedIds.Should().BeEquivalentTo(expectedIds);
        }
    }

    [Test]
    public void It_should_never_compile_distinct_into_any_authorized_candidate_relation()
    {
        foreach (var (description, authorization) in BuildAuthorizationMatrix())
        {
            foreach (var mode in BuildEveryCandidateMode())
            {
                Compile(mode, authorization)
                    .PageDocumentIdSql.Should()
                    .NotContain(
                        "DISTINCT",
                        $"uniqueness must hold by construction for {description}, not by a sort"
                    );
            }
        }
    }

    [Test]
    public async Task It_should_select_the_inclusive_cursor_window_only()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: 20L,
            cursorMaximum: 40L
        );

        ids.Should().Equal(20L, 30L, 40L);
    }

    [Test]
    public async Task It_should_return_no_rows_for_an_inverted_cursor_range()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: 40L,
            cursorMaximum: 20L
        );

        ids.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_return_no_rows_for_a_zero_page_size()
    {
        // Direct proof that a zero-size cursor page is correct at the SQL level, independent of the
        // hydration batch's zero-limit short-circuit, which keys off the traditional Limit role only.
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            pageSize: 0L
        );

        ids.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_return_only_the_lowest_in_range_id_for_a_page_size_of_one()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: 25L,
            pageSize: 1L
        );

        ids.Should().Equal(30L);
    }

    [Test]
    public async Task It_should_select_every_candidate_for_the_maximum_page_size()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            pageSize: 500L
        );

        ids.Should().Equal(_allDocumentIds);
    }

    [Test]
    public async Task It_should_select_every_candidate_for_extreme_int64_bounds()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: long.MinValue,
            cursorMaximum: long.MaxValue
        );

        ids.Should().Equal(_allDocumentIds);
    }

    [Test]
    public async Task It_should_execute_the_unpaged_candidate_relation_inside_a_common_table_expression()
    {
        // The partition consumer wraps this relation in a CTE and supplies its own ordering. A candidate
        // relation that emitted its own ORDER BY would still run here on PostgreSQL but not on SQL Server,
        // so the wrapped form is executed on both providers.
        var plan = Compile(new PageCandidateMode.UnpagedCandidates(), authorization: null);
        var wrappedSql =
            $"WITH candidates AS (\n{plan.PageDocumentIdSql.TrimEnd().TrimEnd(';')}\n)\n"
            + "SELECT \"DocumentId\", ROW_NUMBER() OVER (ORDER BY \"DocumentId\") AS row_number, "
            + "COUNT(*) OVER () AS candidate_count FROM candidates ORDER BY \"DocumentId\";";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(wrappedSql, connection);

        BindParameters(command, plan, BuildParameterValues(new PageCandidateMode.UnpagedCandidates(), null));

        List<long> ids = [];
        long candidateCount = 0;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
            candidateCount = reader.GetInt64(2);
        }

        ids.Should().Equal(_allDocumentIds);
        candidateCount.Should().Be(_allDocumentIds.Length);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<long>> SelectCandidateIdsAsync(
        PageCandidateMode mode,
        PageDocumentIdAuthorizationSpec? authorization,
        long cursorMinimum = long.MinValue,
        long cursorMaximum = long.MaxValue,
        long pageSize = 500L
    )
    {
        var plan = Compile(mode, authorization);

        return await SelectPlannedIdsAsync(
            plan,
            BuildParameterValues(mode, authorization, cursorMinimum, cursorMaximum, pageSize)
        );
    }

    private async Task<IReadOnlyList<long>> SelectPlannedIdsAsync(
        PageDocumentIdSqlPlan plan,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(plan.PageDocumentIdSql, connection);

        BindParameters(command, plan, parameterValues);

        List<long> ids = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static void BindParameters(
        NpgsqlCommand command,
        PageDocumentIdSqlPlan plan,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        // Npgsql infers bigint[] and text[] from the CLR array the parameterization supplies, so array
        // and scalar roles bind through the same call.
        foreach (var parameter in plan.PageParametersInOrder)
        {
            command.Parameters.AddWithValue(
                parameter.ParameterName,
                parameterValues[parameter.ParameterName]!
            );
        }
    }

    private static IReadOnlyDictionary<string, object?> BuildParameterValues(
        PageCandidateMode mode,
        PageDocumentIdAuthorizationSpec? authorization,
        long cursorMinimum = long.MinValue,
        long cursorMaximum = long.MaxValue,
        long pageSize = 500L
    )
    {
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal);

        switch (mode)
        {
            case PageCandidateMode.Traditional traditional:
                parameterValues[traditional.OffsetParameterName] = 0L;
                parameterValues[traditional.LimitParameterName] = pageSize;
                break;
            case PageCandidateMode.Cursor cursor:
                parameterValues[cursor.InclusiveMinimumParameterName] = cursorMinimum;
                parameterValues[cursor.InclusiveMaximumParameterName] = cursorMaximum;
                parameterValues[cursor.PageSizeParameterName] = pageSize;
                break;
        }

        if (authorization?.ClaimEducationOrganizationIdParameterization is { } claimParameterization)
        {
            AuthorizationClaimEducationOrganizationIdParameterValues.AddTo(
                parameterValues,
                claimParameterization
            );
        }

        NamespacePrefixParameterValueBinder.Bind(
            parameterValues,
            authorization?.NamespacePrefixParameterization
        );

        return parameterValues;
    }

    private static PageDocumentIdSqlPlan Compile(
        PageCandidateMode mode,
        PageDocumentIdAuthorizationSpec? authorization
    )
    {
        return new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
            new PageDocumentIdQuerySpec(
                RootTable: _rootTable,
                Predicates: [],
                UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                Mode: mode,
                Authorization: authorization
            )
        );
    }

    private static IReadOnlyList<PageCandidateMode> BuildEveryCandidateMode() =>
        [
            new PageCandidateMode.Traditional(),
            new PageCandidateMode.Cursor(),
            new PageCandidateMode.UnpagedCandidates(),
        ];

    /// <summary>
    /// The traditional and cursor paging choices a live collection request can carry. Both select every
    /// candidate the filters allow, so the expected membership isolates filtering from paging.
    /// </summary>
    private static IReadOnlyList<CollectionPaging> BuildEveryPagingChoice() =>
        [
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 500, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            new CollectionPaging.Cursor(new CursorRange(long.MinValue, long.MaxValue), new PageSize(500)),
        ];

    /// <summary>
    /// The authorization shapes the regular-resource planner supports: no further restrictions, a
    /// relationship strategy, a namespace check, and a custom view check.
    /// </summary>
    private static IReadOnlyList<(
        string Description,
        PageDocumentIdAuthorizationSpec? Authorization
    )> BuildRegularResourceAuthorizationMatrix() =>
        [
            ("no further restrictions", null),
            (
                "relationship authorization on a root EducationOrganization subject",
                CandidateProbeAuthorizationSpecs.RelationshipEdOrg(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _schoolIdColumn,
                    _claimEducationOrganizationIds
                )
            ),
            (
                "namespace authorization",
                CandidateProbeAuthorizationSpecs.Namespace(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _namespaceColumn,
                    AuthorizedNamespacePrefix
                )
            ),
            (
                "single-step custom view authorization",
                CandidateProbeAuthorizationSpecs.SingleStepCustomView(
                    _rootTable,
                    _documentIdColumn,
                    _customViewTable
                )
            ),
        ];

    /// <summary>
    /// The authorization shapes the descriptor planner supports: no further restrictions, a namespace
    /// check, a custom view check, and the two combined. Only relationship strategies are absent,
    /// because the descriptor path compiles no relationship group and binds no claim
    /// EducationOrganization values. Custom-view checks need no claim binding and do reach this
    /// consumer: the descriptor read handler passes its configured checks straight into the shared
    /// authorization spec.
    /// </summary>
    private static IReadOnlyList<(
        string Description,
        PageDocumentIdAuthorizationSpec? Authorization,
        long[] ExpectedIds
    )> BuildDescriptorAuthorizationMatrix() =>
        [
            (
                "no further restrictions",
                null,
                [.. _authorizedDescriptorDocumentIds, UnauthorizedNamespaceDescriptorDocumentId]
            ),
            (
                "namespace authorization",
                CandidateProbeAuthorizationSpecs.Namespace(
                    SqlDialect.Pgsql,
                    _descriptorTable,
                    _namespaceColumn,
                    AuthorizedNamespacePrefix
                ),
                _authorizedDescriptorDocumentIds
            ),
            (
                "single-step custom view authorization",
                CandidateProbeAuthorizationSpecs.SingleStepCustomView(
                    _descriptorTable,
                    _documentIdColumn,
                    _descriptorCustomViewTable
                ),
                _authorizedDescriptorDocumentIds
            ),
            (
                "namespace and custom view authorization combined",
                CandidateProbeAuthorizationSpecs.NamespaceAndCustomView(
                    SqlDialect.Pgsql,
                    _descriptorTable,
                    _namespaceColumn,
                    _documentIdColumn,
                    _descriptorCustomViewTable,
                    AuthorizedNamespacePrefix
                ),
                _authorizedDescriptorDocumentIds
            ),
        ];

    /// <summary>
    /// Every authorization shape the candidate compiler emits. <c>OwnershipBased</c> is deliberately
    /// absent: it is known but not enabled for GET-many and fails closed in the authorization planner, so
    /// no candidate SQL is ever compiled for it.
    /// </summary>
    private static IReadOnlyList<(
        string Description,
        PageDocumentIdAuthorizationSpec? Authorization
    )> BuildAuthorizationMatrix() =>
        [
            ("no further restrictions", null),
            (
                "relationship authorization on a root EducationOrganization subject",
                CandidateProbeAuthorizationSpecs.RelationshipEdOrg(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _schoolIdColumn,
                    _claimEducationOrganizationIds
                )
            ),
            (
                "two OR-combined relationship strategies matching the same root",
                CandidateProbeAuthorizationSpecs.TwoRelationshipStrategies(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _schoolIdColumn,
                    _claimEducationOrganizationIds
                )
            ),
            (
                "relationship authorization on a self person subject",
                CandidateProbeAuthorizationSpecs.RelationshipSelfPerson(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _documentIdColumn,
                    _claimEducationOrganizationIds
                )
            ),
            (
                "namespace authorization",
                CandidateProbeAuthorizationSpecs.Namespace(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _namespaceColumn,
                    AuthorizedNamespacePrefix
                )
            ),
            (
                "single-step custom view authorization",
                CandidateProbeAuthorizationSpecs.SingleStepCustomView(
                    _rootTable,
                    _documentIdColumn,
                    _customViewTable
                )
            ),
            (
                "multi-step custom view authorization through a duplicating child table",
                CandidateProbeAuthorizationSpecs.MultiStepCustomView(
                    _rootTable,
                    _documentIdColumn,
                    _childTable,
                    _studentDocumentIdColumn,
                    _customViewTable
                )
            ),
            (
                "namespace and custom view authorization combined",
                CandidateProbeAuthorizationSpecs.NamespaceAndCustomView(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _namespaceColumn,
                    _documentIdColumn,
                    _customViewTable,
                    AuthorizedNamespacePrefix
                )
            ),
        ];

    private static IReadOnlyList<string> BuildSchemaStatements()
    {
        var personAuthObject = CandidateProbeAuthorizationSpecs.SelfPersonAuthObject;
        var edOrgAuthObject = CandidateProbeAuthorizationSpecs.EdOrgAuthObject;

        return
        [
            "CREATE SCHEMA IF NOT EXISTS edfi;",
            "CREATE SCHEMA IF NOT EXISTS auth;",
            "CREATE SCHEMA IF NOT EXISTS dms;",
            $"""
                CREATE TABLE {Quote(_rootTable)} (
                    "DocumentId" bigint PRIMARY KEY,
                    "SchoolId" bigint NOT NULL,
                    "Namespace" varchar(255) NOT NULL,
                    "ContentVersion" bigint NOT NULL
                );
                """,
            $"""
                CREATE TABLE {Quote(_childTable)} (
                    "DocumentId" bigint NOT NULL,
                    "Student_DocumentId" bigint NOT NULL
                );
                """,
            $"CREATE TABLE {Quote(_customViewTable)} (\"DocumentId\" bigint NOT NULL);",
            $"CREATE TABLE {Quote(_descriptorCustomViewTable)} (\"DocumentId\" bigint NOT NULL);",
                // The descriptor consumer's real root, carrying the columns its discriminator, filters,
                // change-version window, and namespace authorization read.
                $"""
                CREATE TABLE {Quote(_descriptorTable)} (
                    "DocumentId" bigint PRIMARY KEY,
                    "ResourceKeyId" smallint NOT NULL,
                    "Namespace" varchar(255) NOT NULL,
                    "CodeValue" varchar(50) NOT NULL,
                    "ContentVersion" bigint NOT NULL
                );
                """,
                // The production authorization hierarchy table, with the composite primary key the generated
                // DDL declares. Fan-out must come from distinct legal tuples, never from a duplicate this
                // constraint forbids.
                $"""
                CREATE TABLE {Quote(edOrgAuthObject.Name)} (
                    "SourceEducationOrganizationId" bigint NOT NULL,
                    "TargetEducationOrganizationId" bigint NOT NULL,
                    CONSTRAINT "PK_EducationOrganizationIdToEducationOrganizationId"
                        PRIMARY KEY ("SourceEducationOrganizationId", "TargetEducationOrganizationId")
                );
                """,
                // The person auth object is a view over this association in production, so the probe supplies
                // the association rather than standing the view up as a base table.
                $"""
                CREATE TABLE {Quote(_studentSchoolAssociationTable)} (
                    "Student_DocumentId" bigint NOT NULL,
                    "SchoolId_Unified" bigint NOT NULL
                );
                """,
            $"""
                INSERT INTO {Quote(_rootTable)} ("DocumentId", "SchoolId", "Namespace", "ContentVersion")
                SELECT id, {DirectClaimEducationOrganizationId}, '{AuthorizedNamespace}', id
                FROM (VALUES (10),(20),(30),(40),(50)) AS seed(id);
                """,
            $"""
                INSERT INTO {Quote(_descriptorTable)} (
                    "DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ContentVersion"
                )
                VALUES
                    (110, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{AuthorizedNamespace}', '{MatchingDescriptorCodeValue}', 110),
                    (120, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{AuthorizedNamespace}', '{MatchingDescriptorCodeValue}', 120),
                    (130, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{AuthorizedNamespace}', '{MatchingDescriptorCodeValue}', 130),
                    (140, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{AuthorizedNamespace}', '{MatchingDescriptorCodeValue}', 140),
                    (150, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{AuthorizedNamespace}', '{MatchingDescriptorCodeValue}', 150),
                    ({UnrelatedResourceKeyDescriptorDocumentId}, {CandidateProbePlannerInputs.UnrelatedDescriptorResourceKeyId}, '{AuthorizedNamespace}', '{MatchingDescriptorCodeValue}', {UnrelatedResourceKeyDescriptorDocumentId}),
                    ({UnauthorizedNamespaceDescriptorDocumentId}, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{UnauthorizedNamespace}', '{MatchingDescriptorCodeValue}', {UnauthorizedNamespaceDescriptorDocumentId}),
                    ({NonMatchingCodeValueDescriptorDocumentId}, {CandidateProbePlannerInputs.DescriptorResourceKeyId}, '{AuthorizedNamespace}', '{NonMatchingDescriptorCodeValue}', {NonMatchingCodeValueDescriptorDocumentId});
                """,
            $"""
                INSERT INTO {Quote(_childTable)} ("DocumentId", "Student_DocumentId")
                SELECT r."DocumentId", r."DocumentId"
                FROM {Quote(_rootTable)} r
                CROSS JOIN (VALUES (1),(2)) AS duplicate(n);
                """,
            $"""
                INSERT INTO {Quote(_customViewTable)} ("DocumentId")
                SELECT r."DocumentId"
                FROM {Quote(_rootTable)} r
                CROSS JOIN (VALUES (1),(2)) AS duplicate(n);
                """,
                // Two rows per authorized descriptor, and deliberately no row for the unauthorized-namespace
                // descriptor. The duplication proves a join-based implementation would multiply a descriptor
                // candidate; the omission makes the custom-view case exclude a row that passes both the
                // ResourceKeyId discriminator and the CodeValue filter, so the check demonstrably restricts.
                $"""
                INSERT INTO {Quote(_descriptorCustomViewTable)} ("DocumentId")
                SELECT d."DocumentId"
                FROM {Quote(_descriptorTable)} d
                CROSS JOIN (VALUES (1),(2)) AS duplicate(n)
                WHERE d."DocumentId" IN ({AuthorizedDescriptorDocumentIdList});
                """,
                // Three distinct production-legal tuples, all admitted by the composite primary key. The
                // normal direction reads Target as its subject, so the first two fan into the subject
                // value the roots carry; the inverted direction reads Source, so the first and third do.
                // Each direction needs its own pair: one direction's fan-out is not the other's.
                $"""
                INSERT INTO {Quote(edOrgAuthObject.Name)} (
                    "SourceEducationOrganizationId", "TargetEducationOrganizationId"
                )
                VALUES
                    ({DirectClaimEducationOrganizationId}, {DirectClaimEducationOrganizationId}),
                    ({IndirectClaimEducationOrganizationId}, {DirectClaimEducationOrganizationId}),
                    ({DirectClaimEducationOrganizationId}, {IndirectClaimEducationOrganizationId});
                """,
            $"""
                INSERT INTO {Quote(_studentSchoolAssociationTable)} ("Student_DocumentId", "SchoolId_Unified")
                SELECT r."DocumentId", {DirectClaimEducationOrganizationId}
                FROM {Quote(_rootTable)} r;
                """,
                // The production person auth view, verbatim in shape: DISTINCT over the hierarchy joined to
                // the association. Its two hierarchy sources still expose two rows per person, so the fan-out
                // survives the DISTINCT exactly as production would produce it.
                $"""
                CREATE VIEW {Quote(personAuthObject.Name)} AS
                SELECT DISTINCT
                    edOrg."SourceEducationOrganizationId",
                    ssa."Student_DocumentId"
                FROM {Quote(edOrgAuthObject.Name)} edOrg
                INNER JOIN {Quote(_studentSchoolAssociationTable)} ssa
                    ON edOrg."TargetEducationOrganizationId" = ssa."SchoolId_Unified";
                """,
        ];
    }

    private static string Quote(DbTableName table) => $"\"{table.Schema.Value}\".\"{table.Name}\"";
}
