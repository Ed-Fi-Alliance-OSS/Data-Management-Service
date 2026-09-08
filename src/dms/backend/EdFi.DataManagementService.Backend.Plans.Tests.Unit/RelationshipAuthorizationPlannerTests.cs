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
                DataStoreIds: []
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
    public void It_should_reuse_executable_shape_for_equivalent_stored_plans_with_different_claim_values()
    {
        var mappingSet = CreateMultipleRootSubjectMappingSet();
        var resource = new QualifiedResourceName("Ed-Fi", "TestResource");
        var configuredStrategies = CreateConfiguredAuthorizationStrategies(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );
        var planner = CreatePlanner();

        var firstResult = planner.PlanStoredValues(
            mappingSet,
            resource,
            configuredStrategies,
            new RelationalAuthorizationContext([100L], [])
        );
        var secondResult = planner.PlanStoredValues(
            mappingSet,
            resource,
            configuredStrategies,
            new RelationalAuthorizationContext([200L, 300L], [])
        );

        var firstAuthorized = firstResult
            .Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Subject;
        var secondAuthorized = secondResult
            .Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Subject;

        firstAuthorized.ExecutableShape.Should().NotBeNull();
        secondAuthorized.ExecutableShape.Should().BeSameAs(firstAuthorized.ExecutableShape);
        secondAuthorized.CheckSpecs.Should().BeSameAs(firstAuthorized.CheckSpecs);
        secondAuthorized
            .ClaimEducationOrganizationIdParameterization!.ClaimEducationOrganizationIds.Should()
            .Equal(200L, 300L);
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
            .Select(mappingSet, resource, CreateSupportedStrategy(configuredAuthorizationStrategies[0]))
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
    public void It_should_report_missing_people_auth_view_associations_when_proposed_check_spec_planning_fails()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var mappingSet = CreatePeopleSubjectMappingSet(resource, SecurableElementKind.Student);
        var expectedAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], []),
            CreateWritePlan(mappingSet, resource)
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures.Should().HaveCount(2);
        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations,
                RelationshipAuthorizationFailureKind.MissingProposedRootBinding
            );

        var missingAuthViewFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
        );
        missingAuthViewFailure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        missingAuthViewFailure.AuthObject.Should().Be(expectedAuthObject);
        missingAuthViewFailure.PersonMetadata.Should().NotBeNull();
        missingAuthViewFailure
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Student);
        missingAuthViewFailure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
        missingAuthViewFailure
            .Location?.AuthorizationObjectName.Should()
            .Be(expectedAuthObject.Name.ToString());

        var missingBindingFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.MissingProposedRootBinding
        );
        missingBindingFailure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        missingBindingFailure.AuthObject.Should().Be(expectedAuthObject);
        missingBindingFailure.PersonMetadata.Should().NotBeNull();
        missingBindingFailure
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Student);
        missingBindingFailure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
        missingBindingFailure.Location?.Table.Should().Be(Table(resource.ResourceName));
        missingBindingFailure.Location?.Column.Should().Be(AuthNames.StudentDocumentId);
    }

    [Test]
    public void It_should_report_proposed_missing_bindings_when_independent_subject_selection_failures_exist()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "UnresolvedEdOrgStudentAuthorizationResource");
        var mappingSet = CreateMixedUnresolvedEdOrgAndRootStudentSubjectMappingSet(resource);
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], []),
            CreateWritePlan(mappingSet, resource)
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                RelationshipAuthorizationFailureKind.MissingProposedRootBinding
            );

        var unresolvedFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
        );
        unresolvedFailure.Location?.Kind.Should().Be(SecurableElementKind.EducationOrganization);
        unresolvedFailure.Location?.JsonPath.Should().Be("$.missingSchoolReference.schoolId");

        var missingBindingFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.MissingProposedRootBinding
        );
        missingBindingFailure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        missingBindingFailure
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        missingBindingFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        missingBindingFailure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
    }

    [Test]
    public void It_should_report_proposed_missing_people_auth_view_associations_when_other_people_paths_are_unresolved()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "PartiallyResolvedStudentAuthorizationResource");
        var mappingSet = CreatePartiallyResolvedStudentSubjectMappingSet(resource);
        var expectedStudentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], []),
            CreateWritePlan(mappingSet, resource, AuthNames.StudentDocumentId)
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
            );

        var unresolvedFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
        );
        unresolvedFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        unresolvedFailure.Location?.JsonPath.Should().Be("$.missingStudentReference.studentUniqueId");

        var missingAuthViewFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
        );
        missingAuthViewFailure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        missingAuthViewFailure.AuthObject.Should().Be(expectedStudentAuthObject);
        missingAuthViewFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        missingAuthViewFailure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
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
        failure.PersonMetadata.Should().NotBeNull();
        failure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        failure.PersonMetadata.AuthObject.Should().Be(expectedAuthObject);
        failure.PersonMetadata.Path.Should().NotBeNull();
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

        peopleFailure.PersonMetadata.Should().NotBeNull();
        peopleFailure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        peopleFailure.PersonMetadata.AuthObject.Should().Be(expectedStudentAuthObject);
        peopleFailure.PersonMetadata.Path.Should().NotBeNull();
        peopleFailure
            .Location?.AuthorizationObjectName.Should()
            .Be(expectedStudentAuthObject.Name.ToString());
        peopleFailure.Contributors.Should().ContainSingle();
        peopleFailure.Hint.Should().Contain(expectedStudentAuthObject.FailureHint);
    }

    [Test]
    public void It_should_keep_mixed_subject_ordinals_edorg_first_then_people_eligibility_order()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "MixedPeopleOrdinalResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedRootEdOrgAndPeopleSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.NoClaims>();

        var noClaimsResult = (RelationshipAuthorizationResult.NoClaims)result;

        var subjects = noClaimsResult.CheckSpecs.Should().ContainSingle().Which.Subjects;

        subjects
            .Select(static subject => subject.Contributors.Single().Kind)
            .Should()
            .Equal(
                SecurableElementKind.EducationOrganization,
                SecurableElementKind.Student,
                SecurableElementKind.Contact,
                SecurableElementKind.Staff
            );
        subjects
            .Select(static subject => subject.Column.Value)
            .Should()
            .Equal(
                "SchoolReference_SchoolId",
                "ZStudent_DocumentId",
                "AContact_DocumentId",
                "BStaff_DocumentId"
            );
        noClaimsResult
            .Failures.Select(static failure => failure.AuthObject!.Name.Name)
            .Should()
            .Equal(
                "EducationOrganizationIdToEducationOrganizationId",
                "EducationOrganizationIdToStudentDocumentId",
                "EducationOrganizationIdToContactDocumentId",
                "EducationOrganizationIdToStaffDocumentId"
            );
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
        failure.PersonMetadata.Should().NotBeNull();
        failure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        failure.PersonMetadata.AuthObject.Should().Be(expectedAuthObject);
        failure.PersonMetadata.Path.Should().NotBeNull();
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
        failure.PersonMetadata.Should().NotBeNull();
        failure.PersonMetadata!.PersonKind.Should().Be(personKind);
        failure.PersonMetadata.AuthObject.Should().Be(expectedAuthObject);
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
    public void It_should_report_missing_people_auth_views_when_auth_hierarchy_is_absent()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var mappingSetWithHierarchy = CreatePeopleSubjectMappingSet(
            resource,
            SecurableElementKind.Student,
            includeRequiredPeopleAuthAssociationResources: true
        );
        var mappingSet = mappingSetWithHierarchy with
        {
            Model = mappingSetWithHierarchy.Model with { AuthEdOrgHierarchy = null },
        };
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failure = ((RelationshipAuthorizationResult.SecurityConfigurationError)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations);
        failure.Hint.Should().Contain("auth EducationOrganization hierarchy");
        failure.Hint.Should().NotContain("missing required association resources");

        foreach (var requiredResourceName in AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames)
        {
            failure.Hint.Should().NotContain(requiredResourceName);
        }
    }

    [Test]
    public void It_should_plan_people_auth_views_when_auth_hierarchy_and_required_associations_are_present()
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

        var authorizedResult = result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>().Subject;

        authorizedResult
            .CheckSpecs.Should()
            .ContainSingle()
            .Which.Subjects.Should()
            .ContainSingle()
            .Which.AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
    }

    [Test]
    public void It_should_report_missing_people_auth_view_associations_with_independent_subject_selection_failures()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "UnresolvedEdOrgStudentAuthorizationResource");
        var expectedStudentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedUnresolvedEdOrgAndRootStudentSubjectMappingSet(
                resource,
                includeRequiredPeopleAuthAssociationResources: false
            ),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
            );

        var unresolvedFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
        );
        unresolvedFailure.Location?.Kind.Should().Be(SecurableElementKind.EducationOrganization);
        unresolvedFailure.Location?.JsonPath.Should().Be("$.missingSchoolReference.schoolId");

        var missingAuthViewFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
        );
        missingAuthViewFailure
            .ConfiguredStrategy?.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople);
        missingAuthViewFailure.RelationshipLocalOrder.Should().Be(0);
        missingAuthViewFailure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        missingAuthViewFailure.AuthObject.Should().Be(expectedStudentAuthObject);
        missingAuthViewFailure.PersonMetadata.Should().NotBeNull();
        missingAuthViewFailure
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Student);
        missingAuthViewFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        missingAuthViewFailure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
        missingAuthViewFailure.Contributors.Should().ContainSingle();
    }

    [Test]
    public void It_should_report_missing_people_auth_view_associations_when_other_people_paths_are_unresolved()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "PartiallyResolvedStudentAuthorizationResource");
        var expectedStudentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreatePartiallyResolvedStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
            );

        var unresolvedFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
        );
        unresolvedFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        unresolvedFailure.Location?.JsonPath.Should().Be("$.missingStudentReference.studentUniqueId");

        var missingAuthViewFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
        );
        missingAuthViewFailure
            .ConfiguredStrategy?.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        missingAuthViewFailure.RelationshipLocalOrder.Should().Be(0);
        missingAuthViewFailure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        missingAuthViewFailure.AuthObject.Should().Be(expectedStudentAuthObject);
        missingAuthViewFailure.PersonMetadata.Should().NotBeNull();
        missingAuthViewFailure
            .PersonMetadata!.PersonKind.Should()
            .Be(RelationshipAuthorizationPersonKind.Student);
        missingAuthViewFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        missingAuthViewFailure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
        missingAuthViewFailure
            .Contributors.Should()
            .ContainSingle()
            .Which.JsonPath.Should()
            .Be("$.studentReference.studentUniqueId");
    }

    [Test]
    public void It_should_merge_missing_people_auth_view_association_contributors_for_the_same_auth_view()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "MultipleStudentAuthorizationResource");
        var expectedAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMultipleStudentSubjectMappingSet(
                resource,
                includeRequiredPeopleAuthAssociationResources: false
            ),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failure = ((RelationshipAuthorizationResult.SecurityConfigurationError)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations);
        failure.AuthObject.Should().Be(expectedAuthObject);
        failure.PersonMetadata.Should().NotBeNull();
        failure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        failure.Location?.JsonPath.Should().Be("$.studentReference.studentUniqueId");
        failure
            .Contributors.Select(static contributor => contributor.JsonPath)
            .Should()
            .Equal("$.studentReference.studentUniqueId", "$.alternateStudentReference.studentUniqueId");
        failure
            .Contributors.Select(static contributor => contributor.ReadableName)
            .Should()
            .Equal("StudentUniqueId", "StudentUniqueId");
    }

    [Test]
    public void It_should_report_missing_people_auth_view_associations_once_per_people_strategy()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentAuthorizationResource");
        var studentAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var responsibilityAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreatePeopleSubjectMappingSet(resource, SecurableElementKind.Student),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures.Should().HaveCount(2);
        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .OnlyContain(static failureKind =>
                failureKind == RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
            );
        failures
            .Select(static failure => failure.ConfiguredStrategy?.StrategyName)
            .Should()
            .Equal(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
            );
        failures.Select(static failure => failure.RelationshipLocalOrder).Should().Equal(0, 1);
        failures
            .Select(static failure => failure.AuthObject)
            .Should()
            .Equal(studentAuthObject, responsibilityAuthObject);
        failures
            .SelectMany(static failure => failure.Contributors)
            .Select(static contributor => contributor.JsonPath)
            .Should()
            .Equal("$.studentReference.studentUniqueId", "$.studentReference.studentUniqueId");
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

    [TestCaseSource(nameof(PeopleStoredGetManyStrategyCases))]
    public void It_should_plan_stored_get_many_people_strategies_with_selected_subjects(
        string strategyName,
        RelationshipAuthorizationHierarchyDirection expectedDirection,
        IReadOnlyList<ExpectedStoredPeopleSubject> expectedSubjects
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StoredPeopleStrategyMatrixResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedRootEdOrgAndPeopleSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(strategyName),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;

        checkSpec.ConfiguredStrategy.Should().Be(new ConfiguredAuthorizationStrategy(strategyName, 0));
        checkSpec.RelationshipLocalOrder.Should().Be(0);
        checkSpec.Direction.Should().Be(expectedDirection);
        checkSpec.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        checkSpec.CheckTarget.Should().BeOfType<RelationshipAuthorizationCheckTarget.Stored>();
        checkSpec
            .Subjects.Select(static subject =>
                (
                    subject.Contributors.Single().Kind,
                    subject.Column.Value,
                    subject.AuthObject,
                    subject.IsPersonSubject
                )
            )
            .Should()
            .Equal(
                expectedSubjects.Select(static subject =>
                    (
                        subject.Kind,
                        subject.Column.Value,
                        subject.AuthObject,
                        subject.Kind
                            is SecurableElementKind.Student
                                or SecurableElementKind.Contact
                                or SecurableElementKind.Staff
                    )
                )
            );

        foreach (var subject in checkSpec.Subjects.Where(static subject => subject.IsPersonSubject))
        {
            subject.PersonMetadata.Should().NotBeNull();
            subject.AuthObject.AllowsDirectClaimMatch.Should().BeFalse();
            subject.AuthObject.ClaimEducationOrganizationIdColumn.Should().Be(AuthNames.SourceEdOrgId);
        }

        var edOrgSubjects = checkSpec.Subjects.Where(static subject => !subject.IsPersonSubject).ToArray();
        if (edOrgSubjects.Length > 0)
        {
            edOrgSubjects.Should().OnlyContain(static subject => subject.AuthObject.AllowsDirectClaimMatch);
        }
    }

    [TestCaseSource(nameof(PeopleStoredGetManyStrategyCases))]
    public void It_should_plan_proposed_people_strategies_with_selected_subjects_and_root_bindings(
        string strategyName,
        RelationshipAuthorizationHierarchyDirection expectedDirection,
        IReadOnlyList<ExpectedStoredPeopleSubject> expectedSubjects
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", "ProposedPeopleStrategyMatrixResource");
        DbColumnName[] rootBindingColumns =
        [
            Col("SchoolReference_SchoolId"),
            Col("ZStudent_DocumentId"),
            Col("AContact_DocumentId"),
            Col("BStaff_DocumentId"),
        ];
        var mappingSet = CreateMixedRootEdOrgAndPeopleSubjectMappingSet(resource);
        var writePlan = CreateWritePlan(mappingSet, resource, rootBindingColumns);
        var planner = CreatePlanner();

        var result = planner.PlanProposedValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(strategyName),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;
        var proposedTarget = checkSpec
            .CheckTarget.Should()
            .BeOfType<RelationshipAuthorizationCheckTarget.Proposed>()
            .Subject;

        checkSpec.ConfiguredStrategy.Should().Be(new ConfiguredAuthorizationStrategy(strategyName, 0));
        checkSpec.RelationshipLocalOrder.Should().Be(0);
        checkSpec.Direction.Should().Be(expectedDirection);
        checkSpec.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        proposedTarget.RootTable.Should().Be(Table(resource.ResourceName));
        checkSpec
            .Subjects.Select(static subject =>
                (
                    subject.Contributors.Single().Kind,
                    subject.Table,
                    subject.Column,
                    subject.AuthObject,
                    subject.IsPersonSubject
                )
            )
            .Should()
            .Equal(
                expectedSubjects.Select(subject =>
                    (
                        subject.Kind,
                        Table(resource.ResourceName),
                        subject.Column,
                        subject.AuthObject,
                        subject.Kind
                            is SecurableElementKind.Student
                                or SecurableElementKind.Contact
                                or SecurableElementKind.Staff
                    )
                )
            );
        proposedTarget
            .SubjectBindingsInOrder.Select(static binding =>
                (
                    binding.Table,
                    binding.Column,
                    binding.BindingIndex,
                    binding.LogicalKey,
                    binding.ParameterSeed
                )
            )
            .Should()
            .Equal(
                expectedSubjects.Select(subject =>
                {
                    var bindingIndex = Array.IndexOf(rootBindingColumns, subject.Column);

                    return (
                        Table(resource.ResourceName),
                        subject.Column,
                        bindingIndex,
                        subject.Column.Value,
                        subject.Column.Value
                    );
                })
            );

        for (var subjectIndex = 0; subjectIndex < checkSpec.Subjects.Count; subjectIndex++)
        {
            var personSubject = checkSpec.Subjects[subjectIndex];

            if (!personSubject.IsPersonSubject)
            {
                continue;
            }

            personSubject
                .AuthObject.SubjectValueColumn.Should()
                .Be(expectedSubjects[subjectIndex].AuthObject.SubjectValueColumn);
            personSubject
                .AuthObject.ClaimEducationOrganizationIdColumn.Should()
                .Be(expectedSubjects[subjectIndex].AuthObject.ClaimEducationOrganizationIdColumn);
            personSubject
                .AuthObject.FailureHint.Should()
                .Be(expectedSubjects[subjectIndex].AuthObject.FailureHint);
            personSubject.PersonMetadata!.ProposedAnchor.Should().NotBeNull();
            personSubject
                .PersonMetadata.ProposedAnchor!.Kind.Should()
                .Be(RelationshipAuthorizationPersonProposedAnchorKind.RootRow);
            personSubject
                .PersonMetadata.ProposedAnchor.Binding.Should()
                .Be(proposedTarget.SubjectBindingsInOrder[subjectIndex]);
        }

        foreach (var edOrgSubject in checkSpec.Subjects.Where(static subject => !subject.IsPersonSubject))
        {
            edOrgSubject.Table.Should().Be(Table(resource.ResourceName));
            edOrgSubject.AuthObject.AllowsDirectClaimMatch.Should().BeTrue();
            edOrgSubject
                .AuthObject.Should()
                .Be(RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(expectedDirection));
        }
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

    [TestCaseSource(nameof(SelfPersonExistingResourceCases))]
    public void It_should_plan_existing_resource_self_person_proposed_specs_with_target_document_id_anchors(
        SecurableElementKind securableElementKind,
        string strategyName
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", GetPersonResourceName(securableElementKind));
        var mappingSet = CreateSelfPersonMappingSet(securableElementKind);
        var writePlan = CreateWritePlan(mappingSet, resource, _documentId);
        var planner = CreatePlanner();

        var result = planner.PlanUpdateValues(
            mappingSet,
            resource,
            CreateConfiguredAuthorizationStrategies(strategyName),
            new RelationalAuthorizationContext([42L], []),
            writePlan
        );

        result.ProposedValues.Should().BeOfType<RelationshipAuthorizationResult.Authorized>();

        var checkSpec = ((RelationshipAuthorizationResult.Authorized)result.ProposedValues)
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject;
        var subject = checkSpec.Subjects.Should().ContainSingle().Subject;
        var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)checkSpec.CheckTarget;

        subject
            .PersonMetadata!.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId);
        subject
            .PersonMetadata.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId);
        subject.PersonMetadata.ProposedAnchor.Binding.Table.Should().Be(Table(resource.ResourceName));
        subject.PersonMetadata.ProposedAnchor.Binding.Column.Should().Be(_documentId);
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
    public void It_should_preserve_skipped_child_collection_people_metadata_when_no_subject_can_execute()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var mappingSet = CreateStudentSelfPersonAndChildStudentMappingSet();
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
        failure
            .IneligibleSubjects.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(
                RelationshipAuthorizationSubjectIneligibilityReason.SelfPersonDocumentIdUnavailableForCreateNew
            );

        var skippedContributor = failure.SkippedContributors.Should().ContainSingle().Subject;

        skippedContributor.JsonPath.Should().Be("$.studentReferences[*].studentReference.studentUniqueId");
        skippedContributor.ReadableName.Should().Be("StudentUniqueId");
        skippedContributor
            .Reason.Should()
            .Be(RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope);
        skippedContributor.Table.Should().Be(Table("StudentStudentReference"));
        skippedContributor.Column.Should().Be(Col("StudentReference_DocumentId"));
    }

    [Test]
    public void It_should_not_report_missing_people_auth_view_associations_for_ineligible_self_person_create_new_subjects()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var mappingSet = CreateSelfPersonMappingSet(
            SecurableElementKind.Student,
            includeRequiredPeopleAuthAssociationResources: false
        );
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

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures.Should().ContainSingle();
        failures[0].FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoExecutableSubjects);
        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .NotContain(RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations);
    }

    [Test]
    public void It_should_not_report_missing_people_auth_view_associations_for_ineligible_self_person_create_new_subjects_when_subject_selection_fails()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var mappingSet = CreateSelfPersonAndUnresolvedStudentMappingSet(
            includeRequiredPeopleAuthAssociationResources: false
        );
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

        var failures = ((RelationshipAuthorizationResult.SecurityConfigurationError)result).Failures;

        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
                RelationshipAuthorizationFailureKind.NoExecutableSubjects
            );
        failures
            .Select(static failure => failure.FailureKind)
            .Should()
            .NotContain(RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations);

        var unresolvedFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
        );
        unresolvedFailure.Location?.Kind.Should().Be(SecurableElementKind.Student);
        unresolvedFailure.Location?.JsonPath.Should().Be("$.missingStudentReference.studentUniqueId");

        var noExecutableSubjectsFailure = failures.Single(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.NoExecutableSubjects
        );
        noExecutableSubjectsFailure
            .IneligibleSubjects.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(
                RelationshipAuthorizationSubjectIneligibilityReason.SelfPersonDocumentIdUnavailableForCreateNew
            );
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
    public void It_should_preserve_skipped_child_collection_people_metadata_when_mixed_strategy_remains_executable()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "RootEdOrgChildStudentAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMixedRootEdOrgAndChildStudentSubjectMappingSet(resource),
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

        checkSpec.Subjects.Should().ContainSingle().Which.IsPersonSubject.Should().BeFalse();

        var skippedContributor = checkSpec.SkippedContributors.Should().ContainSingle().Subject;

        skippedContributor.Kind.Should().Be(SecurableElementKind.Student);
        skippedContributor.JsonPath.Should().Be("$.studentReferences[*].studentReference.studentUniqueId");
        skippedContributor.ReadableName.Should().Be("StudentUniqueId");
        skippedContributor.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        skippedContributor
            .AuthObject.Should()
            .Be(
                RelationshipAuthorizationAuthObject.CreatePerson(
                    RelationshipAuthorizationPersonAuthViewKind.Student
                )
            );
        skippedContributor
            .Reason.Should()
            .Be(RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope);
        skippedContributor.Table.Should().Be(Table($"{resource.ResourceName}StudentReference"));
        skippedContributor.Column.Should().Be(Col("StudentReference_DocumentId"));
    }

    [Test]
    public void It_should_keep_people_only_child_collection_paths_as_no_applicable_subject_failures()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "ChildStudentAuthorizationResource");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateChildStudentSubjectMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failure = ((RelationshipAuthorizationResult.SecurityConfigurationError)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoApplicableRootSubject);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location.JsonPath.Should().Be("$.studentReferences[*].studentReference.studentUniqueId");
        failure
            .SkippedContributors.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(RelationshipAuthorizationSkippedSubjectReason.ChildCollectionPersonPathOutsideSubjectScope);
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
    public void It_should_preserve_unresolved_people_contribution_order_after_planner_failure_aggregation()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "UnresolvedStudentCarrier");
        const string firstConfiguredPath = "$.zStudentReference.studentUniqueId";
        const string secondConfiguredPath = "$.aStudentReference.studentUniqueId";
        var mappingSet = CreateUnresolvedStudentPathOrderingMappingSet(
            resource,
            firstConfiguredPath,
            secondConfiguredPath
        );
        var configuredStrategies = CreateConfiguredAuthorizationStrategies(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        );
        var authorizationContext = new RelationalAuthorizationContext([42L], []);
        var writePlan = CreateMinimalWritePlan(mappingSet, resource);
        var planner = CreatePlanner();

        AssertUnresolvedPeopleFailureOrder(
            GetSecurityConfigurationFailures(
                planner.PlanStoredValues(mappingSet, resource, configuredStrategies, authorizationContext)
            ),
            (firstConfiguredPath, 0),
            (secondConfiguredPath, 1)
        );
        AssertUnresolvedPeopleFailureOrder(
            GetSecurityConfigurationFailures(
                planner.PlanProposedValues(
                    mappingSet,
                    resource,
                    configuredStrategies,
                    authorizationContext,
                    writePlan
                )
            ),
            (firstConfiguredPath, 0),
            (secondConfiguredPath, 1)
        );

        var updatePlan = planner.PlanUpdateValues(
            mappingSet,
            resource,
            configuredStrategies,
            authorizationContext,
            writePlan
        );

        AssertUnresolvedPeopleFailureOrder(
            updatePlan.SecurityConfigurationFailures,
            (firstConfiguredPath, 0),
            (secondConfiguredPath, 1)
        );
        AssertUnresolvedPeopleFailureOrder(
            GetSecurityConfigurationFailures(updatePlan.StoredValues),
            (firstConfiguredPath, 0),
            (secondConfiguredPath, 1)
        );
        AssertUnresolvedPeopleFailureOrder(
            GetSecurityConfigurationFailures(updatePlan.ProposedValues),
            (firstConfiguredPath, 0),
            (secondConfiguredPath, 1)
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
                AuthorizationStrategyNameConstants.OwnershipBased
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
                AuthorizationStrategyNameConstants.OwnershipBased
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
    public void It_should_not_duplicate_no_applicable_people_failures_from_selector()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentlessAuthorizationResource");
        var expectedAuthObject = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            ),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>();

        var failure = ((RelationshipAuthorizationResult.SecurityConfigurationError)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoApplicableRootSubject);
        failure
            .ConfiguredStrategy?.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        failure.RelationshipLocalOrder.Should().Be(0);
        failure.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Stored);
        failure.AuthObject.Should().Be(expectedAuthObject);
        failure.Location!.Kind.Should().Be(SecurableElementKind.Student);
        failure.Location.AuthorizationObjectName.Should().Be(expectedAuthObject.Name.ToString());
        failure.PersonMetadata!.PersonKind.Should().Be(RelationshipAuthorizationPersonKind.Student);
        failure.PersonMetadata.AuthObject.Should().Be(expectedAuthObject);
    }

    [Test]
    public void It_should_map_known_but_not_enabled_outcomes_to_shared_failure_metadata()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(resource),
            resource,
            CreateConfiguredAuthorizationStrategies(AuthorizationStrategyNameConstants.OwnershipBased),
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
            .Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    [Test]
    public void It_should_keep_resolved_custom_view_strategies_as_known_but_not_enabled()
    {
        var targetResource = new QualifiedResourceName("Ed-Fi", "School");
        var basisResource = new QualifiedResourceName("Ed-Fi", "Student");
        var strategyName = "StudentWithSchoolAuthorization";
        var planner = CreatePlanner();

        var result = planner.PlanStoredValues(
            CreateMinimalMappingSet(targetResource, basisResource),
            targetResource,
            CreateConfiguredAuthorizationStrategies(strategyName),
            new RelationalAuthorizationContext([42L], [])
        );

        result.Should().BeOfType<RelationshipAuthorizationResult.KnownButNotEnabled>();

        var failure = ((RelationshipAuthorizationResult.KnownButNotEnabled)result)
            .Failures.Should()
            .ContainSingle()
            .Subject;

        failure.FailureKind.Should().Be(RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy);
        failure.ConfiguredStrategy?.StrategyName.Should().Be(strategyName);
        failure.RelationshipLocalOrder.Should().Be(0);
        failure.Location?.AuthorizationObjectName.Should().Be("Ed-Fi.Student");
        failure.Hint.Should().Contain("Basis resource: 'Ed-Fi.Student'");
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
                AuthorizationStrategyNameConstants.OwnershipBased,
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
            .Be(AuthorizationStrategyNameConstants.OwnershipBased);
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

    private static MappingSet CreateMixedRootEdOrgAndPeopleSubjectMappingSet(QualifiedResourceName resource)
    {
        const string schoolIdPath = "$.schoolReference.schoolId";
        const string studentPath = "$.zStudentReference.studentUniqueId";
        const string contactPath = "$.aContactReference.contactUniqueId";
        const string staffPath = "$.bStaffReference.staffUniqueId";
        var studentDocumentIdColumn = Col("ZStudent_DocumentId");
        var contactDocumentIdColumn = Col("AContact_DocumentId");
        var staffDocumentIdColumn = Col("BStaff_DocumentId");
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
                CreatePersonDocumentIdColumn(
                    studentDocumentIdColumn,
                    studentPath,
                    SecurableElementKind.Student
                ),
                CreatePersonDocumentIdColumn(
                    contactDocumentIdColumn,
                    contactPath,
                    SecurableElementKind.Contact
                ),
                CreatePersonDocumentIdColumn(staffDocumentIdColumn, staffPath, SecurableElementKind.Staff),
            ]
        );
        var concreteResource = CreateConcrete(
            resource.ResourceName,
            CreateModelWithTables(
                resource.ResourceName,
                rootTable,
                [
                    CreatePersonReferenceBinding(
                        rootTable,
                        studentDocumentIdColumn,
                        studentPath,
                        SecurableElementKind.Student
                    ),
                    CreatePersonReferenceBinding(
                        rootTable,
                        contactDocumentIdColumn,
                        contactPath,
                        SecurableElementKind.Contact
                    ),
                    CreatePersonReferenceBinding(
                        rootTable,
                        staffDocumentIdColumn,
                        staffPath,
                        SecurableElementKind.Staff
                    ),
                ]
            ),
            new ResourceSecurableElements(
                [new EdOrgSecurableElement(schoolIdPath, "SchoolId")],
                [],
                [studentPath],
                [contactPath],
                [staffPath]
            )
        );

        return CreateMappingSet([
            concreteResource,
            CreatePersonResource(SecurableElementKind.Student),
            CreatePersonResource(SecurableElementKind.Contact),
            CreatePersonResource(SecurableElementKind.Staff),
            .. CreateRequiredPeopleAuthAssociationResources(),
        ]);
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

    private static MappingSet CreateMixedRootEdOrgAndChildStudentSubjectMappingSet(
        QualifiedResourceName resource
    ) => CreateChildStudentSubjectMappingSet(resource, includeRootEdOrgSubject: true);

    private static MappingSet CreateChildStudentSubjectMappingSet(
        QualifiedResourceName resource,
        bool includeRootEdOrgSubject = false
    )
    {
        const string schoolIdPath = "$.schoolReference.schoolId";
        const string childStudentPath = "$.studentReferences[*].studentReference.studentUniqueId";
        var childTableName = Table($"{resource.ResourceName}StudentReference");
        var rootTable = CreateRootTable(
            Table(resource.ResourceName),
            includeRootEdOrgSubject
                ?
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
                : []
        );
        var childTable = CreateChildTable(
            childTableName,
            "$.studentReferences[*]",
            [
                new DbColumnModel(
                    Col("StudentReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(childStudentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );
        var concreteResource = CreateConcrete(
            resource.ResourceName,
            CreateModelWithTables(
                resource.ResourceName,
                rootTable,
                [childTable],
                [
                    new DocumentReferenceBinding(
                        true,
                        Path(GetReferenceObjectPath(childStudentPath)),
                        childTableName,
                        Col("StudentReference_DocumentId"),
                        new QualifiedResourceName("Ed-Fi", "Student"),
                        [
                            new ReferenceIdentityBinding(
                                Path(childStudentPath),
                                Path(childStudentPath),
                                Col("StudentUniqueId")
                            ),
                        ]
                    ),
                ]
            ),
            new ResourceSecurableElements(
                includeRootEdOrgSubject ? [new EdOrgSecurableElement(schoolIdPath, "SchoolId")] : [],
                [],
                [childStudentPath],
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

    private static MappingSet CreateMixedUnresolvedEdOrgAndRootStudentSubjectMappingSet(
        QualifiedResourceName resource,
        bool includeRequiredPeopleAuthAssociationResources = true
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
            studentPath,
            includeRequiredPeopleAuthAssociationResources
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

    private static MappingSet CreateSelfPersonMappingSet(
        SecurableElementKind securableElementKind,
        bool includeRequiredPeopleAuthAssociationResources = true
    )
    {
        List<ConcreteResourceModel> concreteResources = [CreatePersonResource(securableElementKind)];

        if (includeRequiredPeopleAuthAssociationResources)
        {
            concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources());
        }

        return CreateMappingSet(concreteResources);
    }

    private static MappingSet CreateSelfPersonAndUnresolvedStudentMappingSet(
        bool includeRequiredPeopleAuthAssociationResources = true
    )
    {
        const string missingStudentPath = "$.missingStudentReference.studentUniqueId";
        List<ConcreteResourceModel> concreteResources =
        [
            CreateConcrete(
                "Student",
                CreateModelWithTables("Student", CreateRootTable(Table("Student")), []),
                new ResourceSecurableElements(
                    [],
                    [],
                    [GetPersonSelfJsonPath(SecurableElementKind.Student), missingStudentPath],
                    [],
                    []
                )
            ),
        ];

        if (includeRequiredPeopleAuthAssociationResources)
        {
            concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources());
        }

        return CreateMappingSet(concreteResources);
    }

    private static MappingSet CreateStudentSelfPersonAndChildStudentMappingSet()
    {
        const string childStudentPath = "$.studentReferences[*].studentReference.studentUniqueId";
        var childTableName = Table("StudentStudentReference");
        var childTable = CreateChildTable(
            childTableName,
            "$.studentReferences[*]",
            [
                new DbColumnModel(
                    Col("StudentReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    Path(childStudentPath),
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ]
        );

        return CreateMappingSet(
            CreateConcrete(
                "Student",
                CreateModelWithTables(
                    "Student",
                    CreateRootTable(Table("Student")),
                    [childTable],
                    [
                        new DocumentReferenceBinding(
                            true,
                            Path(GetReferenceObjectPath(childStudentPath)),
                            childTableName,
                            Col("StudentReference_DocumentId"),
                            new QualifiedResourceName("Ed-Fi", "Student"),
                            [
                                new ReferenceIdentityBinding(
                                    Path(childStudentPath),
                                    Path(childStudentPath),
                                    Col("StudentUniqueId")
                                ),
                            ]
                        ),
                    ]
                ),
                new ResourceSecurableElements(
                    [],
                    [],
                    [GetPersonSelfJsonPath(SecurableElementKind.Student), childStudentPath],
                    [],
                    []
                )
            )
        );
    }

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
        string studentPath,
        bool includeRequiredPeopleAuthAssociationResources = true
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

        List<ConcreteResourceModel> concreteResources =
        [
            concreteResource,
            CreatePersonResource(SecurableElementKind.Student),
        ];

        if (includeRequiredPeopleAuthAssociationResources)
        {
            concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources());
        }

        return CreateMappingSet(concreteResources);
    }

    private static MappingSet CreateMinimalMappingSet(
        QualifiedResourceName resource,
        params QualifiedResourceName[] additionalResources
    ) =>
        CreateMappingSet([
            .. new[] { resource }
                .Concat(additionalResources)
                .Select(static resource =>
                    CreateConcrete(
                        resource.ResourceName,
                        CreateModelWithTables(
                            resource.ResourceName,
                            CreateRootTable(Table(resource.ResourceName)),
                            []
                        ),
                        new ResourceSecurableElements([], [], [], [], [])
                    )
                ),
        ]);

    private static DbColumnModel CreatePersonDocumentIdColumn(
        DbColumnName documentIdColumn,
        string jsonPath,
        SecurableElementKind securableElementKind
    ) =>
        new(
            documentIdColumn,
            ColumnKind.DocumentFk,
            null,
            false,
            Path(jsonPath),
            new QualifiedResourceName("Ed-Fi", GetPersonResourceName(securableElementKind))
        );

    private static DocumentReferenceBinding CreatePersonReferenceBinding(
        DbTableModel rootTable,
        DbColumnName documentIdColumn,
        string jsonPath,
        SecurableElementKind securableElementKind
    ) =>
        new(
            true,
            Path(GetReferenceObjectPath(jsonPath)),
            rootTable.Table,
            documentIdColumn,
            new QualifiedResourceName("Ed-Fi", GetPersonResourceName(securableElementKind)),
            [
                new ReferenceIdentityBinding(
                    Path(jsonPath),
                    Path(jsonPath),
                    Col($"{GetPersonResourceName(securableElementKind)}UniqueId")
                ),
            ]
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

    private static MappingSet CreatePartiallyResolvedStudentSubjectMappingSet(QualifiedResourceName resource)
    {
        const string studentPath = "$.studentReference.studentUniqueId";
        const string missingStudentPath = "$.missingStudentReference.studentUniqueId";
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
                ]
            ),
            new ResourceSecurableElements([], [], [studentPath, missingStudentPath], [], [])
        );

        return CreateMappingSet([concreteResource, CreatePersonResource(SecurableElementKind.Student)]);
    }

    private static MappingSet CreateUnresolvedStudentPathOrderingMappingSet(
        QualifiedResourceName resource,
        string firstConfiguredPath,
        string secondConfiguredPath
    ) =>
        CreateMappingSet(
            CreateConcrete(
                resource.ResourceName,
                CreateModelWithTables(
                    resource.ResourceName,
                    CreateRootTable(Table(resource.ResourceName)),
                    []
                ),
                new ResourceSecurableElements([], [], [firstConfiguredPath, secondConfiguredPath], [], [])
            )
        );

    private static MappingSet CreateMultipleStudentSubjectMappingSet(
        QualifiedResourceName resource,
        bool includeRequiredPeopleAuthAssociationResources = true
    )
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

        List<ConcreteResourceModel> concreteResources =
        [
            concreteResource,
            CreatePersonResource(SecurableElementKind.Student),
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

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> GetSecurityConfigurationFailures(
        RelationshipAuthorizationResult result
    ) =>
        result
            .Should()
            .BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>()
            .Subject.Failures;

    private static void AssertUnresolvedPeopleFailureOrder(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures,
        params (string JsonPath, int ContributionOrder)[] expectedOrder
    ) =>
        failures
            .Select(static failure =>
                (failure.Location!.JsonPath!, failure.Contributors.Single().ContributionOrder)
            )
            .Should()
            .Equal(expectedOrder);

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

    private static SupportedRelationshipAuthorizationStrategy CreateSupportedStrategy(
        ConfiguredAuthorizationStrategy configuredStrategy
    ) =>
        new(
            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            configuredStrategy,
            0,
            [new(SecurableElementKind.EducationOrganization)]
        );

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
                TriggersInCreateOrder: [],
                AuthEdOrgHierarchy: CreateAuthEdOrgHierarchy()
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

    private static AuthEdOrgHierarchy CreateAuthEdOrgHierarchy() =>
        new([
            new AuthEdOrgEntity(
                "School",
                new DbTableName(_edfiSchema, "School"),
                new DbColumnName("SchoolId"),
                []
            ),
        ]);

    private static RelationshipAuthorizationPlanner CreatePlanner() => new(CreateSelector());

    private static RelationalEdOrgAuthorizationSubjectSelector CreateSelector() =>
        new(new RelationalEdOrgAuthorizationElementResolutionCache());

    private static IEnumerable<TestCaseData> PeopleStoredGetManyStrategyCases()
    {
        var edOrgNormal = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Normal
        );
        var edOrgInverted = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
            RelationshipAuthorizationHierarchyDirection.Inverted
        );
        var student = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Student
        );
        var contact = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Contact
        );
        var staff = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.Staff
        );
        var studentThroughResponsibility = RelationshipAuthorizationAuthObject.CreatePerson(
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
        );

        ExpectedStoredPeopleSubject[] mixedNormalSubjects =
        [
            new(SecurableElementKind.EducationOrganization, Col("SchoolReference_SchoolId"), edOrgNormal),
            new(SecurableElementKind.Student, Col("ZStudent_DocumentId"), student),
            new(SecurableElementKind.Contact, Col("AContact_DocumentId"), contact),
            new(SecurableElementKind.Staff, Col("BStaff_DocumentId"), staff),
        ];
        ExpectedStoredPeopleSubject[] mixedInvertedSubjects =
        [
            new(SecurableElementKind.EducationOrganization, Col("SchoolReference_SchoolId"), edOrgInverted),
            new(SecurableElementKind.Student, Col("ZStudent_DocumentId"), student),
            new(SecurableElementKind.Contact, Col("AContact_DocumentId"), contact),
            new(SecurableElementKind.Staff, Col("BStaff_DocumentId"), staff),
        ];
        ExpectedStoredPeopleSubject[] peopleOnlySubjects =
        [
            new(SecurableElementKind.Student, Col("ZStudent_DocumentId"), student),
            new(SecurableElementKind.Contact, Col("AContact_DocumentId"), contact),
            new(SecurableElementKind.Staff, Col("BStaff_DocumentId"), staff),
        ];
        ExpectedStoredPeopleSubject[] studentsOnlySubjects =
        [
            new(SecurableElementKind.Student, Col("ZStudent_DocumentId"), student),
        ];
        ExpectedStoredPeopleSubject[] studentsOnlyThroughResponsibilitySubjects =
        [
            new(SecurableElementKind.Student, Col("ZStudent_DocumentId"), studentThroughResponsibility),
        ];

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
            RelationshipAuthorizationHierarchyDirection.Normal,
            mixedNormalSubjects
        ).SetName("RelationshipsWithEdOrgsAndPeople");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
            RelationshipAuthorizationHierarchyDirection.Inverted,
            mixedInvertedSubjects
        ).SetName("RelationshipsWithEdOrgsAndPeopleInverted");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            peopleOnlySubjects
        ).SetName("RelationshipsWithPeopleOnly");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            studentsOnlySubjects
        ).SetName("RelationshipsWithStudentsOnly");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
            RelationshipAuthorizationHierarchyDirection.Normal,
            studentsOnlyThroughResponsibilitySubjects
        ).SetName("RelationshipsWithStudentsOnlyThroughResponsibility");
    }

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

    private static IEnumerable<TestCaseData> SelfPersonExistingResourceCases()
    {
        yield return new TestCaseData(
            SecurableElementKind.Student,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
        ).SetName("Student");

        yield return new TestCaseData(
            SecurableElementKind.Contact,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        ).SetName("Contact");

        yield return new TestCaseData(
            SecurableElementKind.Staff,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly
        ).SetName("Staff");
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

    public sealed record ExpectedStoredPeopleSubject(
        SecurableElementKind Kind,
        DbColumnName Column,
        RelationshipAuthorizationAuthObject AuthObject
    );
}
