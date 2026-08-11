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
//
// The staff view is covered separately: it is the one two-arm view (assignment +
// employment associations, combined with UNION ALL since DMS-1329). Its probes expand
// into an appendrel — trivial per-arm Subquery Scan nodes may legitimately remain, so
// the inlining evidence is that both arms' base relations are scanned directly (the
// closure once per arm) — and a deduplicating UNION would reintroduce the per-probe
// Subquery Scan + HashAggregate over the claim-filtered staff set that these tests
// assert against.
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
    private const string StaffClassificationDescriptor =
        "uri://ed-fi.org/StaffClassificationDescriptor#Teacher";
    private const string EdOrgClosureRelationName = "EducationOrganizationIdToEducationOrganizationId";
    private const string StudentSchoolAssociationRelationName = "StudentSchoolAssociation";
    private const string AssignmentAssociationRelationName =
        "StaffEducationOrganizationAssignmentAssociation";
    private const string EmploymentAssociationRelationName =
        "StaffEducationOrganizationEmploymentAssociation";

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

    private static readonly StaffSeed _assignedStaffSeed = new(
        new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000071")),
        "40001",
        "Avery",
        "Assigned"
    );

    private static readonly StaffEducationOrganizationAssignmentAssociationSeed _staffAssignmentSeed = new(
        new DocumentUuid(Guid.Parse("13131313-0000-0000-0000-000000000072")),
        "40001",
        100,
        StaffClassificationDescriptor,
        new DateOnly(2025, 8, 1)
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
    private long _assignedStaffDocumentId;

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

        await _context.SeedStaffClassificationDescriptorAsync(
            Guid.Parse("13131313-0000-0000-0000-000000000073"),
            StaffClassificationDescriptor
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateStaffAsync(_assignedStaffSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateStaffEducationOrganizationAssignmentAssociationAsync(_staffAssignmentSeed)
        );

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
        _assignedStaffDocumentId = await _context.Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."Staff"
            WHERE "StaffUniqueId" = @staffUniqueId;
            """,
            new NpgsqlParameter("staffUniqueId", _assignedStaffSeed.StaffUniqueId)
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

    [Test]
    public async Task It_expands_the_staff_single_record_exists_probe_into_both_arms_without_a_dedup_node()
    {
        // The same single-record EXISTS membership core, against the two-arm staff view. With
        // UNION ALL between the arms (DMS-1329), the probe expands into per-arm probes with no
        // dedup node anywhere; a deduplicating UNION would reintroduce the per-probe
        // Subquery Scan + HashAggregate over the claim-filtered staff set.
        var plan = await ExplainJsonAsync(
            """
            SELECT 1
            FROM "auth"."EducationOrganizationIdToStaffDocumentId" a
            WHERE a."Staff_DocumentId" = @staffDocumentId
              AND a."SourceEducationOrganizationId" = ANY(@claimEducationOrganizationIds)
            """,
            new NpgsqlParameter("staffDocumentId", _assignedStaffDocumentId),
            new NpgsqlParameter("claimEducationOrganizationIds", new[] { ClaimEducationOrganizationId })
        );

        AssertStaffViewArmsInlined(plan);

        // Correlated per-probe shape: the whole plan must be dedup-free — the staff-view
        // equivalent of the "no HashAggregate over the closure per probe" criterion.
        CollectNodeTypes(plan)
            .Should()
            .NotContain(
                nodeType => _dedupNodeTypes.Contains(nodeType),
                "no dedup node may materialize the staff auth set per probe (DMS-1329)"
            );

        var conditions = CollectConditionText(plan);
        conditions.Should().Contain(text => text.Contains("SourceEducationOrganizationId"));
        conditions.Should().Contain(text => text.Contains("Staff_DocumentId"));
    }

    [Test]
    public async Task It_keeps_any_staff_get_many_dedup_above_the_claim_filter()
    {
        // The GET-many membership shape against the staff view. Same tolerance as the student
        // GET-many test: a once-per-query dedup of the claim-filtered rows is legitimate; a dedup
        // over an unfiltered arm is the regression.
        var plan = await ExplainJsonAsync(
            """
            SELECT r."DocumentId"
            FROM "edfi"."Staff" r
            WHERE r."DocumentId" IN (
                SELECT a."Staff_DocumentId"
                FROM "auth"."EducationOrganizationIdToStaffDocumentId" a
                WHERE a."SourceEducationOrganizationId" = ANY(@claimEducationOrganizationIds)
            )
            """,
            new NpgsqlParameter("claimEducationOrganizationIds", new[] { ClaimEducationOrganizationId })
        );

        AssertStaffViewArmsInlined(plan);

        foreach (var closurePath in FindAllRelationScanPaths(plan, EdOrgClosureRelationName))
        {
            AssertClaimFilterAppliedBeneathAnyDedup(closurePath);
        }
    }

    /// <summary>
    /// Asserts the two-arm staff view was expanded into the probing query: each arm's association
    /// table is scanned directly and the EdOrg closure is scanned once per arm. Unlike the
    /// single-arm views, trivial per-arm Subquery Scan nodes may legitimately remain over the
    /// appendrel, so direct base-relation scans are the inlining evidence here.
    /// </summary>
    private static void AssertStaffViewArmsInlined(JsonElement plan)
    {
        FindAllRelationScanPaths(plan, AssignmentAssociationRelationName)
            .Should()
            .ContainSingle("the assignment arm should scan its association table directly");
        FindAllRelationScanPaths(plan, EmploymentAssociationRelationName)
            .Should()
            .ContainSingle("the employment arm should scan its association table directly");
        FindAllRelationScanPaths(plan, EdOrgClosureRelationName)
            .Should()
            .HaveCount(2, "each staff view arm should scan the EdOrg closure directly");
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

    /// <summary>
    /// Returns the root→scan node path for every direct scan of <paramref name="relationName"/>.
    /// Multi-arm (appendrel) plans scan the same relation once per arm, so callers assert on the
    /// full path set instead of the single path <see cref="FindRelationScanPath"/> returns.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<JsonElement>> FindAllRelationScanPaths(
        JsonElement plan,
        string relationName
    )
    {
        var paths = new List<IReadOnlyList<JsonElement>>();
        CollectRelationScanPaths(plan, relationName, [], paths);
        return paths;
    }

    private static void CollectRelationScanPaths(
        JsonElement node,
        string relationName,
        List<JsonElement> currentPath,
        List<IReadOnlyList<JsonElement>> paths
    )
    {
        currentPath.Add(node);

        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == relationName)
        {
            paths.Add([.. currentPath]);
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectRelationScanPaths(child, relationName, currentPath, paths);
            }
        }

        currentPath.RemoveAt(currentPath.Count - 1);
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
