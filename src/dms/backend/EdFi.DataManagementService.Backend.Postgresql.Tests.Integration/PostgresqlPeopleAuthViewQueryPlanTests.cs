// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

// ═══════════════════════════════════════════════════════════════════
// People auth view query-plan evidence (DMS-1329, AC4)
//
// With SELECT DISTINCT removed from auth.EducationOrganizationIdToStudentDocumentId,
// PostgreSQL flattens the view into the probing query instead of materializing the
// client's full EdOrg→student set behind an unflattenable dedup node. These tests
// EXPLAIN (FORMAT JSON) the two audited consumer shapes and assert, scoped to the
// auth-view subtree (the EducationOrganizationIdToEducationOrganizationId and
// StudentSchoolAssociation scans):
//   1. the view is inlined — both base relations are scanned directly and no
//      Subquery Scan node remains anywhere in the plan, and
//   2. the person/claim predicates reached the plan (as Filter / Index Cond /
//      Hash Cond / Join Filter conditions), and
//   3. no Aggregate/HashAggregate/Unique node sits between those scans and the
//      root — the pre-change plans dedup-materialized the closure per probe.
//
// The dedup and subquery nodes are structural for a DISTINCT view (the planner must
// emit them regardless of row counts), so the assertions are meaningful at test scale.
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_People_Auth_View_Query_Plan
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    private const string EdOrgClosureRelationName = "EducationOrganizationIdToEducationOrganizationId";
    private const string StudentSchoolAssociationRelationName = "StudentSchoolAssociation";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000002")), 200, "East School"),
        new(new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000011")),
        2026,
        true,
        "2026"
    );

    private static readonly StudentSeed _dualEnrolledStudentSeed = new(
        new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000021")),
        "30001",
        "Dana",
        "Dual"
    );

    private static readonly StudentSeed _otherStudentSeed = new(
        new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000022")),
        "30002",
        "Uri",
        "Unreachable"
    );

    private static readonly StudentSchoolAssociationSeed[] _studentSchoolAssociationSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000031")),
            "30001",
            100,
            2026,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000032")),
            "30001",
            200,
            2026,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000033")),
            "30002",
            300,
            2026,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
    ];

    private static readonly StudentAcademicRecordSeed[] _studentAcademicRecordSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000041")),
            100,
            2026,
            "30001",
            TermDescriptor
        ),
        new(
            new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000042")),
            300,
            2026,
            "30002",
            TermDescriptor
        ),
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;
    private long _dualEnrolledStudentDocumentId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false
        );
        await _context.SeedSchoolDescriptorDataAsync();
        await _context.SeedTermDescriptorAsync(
            Guid.Parse("13131313-0000-0000-0000-000000000051"),
            TermDescriptor
        );

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        await _context.SeedSchoolYearTypeAsync(_schoolYearSeed);
        await _context.SeedStudentAsync(_dualEnrolledStudentSeed);
        await _context.SeedStudentAsync(_otherStudentSeed);

        foreach (var associationSeed in _studentSchoolAssociationSeeds)
        {
            await _context.SeedStudentSchoolAssociationAsync(associationSeed);
        }

        foreach (var studentAcademicRecordSeed in _studentAcademicRecordSeeds)
        {
            await _context.SeedStudentAcademicRecordAsync(studentAcademicRecordSeed);
        }

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);

        _dualEnrolledStudentDocumentId = await _context.Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."Student"
            WHERE "StudentUniqueId" = @studentUniqueId;
            """,
            new NpgsqlParameter("studentUniqueId", _dualEnrolledStudentSeed.StudentUniqueId)
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

    [Test]
    public async Task It_flattens_the_single_record_exists_probe_without_a_dedup_node()
    {
        // The membership core of the single-record EXISTS probe
        // (SingleRecordRelationshipAuthorizationSqlCompiler.AppendAuthorizationExistsSelectSql).
        var plan = await ExplainJsonAsync(
            """
            SELECT 1
            FROM "auth"."EducationOrganizationIdToStudentDocumentId" a
            WHERE a."Student_DocumentId" = @studentDocumentId
              AND a."SourceEducationOrganizationId" = ANY(@claimEducationOrganizationIds)
            """,
            new NpgsqlParameter("studentDocumentId", _dualEnrolledStudentDocumentId),
            new NpgsqlParameter("claimEducationOrganizationIds", new[] { ClaimEducationOrganizationId })
        );

        var (closurePath, associationPath) = AssertViewInlinedWithPushedPredicates(
            plan,
            expectStudentDocumentIdCondition: true
        );

        // Correlated per-probe shape: the plan must be entirely dedup-free — this is the
        // "no HashAggregate over the closure per probe" acceptance criterion verbatim.
        AssertNoDedupNodeOnPath(closurePath);
        AssertNoDedupNodeOnPath(associationPath);
    }

    [Test]
    public async Task It_flattens_the_get_many_membership_subquery_without_a_dedup_node()
    {
        // The GET-many person membership shape
        // (PageDocumentIdSqlCompiler.AppendPersonAuthViewMembershipSubquerySql): the probing root
        // table filters its person DocumentId by IN-membership in the auth view.
        var plan = await ExplainJsonAsync(
            """
            SELECT r."DocumentId"
            FROM "edfi"."StudentAcademicRecord" r
            WHERE r."Student_DocumentId" IN (
                SELECT a."Student_DocumentId"
                FROM "auth"."EducationOrganizationIdToStudentDocumentId" a
                WHERE a."SourceEducationOrganizationId" = ANY(@claimEducationOrganizationIds)
            )
            """,
            new NpgsqlParameter("claimEducationOrganizationIds", new[] { ClaimEducationOrganizationId })
        );

        var (closurePath, _) = AssertViewInlinedWithPushedPredicates(
            plan,
            expectStudentDocumentIdCondition: true
        );

        AssertClaimFilterAppliedBeneathAnyDedup(closurePath);
    }

    private async Task<JsonElement> ExplainJsonAsync(string sql, params NpgsqlParameter[] parameters)
    {
        var rows = await _context.Database.QueryRowsAsync($"EXPLAIN (FORMAT JSON) {sql}", parameters);
        rows.Should().ContainSingle();

        var explainJson = rows[0]["QUERY PLAN"]?.ToString();
        explainJson.Should().NotBeNullOrEmpty();

        // Retained as PR evidence for the AC4 EXPLAIN verification.
        await TestContext.Out.WriteLineAsync(explainJson);

        using var document = JsonDocument.Parse(explainJson!);
        return document.RootElement[0].GetProperty("Plan").Clone();
    }

    /// <summary>
    /// Asserts the auth view was flattened into the probing query: both base relations are scanned
    /// directly, no Subquery Scan remains, and the claim (and optionally person) predicates appear
    /// as plan conditions. Returns the root→scan path to the closure scan for further dedup checks.
    /// </summary>
    private static (
        IReadOnlyList<JsonElement> ClosurePath,
        IReadOnlyList<JsonElement> AssociationPath
    ) AssertViewInlinedWithPushedPredicates(JsonElement plan, bool expectStudentDocumentIdCondition)
    {
        // Inlined: the view's base relations are scanned directly, with no Subquery Scan
        // anywhere (the unflattenable DISTINCT view forced one per probe before DMS-1329).
        var closurePath = FindRelationScanPath(plan, EdOrgClosureRelationName);
        var associationPath = FindRelationScanPath(plan, StudentSchoolAssociationRelationName);
        CollectNodeTypes(plan).Should().NotContain("Subquery Scan");

        // Predicate pushdown: the claim filter and the person correlation both survived into
        // plan conditions instead of being applied above a materialized view output.
        var conditions = CollectConditionText(plan);
        conditions.Should().Contain(text => text.Contains("SourceEducationOrganizationId"));
        if (expectStudentDocumentIdCondition)
        {
            conditions.Should().Contain(text => text.Contains("Student_DocumentId"));
        }

        return (closurePath, associationPath);
    }

    private static readonly HashSet<string> _dedupNodeTypes = ["Aggregate", "Unique", "Group", "SetOp"];

    private static void AssertNoDedupNodeOnPath(IReadOnlyList<JsonElement> path)
    {
        path.Select(GetNodeType)
            .Should()
            .NotContain(
                nodeType => _dedupNodeTypes.Contains(nodeType),
                "no dedup node may materialize the auth-view subtree per probe (DMS-1329)"
            );
    }

    /// <summary>
    /// The uncorrelated GET-many membership subquery runs once per query, so the planner may
    /// legitimately unique-ify the claim-filtered rows before joining (a single dedup of a small
    /// filtered set — not the per-probe materialization DMS-1329 removed). What must never come
    /// back is a dedup over the UNFILTERED closure: any dedup node on the path to the closure scan
    /// must have the claim filter applied beneath it.
    /// </summary>
    private static void AssertClaimFilterAppliedBeneathAnyDedup(IReadOnlyList<JsonElement> closurePath)
    {
        var deepestDedupIndex = -1;
        for (var index = 0; index < closurePath.Count; index++)
        {
            if (_dedupNodeTypes.Contains(GetNodeType(closurePath[index])))
            {
                deepestDedupIndex = index;
            }
        }

        if (deepestDedupIndex < 0)
        {
            // Fully dedup-free plan (e.g. hash semi join) — nothing more to prove.
            return;
        }

        closurePath
            .Skip(deepestDedupIndex + 1)
            .SelectMany(CollectOwnConditionText)
            .Should()
            .Contain(
                text => text.Contains("SourceEducationOrganizationId"),
                "the claim filter must be applied beneath any dedup node — a dedup over the "
                    + "unfiltered closure is the DMS-1329 regression"
            );
    }

    /// <summary>
    /// Returns the root→scan node path for the single scan of <paramref name="relationName"/>,
    /// failing the test when the relation is not scanned directly (i.e. the view was not inlined).
    /// </summary>
    private static IReadOnlyList<JsonElement> FindRelationScanPath(JsonElement plan, string relationName)
    {
        var path = new List<JsonElement>();
        TryFindRelationScanPath(plan, relationName, path)
            .Should()
            .BeTrue($"relation '{relationName}' should be scanned directly in the flattened plan");
        return path;
    }

    private static bool TryFindRelationScanPath(JsonElement node, string relationName, List<JsonElement> path)
    {
        path.Add(node);

        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == relationName)
        {
            return true;
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                if (TryFindRelationScanPath(child, relationName, path))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static List<string> CollectNodeTypes(JsonElement plan)
    {
        var nodeTypes = new List<string>();
        Visit(plan, node => nodeTypes.Add(GetNodeType(node)));
        return nodeTypes;
    }

    private static readonly string[] _conditionProperties =
    [
        "Filter",
        "Index Cond",
        "Hash Cond",
        "Join Filter",
        "Recheck Cond",
        "Merge Cond",
    ];

    /// <summary>
    /// Collects the text of every predicate-bearing plan property (Filter, Index Cond, Hash Cond,
    /// Join Filter, Recheck Cond, Merge Cond) attached to a single plan node.
    /// </summary>
    private static List<string> CollectOwnConditionText(JsonElement node)
    {
        var conditions = new List<string>();
        foreach (var propertyName in _conditionProperties)
        {
            if (node.TryGetProperty(propertyName, out var condition))
            {
                conditions.Add(condition.GetString() ?? string.Empty);
            }
        }

        return conditions;
    }

    /// <summary>
    /// Collects the predicate-bearing property text across the whole plan tree.
    /// </summary>
    private static List<string> CollectConditionText(JsonElement plan)
    {
        var conditions = new List<string>();
        Visit(plan, node => conditions.AddRange(CollectOwnConditionText(node)));
        return conditions;
    }

    private static string GetNodeType(JsonElement node) =>
        node.TryGetProperty("Node Type", out var nodeType)
            ? nodeType.GetString() ?? string.Empty
            : string.Empty;

    private static void Visit(JsonElement node, Action<JsonElement> visit)
    {
        visit(node);
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                Visit(child, visit);
            }
        }
    }
}
