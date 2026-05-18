// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture asserting all five PrimaryAssociation authorization indexes are emitted
/// when their resources are present in the model set.
/// </summary>
[TestFixture]
public class Given_All_Five_PrimaryAssociations_Are_Present
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentContactAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStaffEducationOrganizationAssignmentAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStaffEducationOrganizationEmploymentAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentEducationOrganizationResponsibilityAssociation()
                );
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_exactly_five_authorization_indexes()
    {
        _authIndexes.Should().HaveCount(5);
    }

    [Test]
    public void It_should_emit_StudentSchoolAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StudentSchoolAssociation");
        index.Name.Value.Should().Be("IX_StudentSchoolAssociation_SchoolId_Unified_Auth");
        index.KeyColumns.Select(c => c.Value).Should().Equal("SchoolId_Unified");
        index.IncludeColumns.Should().NotBeNull();
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Student_DocumentId");
        index.IsUnique.Should().BeFalse();
    }

    [Test]
    public void It_should_emit_StudentContactAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StudentContactAssociation");
        index.Name.Value.Should().Be("IX_StudentContactAssociation_Student_DocumentId_Auth");
        index.KeyColumns.Select(c => c.Value).Should().Equal("Student_DocumentId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Contact_DocumentId");
    }

    [Test]
    public void It_should_emit_StaffEducationOrganizationAssignmentAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StaffEducationOrganizationAssignmentAssociation");
        index
            .Name.Value.Should()
            .Be(
                "IX_StaffEducationOrganizationAssignmentAssociation_EducationOrganization_EducationOrganizationId_Auth"
            );
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Staff_DocumentId");
    }

    [Test]
    public void It_should_emit_StaffEducationOrganizationEmploymentAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StaffEducationOrganizationEmploymentAssociation");
        index
            .Name.Value.Should()
            .Be(
                "IX_StaffEducationOrganizationEmploymentAssociation_EducationOrganization_EducationOrganizationId_Auth"
            );
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Staff_DocumentId");
    }

    [Test]
    public void It_should_emit_StudentEducationOrganizationResponsibilityAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StudentEducationOrganizationResponsibilityAssociation");
        index
            .Name.Value.Should()
            .Be(
                "IX_StudentEducationOrganizationResponsibilityAssociation_EducationOrganization_EducationOrganizationId_Auth"
            );
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Student_DocumentId");
    }

    [Test]
    public void It_should_classify_all_indexes_as_Authorization()
    {
        _authIndexes.Should().AllSatisfy(i => i.Kind.Should().Be(DbIndexKind.Authorization));
    }

    private DbIndexInfo SingleByTable(string tableName) =>
        _authIndexes.Single(i => i.Table.Name == tableName);
}

/// <summary>
/// Test fixture asserting PrimaryAssociation indexes are silently skipped when the resource
/// is not present in the model set.
/// </summary>
[TestFixture]
public class Given_No_PrimaryAssociation_Resources_Are_Present
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Course"))
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_emit_any_authorization_indexes()
    {
        _authIndexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting PrimaryAssociation key resolves through a UnifiedAlias to the
/// canonical storage column.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_With_UnifiedAlias_Key
{
    private DbIndexInfo _index = default!;

    [SetUp]
    public void Setup()
    {
        var result = AuthorizationIndexTestRunner.Build(ctx =>
            ctx.ConcreteResourcesInNameOrder.Add(
                AuthIndexFixtureResources.BuildStudentSchoolAssociationWithAliasedSchoolId(
                    canonicalColumn: new DbColumnName("SchoolId_Canonical")
                )
            )
        );
        _index = result.IndexesInCreateOrder.Single(i => i.Kind == DbIndexKind.Authorization);
    }

    [Test]
    public void It_should_use_the_canonical_column_in_the_index_key()
    {
        _index.KeyColumns.Select(c => c.Value).Should().Equal("SchoolId_Canonical");
    }

    [Test]
    public void It_should_use_the_canonical_column_in_the_index_name()
    {
        _index.Name.Value.Should().Be("IX_StudentSchoolAssociation_SchoolId_Canonical_Auth");
    }
}

/// <summary>
/// Test fixture asserting the pass silently skips a PrimaryAssociation in default mode when
/// the resource is present but its root table is missing the expected literal key column.
/// Synthetic test fixtures (e.g. <c>small/referential-identity</c>) reuse PA names without
/// carrying the post-key-unification PA columns; in default mode the pass tolerates that and
/// relies on the strict-mode pipeline to catch real schema drift.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Missing_Required_Column_In_Default_Mode
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociationWithoutKeyColumn()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_silently_skip_the_PA_emission()
    {
        _authIndexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting the pass throws in strict mode when a PrimaryAssociation resource
/// is present but its root table is missing the expected literal key column. Strict mode is
/// the production-runtime configuration; throwing here surfaces real schema drift loudly
/// instead of silently emitting zero authorization indexes.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Missing_Required_Column_In_Strict_Mode
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        _exception = TestExceptions.CaptureException(() =>
            AuthorizationIndexTestRunner.BuildStrict(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociationWithoutKeyColumn()
                )
            )
        );
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_should_name_the_resource_and_missing_column()
    {
        _exception!.Message.Should().Contain("StudentSchoolAssociation");
        _exception.Message.Should().Contain("SchoolId_Unified");
    }
}

/// <summary>
/// Test fixture asserting an EducationOrganization securable element on a root reference
/// emits a single-column authorization index.
/// </summary>
[TestFixture]
public class Given_Resource_With_EdOrg_Securable_On_Root_Reference
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildCourseWithEdOrgSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index()
    {
        _authIndexes.Should().ContainSingle();
    }

    [Test]
    public void It_should_index_the_resolved_identity_column()
    {
        var index = _authIndexes.Single();
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
    }

    [Test]
    public void It_should_have_no_INCLUDE_columns()
    {
        var index = _authIndexes.Single();
        index.IncludeColumns.Should().BeNull();
    }

    [Test]
    public void It_should_use_the_Auth_suffix()
    {
        _authIndexes.Single().Name.Value.Should().EndWith("_Auth");
    }
}

/// <summary>
/// Test fixture asserting a Namespace securable element on a root scalar emits an
/// authorization index on the matching root column.
/// </summary>
[TestFixture]
public class Given_Resource_With_Namespace_Securable_On_Root_Scalar
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithNamespaceSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index_on_the_Namespace_column()
    {
        _authIndexes.Should().ContainSingle();
        _authIndexes.Single().KeyColumns.Select(c => c.Value).Should().Equal("Namespace");
    }

    [Test]
    public void It_should_have_no_INCLUDE_columns()
    {
        _authIndexes.Single().IncludeColumns.Should().BeNull();
    }
}

/// <summary>
/// Test fixture asserting an array-nested Namespace securable element resolves to a column on
/// the child collection table (e.g. <c>$.requiredAssessments[*].assessmentReference.namespace</c>
/// on a GraduationPlan-like resource resolves to the child table's namespace identity scalar)
/// and emits an authorization index there.
/// </summary>
[TestFixture]
public class Given_Resource_With_Nested_Namespace_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithNestedNamespaceSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index_on_the_child_collection_table()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("GraduationPlanLikeRequiredAssessment");
        index.KeyColumns.Select(c => c.Value).Should().Equal("RequiredAssessmentAssessment_Namespace");
        index.IncludeColumns.Should().BeNull();
    }
}

/// <summary>
/// Test fixture asserting an array-nested EducationOrganization securable element resolves to
/// a column on the child collection table and emits an authorization index there.
/// </summary>
[TestFixture]
public class Given_Resource_With_Nested_EdOrg_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithNestedEdOrgSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index_on_the_child_collection_table()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("AssessmentAdministrationParticipationLikeAdministrationPoint");
        index
            .KeyColumns.Select(c => c.Value)
            .Should()
            .Equal("AdministeringOrganization_EducationOrganizationId");
        index.IncludeColumns.Should().BeNull();
    }
}

/// <summary>
/// Test fixture asserting that when a PrimaryAssociation resource also exposes its own EdOrg
/// securable that resolves to the same key column, only the PA index is emitted (the EdOrg
/// emission is suppressed by the PA-coverage dedup).
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Has_Own_EdOrg_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociationWithSchoolEdOrgSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i =>
                i.Kind == DbIndexKind.Authorization && i.Table.Name == "StudentSchoolAssociation"
            )
            .ToArray();
    }

    [Test]
    public void It_should_emit_only_the_PA_index_on_SchoolId()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.KeyColumns.Select(c => c.Value).Should().Equal("SchoolId_Unified");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Student_DocumentId");
    }
}

/// <summary>
/// Test fixture asserting a direct (1-hop) Student securable resolves to a single person-join
/// auth index on the subject's FK column with <c>INCLUDE (DocumentId)</c>.
/// </summary>
[TestFixture]
public class Given_Resource_With_Direct_Student_Reference
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithDirectStudentReference()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_one_person_join_auth_index_on_the_subject_FK()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("DirectStudentRefCarrier");
        index.KeyColumns.Select(c => c.Value).Should().Equal("Student_DocumentId");
        index.IncludeColumns.Should().NotBeNull();
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_DirectStudentRefCarrier_Student_DocumentId_Auth");
    }
}

/// <summary>
/// Test fixture asserting a transitive (2-hop) Student securable — modeled after
/// <c>CourseTranscript → StudentAcademicRecord → Student</c> — emits an auth index per hop on
/// the source table at each hop, each with <c>INCLUDE (DocumentId)</c>.
/// </summary>
[TestFixture]
public class Given_Resource_With_Transitive_Student_Reference
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithTransitiveStudentReference()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentAcademicRecordIntermediate()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_two_person_join_auth_indexes_one_per_hop()
    {
        _authIndexes.Should().HaveCount(2);
    }

    [Test]
    public void It_should_emit_an_auth_index_on_the_subject_hop_FK()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "CourseTranscriptLike"
            && i.KeyColumns[0].Value == "StudentAcademicRecord_DocumentId"
        );
        index.IncludeColumns.Should().NotBeNull();
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_CourseTranscriptLike_StudentAcademicRecord_DocumentId_Auth");
    }

    [Test]
    public void It_should_emit_an_auth_index_on_the_intermediate_hop_FK()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "StudentAcademicRecordLike" && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        index.IncludeColumns.Should().NotBeNull();
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_StudentAcademicRecordLike_Student_DocumentId_Auth");
    }
}

/// <summary>
/// Test fixture asserting that a single subject declaring both Student and Contact securable
/// elements on non-overlapping FK columns emits one auth index per kind, each on its own FK
/// column with <c>INCLUDE (DocumentId)</c>.
/// </summary>
[TestFixture]
public class Given_Resource_With_Multiple_Person_Securables
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithMultiplePersonSecurables()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Contact"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_exactly_two_person_join_auth_indexes()
    {
        _authIndexes.Should().HaveCount(2);
    }

    [Test]
    public void It_should_emit_an_auth_index_on_the_Student_FK()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "MultiPersonCarrier" && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_MultiPersonCarrier_Student_DocumentId_Auth");
    }

    [Test]
    public void It_should_emit_an_auth_index_on_the_Contact_FK()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "MultiPersonCarrier" && i.KeyColumns[0].Value == "Contact_DocumentId"
        );
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_MultiPersonCarrier_Contact_DocumentId_Auth");
    }
}

/// <summary>
/// Test fixture asserting that when the subject IS the person resource itself (e.g. Student
/// with <c>$.studentUniqueId</c>), no person-join auth index is emitted — the root table's
/// <c>DocumentId</c> is the auth anchor.
/// </summary>
[TestFixture]
public class Given_Person_Resource_With_Self_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildPersonResourceWithSelfSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_emit_any_person_join_auth_indexes()
    {
        _authIndexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting that when a subject's Student securable resolves to multiple
/// candidate chains, BFS picks the shortest. A 3-hop short chain and a 4-hop long chain both
/// reach <c>Ed-Fi.Student</c>; the pass must emit indexes only for the 3-hop chain.
/// </summary>
[TestFixture]
public class Given_Resource_Chain_Of_Three_Hops
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                foreach (var r in AuthIndexFixtureResources.BuildResourceChainOfThreeHops())
                {
                    ctx.ConcreteResourcesInNameOrder.Add(r);
                }
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_exactly_three_person_join_auth_indexes()
    {
        _authIndexes.Should().HaveCount(3);
    }

    [Test]
    public void It_should_emit_the_subject_hop_on_the_short_chain_FK()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "ThreeHopChainSubject" && i.KeyColumns[0].Value == "ShortHop1_DocumentId"
        );
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_ThreeHopChainSubject_ShortHop1_DocumentId_Auth");
    }

    [Test]
    public void It_should_emit_the_first_intermediate_hop_on_the_short_chain()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "ShortHop1" && i.KeyColumns[0].Value == "ShortHop2_DocumentId"
        );
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_ShortHop1_ShortHop2_DocumentId_Auth");
    }

    [Test]
    public void It_should_emit_the_terminal_hop_landing_on_Student()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "ShortHop2" && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_ShortHop2_Student_DocumentId_Auth");
    }

    [Test]
    public void It_should_not_emit_indexes_for_the_long_chain_hops()
    {
        _authIndexes
            .Should()
            .NotContain(i =>
                i.Table.Name == "LongHop1" || i.Table.Name == "LongHop2" || i.Table.Name == "LongHop3"
            );
        _authIndexes
            .Should()
            .NotContain(i =>
                i.Table.Name == "ThreeHopChainSubject" && i.KeyColumns[0].Value == "LongHop1_DocumentId"
            );
    }
}

/// <summary>
/// Test fixture asserting the pass throws on an unresolvable EdOrg securable JSON path.
/// </summary>
[TestFixture]
public class Given_Unresolvable_EdOrg_Securable_Path
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        _exception = TestExceptions.CaptureException(() =>
            AuthorizationIndexTestRunner.Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithUnresolvableEdOrgSecurable()
                )
            )
        );
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_should_name_the_resource_and_offending_path()
    {
        _exception!.Message.Should().Contain("UnresolvableResource");
        _exception.Message.Should().Contain("$.nonexistentReference.id");
    }
}

/// <summary>
/// Test fixture asserting the pass throws on an unresolvable Student / Contact / Staff
/// securable JSON path — same contract as the EdOrg unresolvable case.
/// </summary>
[TestFixture]
public class Given_Unresolvable_Person_Securable_Path
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        _exception = TestExceptions.CaptureException(() =>
            AuthorizationIndexTestRunner.Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithUnresolvablePersonPath()
                )
            )
        );
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_should_name_the_resource_and_offending_path()
    {
        _exception!.Message.Should().Contain("PersonUnresolvableCarrier");
        _exception.Message.Should().Contain("$.nonexistentReference.studentUniqueId");
    }
}

/// <summary>
/// Test fixture asserting that when a person-hop FK column on the source root table is a
/// <see cref="ColumnStorage.UnifiedAlias"/>, the emitted auth index keys on the alias's
/// canonical column (not the alias literal), and the index name uses the canonical column.
/// </summary>
[TestFixture]
public class Given_Resource_With_Unified_Alias_Fk_On_Person_Hop
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithUnifiedAliasFkOnPersonHop()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_key_the_emitted_index_on_the_canonical_column()
    {
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("AliasFkCarrier");
        index.KeyColumns.Select(c => c.Value).Should().Equal("Student_DocumentId_Unified");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
    }

    [Test]
    public void It_should_name_the_index_using_the_canonical_column()
    {
        var index = _authIndexes.Single();
        index.Name.Value.Should().Be("IX_AliasFkCarrier_Student_DocumentId_Unified_Auth");
    }

    [Test]
    public void It_should_not_emit_an_index_keyed_on_the_alias_literal()
    {
        _authIndexes.Should().NotContain(i => i.KeyColumns[0].Value == "Student_DocumentId");
    }
}

/// <summary>
/// Test fixture asserting the shared-intermediate-table dedup contract: when two subjects'
/// person chains traverse the same <c>(intermediate table, FK column)</c> hop, the pass emits
/// exactly one auth index for that shared hop — the second subject's iteration sees the
/// existing auth-index lookup entry and skips emission.
/// </summary>
[TestFixture]
public class Given_Shared_Intermediate_Table_Between_Two_Subjects
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithTransitiveStudentReference()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildSecondTransitiveStudentSubject()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentAcademicRecordIntermediate()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_exactly_one_auth_index_for_the_shared_hop()
    {
        _authIndexes
            .Should()
            .ContainSingle(i =>
                i.Table.Name == "StudentAcademicRecordLike" && i.KeyColumns[0].Value == "Student_DocumentId"
            );
    }

    [Test]
    public void It_should_emit_distinct_auth_indexes_for_each_subjects_first_hop()
    {
        _authIndexes
            .Should()
            .ContainSingle(i =>
                i.Table.Name == "CourseTranscriptLike"
                && i.KeyColumns[0].Value == "StudentAcademicRecord_DocumentId"
            );
        _authIndexes
            .Should()
            .ContainSingle(i =>
                i.Table.Name == "ReportCardLike"
                && i.KeyColumns[0].Value == "StudentAcademicRecord_DocumentId"
            );
    }

    [Test]
    public void It_should_emit_exactly_three_auth_indexes_total()
    {
        _authIndexes.Should().HaveCount(3);
    }
}

/// <summary>
/// Test fixture asserting the PK/UK contract: when a person-join hop lands on the leading
/// column of an existing PrimaryKey or UniqueConstraint index — and no auth index already
/// covers it — the pass emits a SEPARATE auth index with <c>INCLUDE (DocumentId)</c>. The
/// structural PK/UK is never widened: it doesn't supply <c>INCLUDE (DocumentId)</c> on its own,
/// so the person-join index still adds value.
/// </summary>
[TestFixture]
public class Given_Person_Chain_Hitting_PkUk_Leading_Column
{
    private IReadOnlyList<DbIndexInfo> _allIndexes = default!;

    [SetUp]
    public void Setup()
    {
        var collisionTable = new DbTableName(new DbSchemaName("edfi"), "StudentAcademicRecordLike");
        var collisionColumn = new DbColumnName("Student_DocumentId");

        _allIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                // Seed a PrimaryKey index at (StudentAcademicRecordLike, Student_DocumentId) so
                // the leading-column dedup set sees a structural index at the hop's (table, key)
                // before person-join emission runs.
                ctx.IndexInventory.Add(
                    new DbIndexInfo(
                        new DbIndexName("PK_StudentAcademicRecordLike_StudentScope"),
                        collisionTable,
                        KeyColumns: [collisionColumn],
                        IsUnique: true,
                        Kind: DbIndexKind.PrimaryKey
                    )
                );

                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithTransitiveStudentReference()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentAcademicRecordIntermediate()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.ToArray();
    }

    [Test]
    public void It_should_emit_a_separate_auth_index_at_the_pk_leading_column()
    {
        var authIndex = _allIndexes.Single(i =>
            i.Kind == DbIndexKind.Authorization
            && i.Table.Name == "StudentAcademicRecordLike"
            && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        authIndex.IncludeColumns.Should().NotBeNull();
        authIndex.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        authIndex.Name.Value.Should().Be("IX_StudentAcademicRecordLike_Student_DocumentId_Auth");
    }

    [Test]
    public void It_should_leave_the_pk_index_unmodified()
    {
        var pk = _allIndexes.Single(i => i.Kind == DbIndexKind.PrimaryKey);
        pk.IncludeColumns.Should().BeNull();
        pk.Table.Name.Should().Be("StudentAcademicRecordLike");
        pk.KeyColumns.Select(c => c.Value).Should().Equal("Student_DocumentId");
        pk.Name.Value.Should().Be("PK_StudentAcademicRecordLike_StudentScope");
    }
}

/// <summary>
/// Test fixture asserting the PA-collision-widen contract: when a person-join hop lands on a
/// <c>(table, leading key)</c> already covered by a PrimaryAssociation auth index, the pass
/// widens the PA index's <c>IncludeColumns</c> in place rather than emitting a separate
/// auth index. The merged INCLUDE list is sorted ordinal-ascending by <c>DbColumnName.Value</c>
/// and deduped — for the <c>StudentContactAssociation</c> shape this yields
/// <c>[Contact_DocumentId, DocumentId]</c>.
/// </summary>
[TestFixture]
public class Given_Person_Chain_Hitting_Pa_Covered_Column
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildPersonChainHittingPaCoveredColumn()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentContactAssociationWithStudentBinding()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_exactly_one_auth_index_at_the_pa_covered_key()
    {
        _authIndexes
            .Should()
            .ContainSingle(i =>
                i.Table.Name == "StudentContactAssociation" && i.KeyColumns[0].Value == "Student_DocumentId"
            );
    }

    [Test]
    public void It_should_widen_the_pa_index_include_columns_in_canonical_order()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "StudentContactAssociation" && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        index.IncludeColumns.Should().NotBeNull();
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Contact_DocumentId", "DocumentId");
        index.Name.Value.Should().Be("IX_StudentContactAssociation_Student_DocumentId_Auth");
    }

    [Test]
    public void It_should_still_emit_the_first_hop_on_the_subject_FK()
    {
        var index = _authIndexes.Single(i =>
            i.Table.Name == "PaCollisionSubject"
            && i.KeyColumns[0].Value == "StudentContactAssociation_DocumentId"
        );
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_PaCollisionSubject_StudentContactAssociation_DocumentId_Auth");
    }
}

/// <summary>
/// Test fixture asserting the mixed array-nested + resolvable case: when at least one person
/// securable path resolves, array-nested paths are silently skipped (no throw) and an auth
/// index is emitted only for the resolvable path's hop.
/// </summary>
[TestFixture]
public class Given_Resource_With_Mixed_Array_Nested_And_Resolvable_Person_Path
{
    private DerivedRelationalModelSet _modelSet = default!;
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _modelSet = AuthorizationIndexTestRunner.Build(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(
                AuthIndexFixtureResources.BuildResourceWithMixedArrayNestedAndResolvablePersonPath()
            );
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
        });

        _authIndexes = _modelSet
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_throw_and_should_emit_exactly_one_auth_index()
    {
        _authIndexes.Should().ContainSingle();
    }

    [Test]
    public void It_should_emit_the_auth_index_for_the_resolvable_path_only()
    {
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("MixedPersonPathCarrier");
        index.KeyColumns.Select(c => c.Value).Should().Equal("Student_DocumentId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("DocumentId");
        index.Name.Value.Should().Be("IX_MixedPersonPathCarrier_Student_DocumentId_Auth");
    }
}

/// <summary>
/// Test fixture asserting that when a resource's only person securable path is array-nested
/// (and no other securable element resolved), the pass throws with the runtime resolver's
/// "unsupported child-table traversal" message shape, naming both the resource and the path.
/// </summary>
[TestFixture]
public class Given_Resource_With_Array_Nested_Person_Path_Only
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        _exception = TestExceptions.CaptureException(() =>
            AuthorizationIndexTestRunner.Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithArrayNestedPersonPathOnly()
                )
            )
        );
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_should_use_the_runtime_unsupported_child_table_traversal_message()
    {
        _exception!.Message.Should().Contain("unsupported child-table traversal");
    }

    [Test]
    public void It_should_name_the_resource_and_offending_path()
    {
        _exception!.Message.Should().Contain("ArrayNestedPersonCarrier");
        _exception.Message.Should().Contain("$.items[*].studentReference.studentUniqueId");
    }
}

/// <summary>
/// Test fixture asserting the cross-pass <c>anyResolved</c> carryover: when a resource has a
/// resolvable Namespace securable AND an array-nested-only Student path, the Namespace
/// resolution marks the resource as "any resolved," so the array-nested Student path is
/// silently skipped instead of throwing. Guards the seeding of
/// <c>resourcesWithResolvedSecurable</c> from <c>EmitSecurableElementIndexes</c> into
/// <c>EmitPersonJoinIndexes</c>.
/// </summary>
[TestFixture]
public class Given_Resource_With_Resolved_Namespace_And_Array_Nested_Student_Path
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithResolvedNamespaceAndArrayNestedStudentPath()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_throw_and_should_emit_the_namespace_auth_index()
    {
        _authIndexes
            .Should()
            .ContainSingle(i =>
                i.Table.Name == "NamespaceAndArrayNestedStudentCarrier"
                && i.KeyColumns[0].Value == "Namespace"
            );
    }

    [Test]
    public void It_should_not_emit_any_student_auth_index()
    {
        _authIndexes.Should().NotContain(i => i.KeyColumns[0].Value == "Student_DocumentId");
    }
}

/// <summary>
/// Test fixture asserting the cross-kind <c>anyResolved</c> carryover within
/// <c>EmitPersonJoinIndexes</c>: when a resource resolves a Student path AND has an
/// array-nested-only Contact path, the Student resolution marks the resource as "any resolved,"
/// so the array-nested Contact path is silently skipped instead of throwing. Guards the
/// shared <c>anyResolved</c> across Student / Contact / Staff iterations in the pass.
/// </summary>
[TestFixture]
public class Given_Resource_With_Resolved_Student_And_Array_Nested_Contact_Path
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithResolvedStudentAndArrayNestedContactPath()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_throw_and_should_emit_the_student_auth_index()
    {
        _authIndexes
            .Should()
            .ContainSingle(i =>
                i.Table.Name == "StudentAndArrayNestedContactCarrier"
                && i.KeyColumns[0].Value == "Student_DocumentId"
            );
    }

    [Test]
    public void It_should_not_emit_any_contact_auth_index()
    {
        _authIndexes.Should().NotContain(i => i.KeyColumns[0].Value == "Contact_DocumentId");
    }
}

/// <summary>
/// Test fixture asserting two builds with the same input produce identical authorization
/// index entries (determinism).
/// </summary>
[TestFixture]
public class Given_Two_Builds_With_The_Same_Input
{
    private IReadOnlyList<DbIndexInfo> _firstBuild = default!;
    private IReadOnlyList<DbIndexInfo> _secondBuild = default!;

    [SetUp]
    public void Setup()
    {
        IReadOnlyList<DbIndexInfo> Run() =>
            AuthorizationIndexTestRunner
                .Build(ctx =>
                {
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildStudentSchoolAssociation()
                    );
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildResourceWithNamespaceSecurable()
                    );
                })
                .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
                .ToArray();

        _firstBuild = Run();
        _secondBuild = Run();
    }

    [Test]
    public void It_should_produce_identical_index_entries()
    {
        _secondBuild.Should().BeEquivalentTo(_firstBuild, options => options.WithStrictOrdering());
    }
}

/// <summary>
/// Determinism test for the person-join branch: two builds over the same fixture (which
/// exercises a person hop colliding with a PA-covered key — triggering the in-place INCLUDE
/// widening) must produce element-wise identical <c>IndexesInCreateOrder</c>, including the
/// widened <c>IncludeColumns</c> sequence in canonical ordinal order.
/// </summary>
[TestFixture]
public class Given_Person_Indexes_Are_Built_Twice
{
    private IReadOnlyList<DbIndexInfo> _firstBuild = default!;
    private IReadOnlyList<DbIndexInfo> _secondBuild = default!;

    [SetUp]
    public void Setup()
    {
        IReadOnlyList<DbIndexInfo> Run() =>
            AuthorizationIndexTestRunner
                .Build(ctx =>
                {
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildPersonChainHittingPaCoveredColumn()
                    );
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildStudentContactAssociationWithStudentBinding()
                    );
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildResourceWithTransitiveStudentReference()
                    );
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildStudentAcademicRecordIntermediate()
                    );
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildPlainResource("Student")
                    );
                })
                .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
                .ToArray();

        _firstBuild = Run();
        _secondBuild = Run();
    }

    [Test]
    public void It_should_produce_identical_index_entries_including_widened_includes()
    {
        _secondBuild.Should().BeEquivalentTo(_firstBuild, options => options.WithStrictOrdering());
    }

    [Test]
    public void It_should_produce_identical_widened_include_columns_on_the_pa_collision_index()
    {
        var firstWidened = _firstBuild.Single(i =>
            i.Table.Name == "StudentContactAssociation" && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        var secondWidened = _secondBuild.Single(i =>
            i.Table.Name == "StudentContactAssociation" && i.KeyColumns[0].Value == "Student_DocumentId"
        );

        secondWidened
            .IncludeColumns!.Select(c => c.Value)
            .Should()
            .Equal(firstWidened.IncludeColumns!.Select(c => c.Value));
        secondWidened.IncludeColumns!.Select(c => c.Value).Should().Equal("Contact_DocumentId", "DocumentId");
    }
}

/// <summary>
/// Test fixture asserting that include-widening targets the correct inventory row when two
/// auth indexes share the same <see cref="DbIndexInfo.Name"/> but live in different schemas.
/// Auth index names are <c>IX_{TableName}_{Columns}_Auth</c> with no schema prefix, so dialect
/// uniqueness scoping (per-schema in Pgsql, per-(schema, table) in Mssql) permits same-named
/// auth indexes in different <c>(schema, table)</c>. The widen branch in
/// <see cref="DeriveAuthorizationIndexInventoryPass"/> must match by <c>Name</c> AND
/// <c>Table</c> — matching by <c>Name</c> alone could mutate the first same-named inventory
/// entry instead of the intended scoped row.
/// </summary>
[TestFixture]
public class Given_Auth_Index_Names_Collide_Across_Schemas
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        // Pre-seed an Auth-kind index in a non-edfi schema with the SAME name the pass will
        // emit for edfi.StudentContactAssociation's PA index. The seeded entry is added to the
        // inventory BEFORE the pass runs, so a Name-only match in the widen loop would update
        // this row instead of the pass's own emission.
        var extSchemaTable = new DbTableName(new DbSchemaName("ext"), "StudentContactAssociation");
        var fkColumn = new DbColumnName("Student_DocumentId");
        var collidingName = new DbIndexName(
            ConstraintNaming.BuildAuthorizationIndexName(extSchemaTable, [fkColumn])
        );

        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.IndexInventory.Add(
                    new DbIndexInfo(
                        collidingName,
                        extSchemaTable,
                        KeyColumns: [fkColumn],
                        IsUnique: false,
                        Kind: DbIndexKind.Authorization,
                        IncludeColumns: null
                    )
                );

                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildPersonChainHittingPaCoveredColumn()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentContactAssociationWithStudentBinding()
                );
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Student"));
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_widen_only_the_edfi_scoped_entry()
    {
        var edfiEntry = _authIndexes.Single(i =>
            i.Table.Schema.Value == "edfi"
            && i.Table.Name == "StudentContactAssociation"
            && i.KeyColumns[0].Value == "Student_DocumentId"
        );
        edfiEntry.IncludeColumns.Should().NotBeNull();
        edfiEntry.IncludeColumns!.Select(c => c.Value).Should().Equal("Contact_DocumentId", "DocumentId");
    }

    [Test]
    public void It_should_leave_the_same_named_entry_in_the_other_schema_untouched()
    {
        var extEntry = _authIndexes.Single(i =>
            i.Table.Schema.Value == "ext" && i.Table.Name == "StudentContactAssociation"
        );
        extEntry.IncludeColumns.Should().BeNull();
        extEntry.Name.Value.Should().Be("IX_StudentContactAssociation_Student_DocumentId_Auth");
    }
}

/// <summary>
/// Test fixture asserting that an FK-support index and an Authorization index can target the
/// same root-table column without tripping <c>ValidateIndexNameUniqueness</c> in
/// <see cref="RelationalModelSetBuilderContext.BuildResult"/>. The <c>_Auth</c> suffix on
/// authorization index names is the load-bearing differentiator; this test guards against a
/// future change to <see cref="ConstraintNaming.BuildAuthorizationIndexName"/> dropping or
/// shifting that suffix in a way that would collide with FK-support index names.
/// </summary>
[TestFixture]
public class Given_FkSupport_And_Authorization_Index_On_Same_Column
{
    private DerivedRelationalModelSet _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = AuthorizationIndexTestRunner.Build(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildCourseWithEdOrgSecurable());

            // Pre-seed an FK-support index targeting the same root column the auth pass will
            // emit on (the identity scalar). FK-support indexes normally go on FK DocumentId
            // columns; this synthetic seeding deliberately collides on the identity column to
            // exercise the name-uniqueness invariant. If the auth pass dropped its `_Auth`
            // suffix, the names would collide and BuildResult would throw via
            // ValidateIndexNameUniqueness.
            var courseTable = new DbTableName(new DbSchemaName("edfi"), "Course");
            var sharedColumn = new DbColumnName("EducationOrganization_EducationOrganizationId");
            ctx.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName(
                        ConstraintNaming.BuildForeignKeySupportIndexName(courseTable, [sharedColumn])
                    ),
                    courseTable,
                    KeyColumns: [sharedColumn],
                    IsUnique: false,
                    Kind: DbIndexKind.ForeignKeySupport
                )
            );
        });
    }

    [Test]
    public void It_should_emit_both_indexes_with_distinct_names()
    {
        var indexNames = _result
            .IndexesInCreateOrder.Where(i => i.Table.Name == "Course")
            .Select(i => i.Name.Value)
            .ToArray();

        indexNames.Should().HaveCount(2);
        indexNames.Should().Contain("IX_Course_EducationOrganization_EducationOrganizationId");
        indexNames.Should().Contain("IX_Course_EducationOrganization_EducationOrganizationId_Auth");
    }
}

/// <summary>
/// Test fixture asserting a securable-element authorization index is suppressed when an existing
/// PrimaryKey or UniqueConstraint index already leads on the same <c>(table, column)</c>. The
/// PK/UK already supports the same equality lookup, so an extra non-unique covering index would
/// be dead weight on writes and storage.
/// </summary>
[TestFixture]
public class Given_PkUk_Already_Covers_The_Auth_Column
{
    private IReadOnlyList<DbIndexInfo> _authIndexesOnCourse = default!;

    [SetUp]
    public void Setup()
    {
        var courseTable = new DbTableName(new DbSchemaName("edfi"), "Course");
        var authColumn = new DbColumnName("EducationOrganization_EducationOrganizationId");

        var result = AuthorizationIndexTestRunner.Build(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildCourseWithEdOrgSecurable());

            // Pre-seed a unique index whose leading column matches the auth pass's target.
            // The auth pass should detect this and suppress its own emission.
            ctx.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName("UX_Course_NK"),
                    courseTable,
                    KeyColumns: [authColumn],
                    IsUnique: true,
                    Kind: DbIndexKind.UniqueConstraint
                )
            );
        });

        _authIndexesOnCourse = result
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization && i.Table == courseTable)
            .ToArray();
    }

    [Test]
    public void It_should_not_emit_a_redundant_authorization_index()
    {
        _authIndexesOnCourse.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting that authorization indexes appear in canonical
/// <c>(schema, table, name)</c> order in <c>IndexesInCreateOrder</c> after the
/// <see cref="CanonicalizeOrderingPass"/> runs. Guards against a future refactor to the new
/// pass that introduces a non-deterministic data structure (e.g. iterating a <c>HashSet</c>):
/// the canonicalize pass is the safety net, this test asserts it.
/// </summary>
[TestFixture]
public class Given_Multiple_Authorization_Indexes_After_Canonicalize
{
    private DbIndexInfo[] _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        // Add resources in non-canonical order so canonicalization actually has work to do.
        var result = AuthorizationIndexTestRunner.BuildWithCanonicalize(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildStudentSchoolAssociation());
            ctx.ConcreteResourcesInNameOrder.Add(
                AuthIndexFixtureResources.BuildResourceWithNamespaceSecurable()
            );
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildStudentContactAssociation());
        });

        _authIndexes = result.IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization).ToArray();
    }

    [Test]
    public void It_should_order_authorization_indexes_by_schema_table_then_name()
    {
        var sortKeys = _authIndexes
            .Select(i => $"{i.Table.Schema.Value}|{i.Table.Name}|{i.Name.Value}")
            .ToArray();
        sortKeys.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Test]
    public void It_should_emit_at_least_two_authorization_indexes()
    {
        // Sanity check the test actually exercises ordering across multiple entries.
        _authIndexes.Length.Should().BeGreaterOrEqualTo(2);
    }
}

/// <summary>
/// Test fixture asserting the canonical pass list contains
/// <see cref="DeriveAuthorizationIndexInventoryPass"/> immediately after
/// <see cref="DeriveAuthHierarchyPass"/> and before the dialect-shortening / canonicalize passes.
/// </summary>
[TestFixture]
public class Given_The_Canonical_Pass_List
{
    [Test]
    public void It_should_register_DeriveAuthorizationIndexInventoryPass_after_DeriveAuthHierarchyPass()
    {
        var passNames = RelationalModelSetPasses.CreateDefault().Select(p => p.GetType().Name).ToArray();

        var hierarchyIndex = Array.IndexOf(passNames, nameof(DeriveAuthHierarchyPass));
        var authIndexInventoryIndex = Array.IndexOf(passNames, nameof(DeriveAuthorizationIndexInventoryPass));

        hierarchyIndex.Should().BeGreaterThan(-1);
        authIndexInventoryIndex.Should().Be(hierarchyIndex + 1);
    }

    [Test]
    public void It_should_run_before_the_dialect_shortening_pass()
    {
        var passNames = RelationalModelSetPasses.CreateDefault().Select(p => p.GetType().Name).ToArray();

        var authIndexInventoryIndex = Array.IndexOf(passNames, nameof(DeriveAuthorizationIndexInventoryPass));
        var dialectShorteningIndex = Array.IndexOf(passNames, "ApplyDialectIdentifierShorteningPass");

        authIndexInventoryIndex.Should().BeLessThan(dialectShorteningIndex);
    }

    [Test]
    public void It_should_be_present_in_the_strict_pass_list()
    {
        RelationalModelSetPasses
            .CreateStrict()
            .Select(p => p.GetType().Name)
            .Should()
            .Contain(nameof(DeriveAuthorizationIndexInventoryPass));
    }
}

/// <summary>
/// Builds a derived relational model set by injecting a configurable fixture pass that
/// populates <c>ConcreteResourcesInNameOrder</c>, then runs the pass under test.
/// </summary>
internal static class AuthorizationIndexTestRunner
{
    public static DerivedRelationalModelSet Build(Action<RelationalModelSetBuilderContext> setup) =>
        BuildWith(setup, throwOnMissingPaLiteral: false, includeCanonicalize: false);

    public static DerivedRelationalModelSet BuildStrict(Action<RelationalModelSetBuilderContext> setup) =>
        BuildWith(setup, throwOnMissingPaLiteral: true, includeCanonicalize: false);

    public static DerivedRelationalModelSet BuildWithCanonicalize(
        Action<RelationalModelSetBuilderContext> setup
    ) => BuildWith(setup, throwOnMissingPaLiteral: false, includeCanonicalize: true);

    private static DerivedRelationalModelSet BuildWith(
        Action<RelationalModelSetBuilderContext> setup,
        bool throwOnMissingPaLiteral,
        bool includeCanonicalize
    )
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        IRelationalModelSetPass[] passes = includeCanonicalize
            ?
            [
                new SetupFixturePass(setup),
                new DeriveAuthorizationIndexInventoryPass(throwOnMissingPaLiteral),
                new CanonicalizeOrderingPass(),
            ]
            :
            [
                new SetupFixturePass(setup),
                new DeriveAuthorizationIndexInventoryPass(throwOnMissingPaLiteral),
            ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    private sealed class SetupFixturePass(Action<RelationalModelSetBuilderContext> setup)
        : IRelationalModelSetPass
    {
        public void Execute(RelationalModelSetBuilderContext context) => setup(context);
    }
}

internal static class TestExceptions
{
    public static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

/// <summary>
/// Hand-built <see cref="ConcreteResourceModel"/> fixtures for authorization-index pass tests.
/// Each builder produces a minimal but semantically valid resource with the columns,
/// reference bindings, and securable elements relevant to the scenario.
/// </summary>
internal static class AuthIndexFixtureResources
{
    private const string EdFi = "Ed-Fi";
    private static readonly DbSchemaName _edfiSchema = new("edfi");

    public static ConcreteResourceModel BuildStudentSchoolAssociation() =>
        BuildPaResource(
            "StudentSchoolAssociation",
            new DbColumnName("SchoolId_Unified"),
            new DbColumnName("Student_DocumentId")
        );

    public static ConcreteResourceModel BuildStudentContactAssociation() =>
        BuildPaResource(
            "StudentContactAssociation",
            new DbColumnName("Student_DocumentId"),
            new DbColumnName("Contact_DocumentId")
        );

    public static ConcreteResourceModel BuildStaffEducationOrganizationAssignmentAssociation() =>
        BuildPaResource(
            "StaffEducationOrganizationAssignmentAssociation",
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Staff_DocumentId")
        );

    public static ConcreteResourceModel BuildStaffEducationOrganizationEmploymentAssociation() =>
        BuildPaResource(
            "StaffEducationOrganizationEmploymentAssociation",
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Staff_DocumentId")
        );

    public static ConcreteResourceModel BuildStudentEducationOrganizationResponsibilityAssociation() =>
        BuildPaResource(
            "StudentEducationOrganizationResponsibilityAssociation",
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Student_DocumentId")
        );

    public static ConcreteResourceModel BuildStudentSchoolAssociationWithAliasedSchoolId(
        DbColumnName canonicalColumn
    )
    {
        var keyLiteral = new DbColumnName("SchoolId_Unified");
        var includeLiteral = new DbColumnName("Student_DocumentId");

        var columns = new[]
        {
            BuildScalarColumn(canonicalColumn),
            BuildAliasColumn(keyLiteral, canonicalColumn),
            BuildScalarColumn(includeLiteral),
        };

        return BuildResourceFromColumns("StudentSchoolAssociation", columns);
    }

    public static ConcreteResourceModel BuildStudentSchoolAssociationWithoutKeyColumn()
    {
        // Root has Student_DocumentId but no SchoolId_Unified column — PA literal lookup must throw.
        var columns = new[] { BuildScalarColumn(new DbColumnName("Student_DocumentId")) };

        return BuildResourceFromColumns("StudentSchoolAssociation", columns);
    }

    public static ConcreteResourceModel BuildStudentSchoolAssociationWithSchoolEdOrgSecurable()
    {
        var resourceName = "StudentSchoolAssociation";
        var keyColumn = new DbColumnName("SchoolId_Unified");
        var includeColumn = new DbColumnName("Student_DocumentId");
        var rootTable = new DbTableName(_edfiSchema, resourceName);

        // The same SchoolId_Unified column resolves both the PA key and the EdOrg securable —
        // the pass must dedup the EdOrg emission against the PA-covered set.
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
            Table: rootTable,
            FkColumn: keyColumn,
            TargetResource: new QualifiedResourceName(EdFi, "School"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.schoolId"),
                    JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                    keyColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(keyColumn), BuildScalarColumn(includeColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    public static ConcreteResourceModel BuildPlainResource(string resourceName) =>
        BuildResourceFromColumns(resourceName, [BuildScalarColumn(new DbColumnName("DocumentId"))]);

    public static ConcreteResourceModel BuildCourseWithEdOrgSecurable()
    {
        var resourceName = "Course";
        var fkColumn = new DbColumnName("EducationOrganization_DocumentId");
        var identityColumn = new DbColumnName("EducationOrganization_EducationOrganizationId");
        var rootTable = new DbTableName(_edfiSchema, resourceName);

        var columns = new[]
        {
            BuildScalarColumn(new DbColumnName("DocumentId")),
            BuildScalarColumn(fkColumn),
            BuildScalarColumn(identityColumn),
        };

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.educationOrganizationReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "EducationOrganization"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.educationOrganizationId"),
                    JsonPathExpressionCompiler.Compile(
                        "$.educationOrganizationReference.educationOrganizationId"
                    ),
                    identityColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            columns,
            [binding],
            new ResourceSecurableElements(
                EducationOrganization:
                [
                    new EdOrgSecurableElement(
                        "$.educationOrganizationReference.educationOrganizationId",
                        "EducationOrganizationId"
                    ),
                ],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    public static ConcreteResourceModel BuildResourceWithNamespaceSecurable()
    {
        var columns = new[]
        {
            BuildScalarColumn(new DbColumnName("DocumentId")),
            BuildScalarColumnWithJsonPath(new DbColumnName("Namespace"), "$.namespace"),
        };

        return BuildResource(
            "NamespaceCarrier",
            columns,
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: ["$.namespace"],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    public static ConcreteResourceModel BuildResourceWithNestedNamespaceSecurable()
    {
        var rootResourceName = "GraduationPlanLike";
        var childTableName = new DbTableName(_edfiSchema, "GraduationPlanLikeRequiredAssessment");
        var nestedNamespacePath = "$.requiredAssessments[*].assessmentReference.namespace";
        var nestedNamespaceColumn = new DbColumnName("RequiredAssessmentAssessment_Namespace");

        var rootTable = new DbTableModel(
            new DbTableName(_edfiSchema, rootResourceName),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                $"PK_{rootResourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            []
        );

        var childTable = new DbTableModel(
            childTableName,
            JsonPathExpressionCompiler.Compile("$.requiredAssessments[*]"),
            new TableKey(
                "PK_GraduationPlanLikeRequiredAssessment",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                BuildScalarColumn(new DbColumnName("CollectionItemId")),
                BuildScalarColumn(new DbColumnName("RequiredAssessmentAssessment_DocumentId")),
                BuildScalarColumn(nestedNamespaceColumn),
            ],
            []
        );

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile(
                "$.requiredAssessments[*].assessmentReference"
            ),
            Table: childTableName,
            FkColumn: new DbColumnName("RequiredAssessmentAssessment_DocumentId"),
            TargetResource: new QualifiedResourceName(EdFi, "Assessment"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.assessmentIdentifier"),
                    JsonPathExpressionCompiler.Compile(
                        "$.requiredAssessments[*].assessmentReference.assessmentIdentifier"
                    ),
                    new DbColumnName("RequiredAssessmentAssessment_AssessmentIdentifier")
                ),
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.namespace"),
                    JsonPathExpressionCompiler.Compile(nestedNamespacePath),
                    nestedNamespaceColumn
                ),
            ]
        );

        var qualifiedName = new QualifiedResourceName(EdFi, rootResourceName);
        var resourceKey = new ResourceKeyEntry(1, qualifiedName, "1.0.0", false);
        var relationalModel = new RelationalResourceModel(
            qualifiedName,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable],
            [binding],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [nestedNamespacePath],
                Student: [],
                Contact: [],
                Staff: []
            ),
        };
    }

    public static ConcreteResourceModel BuildResourceWithNestedEdOrgSecurable()
    {
        var rootResourceName = "AssessmentAdministrationParticipationLike";
        var childTableName = new DbTableName(
            _edfiSchema,
            "AssessmentAdministrationParticipationLikeAdministrationPoint"
        );
        var nestedEdOrgPath =
            "$.assessmentAdministrationPoints[*].administeringOrganizationReference.educationOrganizationId";
        var nestedEdOrgColumn = new DbColumnName("AdministeringOrganization_EducationOrganizationId");

        var rootTable = new DbTableModel(
            new DbTableName(_edfiSchema, rootResourceName),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                $"PK_{rootResourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            []
        );

        var childTable = new DbTableModel(
            childTableName,
            JsonPathExpressionCompiler.Compile("$.assessmentAdministrationPoints[*]"),
            new TableKey(
                "PK_AssessmentAdministrationParticipationLikeAdministrationPoint",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                BuildScalarColumn(new DbColumnName("CollectionItemId")),
                BuildScalarColumn(new DbColumnName("AdministeringOrganization_DocumentId")),
                BuildScalarColumn(nestedEdOrgColumn),
            ],
            []
        );

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile(
                "$.assessmentAdministrationPoints[*].administeringOrganizationReference"
            ),
            Table: childTableName,
            FkColumn: new DbColumnName("AdministeringOrganization_DocumentId"),
            TargetResource: new QualifiedResourceName(EdFi, "EducationOrganization"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.educationOrganizationId"),
                    JsonPathExpressionCompiler.Compile(nestedEdOrgPath),
                    nestedEdOrgColumn
                ),
            ]
        );

        var qualifiedName = new QualifiedResourceName(EdFi, rootResourceName);
        var resourceKey = new ResourceKeyEntry(1, qualifiedName, "1.0.0", false);
        var relationalModel = new RelationalResourceModel(
            qualifiedName,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable],
            [binding],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization:
                [
                    new EdOrgSecurableElement(nestedEdOrgPath, "EducationOrganizationId"),
                ],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            ),
        };
    }

    public static ConcreteResourceModel BuildResourceWithUnresolvableEdOrgSecurable()
    {
        var columns = new[] { BuildScalarColumn(new DbColumnName("DocumentId")) };

        return BuildResource(
            "UnresolvableResource",
            columns,
            [],
            new ResourceSecurableElements(
                EducationOrganization: [new EdOrgSecurableElement("$.nonexistentReference.id", "Id")],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Subject resource with a single direct reference to <c>Ed-Fi.Student</c> and a Student
    /// securable element on <c>$.studentReference.studentUniqueId</c>. Drives the 1-hop direct
    /// person-join case: a single auth index on the subject's root table keyed by the FK
    /// <c>Student_DocumentId</c> column, INCLUDE <c>DocumentId</c>. Tests using this builder
    /// must also add <see cref="BuildPlainResource"/>(<c>"Student"</c>) to the context so the
    /// BFS resource lookup can resolve the target person resource.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithDirectStudentReference()
    {
        const string resourceName = "DirectStudentRefCarrier";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName("Student_DocumentId");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    fkColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Subject resource modeled after <c>CourseTranscript → StudentAcademicRecord → Student</c>
    /// from <c>auth.md</c>. Has a single root-level reference to
    /// <c>Ed-Fi.StudentAcademicRecord</c> and a Student securable element whose prefix matches
    /// that reference. Drives the 2-hop transitive case: BFS walks the intermediate's binding
    /// to <c>Ed-Fi.Student</c>, emitting one auth index per hop, each INCLUDE the source
    /// table's <c>DocumentId</c>. Tests using this builder must also add
    /// <see cref="BuildStudentAcademicRecordIntermediate"/> and
    /// <see cref="BuildPlainResource"/>(<c>"Student"</c>) to the context.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithTransitiveStudentReference()
    {
        const string resourceName = "CourseTranscriptLike";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName("StudentAcademicRecord_DocumentId");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentAcademicRecordReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "StudentAcademicRecordLike"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentAcademicRecordReference.studentUniqueId"),
                    fkColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentAcademicRecordReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Intermediate resource for the transitive-reference fixture. Mirrors a
    /// <c>StudentAcademicRecord</c>-shaped table whose root-level
    /// <see cref="DocumentReferenceBinding"/> points to <c>Ed-Fi.Student</c> via
    /// <c>$.studentReference</c> on the <c>Student_DocumentId</c> column. Carries no securable
    /// elements of its own — its only role is to extend the BFS chain by one hop.
    /// </summary>
    public static ConcreteResourceModel BuildStudentAcademicRecordIntermediate()
    {
        const string resourceName = "StudentAcademicRecordLike";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName("Student_DocumentId");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    fkColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            ResourceSecurableElements.Empty
        );
    }

    /// <summary>
    /// Subject resource whose Student FK column is a <see cref="ColumnStorage.UnifiedAlias"/>
    /// pointing at a canonical column. Drives canonical-column resolution in
    /// <c>AddPersonJoinIndex</c>: the emitted auth index must key on the canonical column, not
    /// the alias literal, and the index name must use the canonical column. Tests using this
    /// builder must also add <see cref="BuildPlainResource"/>(<c>"Student"</c>).
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithUnifiedAliasFkOnPersonHop()
    {
        const string resourceName = "AliasFkCarrier";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var aliasFk = new DbColumnName("Student_DocumentId");
        var canonicalFk = new DbColumnName("Student_DocumentId_Unified");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: aliasFk,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    aliasFk
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [
                BuildScalarColumn(new DbColumnName("DocumentId")),
                BuildScalarColumn(canonicalFk),
                BuildAliasColumn(aliasFk, canonicalFk),
            ],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Second subject sharing the <c>StudentAcademicRecordLike</c> intermediate with
    /// <see cref="BuildResourceWithTransitiveStudentReference"/>. Drives the
    /// shared-intermediate-table scenario: both subjects' chains traverse the same
    /// <c>(StudentAcademicRecordLike, Student_DocumentId)</c> hop, which must emit exactly one
    /// auth index across the two subjects' processing iterations.
    /// </summary>
    public static ConcreteResourceModel BuildSecondTransitiveStudentSubject()
    {
        const string resourceName = "ReportCardLike";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName("StudentAcademicRecord_DocumentId");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentAcademicRecordReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "StudentAcademicRecordLike"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentAcademicRecordReference.studentUniqueId"),
                    fkColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentAcademicRecordReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// <c>StudentContactAssociation</c>-shaped resource carrying both the PA columns
    /// (<c>Student_DocumentId</c>, <c>Contact_DocumentId</c>) AND a root-level
    /// <see cref="DocumentReferenceBinding"/> from <c>Student_DocumentId</c> to
    /// <c>Ed-Fi.Student</c>. Drives the PA-collision-widen case: the pass emits the PA covering
    /// index <c>IX_StudentContactAssociation_Student_DocumentId_Auth</c> INCLUDE
    /// <c>[Contact_DocumentId]</c>, then a person-join hop landing on the same
    /// <c>(table, key)</c> widens that INCLUDE to <c>[Contact_DocumentId, DocumentId]</c>.
    /// </summary>
    public static ConcreteResourceModel BuildStudentContactAssociationWithStudentBinding()
    {
        const string resourceName = "StudentContactAssociation";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var studentFk = new DbColumnName("Student_DocumentId");
        var contactFk = new DbColumnName("Contact_DocumentId");

        var studentBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: studentFk,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    studentFk
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [
                BuildScalarColumn(new DbColumnName("DocumentId")),
                BuildScalarColumn(studentFk),
                BuildScalarColumn(contactFk),
            ],
            [studentBinding],
            ResourceSecurableElements.Empty
        );
    }

    /// <summary>
    /// Subject resource whose Student securable path drives a 2-hop chain through
    /// <c>StudentContactAssociation</c> to <c>Ed-Fi.Student</c>. The second hop lands on
    /// <c>(StudentContactAssociation, Student_DocumentId)</c> — already covered by a PA auth
    /// index — exercising the in-place widening branch in
    /// <c>AddPersonJoinIndex</c>. Tests using this builder must also add
    /// <see cref="BuildStudentContactAssociationWithStudentBinding"/> and
    /// <see cref="BuildPlainResource"/>(<c>"Student"</c>) to the context.
    /// </summary>
    public static ConcreteResourceModel BuildPersonChainHittingPaCoveredColumn()
    {
        const string resourceName = "PaCollisionSubject";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName("StudentContactAssociation_DocumentId");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentContactAssociationReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "StudentContactAssociation"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile(
                        "$.studentContactAssociationReference.studentUniqueId"
                    ),
                    fkColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentContactAssociationReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Subject resource declaring two Student securable paths: one array-nested (skipped by the
    /// pass) and one resolvable to a direct reference. Drives the silent-skip branch: no throw,
    /// and exactly one auth index emitted (for the resolvable path's hop). Tests using this
    /// builder must also add <see cref="BuildPlainResource"/>(<c>"Student"</c>).
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithMixedArrayNestedAndResolvablePersonPath()
    {
        const string resourceName = "MixedPersonPathCarrier";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName("Student_DocumentId");

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    fkColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student:
                [
                    "$.studentReference.studentUniqueId",
                    "$.items[*].studentReference.studentUniqueId",
                ],
                Contact: [],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Subject resource declaring a resolvable Namespace securable element AND an array-nested
    /// Student securable path. Drives the cross-pass <c>anyResolved</c> carryover from
    /// <c>EmitSecurableElementIndexes</c> into <c>EmitPersonJoinIndexes</c> — the Namespace
    /// resolution must mark the resource so the array-nested Student path is silently skipped.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithResolvedNamespaceAndArrayNestedStudentPath() =>
        BuildResource(
            "NamespaceAndArrayNestedStudentCarrier",
            [
                BuildScalarColumn(new DbColumnName("DocumentId")),
                BuildScalarColumnWithJsonPath(new DbColumnName("Namespace"), "$.namespace"),
            ],
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: ["$.namespace"],
                Student: ["$.items[*].studentReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );

    /// <summary>
    /// Subject resource declaring a resolvable Student securable element AND an array-nested
    /// Contact securable path. Drives the cross-kind <c>anyResolved</c> carryover within
    /// <c>EmitPersonJoinIndexes</c> — the Student resolution must mark the resource so the
    /// array-nested Contact path is silently skipped.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithResolvedStudentAndArrayNestedContactPath()
    {
        const string resourceName = "StudentAndArrayNestedContactCarrier";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var studentFk = new DbColumnName("Student_DocumentId");

        var studentBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: studentFk,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    studentFk
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(studentFk)],
            [studentBinding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: ["$.items[*].contactReference.contactUniqueId"],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Subject resource whose only Student securable path is array-nested (contains <c>[*]</c>),
    /// and which has no other resolvable securable elements. Person-join BFS currently follows
    /// only root-table bindings, so array-nested paths are unsupported. Drives the throw branch
    /// in <c>EmitPersonJoinIndexes</c> mirroring the runtime resolver's
    /// "unsupported child-table traversal" message.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithArrayNestedPersonPathOnly() =>
        BuildResource(
            "ArrayNestedPersonCarrier",
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.items[*].studentReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );

    /// <summary>
    /// Subject resource carrying a Student securable element whose reference prefix doesn't
    /// match any <see cref="DocumentReferenceBinding"/> on the root table. Drives the
    /// unresolvable-person-path case: the pass throws
    /// <see cref="InvalidOperationException"/> naming both the resource and the offending
    /// JSON path.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithUnresolvablePersonPath() =>
        BuildResource(
            "PersonUnresolvableCarrier",
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.nonexistentReference.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );

    /// <summary>
    /// Subject resource declaring two distinct person securable elements (Student and Contact)
    /// with non-overlapping FK columns. Drives the multi-kind 1-hop case: one auth index emitted
    /// per kind on its own FK column, each INCLUDE <c>DocumentId</c>. Tests using this builder
    /// must also add <see cref="BuildPlainResource"/>(<c>"Student"</c>) and
    /// <see cref="BuildPlainResource"/>(<c>"Contact"</c>) to the context.
    /// </summary>
    public static ConcreteResourceModel BuildResourceWithMultiplePersonSecurables()
    {
        const string resourceName = "MultiPersonCarrier";
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var studentFk = new DbColumnName("Student_DocumentId");
        var contactFk = new DbColumnName("Contact_DocumentId");

        var studentBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
            Table: rootTable,
            FkColumn: studentFk,
            TargetResource: new QualifiedResourceName(EdFi, "Student"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.studentUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                    studentFk
                ),
            ]
        );

        var contactBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.contactReference"),
            Table: rootTable,
            FkColumn: contactFk,
            TargetResource: new QualifiedResourceName(EdFi, "Contact"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.contactUniqueId"),
                    JsonPathExpressionCompiler.Compile("$.contactReference.contactUniqueId"),
                    contactFk
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [
                BuildScalarColumn(new DbColumnName("DocumentId")),
                BuildScalarColumn(studentFk),
                BuildScalarColumn(contactFk),
            ],
            [studentBinding, contactBinding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: ["$.contactReference.contactUniqueId"],
                Staff: []
            )
        );
    }

    /// <summary>
    /// Builds <c>Ed-Fi.Student</c> with its own <c>$.studentUniqueId</c> as a Student securable
    /// element. The person resource is its own auth anchor — no person-join index is needed
    /// because the root table's <c>DocumentId</c> already identifies the Student.
    /// </summary>
    public static ConcreteResourceModel BuildPersonResourceWithSelfSecurable() =>
        BuildResource(
            "Student",
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );

    /// <summary>
    /// Builds the full set of resources for the three-hop BFS shortest-path scenario: a subject
    /// resource with two Student securable paths driving two independent BFS searches — one
    /// short (3-hop) chain to <c>Ed-Fi.Student</c> and one longer (4-hop) chain. The pass must
    /// pick the shortest. Emitted indexes are expected to cover the short chain only.
    /// </summary>
    public static IReadOnlyList<ConcreteResourceModel> BuildResourceChainOfThreeHops()
    {
        const string subjectName = "ThreeHopChainSubject";
        var subjectRoot = new DbTableName(_edfiSchema, subjectName);

        var shortHop1Fk = new DbColumnName("ShortHop1_DocumentId");
        var longHop1Fk = new DbColumnName("LongHop1_DocumentId");

        var shortBinding = BuildPersonChainBinding(subjectRoot, "$.shortChainRef", shortHop1Fk, "ShortHop1");
        var longBinding = BuildPersonChainBinding(subjectRoot, "$.longChainRef", longHop1Fk, "LongHop1");

        var subject = BuildResource(
            subjectName,
            [
                BuildScalarColumn(new DbColumnName("DocumentId")),
                BuildScalarColumn(shortHop1Fk),
                BuildScalarColumn(longHop1Fk),
            ],
            [shortBinding, longBinding],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.shortChainRef.studentUniqueId", "$.longChainRef.studentUniqueId"],
                Contact: [],
                Staff: []
            )
        );

        return
        [
            subject,
            BuildPersonChainHop("ShortHop1", "ShortHop2_DocumentId", "ShortHop2"),
            BuildPersonChainHop("ShortHop2", "Student_DocumentId", "Student"),
            BuildPersonChainHop("LongHop1", "LongHop2_DocumentId", "LongHop2"),
            BuildPersonChainHop("LongHop2", "LongHop3_DocumentId", "LongHop3"),
            BuildPersonChainHop("LongHop3", "Student_DocumentId", "Student"),
            BuildPlainResource("Student"),
        ];
    }

    /// <summary>
    /// Builds an intermediate resource with a single root-level
    /// <see cref="DocumentReferenceBinding"/> pointing to the named target resource. The
    /// <c>ReferenceObjectPath</c> doesn't matter for person-join BFS (which only inspects
    /// <see cref="DocumentReferenceBinding.Table"/> and
    /// <see cref="DocumentReferenceBinding.TargetResource"/>); a generic synthetic path is used.
    /// </summary>
    private static ConcreteResourceModel BuildPersonChainHop(
        string resourceName,
        string fkColumnName,
        string targetResourceName
    )
    {
        var rootTable = new DbTableName(_edfiSchema, resourceName);
        var fkColumn = new DbColumnName(fkColumnName);

        var binding = BuildPersonChainBinding(
            rootTable,
            $"$.next{targetResourceName}Reference",
            fkColumn,
            targetResourceName
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(new DbColumnName("DocumentId")), BuildScalarColumn(fkColumn)],
            [binding],
            ResourceSecurableElements.Empty
        );
    }

    private static DocumentReferenceBinding BuildPersonChainBinding(
        DbTableName sourceTable,
        string referenceObjectPath,
        DbColumnName fkColumn,
        string targetResourceName
    ) =>
        new(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile(referenceObjectPath),
            Table: sourceTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, targetResourceName),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.documentId"),
                    JsonPathExpressionCompiler.Compile($"{referenceObjectPath}.documentId"),
                    fkColumn
                ),
            ]
        );

    private static ConcreteResourceModel BuildPaResource(
        string resourceName,
        DbColumnName keyColumn,
        DbColumnName includeColumn
    )
    {
        var columns = new[] { BuildScalarColumn(keyColumn), BuildScalarColumn(includeColumn) };
        return BuildResourceFromColumns(resourceName, columns);
    }

    private static ConcreteResourceModel BuildResourceFromColumns(
        string resourceName,
        IReadOnlyList<DbColumnModel> columns
    ) => BuildResource(resourceName, columns, [], ResourceSecurableElements.Empty);

    private static ConcreteResourceModel BuildResource(
        string resourceName,
        IReadOnlyList<DbColumnModel> columns,
        IReadOnlyList<DocumentReferenceBinding> bindings,
        ResourceSecurableElements securableElements
    )
    {
        var qualifiedName = new QualifiedResourceName(EdFi, resourceName);
        var resourceKey = new ResourceKeyEntry(1, qualifiedName, "1.0.0", false);
        var rootTableName = new DbTableName(_edfiSchema, resourceName);

        var rootTable = new DbTableModel(
            rootTableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                $"PK_{resourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        );

        var relationalModel = new RelationalResourceModel(
            qualifiedName,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            bindings,
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        {
            SecurableElements = securableElements,
        };
    }

    private static DbColumnModel BuildScalarColumn(DbColumnName name) =>
        new(
            name,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel BuildScalarColumnWithJsonPath(DbColumnName name, string jsonPath) =>
        new(
            name,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String),
            IsNullable: true,
            SourceJsonPath: JsonPathExpressionCompiler.Compile(jsonPath),
            TargetResource: null
        );

    private static DbColumnModel BuildAliasColumn(DbColumnName aliasName, DbColumnName canonical) =>
        new(
            aliasName,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(canonical, PresenceColumn: null)
        );
}
