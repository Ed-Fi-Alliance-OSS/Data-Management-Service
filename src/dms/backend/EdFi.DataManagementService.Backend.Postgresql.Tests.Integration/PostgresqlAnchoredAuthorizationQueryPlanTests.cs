// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>One EXPLAIN result: the plan tree plus the measurements only ANALYZE produces.</summary>
internal sealed record ExplainedPlan(
    JsonElement Plan,
    string Json,
    double ExecutionTimeMilliseconds,
    long SharedBlocks
);

/// <summary>
/// The pieces the two DMS-1331 plan fixtures share: the subjects under measurement, the two SQL producers, the
/// EXPLAIN mechanics, and the structural claims AC3 makes about the anchored shape.
/// </summary>
internal static class AnchoredAuthorizationPlanSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    public const int PageLimit = 100;

    /// <summary>
    /// One direct-column subject and one transitive subject — the two shapes the rewrite changes. Both are
    /// resources the ticket names.
    /// </summary>
    /// <remarks>
    /// Constraint on anything added here: a subject's root table must not also be a base relation of its own
    /// authorization view. <see cref="AssertAnchoredShape"/> counts scans by the unqualified
    /// <c>Relation Name</c> PostgreSQL reports, so a root the inlined view scans too would be counted twice and
    /// fail as a self-join regression that is not one. Student is the shape that trips it — the student auth
    /// view reads StudentSchoolAssociation — and the same holds for any name <c>tracked_changes_edfi</c>
    /// mirrors. The row-set differential carries such subjects instead; it counts occurrences in SQL text,
    /// where the schema is present.
    /// </remarks>
    public const string DirectSubjectResourceName = "StudentSectionAssociation";
    public const string TransitiveSubjectResourceName = "Grade";

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

    public static readonly long[] Claim =
    [
        RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId,
    ];

    public static IEnumerable<string> SubjectResourceNames =>
        [DirectSubjectResourceName, TransitiveSubjectResourceName];

    public static RelationshipAuthorizationDifferentialSpec SpecFor(string resourceName) =>
        RelationshipAuthorizationDifferentialSpecs
            .Create(SqlDialect.Pgsql, Claim)
            .Single(spec => spec.ResourceName == resourceName);

    /// <summary>The "after" subject, always: the SQL the product itself emits.</summary>
    public static PageDocumentIdSqlPlan CompileProduction(RelationshipAuthorizationDifferentialSpec spec) =>
        new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(spec.QuerySpec);

    public static string EmitPageSql(
        RelationshipAuthorizationDifferentialSpec spec,
        RelationshipAuthorizationPredicateShape shape
    ) =>
        RelationshipAuthorizationDifferentialSqlEmitter
            .Emit(spec.QuerySpec, SqlDialect.Pgsql, shape, Claim.Length)
            .PageSql;

    public static async Task<ExplainedPlan> ExplainAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string sql,
        int offset,
        bool analyze = false
    )
    {
        var options = analyze ? "ANALYZE, BUFFERS, FORMAT JSON" : "FORMAT JSON";
        var rows = await database.QueryRowsAsync(
            $"EXPLAIN ({options}) {sql}",
            ClaimParameter(),
            new NpgsqlParameter("offset", offset),
            new NpgsqlParameter("limit", PageLimit)
        );
        rows.Should().ContainSingle();

        var explainJson = rows[0]["QUERY PLAN"]?.ToString();
        explainJson.Should().NotBeNullOrEmpty();

        using var document = JsonDocument.Parse(explainJson!);
        var root = document.RootElement[0];
        var plan = root.GetProperty("Plan");

        return new ExplainedPlan(
            plan.Clone(),
            explainJson!,
            ReadDouble(root, "Execution Time"),
            (long)ReadDouble(plan, "Shared Hit Blocks") + (long)ReadDouble(plan, "Shared Read Blocks")
        );
    }

    /// <summary>
    /// The two structural claims AC3 makes about the anchored shape: the root relation is read once, and nothing
    /// holds a set drawn from it as a join's inner side.
    /// </summary>
    public static void AssertAnchoredShape(JsonElement plan, string rootRelationName, string context)
    {
        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(plan, rootRelationName)
            .Should()
            .ContainSingle(
                $"the anchored predicate reads '{rootRelationName}' from the root row, so the page plan scans it "
                    + $"exactly once ({context})"
            );

        FindInnerSideMaterializationsOfRootRelation(plan, rootRelationName)
            .Should()
            .BeEmpty(
                $"nothing may hold a set drawn from '{rootRelationName}' as a join's inner side — that set of "
                    + $"every authorized DocumentId, rebuilt on every page, is what DMS-1331 removes ({context})"
            );
    }

    /// <summary>
    /// The authorized-DocumentId set the ticket measured at 15 MB is a set drawn from the root relation and held
    /// as the semi-join's inner side. Anything that holds rows qualifies, because the planner picks the operator:
    /// the observed legacy plans hold it in a <c>Sort</c> under a merge semi-join where the prototype reported a
    /// hash. Scoping to the inner side is what separates that from the page's own keyset <c>Sort</c>, which sits
    /// on the outer path in the anchored plan and is the legitimate cost of ordering a page.
    /// </summary>
    public static IReadOnlyList<JsonElement> FindInnerSideMaterializationsOfRootRelation(
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

    /// <summary>
    /// Counts the rows the authorization predicate admits and requires more than the deepest offset plus the page
    /// limit, so a generator regression fails here instead of quietly turning an EXPLAIN into an empty page.
    /// </summary>
    public static async Task AssertAuthorizedRowPreconditionAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        PageDocumentIdSqlPlan productionPlan,
        string resourceName,
        int deepestOffset
    )
    {
        productionPlan.TotalCountSql.Should().NotBeNull();

        var authorizedRowCount = await database.ExecuteScalarAsync<long>(
            productionPlan.TotalCountSql!,
            ClaimParameter()
        );

        authorizedRowCount
            .Should()
            .BeGreaterThan(
                deepestOffset + PageLimit,
                $"the measured page must sit inside the authorized {resourceName} rows, not past the last one"
            );
    }

    public static NpgsqlParameter ClaimParameter() =>
        new(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds, Claim);

    private static bool IsInnerSideSetHoldingNode(JsonElement node) =>
        _setHoldingNodeTypes.Contains(PostgresqlQueryPlanNavigator.GetNodeType(node))
        && node.TryGetProperty("Parent Relationship", out var parentRelationship)
        && parentRelationship.GetString() == "Inner";

    private static double ReadDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetDouble() : 0;
}

/// <summary>
/// AC3 plan shape, at CI scale, on every pull request. EXPLAINs the page SQL the production compiler emits and
/// asserts the anchored predicate reads the root relation once and never materializes a set drawn from it.
/// </summary>
/// <remarks>
/// <para>
/// The subject is always production's <c>Compile()</c> output. The test-owned emitter appears here only in its
/// Legacy mode, as the non-vacuity control: a plan-shape assertion that no plan could fail proves nothing, so
/// every structural claim below is paired with a run against the shape DMS-1331 replaces, which must come out the
/// other way.
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
    /// <summary>
    /// Deep enough to be a real page and far short of the 8000 authorized rows the CI-scale generator produces,
    /// so the plan being measured is never an empty page past the last authorized row.
    /// </summary>
    private const int PageOffset = 4000;

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    public static IEnumerable<string> SubjectResourceNames =>
        AnchoredAuthorizationPlanSupport.SubjectResourceNames;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(AnchoredAuthorizationPlanSupport.FixtureRelativePath, strict: true);
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
        var spec = AnchoredAuthorizationPlanSupport.SpecFor(resourceName);
        var productionPlan = AnchoredAuthorizationPlanSupport.CompileProduction(spec);

        await AnchoredAuthorizationPlanSupport.AssertAuthorizedRowPreconditionAsync(
            _context.Database,
            productionPlan,
            resourceName,
            PageOffset
        );

        var explained = await ExplainAsync(
            $"production Compile() page SQL — {resourceName}",
            productionPlan.PageDocumentIdSql
        );

        AnchoredAuthorizationPlanSupport.AssertAnchoredShape(
            explained.Plan,
            spec.RootTable.Name,
            $"{resourceName} at offset {PageOffset}"
        );

        var conditions = PostgresqlQueryPlanNavigator.CollectConditionText(explained.Plan);
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
        var spec = AnchoredAuthorizationPlanSupport.SpecFor(resourceName);
        var productionPlan = AnchoredAuthorizationPlanSupport.CompileProduction(spec);

        await AnchoredAuthorizationPlanSupport.AssertAuthorizedRowPreconditionAsync(
            _context.Database,
            productionPlan,
            resourceName,
            PageOffset
        );

        var legacySql = AnchoredAuthorizationPlanSupport.EmitPageSql(
            spec,
            RelationshipAuthorizationPredicateShape.Legacy
        );
        var explained = await ExplainAsync($"legacy self-join page SQL — {resourceName}", legacySql);
        var rootRelationName = spec.RootTable.Name;

        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(explained.Plan, rootRelationName)
            .Should()
            .HaveCountGreaterThanOrEqualTo(
                2,
                $"the legacy predicate reopens '{rootRelationName}' in a primary-key self-join, which PostgreSQL "
                    + "does not collapse — without this the single-scan assertion would prove nothing"
            );

        AnchoredAuthorizationPlanSupport
            .FindInnerSideMaterializationsOfRootRelation(explained.Plan, rootRelationName)
            .Should()
            .NotBeEmpty(
                $"the legacy predicate feeds its semi-join a set drawn from '{rootRelationName}' — without this "
                    + "the no-materialization assertion would prove nothing"
            );
    }

    private async Task<ExplainedPlan> ExplainAsync(string label, string sql)
    {
        var explained = await AnchoredAuthorizationPlanSupport.ExplainAsync(
            _context.Database,
            sql,
            PageOffset
        );

        // Retained as the AC3 plan evidence to report in the pull request.
        await TestContext.Out.WriteLineAsync(
            $"--- {label} (offset {PageOffset}, limit {AnchoredAuthorizationPlanSupport.PageLimit}) ---"
        );
        await TestContext.Out.WriteLineAsync(explained.Json);

        return explained;
    }
}

/// <summary>
/// AC3's literal deep offset, and the before/after page timings, at 120,000 authorized plus 30,000 unauthorized
/// rows per root table. Explicit, because it is a measurement rather than a regression assertion: generating that
/// volume and running repeated <c>EXPLAIN (ANALYZE, BUFFERS)</c> is deliberate work, and the PostgreSQL job runs
/// this assembly unfiltered, so <c>[Explicit]</c> — not a category — is what keeps it off pull-request CI.
/// </summary>
/// <remarks>
/// <para>
/// The "after" arm is always production's <c>Compile()</c> page SQL. The test-owned emitter supplies the "before"
/// (Legacy) arm only. Its Anchored mode is never a plan or timing subject: the differential proves that mode
/// returns the same <i>rows</i> as production, which is all AC2 needs, and two different SQL texts can return
/// identical result sets under entirely different plans. Because before and after therefore come from two
/// different producers, a separate test asserts that the emitter's Anchored SQL plans the same shape as
/// production's at every measured offset, so the reported delta is attributable to the authorization predicate
/// rather than to emitter-versus-compiler scaffolding.
/// </para>
/// <para>
/// Absolute times are hardware-dependent and are reported as evidence, never asserted. The only assertions here
/// are the direction that must hold at any offset: the anchored shape adds no second root scan and holds no set
/// drawn from the root relation. Times come from the server's own <c>Execution Time</c> rather than client
/// wall-clock, so they exclude round-trip; they are also statement-level numbers over rows with no JSON payload,
/// so they exclude hydration and reconstitution entirely.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("Deep-offset plan and timing evidence for DMS-1331; run deliberately and report in the PR.")]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Anchored_Authorization_Deep_Offset_Measurement
{
    /// <summary>The offset AC3 names. The generated authorized population is sized to exceed it.</summary>
    private const int TicketOffset = 100_000;

    /// <summary>
    /// Deep-offset pages run in the low hundreds of milliseconds on a developer machine, where scheduling noise
    /// is a large fraction of that. Both the median and the fastest run are reported, because a single unlucky
    /// sample would otherwise become the number quoted in the pull request.
    /// </summary>
    private const int WarmupIterations = 2;
    private const int MeasuredIterations = 5;

    private static readonly int[] _measuredOffsets = [0, 50_000, TicketOffset];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    public static IEnumerable<string> SubjectResourceNames =>
        AnchoredAuthorizationPlanSupport.SubjectResourceNames;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(AnchoredAuthorizationPlanSupport.FixtureRelativePath, strict: true);
        await PostgresqlRelationalQueryAuthorizationVolumeGenerator.GenerateAsync(
            _context,
            RelationshipAuthorizationVolumeCounts.DeepOffset
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
    /// AC3 satisfied literally: the same structural claims the CI-scale fixture makes, re-run at
    /// <c>OFFSET 100000</c> on a page that really sits inside the authorized rows, with the legacy shape asserted
    /// at the same offset so the result still discriminates.
    /// </summary>
    [TestCaseSource(nameof(SubjectResourceNames))]
    public async Task It_should_keep_the_anchored_shape_at_the_ticket_offset(string resourceName)
    {
        var spec = AnchoredAuthorizationPlanSupport.SpecFor(resourceName);
        var productionPlan = AnchoredAuthorizationPlanSupport.CompileProduction(spec);
        var rootRelationName = spec.RootTable.Name;

        await AnchoredAuthorizationPlanSupport.AssertAuthorizedRowPreconditionAsync(
            _context.Database,
            productionPlan,
            resourceName,
            TicketOffset
        );

        var production = await ExplainAndReportAsync(
            $"production Compile() page SQL — {resourceName}",
            productionPlan.PageDocumentIdSql,
            TicketOffset
        );
        AnchoredAuthorizationPlanSupport.AssertAnchoredShape(
            production.Plan,
            rootRelationName,
            $"{resourceName} at offset {TicketOffset}"
        );

        var legacy = await ExplainAndReportAsync(
            $"legacy self-join page SQL — {resourceName}",
            AnchoredAuthorizationPlanSupport.EmitPageSql(
                spec,
                RelationshipAuthorizationPredicateShape.Legacy
            ),
            TicketOffset
        );

        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(legacy.Plan, rootRelationName)
            .Should()
            .HaveCountGreaterThanOrEqualTo(
                2,
                $"the legacy predicate reopens '{rootRelationName}' at the deep offset too — without this the "
                    + "single-scan assertion would prove nothing here either"
            );
    }

    /// <summary>
    /// The attribution guard. Before and after come from two different producers, so this pins that the emitter's
    /// Anchored SQL — the one the differential proved row-equivalent to production — also plans identically to
    /// production at every measured offset. A divergence here means the before/after delta could be measuring
    /// scaffolding rather than the predicate, and it fails loudly instead of being folded silently into the
    /// reported numbers.
    /// </summary>
    [TestCaseSource(nameof(SubjectResourceNames))]
    public async Task It_should_plan_the_emitter_anchored_page_sql_the_same_as_production(string resourceName)
    {
        var spec = AnchoredAuthorizationPlanSupport.SpecFor(resourceName);
        var productionSql = AnchoredAuthorizationPlanSupport.CompileProduction(spec).PageDocumentIdSql;
        var emitterSql = AnchoredAuthorizationPlanSupport.EmitPageSql(
            spec,
            RelationshipAuthorizationPredicateShape.Anchored
        );

        foreach (var offset in _measuredOffsets)
        {
            var production = await AnchoredAuthorizationPlanSupport.ExplainAsync(
                _context.Database,
                productionSql,
                offset
            );
            var emitter = await AnchoredAuthorizationPlanSupport.ExplainAsync(
                _context.Database,
                emitterSql,
                offset
            );

            PostgresqlQueryPlanNavigator
                .CollectNodeTypes(emitter.Plan)
                .Should()
                .Equal(
                    PostgresqlQueryPlanNavigator.CollectNodeTypes(production.Plan),
                    $"the emitter's anchored SQL must plan like production's at offset {offset} for "
                        + $"{resourceName}, or the reported delta is not attributable to the predicate"
                );
        }
    }

    /// <summary>
    /// The before/after evidence: server-side execution time and buffer counts for the legacy and the production
    /// shape at offset 0, a mid offset, and the ticket's deep offset. Nothing about elapsed time is asserted — the
    /// only assertion is the structural direction, re-checked at every offset.
    /// </summary>
    [TestCaseSource(nameof(SubjectResourceNames))]
    public async Task It_should_report_before_and_after_page_timings(string resourceName)
    {
        var spec = AnchoredAuthorizationPlanSupport.SpecFor(resourceName);
        var productionPlan = AnchoredAuthorizationPlanSupport.CompileProduction(spec);
        var legacySql = AnchoredAuthorizationPlanSupport.EmitPageSql(
            spec,
            RelationshipAuthorizationPredicateShape.Legacy
        );
        var rootRelationName = spec.RootTable.Name;

        await AnchoredAuthorizationPlanSupport.AssertAuthorizedRowPreconditionAsync(
            _context.Database,
            productionPlan,
            resourceName,
            TicketOffset
        );

        var report = new StringBuilder();
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"DMS-1331 deep-offset page timings — {resourceName}"
        );
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"population: {RelationshipAuthorizationVolumeCounts.DeepOffset.AuthorizedRowsPerRoot} authorized "
                + $"+ {RelationshipAuthorizationVolumeCounts.DeepOffset.UnauthorizedRowsPerRoot} unauthorized "
                + $"per root table; page limit {AnchoredAuthorizationPlanSupport.PageLimit}"
        );
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"measurement: EXPLAIN (ANALYZE, BUFFERS) server Execution Time, median of {MeasuredIterations} "
                + $"after {WarmupIterations} warm-up; absolute times are hardware-dependent evidence, not assertions"
        );
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"environment: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors"
        );
        report.AppendLine(
            "before = the legacy self-join shape; after = production's Compile() page SQL; "
                + "blocks = shared buffers hit plus read"
        );

        foreach (var offset in _measuredOffsets)
        {
            var before = await MeasureAsync(legacySql, offset);
            var after = await MeasureAsync(productionPlan.PageDocumentIdSql, offset);

            AnchoredAuthorizationPlanSupport.AssertAnchoredShape(
                after.Plan,
                rootRelationName,
                $"{resourceName} at offset {offset}"
            );

            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"offset {offset} | before median {before.MedianMilliseconds:F2} ms, fastest "
                    + $"{before.FastestMilliseconds:F2} ms, {before.SharedBlocks} blocks | after median "
                    + $"{after.MedianMilliseconds:F2} ms, fastest {after.FastestMilliseconds:F2} ms, "
                    + $"{after.SharedBlocks} blocks | delta median "
                    + $"{PercentDelta(before.MedianMilliseconds, after.MedianMilliseconds)}, fastest "
                    + $"{PercentDelta(before.FastestMilliseconds, after.FastestMilliseconds)}"
            );
        }

        report.AppendLine(
            "prototype reference (StudentSectionAssociation, perf rig 2026-07-23): -33% at offset 0, "
                + "-41% to -46% at deep offsets"
        );

        await TestContext.Out.WriteLineAsync(report.ToString());
    }

    /// <summary>
    /// Runs the statement under EXPLAIN (ANALYZE, BUFFERS) a few times and keeps the median execution time, so a
    /// single unlucky run does not become the reported number. The returned plan is the last run's, which is what
    /// the structural assertions are checked against.
    /// </summary>
    private async Task<PageTimingSample> MeasureAsync(string sql, int offset)
    {
        for (var iteration = 0; iteration < WarmupIterations; iteration++)
        {
            await AnchoredAuthorizationPlanSupport.ExplainAsync(
                _context.Database,
                sql,
                offset,
                analyze: true
            );
        }

        var executionTimes = new double[MeasuredIterations];
        ExplainedPlan explained = null!;

        for (var iteration = 0; iteration < MeasuredIterations; iteration++)
        {
            explained = await AnchoredAuthorizationPlanSupport.ExplainAsync(
                _context.Database,
                sql,
                offset,
                analyze: true
            );
            executionTimes[iteration] = explained.ExecutionTimeMilliseconds;
        }

        Array.Sort(executionTimes);

        return new PageTimingSample(
            executionTimes[MeasuredIterations / 2],
            executionTimes[0],
            explained.SharedBlocks,
            explained.Plan
        );
    }

    private async Task<ExplainedPlan> ExplainAndReportAsync(string label, string sql, int offset)
    {
        var explained = await AnchoredAuthorizationPlanSupport.ExplainAsync(_context.Database, sql, offset);

        await TestContext.Out.WriteLineAsync(
            $"--- {label} (offset {offset}, limit {AnchoredAuthorizationPlanSupport.PageLimit}) ---"
        );
        await TestContext.Out.WriteLineAsync(explained.Json);

        return explained;
    }

    private static string PercentDelta(double before, double after) =>
        before <= 0
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture, $"{(after - before) / before * 100:F1}%");

    private sealed record PageTimingSample(
        double MedianMilliseconds,
        double FastestMilliseconds,
        long SharedBlocks,
        JsonElement Plan
    );
}
