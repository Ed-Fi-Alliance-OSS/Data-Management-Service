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
            .CheckSpecs.Select(static checkSpec => checkSpec.AuthObject)
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
            .CheckSpecs.Select(static checkSpec => checkSpec.AuthObject.AllowsDirectClaimMatch)
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
            .CheckSpecs.Select(static checkSpec => checkSpec.AuthObject)
            .Should()
            .OnlyContain(static authObject =>
                authObject.Equals(
                    RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                        RelationshipAuthorizationHierarchyDirection.Normal
                    )
                )
            );
        authorizedResult
            .CheckSpecs.Select(static checkSpec => checkSpec.AuthObject.AllowsDirectClaimMatch)
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
                        relationshipLocalOrder
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

    private static RelationalResourceModel CreateModelWithTables(
        string resource,
        DbTableModel rootTable,
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
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

    private static MappingSet CreateMappingSet(ConcreteResourceModel concreteResourceModel)
    {
        var resourceKeysInIdOrder = new[] { concreteResourceModel.ResourceKey };

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
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
                ConcreteResourcesInNameOrder: [concreteResourceModel],
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
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static RelationshipAuthorizationPlanner CreatePlanner() => new(CreateSelector());

    private static RelationalEdOrgAuthorizationSubjectSelector CreateSelector() =>
        new(new RelationalEdOrgAuthorizationElementResolutionCache());
}
