// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Ds52_People_Crud_Authorization_Metadata
{
    private MappingSet _mappingSet = null!;
    private RelationshipAuthorizationPlanner _planner = null!;
    private RelationalAuthorizationContext _authorizationContext = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        (_, _mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        _planner = new RelationshipAuthorizationPlanner(
            new RelationalEdOrgAuthorizationSubjectSelector(
                new RelationalEdOrgAuthorizationElementResolutionCache()
            )
        );
        _authorizationContext = new RelationalAuthorizationContext([1255901001L], []);
    }

    [Test]
    public void It_should_leave_person_create_metadata_as_no_further_authorization_required()
    {
        var result = _planner.PlanStoredValues(
            _mappingSet,
            Resource("Student"),
            Strategies(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired),
            _authorizationContext
        );

        var noFurtherAuthorizationRequired = result
            .Should()
            .BeOfType<RelationshipAuthorizationResult.NoFurtherAuthorizationRequired>()
            .Subject;

        noFurtherAuthorizationRequired
            .ConfiguredStrategies.Select(static strategy => strategy.StrategyName)
            .Should()
            .Equal(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired);
    }

    [Test]
    public void It_should_plan_primary_relationship_create_metadata_as_edorg_only()
    {
        var resource = Resource("StudentSchoolAssociation");
        var result = _planner.PlanProposedValues(
            _mappingSet,
            resource,
            Strategies(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly),
            _authorizationContext,
            _mappingSet.GetWritePlanOrThrow(resource)
        );

        var checkSpec = AssertAuthorized(result).CheckSpecs.Should().ContainSingle().Which;
        var subject = checkSpec.Subjects.Should().ContainSingle().Which;

        checkSpec.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        subject.IsPersonSubject.Should().BeFalse();
        subject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
        subject.Contributors.Should().ContainSingle().Which.ReadableName.Should().Be("SchoolId");
    }

    [Test]
    public void It_should_plan_student_contact_association_metadata_as_students_only_for_stored_and_proposed_values()
    {
        var updatePlan = PlanUpdate(
            "StudentContactAssociation",
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );

        AssertSingleStudentSubject(
            AssertAuthorized(updatePlan.StoredValues),
            RelationshipAuthorizationValueSource.Stored
        );
        AssertSingleStudentSubject(
            AssertAuthorized(updatePlan.ProposedValues),
            RelationshipAuthorizationValueSource.Proposed
        );
    }

    [Test]
    public void It_should_plan_through_responsibility_metadata_with_the_responsibility_student_auth_view()
    {
        var updatePlan = PlanUpdate(
            "StudentSpecialEducationProgramEligibilityAssociation",
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
        );

        AssertSingleStudentThroughResponsibilitySubject(
            AssertAuthorized(updatePlan.StoredValues),
            RelationshipAuthorizationValueSource.Stored
        );
        AssertSingleStudentThroughResponsibilitySubject(
            AssertAuthorized(updatePlan.ProposedValues),
            RelationshipAuthorizationValueSource.Proposed
        );
    }

    [Test]
    public void It_should_plan_relationship_based_data_metadata_as_mixed_edorg_and_people_subjects()
    {
        var updatePlan = PlanUpdate(
            "StudentAcademicRecord",
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
        );

        AssertMixedEdOrgAndStudentSubjects(
            AssertAuthorized(updatePlan.StoredValues),
            RelationshipAuthorizationValueSource.Stored
        );
        AssertMixedEdOrgAndStudentSubjects(
            AssertAuthorized(updatePlan.ProposedValues),
            RelationshipAuthorizationValueSource.Proposed
        );
    }

    private RelationshipAuthorizationUpdatePlan PlanUpdate(string resourceName, string strategyName)
    {
        var resource = Resource(resourceName);

        return _planner.PlanUpdateValues(
            _mappingSet,
            resource,
            Strategies(strategyName),
            _authorizationContext,
            _mappingSet.GetWritePlanOrThrow(resource)
        );
    }

    private static RelationshipAuthorizationResult.Authorized AssertAuthorized(
        RelationshipAuthorizationResult result
    ) => result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>().Subject;

    private static void AssertSingleStudentSubject(
        RelationshipAuthorizationResult.Authorized authorized,
        RelationshipAuthorizationValueSource valueSource
    )
    {
        var checkSpec = authorized.CheckSpecs.Should().ContainSingle().Which;
        var subject = checkSpec.Subjects.Should().ContainSingle().Which;

        checkSpec.ValueSource.Should().Be(valueSource);
        subject.AuthObject.Should().Be(PersonAuthObject(RelationshipAuthorizationPersonAuthViewKind.Student));
        subject.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        subject
            .PersonMetadata.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn);
        subject
            .Contributors.Select(static contributor => contributor.Kind)
            .Should()
            .Equal(SecurableElementKind.Student);
        subject
            .Contributors.Select(static contributor => contributor.ReadableName)
            .Should()
            .Equal("StudentUniqueId");
    }

    private static void AssertSingleStudentThroughResponsibilitySubject(
        RelationshipAuthorizationResult.Authorized authorized,
        RelationshipAuthorizationValueSource valueSource
    )
    {
        var checkSpec = authorized.CheckSpecs.Should().ContainSingle().Which;
        var subject = checkSpec.Subjects.Should().ContainSingle().Which;

        checkSpec.ValueSource.Should().Be(valueSource);
        subject
            .AuthObject.Should()
            .Be(PersonAuthObject(RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility));
        subject
            .AuthObject.FailureHint.Should()
            .Contain("StudentEducationOrganizationResponsibilityAssociation");
        subject.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        subject
            .Contributors.Select(static contributor => contributor.ReadableName)
            .Should()
            .Equal("StudentUniqueId");
    }

    private static void AssertMixedEdOrgAndStudentSubjects(
        RelationshipAuthorizationResult.Authorized authorized,
        RelationshipAuthorizationValueSource valueSource
    )
    {
        var checkSpec = authorized.CheckSpecs.Should().ContainSingle().Which;

        checkSpec.ValueSource.Should().Be(valueSource);
        checkSpec.Subjects.Should().HaveCount(2);
        checkSpec
            .Subjects.Should()
            .ContainSingle(subject =>
                subject.AuthObject
                == RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            )
            .Which.Contributors.Select(static contributor => contributor.ReadableName)
            .Should()
            .Equal("EducationOrganizationId");
        checkSpec
            .Subjects.Should()
            .ContainSingle(subject =>
                subject.AuthObject == PersonAuthObject(RelationshipAuthorizationPersonAuthViewKind.Student)
            )
            .Which.Contributors.Select(static contributor => contributor.ReadableName)
            .Should()
            .Equal("StudentUniqueId");
    }

    private static RelationshipAuthorizationAuthObject PersonAuthObject(
        RelationshipAuthorizationPersonAuthViewKind kind
    ) => RelationshipAuthorizationAuthObject.CreatePerson(kind);

    private static QualifiedResourceName Resource(string resourceName) => new("Ed-Fi", resourceName);

    private static ConfiguredAuthorizationStrategy[] Strategies(params string[] strategyNames) =>
        [
            .. strategyNames.Select(
                static (strategyName, index) => new ConfiguredAuthorizationStrategy(strategyName, index)
            ),
        ];
}
