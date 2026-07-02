// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// ReadChanges Authorization View Definition Tests (DMS-1176)
//
// Pin the SQL-free inventory shape: view names, union arms, and the
// current/tracked column references each arm joins and projects.
// Arms are located by their join-table composition (never array
// position) per project convention.
// ═══════════════════════════════════════════════════════════════════

internal static class ReadChangesAuthViewTestHelpers
{
    public const string TrackedChangesSchema = "tracked_changes_edfi";

    public static ReadChangesAuthorizationViewInfo View(ReadChangesAuthViewKind kind) =>
        AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions.Single(definition =>
            definition.Kind == kind
        );

    /// <summary>
    /// Locates the single arm of a view whose join tables (schema-qualified, order-insensitive)
    /// match exactly the supplied set. The zero-join current/current arm is located by passing
    /// no table names.
    /// </summary>
    public static AuthViewArm ArmByJoinTables(AuthViewDefinition view, params string[] qualifiedJoinTables)
    {
        return view
            .Arms.Where(arm =>
                arm.Joins.Select(QualifiedJoinTable)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(
                        qualifiedJoinTables.OrderBy(name => name, StringComparer.Ordinal),
                        StringComparer.Ordinal
                    )
            )
            .Should()
            .ContainSingle(
                $"view '{view.View.Name}' should have exactly one arm joining [{string.Join(", ", qualifiedJoinTables)}]"
            )
            .Subject;
    }

    public static string QualifiedJoinTable(AuthViewJoin join) => Qualified(join.Table);

    public static string Qualified(DbTableName table) => $"{table.Schema.Value}.{table.Name}";

    public static AuthViewOutputColumn OutputColumn(AuthViewArm arm, string columnName) =>
        arm.OutputColumns.Should().ContainSingle(column => column.Column.Value == columnName).Subject;

    public static AuthViewJoinPredicate SinglePredicate(AuthViewArm arm, string joinAlias)
    {
        var join = arm.Joins.Should().ContainSingle(j => j.Alias == joinAlias).Subject;
        return join.On.Should().ContainSingle().Subject;
    }
}

[TestFixture]
public class Given_ReadChanges_Auth_View_Definitions
{
    [Test]
    public void It_should_define_the_four_views_in_alphabetical_name_order()
    {
        AuthObjectDefinitions
            .ReadChangesAuthViews.Select(view => view.View.Name)
            .Should()
            .Equal(
                "EducationOrganizationIdToContactDocumentIdIncludingDeletes",
                "EducationOrganizationIdToStaffDocumentIdIncludingDeletes",
                "EducationOrganizationIdToStudentDocumentIdDeletedResponsibility",
                "EducationOrganizationIdToStudentDocumentIdIncludingDeletes"
            );
    }

    [Test]
    public void It_should_place_every_view_in_the_auth_schema()
    {
        AuthObjectDefinitions
            .ReadChangesAuthViews.Select(view => view.View.Schema)
            .Should()
            .AllBeEquivalentTo(AuthNames.AuthSchema);
    }

    [Test]
    public void It_should_keep_every_view_name_within_the_postgresql_identifier_limit()
    {
        // PostgreSQL truncates identifiers beyond 63 characters silently; the fourth view is
        // deliberately named ...DeletedResponsibility (exactly 63) instead of the design's
        // 70-character ...ThroughDeletedResponsibility.
        AuthObjectDefinitions
            .ReadChangesAuthViews.Select(view => view.View.Name)
            .Should()
            .OnlyContain(name => name.Length <= 63);
    }

    [Test]
    public void It_should_combine_arms_with_union_in_every_view()
    {
        // UNION (not UNION ALL) eliminates duplicate authorization pairs produced by current and
        // tracked-change arms before runtime predicates consume the view (DMS-1178 AC).
        AuthObjectDefinitions
            .ReadChangesAuthViews.Select(view => view.ArmsSetOperator)
            .Should()
            .AllBeEquivalentTo(AuthViewSetOperator.Union);
    }

    [Test]
    public void It_should_not_use_select_distinct_in_any_arm()
    {
        AuthObjectDefinitions
            .ReadChangesAuthViews.SelectMany(view => view.Arms)
            .Should()
            .OnlyContain(arm => !arm.SelectDistinct);
    }

    [Test]
    public void It_should_record_documentid_based_person_and_claim_output_columns()
    {
        foreach (var definition in AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions)
        {
            definition
                .ClaimEducationOrganizationIdColumn.Should()
                .Be(AuthNames.SourceEdOrgId, $"view '{definition.View.Name}'");
            definition
                .PersonDocumentIdOutputColumn.Value.Should()
                .EndWith("_DocumentId", $"view '{definition.View.Name}'");
        }

        ReadChangesAuthViewTestHelpers
            .View(ReadChangesAuthViewKind.Student)
            .PersonDocumentIdOutputColumn.Should()
            .Be(AuthNames.StudentDocumentId);
        ReadChangesAuthViewTestHelpers
            .View(ReadChangesAuthViewKind.Contact)
            .PersonDocumentIdOutputColumn.Should()
            .Be(AuthNames.ContactDocumentId);
        ReadChangesAuthViewTestHelpers
            .View(ReadChangesAuthViewKind.Staff)
            .PersonDocumentIdOutputColumn.Should()
            .Be(AuthNames.StaffDocumentId);
        ReadChangesAuthViewTestHelpers
            .View(ReadChangesAuthViewKind.StudentDeletedResponsibility)
            .PersonDocumentIdOutputColumn.Should()
            .Be(AuthNames.StudentDocumentId);
    }

    [Test]
    public void It_should_join_tracked_change_arms_against_the_current_edorg_hierarchy()
    {
        // Every arm that joins a tracked-change table sources FROM the current
        // auth.EducationOrganizationIdToEducationOrganizationId hierarchy table.
        var trackedArms = AuthObjectDefinitions
            .ReadChangesAuthViews.SelectMany(view => view.Arms)
            .Where(arm =>
                arm.Joins.Any(join =>
                    join.Table.Schema.Value == ReadChangesAuthViewTestHelpers.TrackedChangesSchema
                )
            )
            .ToList();

        trackedArms.Should().NotBeEmpty();
        trackedArms.Should().OnlyContain(arm => arm.SourceTable.Equals(AuthNames.EdOrgIdToEdOrgId));
    }
}

[TestFixture]
public class Given_ReadChanges_Student_View_Definition
{
    private AuthViewDefinition _view = default!;

    [SetUp]
    public void Setup()
    {
        _view = ReadChangesAuthViewTestHelpers.View(ReadChangesAuthViewKind.Student).ViewDefinition;
    }

    [Test]
    public void It_should_have_a_current_arm_selecting_the_people_auth_view_unrenamed()
    {
        var currentArm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(_view);

        ReadChangesAuthViewTestHelpers
            .Qualified(currentArm.SourceTable)
            .Should()
            .Be("auth.EducationOrganizationIdToStudentDocumentId");
        currentArm.OutputColumns.Should().OnlyContain(column => column.OutputName == null);
        ReadChangesAuthViewTestHelpers.OutputColumn(currentArm, "SourceEducationOrganizationId");
        ReadChangesAuthViewTestHelpers.OutputColumn(currentArm, "Student_DocumentId");
    }

    [Test]
    public void It_should_have_a_tracked_arm_joining_old_school_id_and_renaming_old_student_documentid()
    {
        var trackedArm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(
            _view,
            "tracked_changes_edfi.StudentSchoolAssociation"
        );

        var predicate = ReadChangesAuthViewTestHelpers.SinglePredicate(trackedArm, "ssa_tc");
        predicate.LeftAlias.Should().Be("edOrg");
        predicate.LeftColumn.Should().Be(AuthNames.TargetEdOrgId);
        predicate.RightColumn.Value.Should().Be("OldSchoolId_Unified");

        var personColumn = ReadChangesAuthViewTestHelpers.OutputColumn(trackedArm, "OldStudent_DocumentId");
        personColumn.OutputName.Should().Be(AuthNames.StudentDocumentId);
        ReadChangesAuthViewTestHelpers
            .OutputColumn(trackedArm, "SourceEducationOrganizationId")
            .OutputName.Should()
            .BeNull();
    }

    [Test]
    public void It_should_have_exactly_two_arms()
    {
        _view.Arms.Should().HaveCount(2);
    }
}

[TestFixture]
public class Given_ReadChanges_Contact_View_Definition
{
    private AuthViewDefinition _view = default!;

    [SetUp]
    public void Setup()
    {
        _view = ReadChangesAuthViewTestHelpers.View(ReadChangesAuthViewKind.Contact).ViewDefinition;
    }

    [Test]
    public void It_should_have_exactly_four_arms()
    {
        _view.Arms.Should().HaveCount(4);
    }

    [Test]
    public void It_should_have_a_current_arm_selecting_the_people_auth_view()
    {
        var currentArm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(_view);

        ReadChangesAuthViewTestHelpers
            .Qualified(currentArm.SourceTable)
            .Should()
            .Be("auth.EducationOrganizationIdToContactDocumentId");
        ReadChangesAuthViewTestHelpers.OutputColumn(currentArm, "Contact_DocumentId");
    }

    [Test]
    public void It_should_have_a_current_ssa_tracked_sca_arm()
    {
        var arm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(
            _view,
            "edfi.StudentSchoolAssociation",
            "tracked_changes_edfi.StudentContactAssociation"
        );

        var ssaPredicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "ssa");
        ssaPredicate.LeftAlias.Should().Be("edOrg");
        ssaPredicate.LeftColumn.Should().Be(AuthNames.TargetEdOrgId);
        ssaPredicate.RightColumn.Should().Be(AuthNames.SchoolIdUnified);

        var scaPredicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "sca_tc");
        scaPredicate.LeftAlias.Should().Be("ssa");
        scaPredicate.LeftColumn.Should().Be(AuthNames.StudentDocumentId);
        scaPredicate.RightColumn.Value.Should().Be("OldStudent_DocumentId");

        ReadChangesAuthViewTestHelpers
            .OutputColumn(arm, "OldContact_DocumentId")
            .OutputName.Should()
            .Be(AuthNames.ContactDocumentId);
    }

    [Test]
    public void It_should_have_a_tracked_ssa_current_sca_arm()
    {
        var arm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(
            _view,
            "tracked_changes_edfi.StudentSchoolAssociation",
            "edfi.StudentContactAssociation"
        );

        var ssaPredicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "ssa_tc");
        ssaPredicate.LeftAlias.Should().Be("edOrg");
        ssaPredicate.RightColumn.Value.Should().Be("OldSchoolId_Unified");

        var scaPredicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "sca");
        scaPredicate.LeftAlias.Should().Be("ssa_tc");
        scaPredicate.LeftColumn.Value.Should().Be("OldStudent_DocumentId");
        scaPredicate.RightColumn.Should().Be(AuthNames.StudentDocumentId);

        // Current SCA arm projects the live Contact_DocumentId — no rename.
        ReadChangesAuthViewTestHelpers.OutputColumn(arm, "Contact_DocumentId").OutputName.Should().BeNull();
    }

    [Test]
    public void It_should_have_a_tracked_ssa_tracked_sca_arm()
    {
        var arm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(
            _view,
            "tracked_changes_edfi.StudentSchoolAssociation",
            "tracked_changes_edfi.StudentContactAssociation"
        );

        var ssaPredicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "ssa_tc");
        ssaPredicate.LeftAlias.Should().Be("edOrg");
        ssaPredicate.LeftColumn.Should().Be(AuthNames.TargetEdOrgId);
        ssaPredicate.RightColumn.Value.Should().Be("OldSchoolId_Unified");

        var scaPredicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "sca_tc");
        // Both sides share the column name OldStudent_DocumentId, so the left alias is the only
        // thing distinguishing the real join from a self-referential tautology (sca_tc = sca_tc),
        // which would deploy cleanly and silently over-authorize.
        scaPredicate.LeftAlias.Should().Be("ssa_tc");
        scaPredicate.LeftColumn.Value.Should().Be("OldStudent_DocumentId");
        scaPredicate.RightColumn.Value.Should().Be("OldStudent_DocumentId");

        ReadChangesAuthViewTestHelpers
            .OutputColumn(arm, "OldContact_DocumentId")
            .OutputName.Should()
            .Be(AuthNames.ContactDocumentId);
    }
}

[TestFixture]
public class Given_ReadChanges_Staff_View_Definition
{
    private AuthViewDefinition _view = default!;

    [SetUp]
    public void Setup()
    {
        _view = ReadChangesAuthViewTestHelpers.View(ReadChangesAuthViewKind.Staff).ViewDefinition;
    }

    [Test]
    public void It_should_have_exactly_three_arms()
    {
        _view.Arms.Should().HaveCount(3);
    }

    [Test]
    public void It_should_have_a_current_arm_selecting_the_people_auth_view()
    {
        var currentArm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(_view);

        ReadChangesAuthViewTestHelpers
            .Qualified(currentArm.SourceTable)
            .Should()
            .Be("auth.EducationOrganizationIdToStaffDocumentId");
        ReadChangesAuthViewTestHelpers.OutputColumn(currentArm, "Staff_DocumentId");
    }

    [TestCase("StaffEducationOrganizationAssignmentAssociation", "seoaa_tc")]
    [TestCase("StaffEducationOrganizationEmploymentAssociation", "seoea_tc")]
    public void It_should_have_a_tracked_arm_per_staff_association(string trackedTable, string joinAlias)
    {
        var arm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(
            _view,
            $"tracked_changes_edfi.{trackedTable}"
        );

        var predicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, joinAlias);
        predicate.LeftAlias.Should().Be("edOrg");
        predicate.LeftColumn.Should().Be(AuthNames.TargetEdOrgId);
        predicate.RightColumn.Value.Should().Be("OldEducationOrganization_EducationOrganizationId");

        ReadChangesAuthViewTestHelpers
            .OutputColumn(arm, "OldStaff_DocumentId")
            .OutputName.Should()
            .Be(AuthNames.StaffDocumentId);
    }
}

[TestFixture]
public class Given_ReadChanges_Student_Deleted_Responsibility_View_Definition
{
    private AuthViewDefinition _view = default!;

    [SetUp]
    public void Setup()
    {
        _view = ReadChangesAuthViewTestHelpers
            .View(ReadChangesAuthViewKind.StudentDeletedResponsibility)
            .ViewDefinition;
    }

    [Test]
    public void It_should_have_exactly_two_arms()
    {
        _view.Arms.Should().HaveCount(2);
    }

    [Test]
    public void It_should_have_a_current_arm_selecting_the_through_responsibility_people_auth_view()
    {
        var currentArm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(_view);

        ReadChangesAuthViewTestHelpers
            .Qualified(currentArm.SourceTable)
            .Should()
            .Be("auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility");
        ReadChangesAuthViewTestHelpers.OutputColumn(currentArm, "Student_DocumentId");
    }

    [Test]
    public void It_should_have_a_tracked_responsibility_arm_with_renamed_student_documentid()
    {
        var arm = ReadChangesAuthViewTestHelpers.ArmByJoinTables(
            _view,
            "tracked_changes_edfi.StudentEducationOrganizationResponsibilityAssociation"
        );

        var predicate = ReadChangesAuthViewTestHelpers.SinglePredicate(arm, "seora_tc");
        predicate.LeftAlias.Should().Be("edOrg");
        predicate.LeftColumn.Should().Be(AuthNames.TargetEdOrgId);
        predicate.RightColumn.Value.Should().Be("OldEducationOrganization_EducationOrganizationId");

        ReadChangesAuthViewTestHelpers
            .OutputColumn(arm, "OldStudent_DocumentId")
            .OutputName.Should()
            .Be(AuthNames.StudentDocumentId);
    }
}
