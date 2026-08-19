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
// With SELECT DISTINCT removed from the people auth views, PostgreSQL flattens the view
// into the probing query instead of materializing the client's full EdOrg→person set
// behind an unflattenable dedup node. These tests EXPLAIN (FORMAT JSON) the two audited
// consumer shapes — the single-record EXISTS membership core and the GET-many
// IN-membership subquery — against both the single-arm student view and the two-arm
// staff view, and assert:
//   1. the view is inlined — its base relations are scanned directly. For the
//      single-arm views that additionally means no Subquery Scan node anywhere in the
//      plan; the staff view expands into an appendrel where trivial per-arm Subquery
//      Scan nodes may legitimately remain, so there the inlining evidence is that no
//      Subquery Scan spans BOTH arms (see below).
//   2. the person/claim predicates reached the plan (as Filter / Index Cond /
//      Hash Cond / Join Filter conditions), and
//   3. the dedup node that pre-change plans used to materialize the closure per probe
//      is gone. The tolerance differs by consumer shape: the correlated single-record
//      probes must be dedup-free across the WHOLE plan, whereas the uncorrelated
//      GET-many subquery runs once per query and may legitimately unique-ify its rows
//      — but only above the claim filter. A dedup over the UNFILTERED closure is the
//      regression, so the GET-many tests assert instead that the claim filter is
//      applied beneath any dedup node on the path to each closure scan.
//
// The dedup and subquery nodes are structural for a DISTINCT view (the planner must
// emit them regardless of row counts), so the assertions are meaningful at test scale.
//
// The staff view is the one two-arm view (assignment + employment associations,
// combined with UNION ALL since DMS-1329). Its probes expand into an appendrel, so the
// inlining evidence is twofold: both arms' base relations are scanned directly (the
// closure once per arm), AND no Subquery Scan spans both arms. The second half is what
// discriminates. A deduplicating UNION scans exactly the same three relations directly
// — the arms stay separate subplans — and merely stacks one Subquery Scan + dedup node
// over the whole appendrel; PostgreSQL also pushes the simple claim qual beneath that
// dedup either way, because only join quals are blocked from entering an unflattenable
// subquery. So for the staff view the signal is the SCOPE of the Subquery Scan, not the
// presence of one, and not where the claim filter lands.
//
// Plan-shape verification is PostgreSQL-only by design, not by omission. The pathology
// the change removes is specific to PostgreSQL's rewriter: a dedup makes a view
// unflattenable, so join quals cannot be pushed into it. SQL Server expands view
// definitions and can push predicates through a distinct, so there is no equivalent plan
// regression to guard, and the project keeps no SQL Server plan-assertion harness.
// SQL Server parity is proven by outcome instead — the MSSQL twins of every scenario in
// MssqlRelationalQueryAuthorizationTests and MssqlRelationalGetByIdAuthorizationTests
// assert unchanged authorization results, including under duplicate auth pairs.
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

        AssertViewInlinedWithPushedPredicates(plan);

        // Correlated per-probe shape: the plan must be entirely dedup-free — this is the
        // "no HashAggregate over the closure per probe" acceptance criterion verbatim.
        AssertNoDedupNodeAnywhere(plan);
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

        var closurePath = AssertViewInlinedWithPushedPredicates(plan);

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
        AssertNoDedupNodeAnywhere(plan);

        var conditions = PostgresqlQueryPlanNavigator.CollectConditionText(plan);
        conditions.Should().Contain(text => text.Contains("SourceEducationOrganizationId"));
        conditions.Should().Contain(text => text.Contains("Staff_DocumentId"));
    }

    [Test]
    public async Task It_keeps_any_staff_get_many_dedup_above_the_claim_filter()
    {
        // The GET-many membership shape against the staff view. Same tolerance as the student
        // GET-many test: a once-per-query dedup of the claim-filtered rows is legitimate; a dedup
        // over an unfiltered arm is the regression. Note that the dedup-placement check below is
        // satisfied by a deduplicating UNION as well (the claim qual is pushed beneath its dedup),
        // so the regression signal for this shape comes from AssertStaffViewArmsInlined's
        // spans-both-arms Subquery Scan assertion, not from the dedup placement.
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

        // Asserted unconditionally, because AssertClaimFilterAppliedBeneathAnyDedup returns early on
        // a fully dedup-free plan and would otherwise leave pushdown unverified for this shape.
        var conditions = PostgresqlQueryPlanNavigator.CollectConditionText(plan);
        conditions.Should().Contain(text => text.Contains("SourceEducationOrganizationId"));
        conditions.Should().Contain(text => text.Contains("Staff_DocumentId"));

        foreach (
            var closurePath in PostgresqlQueryPlanNavigator.FindAllRelationScanPaths(
                plan,
                EdOrgClosureRelationName
            )
        )
        {
            AssertClaimFilterAppliedBeneathAnyDedup(closurePath);
        }
    }

    /// <summary>
    /// Asserts the two-arm staff view was expanded into the probing query: each arm's association
    /// table is scanned directly, the EdOrg closure is scanned once per arm, and no Subquery Scan
    /// spans both arms. Unlike the single-arm views, trivial per-arm Subquery Scan nodes may
    /// legitimately remain over the appendrel, so a whole-plan Subquery Scan ban would be wrong
    /// here. The last assertion is the load-bearing one: the direct base-relation scans hold for a
    /// deduplicating UNION too, whereas a Subquery Scan reaching both arms' closure scans is
    /// precisely the unflattened pre-DMS-1329 shape.
    /// </summary>
    private static void AssertStaffViewArmsInlined(JsonElement plan)
    {
        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(plan, AssignmentAssociationRelationName)
            .Should()
            .ContainSingle("the assignment arm should scan its association table directly");
        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(plan, EmploymentAssociationRelationName)
            .Should()
            .ContainSingle("the employment arm should scan its association table directly");
        PostgresqlQueryPlanNavigator
            .FindAllRelationScanPaths(plan, EdOrgClosureRelationName)
            .Should()
            .HaveCount(2, "each staff view arm should scan the EdOrg closure directly");

        // Per-arm Subquery Scans reach one closure scan each; an unflattened set-operation subquery
        // reaches both. Scoping the check this way also survives the planner choosing to unique-ify
        // the whole membership subquery once per query, which AssertNoDedupNodeAnywhere would not.
        foreach (
            var subqueryScan in PostgresqlQueryPlanNavigator
                .CollectNodes(plan)
                .Where(node => PostgresqlQueryPlanNavigator.GetNodeType(node) == "Subquery Scan")
        )
        {
            PostgresqlQueryPlanNavigator
                .FindAllRelationScanPaths(subqueryScan, EdOrgClosureRelationName)
                .Should()
                .HaveCountLessThanOrEqualTo(
                    1,
                    "a Subquery Scan spanning both staff arms means the view was not flattened into "
                        + "the probing query — the shape a deduplicating UNION produces (DMS-1329)"
                );
        }
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
    /// directly and exactly once, no Subquery Scan remains, and the claim and person predicates
    /// appear as plan conditions. Applies to the single-arm views only — the staff view scans the
    /// closure once per arm, so it uses <see cref="AssertStaffViewArmsInlined"/> instead. Returns
    /// the root→scan path to the closure scan for further dedup checks.
    /// </summary>
    private static IReadOnlyList<JsonElement> AssertViewInlinedWithPushedPredicates(JsonElement plan)
    {
        // Inlined: the view's base relations are scanned directly, with no Subquery Scan
        // anywhere (the unflattenable DISTINCT view forced one per probe before DMS-1329).
        var closurePath = PostgresqlQueryPlanNavigator.FindRelationScanPath(plan, EdOrgClosureRelationName);
        PostgresqlQueryPlanNavigator.FindRelationScanPath(plan, StudentSchoolAssociationRelationName);
        PostgresqlQueryPlanNavigator.CollectNodeTypes(plan).Should().NotContain("Subquery Scan");

        // Predicate pushdown: the claim filter and the person correlation both survived into
        // plan conditions instead of being applied above a materialized view output. In the
        // GET-many shape the person correlation is the semi-join condition rather than a
        // literal filter, so it appears among the plan conditions either way.
        var conditions = PostgresqlQueryPlanNavigator.CollectConditionText(plan);
        conditions.Should().Contain(text => text.Contains("SourceEducationOrganizationId"));
        conditions.Should().Contain(text => text.Contains("Student_DocumentId"));

        return closurePath;
    }

    private static readonly HashSet<string> _dedupNodeTypes = ["Aggregate", "Unique", "Group", "SetOp"];

    /// <summary>
    /// Asserts no dedup node sits anywhere in the plan. Scoped to the whole tree rather than the
    /// root→scan paths: a path-scoped check would miss a dedup on a sibling branch, and the two
    /// scopes only coincide while the probe SQL stays branch-free.
    /// </summary>
    private static void AssertNoDedupNodeAnywhere(JsonElement plan)
    {
        PostgresqlQueryPlanNavigator
            .CollectNodeTypes(plan)
            .Should()
            .NotContain(
                nodeType => _dedupNodeTypes.Contains(nodeType),
                "no dedup node may materialize the auth set per probe (DMS-1329)"
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
            if (_dedupNodeTypes.Contains(PostgresqlQueryPlanNavigator.GetNodeType(closurePath[index])))
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
            .SelectMany(PostgresqlQueryPlanNavigator.CollectOwnConditionText)
            .Should()
            .Contain(
                text => text.Contains("SourceEducationOrganizationId"),
                "the claim filter must be applied beneath any dedup node — a dedup over the "
                    + "unfiltered closure is the DMS-1329 regression"
            );
    }
}
