// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// AC3 plan shape, at CI scale, on every pull request. EXPLAINs the page SQL the production compiler emits and
/// asserts the anchored predicate reads the root relation once and never materializes a set drawn from it.
/// </summary>
/// <remarks>
/// <para>
/// The subject is always production's <c>Compile()</c> output. The test-owned emitter appears here only in its
/// Legacy mode, as the non-vacuity control: a plan-shape assertion that no plan could fail proves nothing, so
/// every structural claim below is paired with a run against the shape DMS-1331 replaces, which must exhibit the
/// second root scan.
/// </para>
/// <para>
/// These assertions are volume-independent — PostgreSQL never collapses the primary-key self-join at any row
/// count, so the second scan is present in the old shape and absent in the new one whether the table holds ten
/// thousand rows or ten million. CI scale is therefore enough, and only the ticket's literal <c>OFFSET 100000</c>
/// and the before/after timings need the larger data set.
/// </para>
/// <para>
/// Two preconditions keep the measurement from being green but empty. The fixture owns its own database and runs
/// the generator against it rather than assuming another fixture's rows exist; and every test counts the rows the
/// authorization predicate actually admits before its first EXPLAIN, requiring more than the offset plus the page
/// limit, so the plan under measurement is a real page. Statistics are refreshed by the generator itself, which
/// ANALYZEs every table it wrote — including the base tables of
/// <c>auth.EducationOrganizationIdToStudentDocumentId</c>, which is a view and cannot be analyzed directly —
/// before returning.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationshipAuthorizationVolume")]
public class Given_A_Postgresql_Anchored_Authorization_Query_Plan
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int PageLimit = 100;

    /// <summary>
    /// Deep enough to be a real page and far short of the 8000 authorized rows the CI-scale generator produces,
    /// so the plan being measured is never an empty page past the last authorized row.
    /// </summary>
    private const int PageOffset = 4000;

    /// <summary>
    /// One direct-column subject and one transitive subject — the two shapes the rewrite changes. Both are
    /// resources the ticket names.
    /// </summary>
    private const string DirectSubjectResourceName = "StudentSectionAssociation";
    private const string TransitiveSubjectResourceName = "Grade";

    /// <summary>
    /// The node types that hold a row set in memory. PostgreSQL reports a hash aggregate as node type
    /// <c>Aggregate</c> with a hashed strategy, and it picks whichever of these fits the semi-join it is given —
    /// the observed legacy plans use <c>Sort</c> where the ticket's prototype reported a hash — so the set is
    /// wider than the two names AC3 happens to mention.
    /// </summary>
    private static readonly string[] _setHoldingNodeTypes =
    [
        "Hash",
        "Aggregate",
        "Sort",
        "Materialize",
        "Memoize",
    ];

    private static readonly long[] _claim =
    [
        RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId,
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    public static IEnumerable<string> SubjectResourceNames =>
        [DirectSubjectResourceName, TransitiveSubjectResourceName];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true);
        await PostgresqlRelationalQueryAuthorizationVolumeGenerator.GenerateAsync(
            _context,
            RelationshipAuthorizationVolumeCounts.Ci
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    /// <summary>
    /// AC3 on production's own SQL: the root relation is read once, nothing materializes a set drawn from it,
    /// and both the person and the claim predicate reached the plan as conditions rather than being applied
    /// above a materialized intermediate.
    /// </summary>
    [TestCaseSource(nameof(SubjectResourceNames))]
    public async Task It_should_read_the_root_relation_once_in_the_anchored_page_plan(string resourceName)
    {
        var spec = SpecFor(resourceName);
        var productionPlan = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(spec.QuerySpec);

        await AssertAuthorizedRowPreconditionAsync(productionPlan, resourceName);

        var plan = await ExplainJsonAsync(
            $"production Compile() page SQL — {resourceName}",
            productionPlan.PageDocumentIdSql
        );
        var rootRelationName = spec.RootTable.Name;

        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(plan, rootRelationName)
            .Should()
            .ContainSingle(
                $"the anchored predicate reads '{rootRelationName}' from the root row, so the page plan scans it "
                    + "exactly once"
            );

        FindInnerSideMaterializationsOfRootRelation(plan, rootRelationName)
            .Should()
            .BeEmpty(
                $"nothing may hold a set drawn from '{rootRelationName}' as a join's inner side — that set of "
                    + "every authorized DocumentId, rebuilt on every page, is what DMS-1331 removes"
            );

        var conditions = PostgresqlQueryPlanNavigator.CollectConditionText(plan);
        conditions
            .Should()
            .Contain(
                text => text.Contains("Student_DocumentId"),
                "the person predicate must reach the plan as a condition"
            );
        conditions
            .Should()
            .Contain(
                text => text.Contains("SourceEducationOrganizationId"),
                "the claim predicate must reach the plan as a condition"
            );
    }

    /// <summary>
    /// The non-vacuity control. Both structural claims above are run against the primary-key self-join DMS-1331
    /// replaces, and both must come out the other way there: the root relation appears twice, and a set drawn
    /// from it is held as a join's inner side. Without this, a plan-shape assertion that no plan could fail would
    /// read as evidence.
    /// </summary>
    [TestCaseSource(nameof(SubjectResourceNames))]
    public async Task It_should_read_the_root_relation_twice_in_the_legacy_page_plan(string resourceName)
    {
        var spec = SpecFor(resourceName);
        var productionPlan = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(spec.QuerySpec);

        await AssertAuthorizedRowPreconditionAsync(productionPlan, resourceName);

        var legacySql = RelationshipAuthorizationDifferentialSqlEmitter
            .Emit(
                spec.QuerySpec,
                SqlDialect.Pgsql,
                RelationshipAuthorizationPredicateShape.Legacy,
                _claim.Length
            )
            .PageSql;
        var plan = await ExplainJsonAsync($"legacy self-join page SQL — {resourceName}", legacySql);
        var rootRelationName = spec.RootTable.Name;

        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(plan, rootRelationName)
            .Should()
            .HaveCountGreaterThanOrEqualTo(
                2,
                $"the legacy predicate reopens '{rootRelationName}' in a primary-key self-join, which PostgreSQL "
                    + "does not collapse — without this the single-scan assertion would prove nothing"
            );

        FindInnerSideMaterializationsOfRootRelation(plan, rootRelationName)
            .Should()
            .NotBeEmpty(
                $"the legacy predicate feeds its semi-join a set drawn from '{rootRelationName}' — without this "
                    + "the no-materialization assertion would prove nothing"
            );
    }

    /// <summary>
    /// The authorized-DocumentId set the ticket measured at 15 MB is a set drawn from the root relation and held
    /// as the semi-join's inner side. Anything that holds rows qualifies, because the planner picks the operator:
    /// the observed legacy plans hold it in a <c>Sort</c> under a merge semi-join where the prototype reported a
    /// hash. Scoping to the inner side is what separates that from the page's own keyset <c>Sort</c>, which sits
    /// on the outer path in the anchored plan and is the legitimate cost of ordering a page.
    /// </summary>
    private static IReadOnlyList<JsonElement> FindInnerSideMaterializationsOfRootRelation(
        JsonElement plan,
        string rootRelationName
    ) =>
        [
            .. PostgresqlQueryPlanNavigator
                .CollectNodes(plan)
                .Where(IsInnerSideSetHoldingNode)
                .Where(node =>
                    PostgresqlQueryPlanNavigator.FindAllRelationScanPaths(node, rootRelationName).Count > 0
                ),
        ];

    private static bool IsInnerSideSetHoldingNode(JsonElement node) =>
        _setHoldingNodeTypes.Contains(PostgresqlQueryPlanNavigator.GetNodeType(node))
        && node.TryGetProperty("Parent Relationship", out var parentRelationship)
        && parentRelationship.GetString() == "Inner";

    /// <summary>
    /// Counts the rows the authorization predicate admits and requires more than the offset plus the page limit,
    /// so a generator regression fails here instead of quietly turning the EXPLAIN below into an empty page.
    /// </summary>
    private async Task AssertAuthorizedRowPreconditionAsync(
        PageDocumentIdSqlPlan productionPlan,
        string resourceName
    )
    {
        productionPlan.TotalCountSql.Should().NotBeNull();

        var authorizedRowCount = await _context.Database.ExecuteScalarAsync<long>(
            productionPlan.TotalCountSql!,
            ClaimParameter()
        );

        authorizedRowCount
            .Should()
            .BeGreaterThan(
                PageOffset + PageLimit,
                $"the measured page must sit inside the authorized {resourceName} rows, not past the last one"
            );
    }

    private static RelationshipAuthorizationDifferentialSpec SpecFor(string resourceName) =>
        RelationshipAuthorizationDifferentialSpecs
            .Create(SqlDialect.Pgsql, _claim)
            .Single(spec => spec.ResourceName == resourceName);

    private async Task<JsonElement> ExplainJsonAsync(string label, string sql)
    {
        var rows = await _context.Database.QueryRowsAsync(
            $"EXPLAIN (FORMAT JSON) {sql}",
            ClaimParameter(),
            new NpgsqlParameter("offset", PageOffset),
            new NpgsqlParameter("limit", PageLimit)
        );
        rows.Should().ContainSingle();

        var explainJson = rows[0]["QUERY PLAN"]?.ToString();
        explainJson.Should().NotBeNullOrEmpty();

        // Retained as the AC3 plan evidence to report in the pull request.
        await TestContext.Out.WriteLineAsync($"--- {label} (offset {PageOffset}, limit {PageLimit}) ---");
        await TestContext.Out.WriteLineAsync(explainJson);

        using var document = JsonDocument.Parse(explainJson!);
        return document.RootElement[0].GetProperty("Plan").Clone();
    }

    private static NpgsqlParameter ClaimParameter() =>
        new(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds, _claim);
}
