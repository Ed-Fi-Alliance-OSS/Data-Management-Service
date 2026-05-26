// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationPlannerTests
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    [Test]
    public void It_should_normalize_direct_constructor_inputs_the_same_as_client_authorizations_creation()
    {
        var directlyConstructedContext = new RelationalAuthorizationContext(
            [300L, 100L, 300L],
            ["uri://sample-b.org", "uri://sample-a.org", "uri://sample-b.org"]
        );
        var createdContext = RelationalAuthorizationContext.Create(
            new ClientAuthorizations(
                TokenId: "token-id",
                ClientId: "client-id",
                ClaimSetName: "claim-set",
                EducationOrganizationIds:
                [
                    new EducationOrganizationId(300),
                    new EducationOrganizationId(100),
                    new EducationOrganizationId(300),
                ],
                NamespacePrefixes:
                [
                    new NamespacePrefix("uri://sample-b.org"),
                    new NamespacePrefix("uri://sample-a.org"),
                    new NamespacePrefix("uri://sample-b.org"),
                ],
                DmsInstanceIds: []
            )
        );

        directlyConstructedContext.ClaimEducationOrganizationIds.Should().Equal(100L, 300L);
        directlyConstructedContext
            .NamespacePrefixes.Should()
            .Equal("uri://sample-a.org", "uri://sample-b.org");
        createdContext
            .ClaimEducationOrganizationIds.Should()
            .Equal(directlyConstructedContext.ClaimEducationOrganizationIds);
        createdContext.NamespacePrefixes.Should().Equal(directlyConstructedContext.NamespacePrefixes);
    }

    [Test]
    public void It_should_plan_stored_specs_with_strategy_or_order_and_subject_and_semantics()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "TestResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMultipleRootSubjectMappingSet(),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            ),
            new RelationalAuthorizationContext([300L, 100L, 300L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorizedResult = (RelationshipAuthorizationResult.Authorized)result;

        authorizedResult.CheckSpecs.Should().HaveCount(2);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(0, 1);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.Direction)
            .Should()
            .Equal(
                RelationshipAuthorizationHierarchyDirection.Normal,
                RelationshipAuthorizationHierarchyDirection.Inverted
            );
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ValueSource)
            .Should()
            .OnlyContain(static valueSource => valueSource == RelationshipAuthorizationValueSource.Stored);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.Subjects[0].AuthObject)
            .Should()
            .Equal(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Inverted
                )
            );
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.Subjects[0].AuthObject.AllowsDirectClaimMatch)
            .Should()
            .OnlyContain(static allowsDirectClaimMatch => allowsDirectClaimMatch);
        authorizedResult.CheckSpecs.Select(static checkSpec => checkSpec.Subjects.Count).Should().Equal(2, 2);
        authorizedResult
            .CheckSpecs[0]
            .Subjects.Select(static subject => subject.Contributors[0].ReadableName)
            .Should()
            .Equal("SchoolId", "LocalEducationAgencyId");
        authorizedResult.ClaimEducationOrganizationIdParameterization.Should().NotBeNull();
        authorizedResult
            .ClaimEducationOrganizationIdParameterization!.Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray);
        authorizedResult
            .ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds.Should()
            .Equal(100L, 300L);

        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .Should()
            .AllSatisfy(checkTarget =>
                checkTarget.Should().BeOfType<RelationshipAuthorizationCheckTarget.Stored>()
            );
    }

    [Test]
    public void It_should_plan_proposed_specs_with_root_binding_locators_and_parameter_seeds()
    {
        (_, var mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        var resource = new QualifiedResourceName("Ed-Fi", "CourseOffering");
        var writePlan = mappingSet.GetWritePlanOrThrow(resource);
        var rootTablePlan = GetRootTableWritePlan(writePlan);
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorizedResult = (RelationshipAuthorizationResult.Authorized)result;

        authorizedResult.CheckSpecs.Should().HaveCount(2);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(0, 1);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ValueSource)
            .Should()
            .OnlyContain(static valueSource => valueSource == RelationshipAuthorizationValueSource.Proposed);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.Subjects[0].AuthObject)
            .Should()
            .OnlyContain(static authObject =>
                authObject.Equals(
                    RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                        RelationshipAuthorizationHierarchyDirection.Normal
                    )
                )
            );
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.Subjects[0].AuthObject.AllowsDirectClaimMatch)
            .Should()
            .OnlyContain(static allowsDirectClaimMatch => allowsDirectClaimMatch);
        authorizedResult.CheckSpecs.Select(static checkSpec => checkSpec.Subjects.Count).Should().Equal(1, 1);
        authorizedResult.CheckSpecs[0].Subjects[0].Contributors.Should().HaveCount(2);

        foreach (var checkSpec in authorizedResult.CheckSpecs)
        {
            checkSpec.CheckTarget.Should().BeOfType<RelationshipAuthorizationCheckTarget.Proposed>();

            var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)checkSpec.CheckTarget;
            var subject = checkSpec.Subjects[0];
            var expectedBinding = rootTablePlan
                .ColumnBindings.Select(static (binding, index) => (binding, index))
                .Single(entry => entry.binding.Column.ColumnName.Equals(subject.Column));

            proposedTarget.RootTable.Should().Be(rootTablePlan.TableModel.Table);
            proposedTarget.SubjectBindingsInOrder.Should().ContainSingle();
            proposedTarget
                .SubjectBindingsInOrder[0]
                .Should()
                .BeEquivalentTo(
                    new RelationshipAuthorizationProposedValueBinding(
                        subject.Table,
                        subject.Column,
                        expectedBinding.index,
                        subject.Column.Value,
                        expectedBinding.binding.ParameterName
                    )
                );
        }
    }

    [Test]
    public void It_should_plan_update_specs_with_distinct_stored_and_proposed_value_sources()
    {
        (_, var mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        var resource = new QualifiedResourceName("Ed-Fi", "CourseOffering");
        var writePlan = mappingSet.GetWritePlanOrThrow(resource);
        var planner = CreatePlanner();

        var result = planner.PlanUpdateValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.StoredValues.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();
        result.ProposedValues.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();
        result.SecurityConfigurationFailures.Should().BeEmpty();
        result.KnownButNotEnabledFailures.Should().BeEmpty();

        var storedValues = (RelationshipAuthorizationResult.Authorized)result.StoredValues;
        var proposedValues = (RelationshipAuthorizationResult.Authorized)result.ProposedValues;

        storedValues.CheckSpecs.Should().HaveCount(2);
        proposedValues.CheckSpecs.Should().HaveCount(2);
        storedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(0, 1);
        proposedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(
                storedValues.CheckSpecs.Select(static checkSpec =>
                    checkSpec.ConfiguredStrategy.RawConfiguredIndex
                )
            );
        storedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        proposedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(storedValues.CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder));
        storedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.ValueSource)
            .Should()
            .OnlyContain(static valueSource => valueSource == RelationshipAuthorizationValueSource.Stored);
        proposedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.ValueSource)
            .Should()
            .OnlyContain(static valueSource => valueSource == RelationshipAuthorizationValueSource.Proposed);
        storedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .Should()
            .AllSatisfy(checkTarget =>
                checkTarget.Should().BeOfType<RelationshipAuthorizationCheckTarget.Stored>()
            );
        proposedValues
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .Should()
            .AllSatisfy(checkTarget =>
                checkTarget.Should().BeOfType<RelationshipAuthorizationCheckTarget.Proposed>()
            );
    }

    [Test]
    public void It_should_treat_no_further_authorization_required_as_a_noop_in_proposed_value_planning()
    {
        (_, var mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        var resource = new QualifiedResourceName("Ed-Fi", "CourseOffering");
        var writePlan = mappingSet.GetWritePlanOrThrow(resource);
        var rootTablePlan = GetRootTableWritePlan(writePlan);
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorizedResult = (RelationshipAuthorizationResult.Authorized)result;

        authorizedResult.CheckSpecs.Should().HaveCount(2);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.StrategyName)
            .Should()
            .Equal(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            );
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(1, 2);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.Direction)
            .Should()
            .Equal(
                RelationshipAuthorizationHierarchyDirection.Normal,
                RelationshipAuthorizationHierarchyDirection.Inverted
            );
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ValueSource)
            .Should()
            .OnlyContain(static valueSource => valueSource == RelationshipAuthorizationValueSource.Proposed);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .Should()
            .AllSatisfy(checkTarget =>
                checkTarget.Should().BeOfType<RelationshipAuthorizationCheckTarget.Proposed>()
            );

        foreach (var checkSpec in authorizedResult.CheckSpecs)
        {
            var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)checkSpec.CheckTarget;
            var subject = checkSpec.Subjects.Single();
            var expectedBinding = rootTablePlan
                .ColumnBindings.Select(static (binding, index) => (binding, index))
                .Single(entry => entry.binding.Column.ColumnName.Equals(subject.Column));

            proposedTarget.RootTable.Should().Be(rootTablePlan.TableModel.Table);
            proposedTarget
                .SubjectBindingsInOrder.Should()
                .ContainSingle()
                .Which.Should()
                .BeEquivalentTo(
                    new RelationshipAuthorizationProposedValueBinding(
                        subject.Table,
                        subject.Column,
                        expectedBinding.index,
                        subject.Column.Value,
                        expectedBinding.binding.ParameterName
                    )
                );
        }
    }

    [Test]
    public void It_should_report_missing_proposed_root_bindings_as_security_configuration_errors()
    {
        (_, var mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        var resource = new QualifiedResourceName("Ed-Fi", "CourseTranscript");
        var configuredAuthorizationStrategies = CreateConfiguredAuthorizationStrategies(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );
        var selectedSubject = CreateSelector()
            .Select(mappingSet, resource, CreateSupportedStrategies(configuredAuthorizationStrategies))
            .Subjects.Single();
        var writePlan = mappingSet.GetWritePlanOrThrow(resource);
        var rootTablePlan = GetRootTableWritePlan(writePlan);
        var brokenRootTablePlan = rootTablePlan with
        {
            ColumnBindings =
            [
                .. rootTablePlan.ColumnBindings.Where(binding =>
                    !binding.Column.ColumnName.Equals(selectedSubject.Column)
                ),
            ],
        };
        var brokenWritePlan = new ResourceWritePlan(
            writePlan.Model,
            [
                .. writePlan.TablePlansInDependencyOrder.Select(tablePlan =>
                    tablePlan.TableModel.Table.Equals(rootTablePlan.TableModel.Table)
                        ? brokenRootTablePlan
                        : tablePlan
                ),
            ]
        );
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            new RelationalAuthorizationContext([42L], []),
            brokenWritePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var securityConfigurationError = (RelationshipAuthorizationResult.SecurityConfigurationError)result;

        securityConfigurationError.Failures.Should().ContainSingle();
        securityConfigurationError
            .Failures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.MissingProposedRootBinding);
        securityConfigurationError
            .Failures[0]
            .ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Proposed);
        securityConfigurationError
            .Failures[0]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
        securityConfigurationError
            .Failures[0]
            .Location?.JsonPath.Should()
            .Be(selectedSubject.Contributors[0].JsonPath);
        securityConfigurationError
            .Failures[0]
            .Location?.ReadableName.Should()
            .Be(selectedSubject.Contributors[0].ReadableName);
        securityConfigurationError.Failures[0].Location?.Table.Should().Be(selectedSubject.Table);
        securityConfigurationError.Failures[0].Location?.Column.Should().Be(selectedSubject.Column);
    }

    [Test]
    public void It_should_return_explicit_no_claims_results_before_parameterization()
    {
        (_, var mappingSet) = Ds52FixtureHelper.BuildAndCompile();
        var resource = new QualifiedResourceName("Ed-Fi", "CourseTranscript");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
            ),
            new RelationalAuthorizationContext([], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoClaims>();

        var noClaimsResult = (RelationshipAuthorizationResult.NoClaims)result;

        noClaimsResult.CheckSpecs.Should().ContainSingle();
        noClaimsResult.Failures.Should().ContainSingle();
        noClaimsResult
            .Failures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds);
        noClaimsResult.Failures[0].ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        noClaimsResult
            .Failures[0]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
        noClaimsResult.Failures[0].ConfiguredStrategy?.RawConfiguredIndex.Should().Be(0);
        noClaimsResult.Failures[0].RelationshipLocalOrder.Should().Be(0);
    }

    [Test]
    public void It_should_return_people_no_claims_metadata_with_selected_auth_view_and_contributors()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var mappingSet = CreatePeopleSubjectMappingSet(
            resource,
            SecurableElementKind.Student,
            includeRequiredPeopleAuthAssociationResources: true
        );
        var expectedAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoClaims>();

        var noClaimsResult = (RelationshipAuthorizationResult.NoClaims)result;

        noClaimsResult.CheckSpecs.Should().ContainSingle();
        noClaimsResult.Failures.Should().ContainSingle();

        var failure = noClaimsResult.Failures[0];

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds);
        failure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        failure.AuthObject.Should().Be(expectedAuthObject);
        failure
            .PersonMetadata.Should()
            .Be(
                new RelationshipAuthorizationPersonFailureMetadata(
                    RelationshipAuthorizationPersonKind.Student,
                    expectedAuthObject
                )
            );
        failure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
        failure.Location?.ReadableName.Should().Be("StudentUniqueId");
        failure.Location?.AuthorizationObjectName.Should().Be(expectedAuthObject.Name.ToString());
        failure.Contributors.Should().ContainSingle();
        failure.Contributors[0].Kind.Should().Be(SecurableElementKind.Student);
        failure.Hint.Should().Contain(expectedAuthObject.Name.ToString());
        failure.Hint.Should().Contain(expectedAuthObject.FailureHint);
    }

    [Test]
    public void It_should_return_mixed_auth_object_no_claims_metadata_from_subject_auth_objects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "MixedAuthorizationResource");
        var expectedEdOrgAuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );
        var expectedStudentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedRootEdOrgAndStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoClaims>();

        var noClaimsResult = (RelationshipAuthorizationResult.NoClaims)result;

        noClaimsResult.CheckSpecs.Should().ContainSingle();
        noClaimsResult.CheckSpecs[0].Subjects.Should().HaveCount(2);
        noClaimsResult
            .Failures.Select(static failure => failure.AuthObject)
            .Should()
            .Equal(expectedEdOrgAuthObject, expectedStudentAuthObject);

        var peopleFailure = noClaimsResult.Failures.Single(static failure =>
            failure.PersonMetadata is not null
        );

        peopleFailure
            .PersonMetadata.Should()
            .Be(
                new RelationshipAuthorizationPersonFailureMetadata(
                    RelationshipAuthorizationPersonKind.Student,
                    expectedStudentAuthObject
                )
            );
        peopleFailure
            .Location?.AuthorizationObjectName.Should()
            .Be(expectedStudentAuthObject.Name.ToString());
        peopleFailure.Contributors.Should().ContainSingle();
        peopleFailure.Hint.Should().Contain(expectedStudentAuthObject.FailureHint);
    }

    [Test]
    public void It_should_collapse_homogeneous_people_no_claims_metadata_by_subject_auth_object()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "MultipleStudentAuthorizationResource");
        var expectedAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMultipleStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoClaims>();

        var noClaimsResult = (RelationshipAuthorizationResult.NoClaims)result;

        noClaimsResult.CheckSpecs.Should().ContainSingle();
        noClaimsResult.CheckSpecs[0].Subjects.Should().HaveCount(2);
        noClaimsResult.Failures.Should().ContainSingle();

        var failure = noClaimsResult.Failures[0];

        failure.AuthObject.Should().Be(expectedAuthObject);
        failure
            .PersonMetadata.Should()
            .Be(
                new RelationshipAuthorizationPersonFailureMetadata(
                    RelationshipAuthorizationPersonKind.Student,
                    expectedAuthObject
                )
            );
        failure
            .Contributors.Select(static contributor => contributor.JsonPath)
            .Should()
            .Equal("$.studentReference.studentUniqueId", "$.alternateStudentReference.studentUniqueId");
    }

    [TestCaseSource(nameof(PeopleAuthViewSelectionCases))]
    public void It_should_report_missing_people_auth_view_associations_for_selected_people_subjects(
        SecurableElementKind securableElementKind,
        RelationshipAuthorizationPersonAuthViewKind authViewKind,
        RelationshipAuthorizationPersonKind personKind,
        string strategyName
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", $"{personKind}AuthorizationResource");
        var mappingSet = CreatePeopleSubjectMappingSet(resource, securableElementKind);
        var expectedAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(strategyName),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var securityConfigurationError = (RelationshipAuthorizationResult.SecurityConfigurationError)result;

        securityConfigurationError.Failures.Should().ContainSingle();

        var failure = securityConfigurationError.Failures[0];
        failure
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations);
        failure.ConfiguredStrategy?.StrategyName.Should().Be(strategyName);
        failure.RelationshipLocalOrder.Should().Be(0);
        failure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        failure.AuthObject.Should().Be(expectedAuthObject);
        failure.AuthObject?.SubjectValueColumn.Should().Be(expectedAuthObject.SubjectValueColumn);
        failure.AuthObject?.FailureHint.Should().Be(expectedAuthObject.FailureHint);
        failure
            .PersonMetadata.Should()
            .Be(new RelationshipAuthorizationPersonFailureMetadata(personKind, expectedAuthObject));
        failure.Location?.Kind.Should().Be(securableElementKind);
        failure.Location?.JsonPath.Should().Be(GetPersonReferenceJsonPath(securableElementKind));
        failure.Location?.AuthorizationObjectName.Should().Be(expectedAuthObject.Name.ToString());
        failure.Contributors.Should().ContainSingle();
        failure.Contributors[0].Kind.Should().Be(securableElementKind);
        failure.Hint.Should().Contain(resource.ResourceName);
        failure.Hint.Should().Contain(strategyName);
        failure.Hint.Should().Contain(personKind.ToString());
        failure.Hint.Should().Contain(expectedAuthObject.Name.ToString());

        foreach (var requiredResourceName in AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames)
        {
            failure.Hint.Should().Contain(requiredResourceName);
        }
    }

    [Test]
    public void It_should_continue_with_edorg_subjects_for_mixed_strategies_when_people_views_are_suppressed_but_no_people_subject_is_selected()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "TestResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMultipleRootSubjectMappingSet(),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorized = (RelationshipAuthorizationResult.Authorized)result;

        authorized.CheckSpecs.Should().ContainSingle();
        authorized
            .CheckSpecs[0]
            .Subjects[0]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
        authorized.CheckSpecs[0].Subjects.Should().HaveCount(2);
        authorized.CheckSpecs[0].Subjects.Should().OnlyContain(static subject => !subject.IsPersonSubject);
    }

    [Test]
    public void It_should_combine_edorg_and_people_subjects_inside_mixed_strategies()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "MixedAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedRootEdOrgAndStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorized = (RelationshipAuthorizationResult.Authorized)result;

        authorized.CheckSpecs.Should().ContainSingle();

        var checkSpec = authorized.CheckSpecs[0];

        checkSpec.ConfiguredStrategy.RawConfiguredIndex.Should().Be(0);
        checkSpec.RelationshipLocalOrder.Should().Be(0);
        checkSpec
            .Subjects.Single(static subject => !subject.IsPersonSubject)
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
        checkSpec
            .Subjects.Single(static subject => !subject.IsPersonSubject)
            .AuthObject.AllowsDirectClaimMatch.Should()
            .BeTrue();
        checkSpec.Subjects.Should().HaveCount(2);
        checkSpec.Subjects.Count(static subject => !subject.IsPersonSubject).Should().Be(1);
        checkSpec.Subjects.Count(static subject => subject.IsPersonSubject).Should().Be(1);

        var personSubject = checkSpec.Subjects.Single(static subject => subject.IsPersonSubject);

        personSubject.Column.Should().Be(AuthNames.StudentDocumentId);
        personSubject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
    }

    [Test]
    public void It_should_keep_person_auth_view_semantics_the_same_for_inverted_mixed_strategies()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "MixedAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedRootEdOrgAndStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;

        checkSpec.Direction.Should().Be(RelationshipAuthorizationHierarchyDirection.Inverted);
        checkSpec
            .Subjects.Single(static subject => !subject.IsPersonSubject)
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Inverted
                )
            );
        checkSpec
            .Subjects.Single(static subject => !subject.IsPersonSubject)
            .AuthObject.AllowsDirectClaimMatch.Should()
            .BeTrue();
        checkSpec
            .Subjects.Single(static subject => subject.IsPersonSubject)
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
    }

    [Test]
    public void It_should_plan_people_only_strategies_with_people_subjects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreatePeopleSubjectMappingSet(
                resource,
                SecurableElementKind.Student,
                includeRequiredPeopleAuthAssociationResources: true
            ),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;

        checkSpec
            .Subjects[0]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        checkSpec.Subjects.Should().ContainSingle();
        checkSpec.Subjects[0].IsPersonSubject.Should().BeTrue();
        checkSpec
            .Subjects[0]
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Student);
    }

    [Test]
    public void It_should_plan_create_new_people_proposed_specs_with_root_row_anchors()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var mappingSet = CreatePeopleSubjectMappingSet(
            resource,
            SecurableElementKind.Student,
            includeRequiredPeopleAuthAssociationResources: true
        );
        var writePlan = CreateWritePlan(mappingSet, resource, AuthNames.StudentDocumentId);
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;
        var subject = checkSpec.Subjects.Should().ContainSingle().Subject;
        var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)checkSpec.CheckTarget;

        subject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        subject.PersonMetadata!.ProposedAnchor.Should().NotBeNull();
        subject
            .PersonMetadata.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.RootRow);
        subject.PersonMetadata.ProposedAnchor.Binding.Table.Should().Be(Table(resource.ResourceName));
        subject.PersonMetadata.ProposedAnchor.Binding.Column.Should().Be(AuthNames.StudentDocumentId);
        subject.PersonMetadata.ProposedAnchor.Binding.BindingIndex.Should().Be(0);
        proposedTarget
            .SubjectBindingsInOrder.Should()
            .ContainSingle()
            .Which.Should()
            .Be(subject.PersonMetadata.ProposedAnchor.Binding);
    }

    [Test]
    public void It_should_plan_create_new_people_proposed_specs_with_first_hop_anchors_for_transitive_paths()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "CourseTranscript");
        var mappingSet = CreateTransitiveCourseTranscriptStudentMappingSet(resource);
        var writePlan = CreateWritePlan(mappingSet, resource, Col("StudentAcademicRecord_DocumentId"));
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;
        var subject = checkSpec.Subjects.Should().ContainSingle().Subject;
        var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)checkSpec.CheckTarget;

        subject.Table.Should().Be(Table("StudentAcademicRecord"));
        subject.Column.Should().Be(AuthNames.StudentDocumentId);
        subject
            .PersonMetadata!.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath);
        subject
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        subject
            .PersonMetadata.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.FirstHop);
        subject.PersonMetadata.ProposedAnchor.Binding.Table.Should().Be(Table("CourseTranscript"));
        subject
            .PersonMetadata.ProposedAnchor.Binding.Column.Should()
            .Be(Col("StudentAcademicRecord_DocumentId"));
        subject.PersonMetadata.ProposedAnchor.Binding.BindingIndex.Should().Be(0);
        proposedTarget
            .SubjectBindingsInOrder.Should()
            .ContainSingle()
            .Which.Should()
            .Be(subject.PersonMetadata.ProposedAnchor.Binding);
    }

    [Test]
    public void It_should_mark_self_person_create_new_subjects_ineligible_when_no_subject_can_execute()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var mappingSet = CreateSelfPersonMappingSet(SecurableElementKind.Student);
        var writePlan = CreateWritePlan(mappingSet, resource, _documentId);
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failure = ((RelationshipAuthorizationResult.SecurityConfigurationError)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoExecutableSubjects);
        failure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        failure
            .IneligibleSubjects.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(
                RelationshipAuthorizationSubjectIneligibilityReason.SelfPersonDocumentIdUnavailableForCreateNew
            );
        failure.Hint.Should().Contain("SelfPersonDocumentIdUnavailableForCreateNew");
    }

    [Test]
    public void It_should_omit_self_person_create_new_subjects_when_another_strategy_subject_can_execute()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var mappingSet = CreateStudentSelfPersonAndEdOrgMappingSet();
        var writePlan = CreateWritePlan(mappingSet, resource, Col("SchoolId"));
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;
        var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)checkSpec.CheckTarget;

        checkSpec.Subjects.Should().ContainSingle().Which.IsPersonSubject.Should().BeFalse();
        proposedTarget.SubjectBindingsInOrder.Should().ContainSingle();
        checkSpec
            .IneligibleSubjects.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(
                RelationshipAuthorizationSubjectIneligibilityReason.SelfPersonDocumentIdUnavailableForCreateNew
            );
    }

    [Test]
    public void It_should_continue_with_people_subjects_for_mixed_strategies_when_edorg_paths_are_non_root()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "ChildEdOrgStudentAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedChildEdOrgAndRootStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;

        checkSpec.Subjects.Should().ContainSingle();
        checkSpec.Subjects[0].IsPersonSubject.Should().BeTrue();
        checkSpec
            .Subjects[0]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
    }

    [Test]
    public void It_should_keep_unresolved_securable_elements_as_security_configuration_failures()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "UnresolvedEdOrgStudentAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedUnresolvedEdOrgAndRootStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failure = ((RelationshipAuthorizationResult.SecurityConfigurationError)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.UnresolvedSecurableElement);
        failure.Location!.Kind.Should().Be(SecurableElementKind.EducationOrganization);
        failure.Location.JsonPath.Should().Be("$.missingSchoolReference.schoolId");
        failure
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
    }

    [Test]
    public void It_should_preserve_duplicate_people_strategies_as_separate_or_entries()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreatePeopleSubjectMappingSet(
                resource,
                SecurableElementKind.Student,
                includeRequiredPeopleAuthAssociationResources: true
            ),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorized = (RelationshipAuthorizationResult.Authorized)result;

        authorized.CheckSpecs.Should().HaveCount(2);
        authorized
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(0, 1);
        authorized
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        authorized.CheckSpecs.Should().OnlyContain(static checkSpec => checkSpec.Subjects.Count == 1);
        authorized
            .CheckSpecs.Select(static checkSpec => checkSpec.Subjects[0].Column)
            .Should()
            .Equal(AuthNames.StudentDocumentId, AuthNames.StudentDocumentId);
    }

    [Test]
    public void It_should_preserve_relationship_local_order_when_supported_strategies_skip_noop_entries()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "TestResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMultipleRootSubjectMappingSet(),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var authorizedResult = (RelationshipAuthorizationResult.Authorized)result;

        authorizedResult.CheckSpecs.Should().HaveCount(2);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(1, 2);
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
    }

    [Test]
    public void It_should_preserve_relationship_local_order_on_subject_selection_failures_after_classification()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var securityConfigurationErrorResult =
            (RelationshipAuthorizationResult.SecurityConfigurationError)result;

        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.ConfiguredStrategy?.RawConfiguredIndex)
            .Should()
            .Equal(1, 2);
        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.ValueSource)
            .Should()
            .OnlyContain(static valueSource => valueSource == RelationshipAuthorizationValueSource.Stored);
        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.AuthObject)
            .Should()
            .Equal(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Inverted
                )
            );
    }

    [Test]
    public void It_should_return_security_configuration_errors_before_known_but_not_enabled_staging_when_supported_subject_planning_fails()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.NamespaceBased
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var securityConfigurationErrorResult =
            (RelationshipAuthorizationResult.SecurityConfigurationError)result;

        securityConfigurationErrorResult.Failures.Should().HaveCount(2);
        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy
            );
        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.ConfiguredStrategy?.StrategyName)
            .Should()
            .Equal(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.NamespaceBased
            );
        securityConfigurationErrorResult
            .Failures.Select(static failure => failure.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        securityConfigurationErrorResult
            .Failures[0]
            .ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Stored);
        securityConfigurationErrorResult
            .Failures[0]
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                )
            );
    }

    [Test]
    public void It_should_report_update_subject_selection_failures_once()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var mappingSet = CreateMinimalMappingSet(resource);
        var planner = CreatePlanner();

        var result = planner.PlanUpdateValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
            ),
            new RelationalAuthorizationContext([42L], []),
            CreateMinimalWritePlan(mappingSet, resource)
        );

        result.StoredValues.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();
        result.ProposedValues.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();
        result.SecurityConfigurationFailures.Should().HaveCount(2);
        result.KnownButNotEnabledFailures.Should().BeEmpty();
        result
            .SecurityConfigurationFailures.Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject
            );
        result
            .SecurityConfigurationFailures.Select(static failure =>
                failure.ConfiguredStrategy?.RawConfiguredIndex
            )
            .Should()
            .Equal(0, 1);
        result
            .SecurityConfigurationFailures.Select(static failure => failure.ValueSource)
            .Should()
            .Equal((RelationshipAuthorizationValueSource?)null, null);
    }

    [Test]
    public void It_should_map_known_but_not_enabled_outcomes_to_shared_failure_metadata()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(AuthorizationStrategyNameConstants.NamespaceBased),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.KnownButNotEnabled>();

        var knownButNotEnabledResult = (RelationshipAuthorizationResult.KnownButNotEnabled)result;

        knownButNotEnabledResult.Failures.Should().ContainSingle();
        knownButNotEnabledResult
            .Failures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy);
        knownButNotEnabledResult
            .Failures[0]
            .ConfiguredStrategy?.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.NamespaceBased);
    }

    [Test]
    public void It_should_leave_direct_claim_match_disabled_for_non_edorg_auth_objects()
    {
        var authObject = new RelationshipAuthorizationAuthObject(
            new DbTableName(new DbSchemaName("auth"), "NonEdOrgAuthorizationObject"),
            new DbColumnName("SubjectId"),
            new DbColumnName("ClaimId")
        );

        authObject.AllowsDirectClaimMatch.Should().BeFalse();
    }

    [Test]
    public void It_should_return_no_authorization_required_when_no_configured_strategies_are_present()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            [],
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoAuthorizationRequired>();

        var noAuthorizationRequiredResult = (RelationshipAuthorizationResult.NoAuthorizationRequired)result;

        noAuthorizationRequiredResult.ConfiguredStrategies.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_no_further_authorization_required_without_emitting_checks()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoFurtherAuthorizationRequired>();

        var noFurtherAuthorizationRequiredResult =
            (RelationshipAuthorizationResult.NoFurtherAuthorizationRequired)result;

        noFurtherAuthorizationRequiredResult
            .ConfiguredStrategies.Select(static strategy => strategy.RawConfiguredIndex)
            .Should()
            .Equal(0);
    }

    [Test]
    public void It_should_preserve_known_but_not_enabled_failures_when_security_configuration_errors_win()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.NamespaceBased,
                "CustomAuthorizationStrategy"
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var securityConfigurationErrorResult =
            (RelationshipAuthorizationResult.SecurityConfigurationError)result;

        securityConfigurationErrorResult.Failures.Should().HaveCount(2);
        securityConfigurationErrorResult
            .Failures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy);
        securityConfigurationErrorResult
            .Failures[0]
            .ConfiguredStrategy?.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.NamespaceBased);
        securityConfigurationErrorResult.Failures[0].RelationshipLocalOrder.Should().Be(0);
        securityConfigurationErrorResult
            .Failures[1]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy);
        securityConfigurationErrorResult
            .Failures[1]
            .ConfiguredStrategy?.StrategyName.Should()
            .Be("CustomAuthorizationStrategy");
        securityConfigurationErrorResult.Failures[1].RelationshipLocalOrder.Should().Be(1);
    }

    private static MappingSet CreateMultipleRootSubjectMappingSet()
    {
        var rootTable = CreateRootTable(
            Table("TestResource"),
            [
                new DbColumnModel(
                    Col("SchoolReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    Col("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    Col("LocalEducationAgencyReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "LocalEducationAgency")
                ),
                new DbColumnModel(
                    Col("LocalEducationAgencyReference_LocalEducationAgencyId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path("$.localEducationAgencyReference.localEducationAgencyId"),
                    null
                ),
            ]
        );

        return CreateMappingSet(
            CreateConcrete(
                "TestResource",
                CreateModelWithTables(
                    "TestResource",
                    rootTable,
                    [
                        new DocumentReferenceBinding(
                            true,
                            Path("$.schoolReference"),
                            rootTable.Table,
                            Col("SchoolReference_DocumentId"),
                            new QualifiedResourceName("Ed-Fi", "School"),
                            [
                                new ReferenceIdentityBinding(
                                    Path("$.schoolReference.schoolId"),
                                    Path("$.schoolReference.schoolId"),
                                    Col("SchoolReference_SchoolId")
                                ),
                            ]
                        ),
                        new DocumentReferenceBinding(
                            true,
                            Path("$.localEducationAgencyReference"),
                            rootTable.Table,
                            Col("LocalEducationAgencyReference_DocumentId"),
                            new QualifiedResourceName("Ed-Fi", "LocalEducationAgency"),
                            [
                                new ReferenceIdentityBinding(
                                    Path("$.localEducationAgencyReference.localEducationAgencyId"),
                                    Path("$.localEducationAgencyReference.localEducationAgencyId"),
                                    Col("LocalEducationAgencyReference_LocalEducationAgencyId")
                                ),
                            ]
                        ),
                    ]
                ),
                new ResourceSecurableElements(
                    [
                        new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                        new EdOrgSecurableElement(
                            "$.localEducationAgencyReference.localEducationAgencyId",
                            "LocalEducationAgencyId"
                        ),
                    ],
                    [],
                    [],
                    [],
                    []
                )
            )
        );
    }

    private static MappingSet CreateMixedRootEdOrgAndStudentSubjectMappingSet(QualifiedResourceName resource)
    {
        const string schoolIdPath = "$.schoolReference.schoolId";
        const string studentPath = "$.studentReference.studentUniqueId";
        var rootTable = CreateRootTable(
            Table(resource.ResourceName),
            [
                new DbColumnModel(
                    Col("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path(schoolIdPath),
                    null
                ),
                new DbColumnModel(
                    AuthNames.StudentDocumentId,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(studentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        return CreateMixedEdOrgAndStudentMappingSet(
            resource,
            rootTable,
            [],
            schoolIdPath,
            "SchoolId",
            studentPath
        );
    }

    private static MappingSet CreateMixedChildEdOrgAndRootStudentSubjectMappingSet(
        QualifiedResourceName resource
    )
    {
        const string schoolIdPath = "$.schools[*].schoolReference.schoolId";
        const string studentPath = "$.studentReference.studentUniqueId";
        var rootTable = CreateRootTable(
            Table(resource.ResourceName),
            [
                new DbColumnModel(
                    AuthNames.StudentDocumentId,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(studentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var childTable = CreateChildTable(
            Table($"{resource.ResourceName}School"),
            "$.schools[*]",
            [
                new DbColumnModel(
                    Col("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path(schoolIdPath),
                    null
                ),
            ]
        );

        return CreateMixedEdOrgAndStudentMappingSet(
            resource,
            rootTable,
            [childTable],
            schoolIdPath,
            "SchoolId",
            studentPath
        );
    }

    private static MappingSet CreateMixedUnresolvedEdOrgAndRootStudentSubjectMappingSet(
        QualifiedResourceName resource
    )
    {
        const string schoolIdPath = "$.missingSchoolReference.schoolId";
        const string studentPath = "$.studentReference.studentUniqueId";
        var rootTable = CreateRootTable(
            Table(resource.ResourceName),
            [
                new DbColumnModel(
                    AuthNames.StudentDocumentId,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(studentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        return CreateMixedEdOrgAndStudentMappingSet(
            resource,
            rootTable,
            [],
            schoolIdPath,
            "MissingSchoolId",
            studentPath
        );
    }

    private static MappingSet CreateTransitiveCourseTranscriptStudentMappingSet(
        QualifiedResourceName courseTranscript
    )
    {
        const string courseTranscriptStudentPath = "$.studentAcademicRecordReference.studentUniqueId";
        const string studentAcademicRecordStudentPath = "$.studentReference.studentUniqueId";

        var courseTranscriptRoot = CreateRootTable(
            Table(courseTranscript.ResourceName),
            [
                new DbColumnModel(
                    Col("StudentAcademicRecord_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(courseTranscriptStudentPath),
                    new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord")
                ),
            ]
        );
        var courseTranscriptModel = CreateModelWithTables(
            courseTranscript.ResourceName,
            courseTranscriptRoot,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentAcademicRecordReference"),
                    courseTranscriptRoot.Table,
                    Col("StudentAcademicRecord_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "StudentAcademicRecord"),
                    [
                        new ReferenceIdentityBinding(
                            Path(courseTranscriptStudentPath),
                            Path(courseTranscriptStudentPath),
                            Col("StudentAcademicRecord_StudentUniqueId")
                        ),
                    ]
                ),
            ]
        );
        var studentAcademicRecordRoot = CreateRootTable(
            Table("StudentAcademicRecord"),
            [
                new DbColumnModel(
                    AuthNames.StudentDocumentId,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(studentAcademicRecordStudentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var studentAcademicRecordModel = CreateModelWithTables(
            "StudentAcademicRecord",
            studentAcademicRecordRoot,
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.studentReference"),
                    studentAcademicRecordRoot.Table,
                    AuthNames.StudentDocumentId,
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            Path(studentAcademicRecordStudentPath),
                            Path(studentAcademicRecordStudentPath),
                            Col("Student_StudentUniqueId")
                        ),
                    ]
                ),
            ]
        );

        return CreateMappingSet([
            CreateConcrete(
                courseTranscript.ResourceName,
                courseTranscriptModel,
                new ResourceSecurableElements([], [], [courseTranscriptStudentPath], [], [])
            ),
            CreateConcrete(
                "StudentAcademicRecord",
                studentAcademicRecordModel,
                new ResourceSecurableElements([], [], [studentAcademicRecordStudentPath], [], [])
            ),
            CreatePersonResource(SecurableElementKind.Student),
            .. CreateRequiredPeopleAuthAssociationResources(),
        ]);
    }

    private static MappingSet CreateSelfPersonMappingSet(SecurableElementKind securableElementKind) =>
        CreateMappingSet([
            CreatePersonResource(securableElementKind),
            .. CreateRequiredPeopleAuthAssociationResources(),
        ]);

    private static MappingSet CreateStudentSelfPersonAndEdOrgMappingSet()
    {
        const string schoolIdPath = "$.schoolReference.schoolId";
        var rootTable = CreateRootTable(
            Table("Student"),
            [new DbColumnModel(Col("SchoolId"), ColumnKind.Scalar, null, false, Path(schoolIdPath), null)]
        );

        return CreateMappingSet(
            CreateConcrete(
                "Student",
                CreateModelWithTables("Student", rootTable, []),
                new ResourceSecurableElements(
                    [new EdOrgSecurableElement(schoolIdPath, "SchoolId")],
                    [],
                    [GetPersonSelfJsonPath(SecurableElementKind.Student)],
                    [],
                    []
                )
            )
        );
    }

    private static MappingSet CreateMixedEdOrgAndStudentMappingSet(
        QualifiedResourceName resource,
        DbTableModel rootTable,
        IReadOnlyList<DbTableModel> childTables,
        string schoolIdPath,
        string schoolIdReadableName,
        string studentPath
    )
    {
        var concreteResource = CreateConcrete(
            resource.ResourceName,
            CreateModelWithTables(
                resource.ResourceName,
                rootTable,
                childTables,
                [
                    new DocumentReferenceBinding(
                        true,
                        Path(GetReferenceObjectPath(studentPath)),
                        rootTable.Table,
                        AuthNames.StudentDocumentId,
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [
                            new ReferenceIdentityBinding(
                                Path(studentPath),
                                Path(studentPath),
                                Col("StudentUniqueId")
                            ),
                        ]
                    ),
                ]
            ),
            new ResourceSecurableElements(
                [new EdOrgSecurableElement(schoolIdPath, schoolIdReadableName)],
                [],
                [studentPath],
                [],
                []
            )
        );

        return CreateMappingSet([
            concreteResource,
            CreatePersonResource(SecurableElementKind.Student),
            .. CreateRequiredPeopleAuthAssociationResources(),
        ]);
    }

    private static MappingSet CreateMinimalMappingSet(QualifiedResourceName resource) =>
        CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                CreateModelWithTables(
                    resource.ResourceName,
                    CreateRootTable(Table(resource.ResourceName)),
                    []
                ),
                new ResourceSecurableElements([], [], [], [], [])
            )
        );

    private static MappingSet CreatePeopleSubjectMappingSet(
        QualifiedResourceName resource,
        SecurableElementKind securableElementKind,
        bool includeRequiredPeopleAuthAssociationResources = false
    )
    {
        var personReferenceJsonPath = GetPersonReferenceJsonPath(securableElementKind);
        var personDocumentIdColumn = GetPersonDocumentIdColumn(securableElementKind);
        var rootTable = CreateRootTable(
            Table(resource.ResourceName),
            [
                new DbColumnModel(
                    personDocumentIdColumn,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(personReferenceJsonPath),
                    new QualifiedResourceName("Ed-Fi", GetPersonResourceName(securableElementKind))
                ),
            ]
        );
        var concreteResource = CreateConcrete(
            resource.ResourceName,
            CreateModelWithTables(
                resource.ResourceName,
                rootTable,
                [
                    new DocumentReferenceBinding(
                        true,
                        Path(GetReferenceObjectPath(personReferenceJsonPath)),
                        rootTable.Table,
                        personDocumentIdColumn,
                        new QualifiedResourceName("Ed-Fi", GetPersonResourceName(securableElementKind)),
                        [
                            new ReferenceIdentityBinding(
                                Path(personReferenceJsonPath),
                                Path(personReferenceJsonPath),
                                Col($"{GetPersonResourceName(securableElementKind)}UniqueId")
                            ),
                        ]
                    ),
                ]
            ),
            CreatePersonSecurableElements(securableElementKind)
        );
        List<ConcreteResourceModel> concreteResources =
        [
            concreteResource,
            CreatePersonResource(securableElementKind),
        ];

        if (includeRequiredPeopleAuthAssociationResources)
        {
            concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources());
        }

        return CreateMappingSet(
            concreteResources,
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>
            {
                [resource] =
                [
                    new ResolvedSecurableElementPath(
                        securableElementKind,
                        [
                            new ColumnPathStep(
                                rootTable.Table,
                                personDocumentIdColumn,
                                Table(GetPersonResourceName(securableElementKind)),
                                _documentId
                            ),
                        ]
                    ),
                ],
            }
        );
    }

    private static MappingSet CreateMultipleStudentSubjectMappingSet(QualifiedResourceName resource)
    {
        const string studentPath = "$.studentReference.studentUniqueId";
        const string alternateStudentPath = "$.alternateStudentReference.studentUniqueId";
        var alternateStudentDocumentId = Col("AlternateStudent_DocumentId");
        var rootTable = CreateRootTable(
            Table(resource.ResourceName),
            [
                new DbColumnModel(
                    AuthNames.StudentDocumentId,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(studentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
                new DbColumnModel(
                    alternateStudentDocumentId,
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(alternateStudentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var concreteResource = CreateConcrete(
            resource.ResourceName,
            CreateModelWithTables(
                resource.ResourceName,
                rootTable,
                [
                    new DocumentReferenceBinding(
                        true,
                        Path(GetReferenceObjectPath(studentPath)),
                        rootTable.Table,
                        AuthNames.StudentDocumentId,
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [
                            new ReferenceIdentityBinding(
                                Path(studentPath),
                                Path(studentPath),
                                Col("StudentUniqueId")
                            ),
                        ]
                    ),
                    new DocumentReferenceBinding(
                        true,
                        Path(GetReferenceObjectPath(alternateStudentPath)),
                        rootTable.Table,
                        alternateStudentDocumentId,
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [
                            new ReferenceIdentityBinding(
                                Path(alternateStudentPath),
                                Path(alternateStudentPath),
                                Col("AlternateStudentUniqueId")
                            ),
                        ]
                    ),
                ]
            ),
            new ResourceSecurableElements([], [], [studentPath, alternateStudentPath], [], [])
        );

        return CreateMappingSet(
            [
                concreteResource,
                CreatePersonResource(SecurableElementKind.Student),
                .. CreateRequiredPeopleAuthAssociationResources(),
            ],
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>
            {
                [resource] =
                [
                    new ResolvedSecurableElementPath(
                        SecurableElementKind.Student,
                        [
                            new ColumnPathStep(
                                rootTable.Table,
                                AuthNames.StudentDocumentId,
                                Table("Student"),
                                _documentId
                            ),
                        ]
                    ),
                    new ResolvedSecurableElementPath(
                        SecurableElementKind.Student,
                        [
                            new ColumnPathStep(
                                rootTable.Table,
                                alternateStudentDocumentId,
                                Table("Student"),
                                _documentId
                            ),
                        ]
                    ),
                ],
            }
        );
    }

    private static ResourceWritePlan CreateMinimalWritePlan(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        var relationalModel = mappingSet.GetConcreteResourceModelOrThrow(resource).RelationalModel;

        return new ResourceWritePlan(
            relationalModel,
            [
                new TableWritePlan(
                    relationalModel.Root,
                    "",
                    null,
                    null,
                    new BulkInsertBatchingInfo(1, 0, 1),
                    [],
                    []
                ),
            ]
        );
    }

    private static ResourceWritePlan CreateWritePlan(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        params DbColumnName[] rootColumns
    )
    {
        var relationalModel = mappingSet.GetConcreteResourceModelOrThrow(resource).RelationalModel;
        var rootColumnByName = relationalModel.Root.Columns.ToDictionary(
            static column => column.ColumnName,
            static column => column
        );
        var columnBindings = rootColumns
            .Select(
                (column, index) =>
                    new WriteColumnBinding(
                        rootColumnByName.TryGetValue(column, out var columnModel)
                            ? columnModel
                            : new DbColumnModel(column, ColumnKind.ParentKeyPart, null, false, null, null),
                        column.Equals(_documentId)
                            ? new WriteValueSource.DocumentId()
                            : new WriteValueSource.DocumentReference(index),
                        column.Value
                    )
            )
            .ToArray();

        return new ResourceWritePlan(
            relationalModel,
            [
                new TableWritePlan(
                    relationalModel.Root,
                    "",
                    null,
                    null,
                    new BulkInsertBatchingInfo(1, columnBindings.Length, columnBindings.Length),
                    columnBindings,
                    []
                ),
            ]
        );
    }

    private static TableWritePlan GetRootTableWritePlan(ResourceWritePlan writePlan) =>
        writePlan.TablePlansInDependencyOrder.Single(static plan =>
            plan.TableModel.IdentityMetadata.TableKind is DbTableKind.Root
        );

    private static ConfiguredAuthorizationStrategy[] CreateConfiguredAuthorizationStrategies(
        params string[] strategyNames
    ) =>
        [
            .. strategyNames.Select(
                static (strategyName, index) => new ConfiguredAuthorizationStrategy(strategyName, index)
            ),
        ];

    private static IReadOnlyList<SupportedRelationshipAuthorizationStrategy> CreateSupportedStrategies(
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies
    ) =>
        [
            .. configuredAuthorizationStrategies.Select(
                static (configuredStrategy, relationshipLocalOrder) =>
                    new SupportedRelationshipAuthorizationStrategy(
                        RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                        RelationshipAuthorizationHierarchyDirection.Normal,
                        configuredStrategy,
                        relationshipLocalOrder,
                        [new(SecurableElementKind.EducationOrganization)]
                    )
            ),
        ];

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string resource) =>
        new(id, new QualifiedResourceName("Ed-Fi", resource), "1.0", false);

    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path("$"),
            new TableKey("PK_Test", [new DbKeyColumn(_documentId, ColumnKind.ParentKeyPart)]),
            columns ?? [],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [_documentId],
                [_documentId],
                [],
                []
            ),
        };

    private static DbTableModel CreateChildTable(
        DbTableName table,
        string jsonScope,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path(jsonScope),
            new TableKey($"PK_{table.Name}", [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.Scalar)]),
            columns ?? [],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [Col("CollectionItemId")],
                [_documentId],
                [],
                []
            ),
        };

    private static RelationalResourceModel CreateModelWithTables(
        string resource,
        DbTableModel rootTable,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    ) => CreateModelWithTables(resource, rootTable, [], documentReferenceBindings);

    private static RelationalResourceModel CreateModelWithTables(
        string resource,
        DbTableModel rootTable,
        IReadOnlyList<DbTableModel> childTables,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, .. childTables],
            documentReferenceBindings,
            []
        );

    private static ConcreteResourceModel CreateConcrete(
        string resource,
        RelationalResourceModel model,
        ResourceSecurableElements securableElements,
        short resourceKeyId = 1
    ) =>
        new(ResourceKey(resourceKeyId, resource), ResourceStorageKind.RelationalTables, model)
        {
            SecurableElements = securableElements,
        };

    private static ConcreteResourceModel CreatePersonResource(SecurableElementKind securableElementKind)
    {
        var resourceName = GetPersonResourceName(securableElementKind);

        return CreateConcrete(
            resourceName,
            CreateModelWithTables(resourceName, CreateRootTable(Table(resourceName)), []),
            securableElementKind switch
            {
                SecurableElementKind.Student => new ResourceSecurableElements(
                    [],
                    [],
                    [GetPersonSelfJsonPath(securableElementKind)],
                    [],
                    []
                ),
                SecurableElementKind.Contact => new ResourceSecurableElements(
                    [],
                    [],
                    [],
                    [GetPersonSelfJsonPath(securableElementKind)],
                    []
                ),
                SecurableElementKind.Staff => new ResourceSecurableElements(
                    [],
                    [],
                    [],
                    [],
                    [GetPersonSelfJsonPath(securableElementKind)]
                ),
                _ => ResourceSecurableElements.Empty,
            }
        );
    }

    private static IEnumerable<ConcreteResourceModel> CreateRequiredPeopleAuthAssociationResources() =>
        AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames.Select(resourceName =>
            CreateConcrete(
                resourceName,
                CreateModelWithTables(resourceName, CreateRootTable(Table(resourceName)), []),
                ResourceSecurableElements.Empty
            )
        );

    private static MappingSet CreateMappingSet(
        ConcreteResourceModel concreteResourceModel,
        IReadOnlyDictionary<
            QualifiedResourceName,
            IReadOnlyList<ResolvedSecurableElementPath>
        >? securableElementColumnPathsByResource = null
    ) => CreateMappingSet([concreteResourceModel], securableElementColumnPathsByResource);

    private static MappingSet CreateMappingSet(
        IReadOnlyList<ConcreteResourceModel> concreteResourceModels,
        IReadOnlyDictionary<
            QualifiedResourceName,
            IReadOnlyList<ResolvedSecurableElementPath>
        >? securableElementColumnPathsByResource = null
    )
    {
        var concreteResources = concreteResourceModels
            .Select(
                static (concreteResourceModel, index) =>
                    concreteResourceModel with
                    {
                        ResourceKey = concreteResourceModel.ResourceKey with
                        {
                            ResourceKeyId = checked((short)(index + 1)),
                        },
                    }
            )
            .ToArray();
        var resourceKeysInIdOrder = concreteResources
            .Select(static resource => resource.ResourceKey)
            .ToArray();

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: checked((short)concreteResources.Length),
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: resourceKeysInIdOrder
                ),
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, _edfiSchema),
                ],
                ConcreteResourcesInNameOrder: concreteResources,
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: resourceKeysInIdOrder.ToDictionary(
                static resourceKey => resourceKey.Resource,
                static resourceKey => resourceKey.ResourceKeyId
            ),
            ResourceKeyById: resourceKeysInIdOrder.ToDictionary(static resourceKey =>
                resourceKey.ResourceKeyId
            ),
            SecurableElementColumnPathsByResource: securableElementColumnPathsByResource
                ?? new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );
    }

    private static RelationshipAuthorizationPlanner CreatePlanner() => new(CreateSelector());

    private static RelationalEdOrgAuthorizationSubjectSelector CreateSelector() =>
        new(new RelationalEdOrgAuthorizationElementResolutionCache());

    private static IEnumerable<TestCaseData> PeopleAuthViewSelectionCases()
    {
        yield return new TestCaseData(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Student,
            RelationshipAuthorizationPersonKind.Student,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        ).SetName("Student");

        yield return new TestCaseData(
            SecurableElementKind.Contact,
            RelationshipAuthorizationPersonAuthViewKind.Contact,
            RelationshipAuthorizationPersonKind.Contact,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        ).SetName("Contact");

        yield return new TestCaseData(
            SecurableElementKind.Staff,
            RelationshipAuthorizationPersonAuthViewKind.Staff,
            RelationshipAuthorizationPersonKind.Staff,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        ).SetName("Staff");

        yield return new TestCaseData(
            SecurableElementKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility,
            RelationshipAuthorizationPersonKind.Student,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
        ).SetName("StudentThroughResponsibility");
    }

    private static ResourceSecurableElements CreatePersonSecurableElements(
        SecurableElementKind securableElementKind
    ) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => new ResourceSecurableElements(
                [],
                [],
                [GetPersonReferenceJsonPath(securableElementKind)],
                [],
                []
            ),
            SecurableElementKind.Contact => new ResourceSecurableElements(
                [],
                [],
                [],
                [GetPersonReferenceJsonPath(securableElementKind)],
                []
            ),
            SecurableElementKind.Staff => new ResourceSecurableElements(
                [],
                [],
                [],
                [],
                [GetPersonReferenceJsonPath(securableElementKind)]
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static DbColumnName GetPersonDocumentIdColumn(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => AuthNames.StudentDocumentId,
            SecurableElementKind.Contact => AuthNames.ContactDocumentId,
            SecurableElementKind.Staff => AuthNames.StaffDocumentId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static string GetPersonReferenceJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentReference.studentUniqueId",
            SecurableElementKind.Contact => "$.contactReference.contactUniqueId",
            SecurableElementKind.Staff => "$.staffReference.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static string GetPersonSelfJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentUniqueId",
            SecurableElementKind.Contact => "$.contactUniqueId",
            SecurableElementKind.Staff => "$.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };

    private static string GetReferenceObjectPath(string jsonPath) =>
        jsonPath[..jsonPath.LastIndexOf(".", StringComparison.Ordinal)];

    private static string GetPersonResourceName(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "Student",
            SecurableElementKind.Contact => "Contact",
            SecurableElementKind.Staff => "Staff",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported people relationship authorization securable element kind."
            ),
        };
}
