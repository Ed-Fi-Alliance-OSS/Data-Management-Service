// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class ReadChangesAuthorizationPlannerTests
{
    // Covers the unsupported-strategy AC: OwnershipBased, RelationshipsWithPeopleOnly,
    // RelationshipsWithEdOrgsAndPeopleInverted, and the live-only RelationshipsWithEdOrgsAndPeople — all
    // map to a 500 security-configuration outcome. Custom view-based names are recognized since DMS-1193
    // and covered by the custom-view section below.
    [TestCase("OwnershipBased")]
    [TestCase("RelationshipsWithPeopleOnly")]
    [TestCase("RelationshipsWithEdOrgsAndPeopleInverted")]
    [TestCase("RelationshipsWithEdOrgsAndPeople")]
    public void It_returns_security_configuration_for_unsupported_strategy(string strategyName)
    {
        var outcome = Plan(
            EdOrgResource(), // resource with one EdOrg securable (helper below)
            EdOrgTrackedTable(), // tracked table with the OldX EdOrg column (helper below)
            new RelationalAuthorizationContext([1L], []),
            strategyName
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Contain(strategyName);
    }

    [Test]
    public void It_returns_empty_plan_for_NoFurtherAuthorizationRequired()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.RelationshipChecks.Should().BeEmpty();
        plan.NamespaceCheck.Should().BeNull();
    }

    [Test]
    public void It_resolves_edorg_only_to_the_hierarchy_view_normal_direction()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        var subject = plan.RelationshipChecks.Single().Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("OldSchoolId_Unified");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToEducationOrganizationId");
        subject.AuthViewSubjectColumn.Value.Should().Be("TargetEducationOrganizationId"); // normal direction
        subject.AuthViewClaimColumn.Value.Should().Be("SourceEducationOrganizationId");
    }

    [Test]
    public void It_resolves_edorg_only_inverted_to_swapped_columns()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.AuthViewSubjectColumn.Value.Should().Be("SourceEducationOrganizationId"); // inverted
        subject.AuthViewClaimColumn.Value.Should().Be("TargetEducationOrganizationId");
    }

    [Test]
    public void It_returns_security_configuration_when_any_declared_edorg_securable_has_no_tracked_old_column()
    {
        var outcome = Plan(
            MultiEdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure
            .UnavailableStrategyNames.Should()
            .Contain(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
    }

    [Test]
    public void It_resolves_students_only_including_deletes_to_the_including_deletes_view()
    {
        // StudentTrackedTable() has a PersonDocumentId column (PersonKind=Student, OldStudent_DocumentId).
        var outcome = Plan(
            StudentResource(),
            StudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithStudentsOnlyIncludingDeletes"
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("OldStudent_DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
        subject.AuthViewSubjectColumn.Value.Should().Be("Student_DocumentId");
    }

    [Test]
    public void It_resolves_top_level_student_self_document_id_for_including_deletes_relationships()
    {
        // The person resource's own tombstone carries no self person value column; the self path
        // resolves to the DocumentId system column (DMS-1193).
        var outcome = Plan(
            TopLevelStudentResource(),
            TopLevelStudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
        subject.AuthViewSubjectColumn.Value.Should().Be("Student_DocumentId");
    }

    [Test]
    public void It_resolves_top_level_student_self_document_id_for_students_only_including_deletes()
    {
        var outcome = Plan(
            TopLevelStudentResource(),
            TopLevelStudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithStudentsOnlyIncludingDeletes"
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
    }

    [TestCase(
        "Contact",
        "$.contactUniqueId",
        "ContactUniqueId",
        "EducationOrganizationIdToContactDocumentIdIncludingDeletes",
        "Contact_DocumentId"
    )]
    [TestCase(
        "Staff",
        "$.staffUniqueId",
        "StaffUniqueId",
        "EducationOrganizationIdToStaffDocumentIdIncludingDeletes",
        "Staff_DocumentId"
    )]
    public void It_resolves_top_level_contact_and_staff_self_document_id_for_including_deletes_relationships(
        string personResourceName,
        string selfPath,
        string uniqueIdColumn,
        string expectedAuthView,
        string expectedSubjectColumn
    )
    {
        var outcome = Plan(
            TopLevelPersonResource(personResourceName, selfPath, uniqueIdColumn),
            TopLevelPersonTrackedTable(personResourceName, selfPath, uniqueIdColumn),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("DocumentId");
        subject.AuthView.Name.Should().Be(expectedAuthView);
        subject.AuthViewSubjectColumn.Value.Should().Be(expectedSubjectColumn);
    }

    [Test]
    public void It_returns_security_configuration_when_a_self_person_table_lacks_the_document_id_system_column()
    {
        var trackedTable = TopLevelStudentTrackedTable();
        var withoutDocumentId = trackedTable with
        {
            SystemColumns = trackedTable
                .SystemColumns.Where(column => column.Role != TrackedChangeSystemColumnRole.DocumentId)
                .ToList(),
        };

        var outcome = Plan(
            TopLevelStudentResource(),
            withoutDocumentId,
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject.UnavailableStrategyNames.Should()
            .Contain("RelationshipsWithEdOrgsAndPeopleIncludingDeletes");
    }

    [Test]
    public void It_resolves_ds52_top_level_student_self_document_id_for_including_deletes_relationships()
    {
        (DerivedRelationalModelSet modelSet, MappingSet mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        ConcreteResourceModel resource = modelSet.ConcreteResourcesInNameOrder.Single(r =>
            r.ResourceKey.Resource.ResourceName == "Student" && r.ResourceKey.Resource.ProjectName == "Ed-Fi"
        );
        TrackedChangeTableInfo trackedChangeTable = modelSet.TrackedChangeTablesInNameOrder.Single(t =>
            t.SourceTable == resource.RelationalModel.Root.Table
        );

        var outcome = ReadChangesAuthorizationPlanner.Plan(
            mappingSet,
            resource,
            trackedChangeTable,
            [new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsAndPeopleIncludingDeletes", 0)],
            new RelationalAuthorizationContext([1L], [])
        );

        var subject = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.RelationshipChecks.Single()
            .Subjects.Single();
        subject.TrackedOldColumn.Value.Should().Be("DocumentId");
        subject.AuthView.Name.Should().Be("EducationOrganizationIdToStudentDocumentIdIncludingDeletes");
        subject.AuthViewSubjectColumn.Value.Should().Be("Student_DocumentId");
    }

    [Test]
    public void It_returns_security_configuration_when_any_declared_person_securable_has_no_tracked_old_document_id()
    {
        var outcome = Plan(
            MultiStudentResource(),
            PartialMultiStudentTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithStudentsOnlyIncludingDeletes"
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Contain("RelationshipsWithStudentsOnlyIncludingDeletes");
    }

    [Test]
    public void It_returns_security_configuration_when_declared_person_securable_has_no_tracked_old_document_id()
    {
        var outcome = Plan(
            EdOrgAndStudentResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Contain("RelationshipsWithEdOrgsAndPeopleIncludingDeletes");
    }

    // DMS-1188: an authenticated client with ZERO EducationOrganization claim ids that hits a
    // relationship-based ReadChanges strategy must FAIL CLOSED (empty result), mirroring the live
    // path's NoClaims behavior — not throw (which previously surfaced as a generic 500). The planner
    // must still produce a non-null claim parameterization (so the emitter renders the relationship
    // predicate as match-nothing) rather than null (which would omit the predicate = fail-open).
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_returns_a_match_nothing_plan_for_a_relationship_strategy_with_no_claim_edorg_ids(
        SqlDialect dialect
    )
    {
        var outcome = Plan(
            CreateMappingSet(dialect),
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([], []),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;

        // The relationship subject is still planned (fail-closed: the predicate is emitted, not omitted).
        plan.RelationshipChecks.Should().ContainSingle();

        // The claim parameterization is present but represents zero claims in the match-nothing shape:
        //   PG  → PgsqlArray with the base parameter name and no claim ids,
        //   MSSQL → MssqlScalar with no scalar parameter names and no claim ids.
        plan.ClaimParameterization.Should().NotBeNull();
        plan.ClaimParameterization!.ClaimEducationOrganizationIds.Should().BeEmpty();

        AuthorizationClaimEducationOrganizationIdParameterizationKind expectedKind = dialect switch
        {
            SqlDialect.Pgsql => AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray,
            SqlDialect.Mssql => AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
        plan.ClaimParameterization.Kind.Should().Be(expectedKind);

        if (dialect is SqlDialect.Pgsql)
        {
            plan.ClaimParameterization.ParameterNamesInOrder.Should()
                .Equal(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds);
        }
        else
        {
            // MssqlScalar with zero ids → zero scalar parameter names (emitter renders 1 = 0).
            plan.ClaimParameterization.ParameterNamesInOrder.Should().BeEmpty();
        }
    }

    [Test]
    public void It_resolves_namespace_to_the_tracked_old_namespace_column()
    {
        var outcome = Plan(
            NamespaceResource(),
            NamespaceTrackedTable(),
            new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]),
            AuthorizationStrategyNameConstants.NamespaceBased
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.NamespaceCheck!.TrackedOldNamespaceColumn.Value.Should().Be("OldNamespace");
        plan.NamespaceParameterization.Should().NotBeNull();
    }

    [Test]
    public void It_returns_403_no_prefixes_when_namespace_configured_without_prefixes()
    {
        var outcome = Plan(
            NamespaceResource(),
            NamespaceTrackedTable(),
            new RelationalAuthorizationContext([], []),
            AuthorizationStrategyNameConstants.NamespaceBased
        );

        outcome.Should().BeOfType<ReadChangesAuthorizationPlanOutcome.NamespaceNoPrefixesConfigured>();
    }

    [Test]
    public void It_resolves_namespace_for_shared_descriptor_resources_via_descriptor_contract()
    {
        var outcome = Plan(
            DescriptorNamespaceResource(),
            DescriptorNamespaceTrackedTable(),
            new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]),
            AuthorizationStrategyNameConstants.NamespaceBased
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.NamespaceCheck!.TrackedOldNamespaceColumn.Value.Should().Be("OldNamespace");
        plan.NamespaceParameterization.Should().NotBeNull();
    }

    // ---- Custom view-based strategies (DMS-1193) ----------------------------------------------

    [Test]
    public void It_plans_a_self_basis_custom_view_against_the_document_id_system_column()
    {
        // The plan carries no endpoint: /schools/deletes and /schools/keyChanges share this shape.
        var outcome = PlanDs52("School", "SchoolWithAlternativeType");

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.RelationshipChecks.Should().BeEmpty();
        plan.NamespaceCheck.Should().BeNull();
        plan.ClaimParameterization.Should().BeNull();
        var check = plan.CustomViewChecks.Should().ContainSingle().Subject;
        check.ConfiguredStrategy.StrategyName.Should().Be("SchoolWithAlternativeType");
        check.AuthorizationLocalOrder.Should().Be(0);
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "School"));
        check.View.Should().Be(new DbTableName(new DbSchemaName("auth"), "SchoolWithAlternativeType"));
        check.ProbeBasisTombstones.Should().BeFalse();
        check
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.StoredDocumentId>()
            .Which.TrackedColumn.Value.Should()
            .Be("DocumentId");
    }

    [TestCase("StudentSchoolAssociation", "OldStudent_DocumentId")]
    [TestCase("Grade", "OldStudentSectionAssociation_Student_DocumentId")]
    [TestCase("Student", "DocumentId")]
    public void It_plans_a_person_basis_custom_view_against_the_stored_person_document_id(
        string resourceName,
        string expectedTrackedColumn
    )
    {
        var outcome = PlanDs52(resourceName, "StudentWithCTECourseEnrollments");

        var check = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.CustomViewChecks.Should()
            .ContainSingle()
            .Subject;
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "Student"));
        check.View.Should().Be(new DbTableName(new DbSchemaName("auth"), "StudentWithCTECourseEnrollments"));
        check.ProbeBasisTombstones.Should().BeFalse();
        check
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.StoredDocumentId>()
            .Which.TrackedColumn.Value.Should()
            .Be(expectedTrackedColumn);
    }

    [Test]
    public void It_carries_the_including_deletes_suffix_as_the_probe_flag_and_keeps_the_full_view_name()
    {
        var outcome = PlanDs52("StudentSchoolAssociation", "StudentWithCTECourseEnrollmentsIncludingDeletes");

        var check = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.CustomViewChecks.Should()
            .ContainSingle()
            .Subject;
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "Student"));
        check
            .View.Should()
            .Be(new DbTableName(new DbSchemaName("auth"), "StudentWithCTECourseEnrollmentsIncludingDeletes"));
        check.ProbeBasisTombstones.Should().BeTrue();
        check
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.StoredDocumentId>()
            .Which.TrackedColumn.Value.Should()
            .Be("OldStudent_DocumentId");
    }

    [Test]
    public void It_returns_custom_view_security_configuration_for_an_unknown_basis_resource()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "FooWithBar"
        );

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject;
        failure.PlannedChecks.Should().BeEmpty();
        var metadata = failure.Failures.Should().ContainSingle().Subject;
        metadata.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource);
        metadata.Resource.Should().Be(_schoolResource);
        metadata.ConfiguredStrategy.Should().Be(new ConfiguredAuthorizationStrategy("FooWithBar", 0));
        metadata.RelationshipLocalOrder.Should().Be(0);
        metadata
            .Location.Should()
            .BeEquivalentTo(new RelationshipAuthorizationFailureLocation(AuthorizationObjectName: "Foo"));
        metadata.Hint.Should().Contain("{BasisResource}With...");
    }

    [Test]
    public void It_keeps_the_unavailable_strategy_outcome_for_names_outside_the_custom_view_convention()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "Foo"
        );

        outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject.UnavailableStrategyNames.Should()
            .Equal("Foo");
    }

    [Test]
    public void It_returns_no_custom_view_join_path_when_the_basis_is_unreachable_from_the_subject_root()
    {
        // School's root table carries no reference that reaches Grade.
        var outcome = PlanDs52("School", "GradeWithSomething");

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject;
        failure.PlannedChecks.Should().BeEmpty();
        var metadata = failure.Failures.Should().ContainSingle().Subject;
        metadata.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoCustomViewJoinPath);
        metadata.ConfiguredStrategy!.StrategyName.Should().Be("GradeWithSomething");
        metadata.RelationshipLocalOrder.Should().Be(0);
        metadata
            .Location.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationFailureLocation(
                    AuthorizationObjectName: "auth.GradeWithSomething"
                )
            );
        metadata.Hint.Should().Contain("'Ed-Fi.School'").And.Contain("'Ed-Fi.Grade'");
    }

    [Test]
    public void It_carries_planned_checks_configured_ahead_of_a_later_unknown_basis()
    {
        var outcome = PlanDs52("School", "SchoolWithAlternativeType", "FooWithBar");

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject;
        var metadata = failure.Failures.Should().ContainSingle().Subject;
        metadata.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource);
        metadata.ConfiguredStrategy.Should().Be(new ConfiguredAuthorizationStrategy("FooWithBar", 1));
        metadata.RelationshipLocalOrder.Should().Be(1);
        var planned = failure.PlannedChecks.Should().ContainSingle().Subject;
        planned.ConfiguredStrategy.StrategyName.Should().Be("SchoolWithAlternativeType");
        planned.AuthorizationLocalOrder.Should().Be(0);
    }

    [Test]
    public void It_plans_custom_views_alongside_relationship_and_namespace_checks_in_configured_order()
    {
        var outcome = Plan(
            CreateMappingSetWithResources(_schoolResource),
            NamespaceSecuredEdOrgResource(),
            NamespaceSecuredEdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], ["uri://ed-fi.org/"]),
            "SchoolWithAlternativeType",
            AuthorizationStrategyNameConstants.NamespaceBased,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            "SchoolWithCharter"
        );

        var plan = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan;
        plan.RelationshipChecks.Should()
            .ContainSingle()
            .Which.ConfiguredStrategy.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        plan.ClaimParameterization.Should().NotBeNull();
        plan.NamespaceCheck!.TrackedOldNamespaceColumn.Value.Should().Be("OldNamespace");
        plan.NamespaceParameterization.Should().NotBeNull();
        plan.CustomViewChecks.Select(static check => check.ConfiguredStrategy.StrategyName)
            .Should()
            .Equal("SchoolWithAlternativeType", "SchoolWithCharter");
        plan.CustomViewChecks.Select(static check => check.AuthorizationLocalOrder).Should().Equal(0, 3);
        plan.CustomViewChecks.Should().BeInAscendingOrder(static check => check.AuthorizationLocalOrder);
        plan.CustomViewChecks.Should()
            .AllSatisfy(check =>
                check
                    .Basis.Should()
                    .BeOfType<ReadChangesCustomViewBasis.StoredDocumentId>()
                    .Which.TrackedColumn.Value.Should()
                    .Be("DocumentId")
            );
    }

    [Test]
    public void It_prefers_the_standard_edfi_basis_resource_through_the_shared_custom_view_resolution()
    {
        // Same arrangement as RelationshipAuthorizationStrategyClassifierTests
        // .It_prefers_the_standard_edfi_basis_resource_when_custom_view_homographs_exist: the extension
        // homograph is listed first, so a planner that reparsed the name itself would pick Sample.School
        // (not the subject) and fail; resolving through the classifier lands on the Ed-Fi self basis.
        var outcome = Plan(
            CreateMappingSetWithResources(new("Sample", "School"), new("Ed-Fi", "School")),
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext([1L], []),
            "SchoolWithSomething"
        );

        var check = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.CustomViewChecks.Should()
            .ContainSingle()
            .Subject;
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "School"));
        check
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.StoredDocumentId>()
            .Which.TrackedColumn.Value.Should()
            .Be("DocumentId");
    }

    // ---- Custom view-based strategies: live seek and tombstone probe (DMS-1193, Task 46) --------------

    [Test]
    public void It_plans_a_direct_basis_custom_view_as_a_live_seek_paired_on_the_tracked_old_key()
    {
        // StudentSchoolAssociation -> School: one identity reference, one key part, no probe without the suffix.
        var outcome = PlanDs52("StudentSchoolAssociation", "SchoolWithAlternativeType");

        var check = SingleCustomViewCheck(outcome);
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "School"));
        check.ProbeBasisTombstones.Should().BeFalse();
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfiSchema, "School"),
                    _documentId,
                    [KeyPair("SchoolId", "OldSchoolId_Unified")],
                    [],
                    []
                )
            );
    }

    [Test]
    public void It_pairs_a_transitive_basis_through_each_hops_identity_bindings_to_the_subjects_canonical_columns()
    {
        // Grade -> StudentSectionAssociation -> Section -> CourseOffering. CourseOffering's schoolId reaches the
        // subject twice (via Section's school and session references) and lands on the one unified column, so
        // the pair is recorded once.
        var outcome = PlanDs52("Grade", "CourseOfferingWithX");

        var check = SingleCustomViewCheck(outcome);
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "CourseOffering"));
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfiSchema, "CourseOffering"),
                    _documentId,
                    [
                        KeyPair("LocalCourseCode", "OldStudentSectionAssociation_LocalCourseCode"),
                        KeyPair("SchoolId_Unified", "OldSchoolId_Unified"),
                        KeyPair("Session_SchoolYear", "OldSchoolYear_Unified"),
                        KeyPair("Session_SessionName", "OldStudentSectionAssociation_SessionName"),
                    ],
                    [],
                    []
                ),
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_pairs_a_descriptor_identity_part_with_the_tombstones_old_namespace_and_code_value()
    {
        // GradingPeriod's identity is gradingPeriodDescriptor + gradingPeriodName + schoolId + schoolYear.
        var outcome = PlanDs52("Grade", "GradingPeriodWithX");

        var check = SingleCustomViewCheck(outcome);
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfiSchema, "GradingPeriod"),
                    _documentId,
                    [
                        KeyPair("GradingPeriodName", "OldGradingPeriodGradingPeriod_GradingPeriodName"),
                        KeyPair("School_SchoolId", "OldSchoolId_Unified"),
                        KeyPair("SchoolYear_SchoolYear", "OldSchoolYear_Unified"),
                    ],
                    [
                        new ReadChangesCustomViewDescriptorKeyPair(
                            new DbColumnName("GradingPeriodDescriptor_DescriptorId"),
                            new DbColumnName(
                                "OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"
                            ),
                            new DbColumnName(
                                "OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"
                            ),
                            new QualifiedResourceName("Ed-Fi", "GradingPeriodDescriptor")
                        ),
                    ],
                    []
                ),
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_probes_a_descriptor_identity_part_by_comparing_old_namespace_and_code_value_directly()
    {
        var outcome = PlanDs52("Grade", "GradingPeriodWithXIncludingDeletes");

        var check = SingleCustomViewCheck(outcome);
        check.ProbeBasisTombstones.Should().BeTrue();
        var seek = check.Basis.Should().BeOfType<ReadChangesCustomViewBasis.LiveSeek>().Subject;
        seek.ProbeArms.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewProbeArm(
                    new DbTableName(_trackedSchema, "GradingPeriod"),
                    _documentId,
                    [
                        KeyPair("OldGradingPeriodName", "OldGradingPeriodGradingPeriod_GradingPeriodName"),
                        KeyPair("OldSchool_SchoolId", "OldSchoolId_Unified"),
                        KeyPair("OldSchoolYear_SchoolYear", "OldSchoolYear_Unified"),
                    ],
                    [
                        new ReadChangesCustomViewProbeDescriptorKeyPair(
                            new DbColumnName("OldGradingPeriodDescriptor_Namespace"),
                            new DbColumnName("OldGradingPeriodDescriptor_CodeValue"),
                            new DbColumnName(
                                "OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"
                            ),
                            new DbColumnName(
                                "OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"
                            )
                        ),
                    ]
                ),
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_seeks_an_abstract_basis_through_the_union_view_identity_column()
    {
        // ReportCard references the abstract EducationOrganization directly by identity.
        var outcome = PlanDs52("ReportCard", "EducationOrganizationWithX");

        var check = SingleCustomViewCheck(outcome);
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "EducationOrganization"));
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfiSchema, "EducationOrganization_View"),
                    _documentId,
                    [KeyPair("EducationOrganizationId", "OldEducationOrganization_EducationOrganizationId")],
                    [],
                    []
                )
            );
    }

    [Test]
    public void It_probes_an_abstract_basis_with_one_arm_per_concrete_member_tracked_change_table()
    {
        var outcome = PlanDs52("ReportCard", "EducationOrganizationWithXIncludingDeletes");

        var seek = SingleCustomViewCheck(outcome)
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.LiveSeek>()
            .Subject;
        // DS 5.2 has nine concrete EducationOrganization members, in union-arm order.
        seek.ProbeArms.Should().HaveCount(9);
        seek.ProbeArms.Select(static arm => arm.BasisTrackedChangeTable.Name)
            .Should()
            .Equal(
                "CommunityOrganization",
                "CommunityProvider",
                "EducationOrganizationNetwork",
                "EducationServiceCenter",
                "LocalEducationAgency",
                "OrganizationDepartment",
                "PostSecondaryInstitution",
                "School",
                "StateEducationAgency"
            );
        seek.ProbeArms.Should()
            .AllSatisfy(arm =>
            {
                arm.BasisTrackedChangeTable.Schema.Should().Be(_trackedSchema);
                arm.BasisDocumentIdColumn.Should().Be(_documentId);
                arm.DescriptorKeyPairs.Should().BeEmpty();
            });
        seek.ProbeArms[7]
            .Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewProbeArm(
                    new DbTableName(_trackedSchema, "School"),
                    _documentId,
                    [KeyPair("OldSchoolId", "OldEducationOrganization_EducationOrganizationId")],
                    []
                )
            );
        seek.ProbeArms[4]
            .KeyPairs.Should()
            .Equal(KeyPair("OldLocalEducationAgencyId", "OldEducationOrganization_EducationOrganizationId"));
    }

    [Test]
    public void It_accepts_a_securable_non_identity_first_hop_whose_value_the_tombstone_stores()
    {
        // StudentAssessment's reportedSchoolReference is optional and not part of the identity, but it is an
        // EducationOrganization securable element, so the (nullable) old value is on the tombstone.
        var outcome = PlanDs52("StudentAssessment", "SchoolWithAlternativeType");

        var check = SingleCustomViewCheck(outcome);
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfiSchema, "School"),
                    _documentId,
                    [KeyPair("SchoolId", "OldReportedSchool_SchoolId")],
                    [],
                    []
                )
            );
    }

    [Test]
    public void It_adds_a_basis_tombstone_probe_arm_when_the_strategy_name_carries_the_suffix()
    {
        var outcome = PlanDs52("StudentSchoolAssociation", "SchoolWithAlternativeTypeIncludingDeletes");

        var check = SingleCustomViewCheck(outcome);
        check.ProbeBasisTombstones.Should().BeTrue();
        check
            .View.Should()
            .Be(new DbTableName(new DbSchemaName("auth"), "SchoolWithAlternativeTypeIncludingDeletes"));
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.LiveSeek(
                    new DbTableName(_edfiSchema, "School"),
                    _documentId,
                    [KeyPair("SchoolId", "OldSchoolId_Unified")],
                    [],
                    [
                        new ReadChangesCustomViewProbeArm(
                            new DbTableName(_trackedSchema, "School"),
                            _documentId,
                            [KeyPair("OldSchoolId", "OldSchoolId_Unified")],
                            []
                        ),
                    ]
                )
            );
    }

    [TestCase("Section", "LocationWithX", "locationReference", "$.locationReference", "Location")]
    [TestCase("CourseOffering", "CourseWithX", "courseReference", "$.courseReference", "Course")]
    [TestCase(
        "StudentSchoolAssociation",
        "CalendarWithX",
        "calendarReference",
        "$.calendarReference",
        "Calendar"
    )]
    [TestCase("SectionAttendanceTakenEvent", "StaffWithX", "staffReference", "$.staffReference", "Staff")]
    [TestCase(
        "CourseTranscript",
        "StaffWithX",
        "responsibleTeacherStaffReference",
        "$.responsibleTeacherStaffReference",
        "Staff"
    )]
    public void It_fails_when_the_first_hop_is_neither_identifying_nor_securable(
        string resourceName,
        string strategyName,
        string expectedReferenceName,
        string expectedReferenceJsonPath,
        string basisName
    )
    {
        // The reference resolves on the live paths, but a tombstone only stores identifying and securable
        // values, so nothing holds the basis key: the ODS "Non-identifying properties" rule, ReadChanges-only.
        var outcome = PlanDs52(resourceName, strategyName);

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject;
        failure.PlannedChecks.Should().BeEmpty();
        var metadata = failure.Failures.Should().ContainSingle().Subject;
        metadata
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable);
        metadata.Resource.Should().Be(new QualifiedResourceName("Ed-Fi", resourceName));
        metadata.ConfiguredStrategy.Should().Be(new ConfiguredAuthorizationStrategy(strategyName, 0));
        metadata.RelationshipLocalOrder.Should().Be(0);
        metadata
            .Location.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationFailureLocation(
                    JsonPath: expectedReferenceJsonPath,
                    ReadableName: expectedReferenceName,
                    AuthorizationObjectName: $"auth.{strategyName}"
                )
            );
        metadata
            .Hint.Should()
            .Be(
                $"The reference '{expectedReferenceName}' on 'Ed-Fi.{resourceName}' leads to custom view basis 'Ed-Fi.{basisName}' "
                    + "but is neither an identifying property nor a securable element of the subject. This is not supported by "
                    + "Change Queries, which only track deleted/changed values of identifying and securable properties. "
                    + "Should a different authorization strategy be used?"
            );
    }

    [TestCase("StudentSchoolAssociation", "schoolReference", "$.schoolReference")]
    [TestCase("AcademicWeek", "schoolReference", "$.schoolReference")]
    public void It_fails_when_a_descriptor_basis_is_not_identifying_on_the_intermediate_resource(
        string resourceName,
        string expectedReferenceName,
        string expectedReferenceJsonPath
    )
    {
        // The live paths reach SchoolTypeDescriptor through the FK on School, but schoolTypeDescriptor is not
        // part of School's identity, so the subject's tombstone never stores it. The planner must report the
        // ODS "Non-identifying properties" rule as a typed failure instead of throwing.
        var outcome = PlanDs52(resourceName, "SchoolTypeDescriptorWithX");

        var failure = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject;
        failure.PlannedChecks.Should().BeEmpty();
        var metadata = failure.Failures.Should().ContainSingle().Subject;
        metadata
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable);
        metadata.Resource.Should().Be(new QualifiedResourceName("Ed-Fi", resourceName));
        metadata
            .ConfiguredStrategy.Should()
            .Be(new ConfiguredAuthorizationStrategy("SchoolTypeDescriptorWithX", 0));
        metadata.RelationshipLocalOrder.Should().Be(0);
        metadata
            .Location.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationFailureLocation(
                    JsonPath: expectedReferenceJsonPath,
                    ReadableName: expectedReferenceName,
                    AuthorizationObjectName: "auth.SchoolTypeDescriptorWithX"
                )
            );
        metadata
            .Hint.Should()
            .Be(
                $"The descriptor property 'schoolTypeDescriptor' on 'Ed-Fi.School', reached from 'Ed-Fi.{resourceName}' "
                    + "through 'schoolReference', leads to custom view basis 'Ed-Fi.SchoolTypeDescriptor' but is not an "
                    + "identifying property of 'Ed-Fi.School', so the subject's tombstone does not store its value. This is "
                    + "not supported by Change Queries, which only track deleted/changed values of identifying and securable "
                    + "properties. Should a different authorization strategy be used?"
            );
    }

    [Test]
    public void It_applies_the_first_hop_rule_to_the_path_the_live_planner_prefers_for_an_abstract_basis()
    {
        // auth.md ranks StudentSchoolAssociation -> GraduationPlan -> EducationOrganization ahead of the
        // direct School union-arm route. ReadChanges follows the same preferred path, and its first hop
        // (graduationPlanReference) is neither identifying nor securable, so the view fails here even though
        // School's id is on the tombstone. Authorizing through a different path than the live reads would
        // make the same view admit different rows on the two surfaces.
        var outcome = PlanDs52("StudentSchoolAssociation", "EducationOrganizationWithX");

        var metadata = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject.Failures.Should()
            .ContainSingle()
            .Subject;
        metadata
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable);
        metadata.Location!.ReadableName.Should().Be("graduationPlanReference");
        metadata
            .Hint.Should()
            .Contain("'Ed-Fi.StudentSchoolAssociation'")
            .And.Contain("'Ed-Fi.EducationOrganization'");
    }

    [Test]
    public void It_plans_a_descriptor_basis_as_a_seek_of_the_shared_descriptor_table_on_old_namespace_and_code_value()
    {
        // The tombstone stores the descriptor's old Namespace/CodeValue, never its DocumentId, so the check
        // seeks dms.Descriptor (discriminated by the descriptor resource) rather than reading a stored id.
        var outcome = PlanDs52("Grade", "GradeTypeDescriptorWithX");

        var check = SingleCustomViewCheck(outcome);
        check.BasisResource.Should().Be(new QualifiedResourceName("Ed-Fi", "GradeTypeDescriptor"));
        check.ProbeBasisTombstones.Should().BeFalse();
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    new QualifiedResourceName("Ed-Fi", "GradeTypeDescriptor"),
                    new DbColumnName("OldGradeTypeDescriptor_Namespace"),
                    new DbColumnName("OldGradeTypeDescriptor_CodeValue"),
                    null
                )
            );
    }

    [Test]
    public void It_probes_the_shared_descriptor_tombstone_table_for_a_suffixed_descriptor_basis()
    {
        // Grade's gradingPeriodReference carries the GradingPeriodDescriptor identity part; the probe reads
        // the one shared descriptor tracked-change table, discriminated the same way as the live seek.
        var outcome = PlanDs52("Grade", "GradingPeriodDescriptorWithXIncludingDeletes");

        var check = SingleCustomViewCheck(outcome);
        check.ProbeBasisTombstones.Should().BeTrue();
        check
            .Basis.Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    new QualifiedResourceName("Ed-Fi", "GradingPeriodDescriptor"),
                    new DbColumnName("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_Namespace"),
                    new DbColumnName("OldGradingPeriodGradingPeriod_GradingPeriodDescriptor_CodeValue"),
                    new ReadChangesCustomViewDescriptorProbeArm(
                        new DbTableName(_trackedSchema, "Descriptor"),
                        _documentId,
                        new DbColumnName("Discriminator"),
                        new DbColumnName("OldNamespace"),
                        new DbColumnName("OldCodeValue")
                    )
                )
            );
    }

    [Test]
    public void It_fails_a_non_identifying_descriptor_basis_with_the_first_hop_rule()
    {
        // performanceBaseConversionDescriptor is optional on Grade and not part of its identity, so the
        // tombstone has no Namespace/CodeValue for it: ODS's "Non-identifying properties" outcome.
        var outcome = PlanDs52("Grade", "PerformanceBaseConversionDescriptorWithX");

        var metadata = outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration>()
            .Subject.Failures.Should()
            .ContainSingle()
            .Subject;
        metadata
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable);
        metadata
            .Location.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationFailureLocation(
                    JsonPath: "$.performanceBaseConversionDescriptor",
                    ReadableName: "performanceBaseConversionDescriptor",
                    AuthorizationObjectName: "auth.PerformanceBaseConversionDescriptorWithX"
                )
            );
        metadata
            .Hint.Should()
            .StartWith(
                "The reference 'performanceBaseConversionDescriptor' on 'Ed-Fi.Grade' leads to custom view basis 'Ed-Fi.PerformanceBaseConversionDescriptor'"
            );
    }

    private static ReadChangesCustomViewCheckSpec SingleCustomViewCheck(
        ReadChangesAuthorizationPlanOutcome outcome
    ) =>
        outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.CustomViewChecks.Should()
            .ContainSingle()
            .Subject;

    private static ReadChangesCustomViewKeyPair KeyPair(string basisColumn, string trackedOldColumn) =>
        new(new DbColumnName(basisColumn), new DbColumnName(trackedOldColumn));

    // ---- Test wrapper -------------------------------------------------------

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _trackedSchema = new("tracked_changes_edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    private static ReadChangesAuthorizationPlanOutcome Plan(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        RelationalAuthorizationContext context,
        params string[] strategyNames
    ) => Plan(CreateMappingSet(), resource, trackedChangeTable, context, strategyNames);

    private static ReadChangesAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        RelationalAuthorizationContext context,
        params string[] strategyNames
    )
    {
        ConfiguredAuthorizationStrategy[] configuredAuthorizationStrategies =
        [
            .. strategyNames.Select(
                static (strategyName, index) => new ConfiguredAuthorizationStrategy(strategyName, index)
            ),
        ];

        return ReadChangesAuthorizationPlanner.Plan(
            mappingSet,
            resource,
            trackedChangeTable,
            configuredAuthorizationStrategies,
            context
        );
    }

    // ---- Resource-model builder (adapted from RelationalAuthorizationPlannerTests) ------------------

    /// <summary>
    /// A concrete resource whose single EdOrg securable element resolves to a root-table
    /// <c>SchoolId_Unified</c> column at <c>$.schoolReference.schoolId</c>.
    /// </summary>
    private static ConcreteResourceModel EdOrgResource()
    {
        var root = RootTable(
            "School",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
            ]
        );
        var model = new RelationalResourceModel(
            _schoolResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, _schoolResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource that declares two EdOrg securable paths. The paired partial tracked table
    /// intentionally carries only the school path, proving that one successful subject cannot mask
    /// another missing tracked old column.
    /// </summary>
    private static ConcreteResourceModel MultiEdOrgResource()
    {
        var root = RootTable(
            "School",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("DistrictId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.districtReference.districtId"),
                    null
                ),
            ]
        );
        var model = new RelationalResourceModel(
            _schoolResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(6, _schoolResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.districtReference.districtId", "DistrictId"),
                ],
                [],
                [],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource that declares both EdOrg and Student securable elements. Used to prove a
    /// missing tracked-change Student DocumentId column cannot be masked by the resolvable EdOrg subject.
    /// </summary>
    private static ConcreteResourceModel EdOrgAndStudentResource()
    {
        var root = RootTable(
            "StudentSchoolAssociation",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("StudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.studentReference.studentUniqueId"),
                    null
                ),
            ]
        );
        var resource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var model = new RelationalResourceModel(
            resource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(5, resource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource with two Student person securable paths. The paired partial tracked table
    /// intentionally carries only the primary Student path.
    /// </summary>
    private static ConcreteResourceModel MultiStudentResource()
    {
        var studentResource = new QualifiedResourceName("Ed-Fi", "StudentPairAssociation");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "StudentPairAssociation"),
            Path("$"),
            new TableKey("PK_StudentPairAssociation", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("PrimaryStudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.primaryStudentReference.studentUniqueId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("SecondaryStudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.secondaryStudentReference.studentUniqueId"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            studentResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(7, studentResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.primaryStudentReference.studentUniqueId", "$.secondaryStudentReference.studentUniqueId"],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A concrete resource with a single Student person securable element at
    /// <c>$.studentReference.studentUniqueId</c>. Person resolution keys off the tracked table's
    /// <see cref="TrackedChangePersonJoinInfo"/>, so the resource model only needs to be coherent.
    /// </summary>
    private static ConcreteResourceModel StudentResource()
    {
        var studentResource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "StudentSchoolAssociation"),
            Path("$"),
            new TableKey("PK_StudentSchoolAssociation", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("StudentUniqueId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path("$.studentReference.studentUniqueId"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            studentResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(2, studentResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [],
                [],
                ["$.studentReference.studentUniqueId"],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// A top-level Student resource whose Student securable path is the resource's own identity path.
    /// </summary>
    private static ConcreteResourceModel TopLevelStudentResource() =>
        TopLevelPersonResource("Student", "$.studentUniqueId", "StudentUniqueId");

    /// <summary>
    /// A top-level person resource (Student, Contact, or Staff) whose person securable path is the
    /// resource's own unique-id identity path.
    /// </summary>
    private static ConcreteResourceModel TopLevelPersonResource(
        string personResourceName,
        string selfPath,
        string uniqueIdColumn
    )
    {
        var personResource = new QualifiedResourceName("Ed-Fi", personResourceName);
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, personResourceName),
            Path("$"),
            new TableKey($"PK_{personResourceName}", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName(uniqueIdColumn),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 32),
                    false,
                    Path(selfPath),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            personResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        var securableElements = personResourceName switch
        {
            "Student" => new ResourceSecurableElements([], [], [selfPath], [], []),
            "Contact" => new ResourceSecurableElements([], [], [], [selfPath], []),
            "Staff" => new ResourceSecurableElements([], [], [], [], [selfPath]),
            _ => throw new ArgumentOutOfRangeException(nameof(personResourceName), personResourceName, null),
        };
        return new ConcreteResourceModel(
            new ResourceKeyEntry(8, personResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = securableElements,
        };
    }

    /// <summary>
    /// A concrete resource whose single Namespace securable element resolves to a root-table
    /// <c>Namespace</c> column at <c>$.namespace</c>.
    /// </summary>
    private static ConcreteResourceModel NamespaceResource()
    {
        var namespaceResource = new QualifiedResourceName("Ed-Fi", "Survey");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "Survey"),
            Path("$"),
            new TableKey("PK_Survey", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 255),
                    false,
                    Path("$.namespace"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            namespaceResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(3, namespaceResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], ["$.namespace"], [], [], []),
        };
    }

    /// <summary>
    /// A shared-descriptor concrete resource (<see cref="ResourceStorageKind.SharedDescriptorTable"/>)
    /// whose <see cref="DescriptorMetadata"/> column contract maps Namespace to the shared
    /// <c>dms.Descriptor</c> <c>Namespace</c> column. Such resources carry no Namespace securable
    /// element path; resolution flows through the descriptor contract instead.
    /// </summary>
    private static ConcreteResourceModel DescriptorNamespaceResource()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var root = new DbTableModel(
            new DbTableName(_edfiSchema, "Descriptor"),
            Path("$"),
            new TableKey("PK_Descriptor", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 255),
                    false,
                    Path("$.namespace"),
                    null
                ),
            ],
            []
        );
        var model = new RelationalResourceModel(
            descriptorResource,
            _edfiSchema,
            ResourceStorageKind.SharedDescriptorTable,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(4, descriptorResource, "1.0", false),
            ResourceStorageKind.SharedDescriptorTable,
            model,
            new DescriptorMetadata(
                new DescriptorColumnContract(
                    new DbColumnName("Namespace"),
                    new DbColumnName("CodeValue"),
                    null,
                    null,
                    null,
                    null,
                    null
                ),
                DiscriminatorStrategy.ResourceKeyId
            )
        );
    }

    private static DbTableModel RootTable(string name, IReadOnlyList<DbColumnModel> columns) =>
        new(
            new DbTableName(_edfiSchema, name),
            Path("$"),
            new TableKey("PK_" + name, [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            columns,
            []
        );

    // ---- Tracked-change builder (adapted from TrackedChangeQueryPlannerTests) -----------------------

    /// <summary>
    /// A tracked-change table carrying the <c>OldSchoolId_Unified</c> identity/securable column that
    /// mirrors the EdOrg securable element on <see cref="EdOrgResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo EdOrgTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "School"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "School"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldSchoolId_Unified"),
                    NewColumnName: new DbColumnName("NewSchoolId_Unified"),
                    SourceJsonPath: "$.schoolReference.schoolId",
                    CanonicalStorageColumn: new DbColumnName("SchoolId_Unified"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns:
            [
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Id,
                    new DbColumnName("Id"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.ChangeVersion,
                    new DbColumnName("ChangeVersion"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.DocumentId,
                    new DbColumnName("DocumentId"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.CreatedAt,
                    new DbColumnName("CreatedAt"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
            ],
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    /// <summary>
    /// A tracked-change table carrying a denormalized <c>OldStudent_DocumentId</c> person column
    /// (<see cref="TrackedChangeColumnRole.PersonDocumentId"/>) reached through a
    /// <c>Student</c> person join, mirroring the Student securable element on <see cref="StudentResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo StudentTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "StudentSchoolAssociation"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "StudentSchoolAssociation"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldStudent_DocumentId"),
                    NewColumnName: new DbColumnName("NewStudent_DocumentId"),
                    SourceJsonPath: "$.studentReference.studentUniqueId",
                    CanonicalStorageColumn: null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.PersonDocumentId,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement,
                    PersonJoinName: "Student"
                ),
            ],
            SystemColumns:
            [
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Id,
                    new DbColumnName("Id"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.ChangeVersion,
                    new DbColumnName("ChangeVersion"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.DocumentId,
                    new DbColumnName("DocumentId"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.CreatedAt,
                    new DbColumnName("CreatedAt"),
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    IsPrimaryKey: false
                ),
            ],
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins:
            [
                new TrackedChangePersonJoinInfo(
                    "Student",
                    SecurableElementKind.Student,
                    [],
                    new DbTableName(_edfiSchema, "Student"),
                    new DbColumnName("StudentUniqueId"),
                    new DbColumnName("Student_StudentUniqueId")
                ),
            ]
        );

    /// <summary>
    /// A tracked-change table for a top-level Student resource: the identity scalar only, no person value
    /// column (the self person path is served by the <c>DocumentId</c> system column).
    /// </summary>
    private static TrackedChangeTableInfo TopLevelStudentTrackedTable() =>
        TopLevelPersonTrackedTable("Student", "$.studentUniqueId", "StudentUniqueId");

    /// <summary>
    /// A tracked-change table for a top-level person resource (Student, Contact, or Staff) in the
    /// DMS-1193 shape: identity scalar value column, no person joins, DocumentId system column.
    /// </summary>
    private static TrackedChangeTableInfo TopLevelPersonTrackedTable(
        string personResourceName,
        string selfPath,
        string uniqueIdColumn
    ) =>
        new(
            Table: new DbTableName(_trackedSchema, personResourceName),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, personResourceName),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName($"Old{uniqueIdColumn}"),
                    NewColumnName: new DbColumnName($"New{uniqueIdColumn}"),
                    SourceJsonPath: selfPath,
                    CanonicalStorageColumn: null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.String, 32),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    /// <summary>
    /// A tracked-change table carrying only one of <see cref="MultiStudentResource"/>'s declared
    /// Student person DocumentId columns.
    /// </summary>
    private static TrackedChangeTableInfo PartialMultiStudentTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "StudentPairAssociation"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "StudentPairAssociation"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldPrimaryStudent_DocumentId"),
                    NewColumnName: new DbColumnName("NewPrimaryStudent_DocumentId"),
                    SourceJsonPath: "$.primaryStudentReference.studentUniqueId",
                    CanonicalStorageColumn: null,
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    Role: TrackedChangeColumnRole.PersonDocumentId,
                    Origin: TrackedChangeColumnOrigin.SecurableElement,
                    PersonJoinName: "PrimaryStudent"
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins:
            [
                new TrackedChangePersonJoinInfo(
                    "PrimaryStudent",
                    SecurableElementKind.Student,
                    [],
                    new DbTableName(_edfiSchema, "Student"),
                    new DbColumnName("StudentUniqueId"),
                    new DbColumnName("PrimaryStudentUniqueId")
                ),
            ]
        );

    /// <summary>
    /// A tracked-change table carrying the <c>OldNamespace</c> securable column that mirrors the
    /// Namespace securable element on <see cref="NamespaceResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo NamespaceTrackedTable() =>
        new(
            Table: new DbTableName(_trackedSchema, "Survey"),
            Kind: TrackedChangeTableKind.Resource,
            SourceTable: new DbTableName(_edfiSchema, "Survey"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldNamespace"),
                    NewColumnName: new DbColumnName("NewNamespace"),
                    SourceJsonPath: "$.namespace",
                    CanonicalStorageColumn: new DbColumnName("Namespace"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.String, 255),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    /// <summary>
    /// A shared-descriptor tracked-change table (<see cref="TrackedChangeTableKind.SharedDescriptor"/>)
    /// carrying the <c>OldNamespace</c> column for the shared <c>dms.Descriptor</c> table, mirroring
    /// the descriptor column contract on <see cref="DescriptorNamespaceResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo DescriptorNamespaceTrackedTable() =>
        new(
            Table: new DbTableName(new DbSchemaName("tracked_changes_dms"), "Descriptor"),
            Kind: TrackedChangeTableKind.SharedDescriptor,
            SourceTable: new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            ValueColumnsInTableOrder:
            [
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldNamespace"),
                    NewColumnName: new DbColumnName("NewNamespace"),
                    SourceJsonPath: "$.namespace",
                    CanonicalStorageColumn: new DbColumnName("Namespace"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.String, 255),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
            SystemColumns: NamespaceSystemColumns(),
            PrimaryKeyColumns: [],
            DescriptorJoins: [],
            PersonJoins: []
        );

    private static IReadOnlyList<TrackedChangeSystemColumnInfo> NamespaceSystemColumns() =>
        [
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.Id,
                new DbColumnName("Id"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.ChangeVersion,
                new DbColumnName("ChangeVersion"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.DocumentId,
                new DbColumnName("DocumentId"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.CreatedAt,
                new DbColumnName("CreatedAt"),
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: false
            ),
        ];

    // ---- MappingSet builder (adapted from RelationalAuthorizationPlannerTests.EmptyMappingSet) ------

    private static MappingSet CreateMappingSet(SqlDialect dialect = SqlDialect.Pgsql) =>
        new(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 0,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder: []
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    // ---- Custom-view fixtures (DMS-1193) -----------------------------------------------------------

    private static readonly Lazy<(DerivedRelationalModelSet ModelSet, MappingSet MappingSet)> _ds52 = new(
        static () => Ds52FixtureHelper.BuildAndCompile(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    /// <summary>
    /// Plans the named Ed-Fi resource of the authoritative DS 5.2 fixture with its derived tracked-change
    /// table and one claim EdOrg id.
    /// </summary>
    private static ReadChangesAuthorizationPlanOutcome PlanDs52(
        string resourceName,
        params string[] strategyNames
    )
    {
        (DerivedRelationalModelSet modelSet, MappingSet mappingSet) = _ds52.Value;
        var resourceKey = new QualifiedResourceName("Ed-Fi", resourceName);
        ConcreteResourceModel resource = modelSet.ConcreteResourcesInNameOrder.Single(r =>
            r.ResourceKey.Resource == resourceKey
        );
        TrackedChangeTableInfo trackedChangeTable = modelSet.TrackedChangeTablesInNameOrder.Single(t =>
            t.SourceTable == resource.RelationalModel.Root.Table
        );

        return Plan(
            mappingSet,
            resource,
            trackedChangeTable,
            new RelationalAuthorizationContext([1L], []),
            strategyNames
        );
    }

    /// <summary>
    /// A mapping set whose effective schema lists the given resources (in that order) so custom-view
    /// basis names resolve; mirrors <c>RelationshipAuthorizationStrategyClassifierTests.CreateMappingSet</c>,
    /// including one project schema per distinct project in endpoint order.
    /// </summary>
    private static MappingSet CreateMappingSetWithResources(params QualifiedResourceName[] resources)
    {
        List<ProjectSchemaInfo> projectSchemasInEndpointOrder = [];
        List<SchemaComponentInfo> schemaComponentsInEndpointOrder = [];
        HashSet<string> seenProjectNames = [];

        foreach (var resource in resources)
        {
            if (!seenProjectNames.Add(resource.ProjectName))
            {
                continue;
            }

            var isExtensionProject = !string.Equals(resource.ProjectName, "Ed-Fi", StringComparison.Ordinal);
            var endpointName = isExtensionProject ? resource.ProjectName.ToLowerInvariant() : "ed-fi";

            projectSchemasInEndpointOrder.Add(
                new ProjectSchemaInfo(
                    endpointName,
                    resource.ProjectName,
                    "1.0.0",
                    isExtensionProject,
                    new DbSchemaName(endpointName.Replace('-', '_'))
                )
            );
            schemaComponentsInEndpointOrder.Add(
                new SchemaComponentInfo(
                    endpointName,
                    resource.ProjectName,
                    "1.0.0",
                    isExtensionProject,
                    $"{endpointName}-hash"
                )
            );
        }

        IReadOnlyList<ResourceKeyEntry> resourceKeysInIdOrder =
        [
            .. resources.Select(
                static (resource, index) =>
                    new ResourceKeyEntry(
                        ResourceKeyId: (short)(index + 1),
                        Resource: resource,
                        ResourceVersion: "1.0.0",
                        IsAbstractResource: false
                    )
            ),
        ];

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: (short)resourceKeysInIdOrder.Count,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder: schemaComponentsInEndpointOrder,
                    ResourceKeysInIdOrder: resourceKeysInIdOrder
                ),
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: projectSchemasInEndpointOrder,
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    /// <summary>
    /// <see cref="EdOrgResource"/> with an additional root <c>Namespace</c> column declared as a Namespace
    /// securable, so one resource can carry a relationship, a namespace, and a custom-view check at once.
    /// </summary>
    private static ConcreteResourceModel NamespaceSecuredEdOrgResource()
    {
        var root = RootTable(
            "School",
            [
                new DbColumnModel(
                    new DbColumnName("SchoolId_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 255),
                    false,
                    Path("$.namespace"),
                    null
                ),
            ]
        );
        var model = new RelationalResourceModel(
            _schoolResource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, _schoolResource, "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                ["$.namespace"],
                [],
                [],
                []
            ),
        };
    }

    /// <summary>
    /// <see cref="EdOrgTrackedTable"/> plus the <c>OldNamespace</c> column mirroring the Namespace
    /// securable on <see cref="NamespaceSecuredEdOrgResource"/>.
    /// </summary>
    private static TrackedChangeTableInfo NamespaceSecuredEdOrgTrackedTable()
    {
        var edOrgTable = EdOrgTrackedTable();
        return edOrgTable with
        {
            ValueColumnsInTableOrder =
            [
                .. edOrgTable.ValueColumnsInTableOrder,
                new TrackedChangeColumnInfo(
                    OldColumnName: new DbColumnName("OldNamespace"),
                    NewColumnName: new DbColumnName("NewNamespace"),
                    SourceJsonPath: "$.namespace",
                    CanonicalStorageColumn: new DbColumnName("Namespace"),
                    IsOldColumnNullable: false,
                    IsNewColumnNullable: true,
                    ScalarType: new RelationalScalarType(ScalarKind.String, 255),
                    Role: TrackedChangeColumnRole.Scalar,
                    Origin: TrackedChangeColumnOrigin.SecurableElement
                ),
            ],
        };
    }

    /// <summary>
    /// Change queries are out of scope for DMS-1060, and their planner keeps its own allow-list rather than
    /// consulting the relational one. An ownership-token list at or over the defensive cap must therefore
    /// change nothing here: OwnershipBased stays a security-configuration outcome, never the relational
    /// planner's token-cap terminal.
    /// </summary>
    [Test]
    public void It_keeps_ownership_a_security_configuration_outcome_even_over_the_token_cap()
    {
        var outcome = Plan(
            EdOrgResource(),
            EdOrgTrackedTable(),
            new RelationalAuthorizationContext(
                [1L],
                [],
                creatorOwnershipTokenId: null,
                ownershipTokenIds:
                [
                    .. Enumerable
                        .Range(1, OwnershipTokenLimitExceededException.OwnershipTokenLimit)
                        .Select(static value => (short)value),
                ]
            ),
            AuthorizationStrategyNameConstants.OwnershipBased
        );

        outcome
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.SecurityConfiguration>()
            .Which.UnavailableStrategyNames.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
    }
}
