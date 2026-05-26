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
public class Given_RelationshipAuthorizationStrategyClassifier
{
    private static readonly QualifiedResourceName _queryResource = new("Ed-Fi", "School");

    [Test]
    public void It_classifies_supported_normal_and_inverted_edorg_strategies_in_input_order()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
        );

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SupportedStrategies);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted
            );
        classification
            .SupportedStrategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        classification.KnownButNotEnabledStrategies.Should().BeEmpty();
        classification.SecurityConfigurationFailures.Should().BeEmpty();
        classification.NoFurtherAuthorizationRequiredStrategies.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_duplicate_supported_edorg_strategies_as_distinct_or_entries()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
        );

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SupportedStrategies);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted,
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted
            );
        classification
            .SupportedStrategies.Select(static strategy => strategy.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(0, 1, 2, 3);
        classification
            .SupportedStrategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1, 2, 3);
    }

    [Test]
    public void It_emits_an_explicit_no_further_authorization_required_outcome_when_that_is_the_only_effective_strategy()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
        );

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.NoFurtherAuthorizationRequired);
        classification.SupportedStrategies.Should().BeEmpty();
        classification.KnownButNotEnabledStrategies.Should().BeEmpty();
        classification.SecurityConfigurationFailures.Should().BeEmpty();
        classification
            .NoFurtherAuthorizationRequiredStrategies.Select(static strategy => strategy.RawConfiguredIndex)
            .Should()
            .Equal(0);
    }

    [Test]
    public void It_retains_no_further_authorization_required_metadata_when_supported_strategies_exist()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SupportedStrategies);
        classification.NoFurtherAuthorizationRequiredStrategies.Should().ContainSingle();
        classification.NoFurtherAuthorizationRequiredStrategies[0].RawConfiguredIndex.Should().Be(0);
        classification.SupportedStrategies.Should().ContainSingle();
        classification.SupportedStrategies[0].ConfiguredStrategy.RawConfiguredIndex.Should().Be(1);
        classification.SupportedStrategies[0].RelationshipLocalOrder.Should().Be(0);
    }

    [Test]
    public void It_selects_people_relationship_strategies_with_classifier_relationship_local_order()
    {
        ConfiguredAuthorizationStrategy[] configuredAuthorizationStrategies =
        [
            new(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired, 0),
            new(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople, 1),
            new(AuthorizationStrategyNameConstants.NamespaceBased, 2),
            new(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly, 3),
        ];

        var peopleStrategies = RelationshipAuthorizationStrategyClassifier.SelectPeopleRelationshipStrategies(
            configuredAuthorizationStrategies
        );

        peopleStrategies
            .Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeople,
                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnly
            );
        peopleStrategies
            .Select(static strategy => strategy.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(1, 3);
        peopleStrategies.Select(static strategy => strategy.RelationshipLocalOrder).Should().Equal(0, 2);
    }

    [Test]
    public void It_classifies_known_out_of_scope_mixed_strategies_as_known_but_not_enabled()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            AuthorizationStrategyNameConstants.NamespaceBased,
            AuthorizationStrategyNameConstants.OwnershipBased,
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
        );

        classification.Outcome.Should().Be(RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly);
        classification
            .KnownButNotEnabledStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                RelationshipAuthorizationStrategyKind.NamespaceBased,
                RelationshipAuthorizationStrategyKind.OwnershipBased
            );
        classification
            .KnownButNotEnabledStrategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(1, 2);
        classification.NoFurtherAuthorizationRequiredStrategies.Should().ContainSingle();
        classification.SecurityConfigurationFailures.Should().BeEmpty();
    }

    [TestCaseSource(nameof(PeopleStrategyCases))]
    public void It_classifies_people_relationship_strategies_as_supported_with_subject_kind_eligibility(
        string strategyName,
        RelationshipAuthorizationStrategyKind expectedKind,
        RelationshipAuthorizationHierarchyDirection expectedDirection,
        RelationshipAuthorizationStrategySubjectEligibility[] expectedEligibleSubjects
    )
    {
        var classification = Classify(CreateMappingSet(_queryResource), strategyName);

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SupportedStrategies);
        classification.NoFurtherAuthorizationRequiredStrategies.Should().BeEmpty();
        classification.SecurityConfigurationFailures.Should().BeEmpty();
        classification.KnownButNotEnabledStrategies.Should().BeEmpty();
        classification.SupportedStrategies.Should().ContainSingle();

        var supportedStrategy = classification.SupportedStrategies[0];
        supportedStrategy.Kind.Should().Be(expectedKind);
        supportedStrategy.Direction.Should().Be(expectedDirection);
        supportedStrategy.ConfiguredStrategy.RawConfiguredIndex.Should().Be(0);
        supportedStrategy.RelationshipLocalOrder.Should().Be(0);
        supportedStrategy.EligibleSubjects.Should().Equal(expectedEligibleSubjects);
    }

    [Test]
    public void It_preserves_duplicate_supported_people_strategies_as_distinct_or_entries()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
        );

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SupportedStrategies);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
                RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility,
                RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility
            );
        classification
            .SupportedStrategies.Select(static strategy => strategy.ConfiguredStrategy.RawConfiguredIndex)
            .Should()
            .Equal(0, 1, 2, 3);
        classification
            .SupportedStrategies.Select(static strategy => strategy.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1, 2, 3);
        classification
            .SupportedStrategies[2]
            .EligibleSubjects.Should()
            .Equal(
                new RelationshipAuthorizationStrategySubjectEligibility(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
                )
            );
    }

    [Test]
    public void It_prefers_the_standard_edfi_basis_resource_when_custom_view_homographs_exist()
    {
        var classification = Classify(
            CreateMappingSet(new("Sample", "School"), new("Ed-Fi", "School")),
            "SchoolWithSomething"
        );

        classification.Outcome.Should().Be(RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled);
        classification.KnownButNotEnabledStrategies.Should().ContainSingle();
        classification
            .KnownButNotEnabledStrategies[0]
            .BasisResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "School"));
    }

    [Test]
    public void It_accepts_descriptor_basis_resources_for_custom_view_classification()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource, new("Ed-Fi", "SchoolCategoryDescriptor")),
            "SchoolCategoryDescriptorWithSomething"
        );

        classification.Outcome.Should().Be(RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled);
        classification.KnownButNotEnabledStrategies.Should().ContainSingle();
        classification
            .KnownButNotEnabledStrategies[0]
            .BasisResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor"));
    }

    [Test]
    public void It_preserves_known_but_not_enabled_metadata_when_security_configuration_errors_win()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.NamespaceBased,
            "CustomAuthorizationStrategy"
        );

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError);
        classification.KnownButNotEnabledStrategies.Should().ContainSingle();
        classification
            .KnownButNotEnabledStrategies[0]
            .Kind.Should()
            .Be(RelationshipAuthorizationStrategyKind.NamespaceBased);
        classification.SecurityConfigurationFailures.Should().ContainSingle();
        classification
            .SecurityConfigurationFailures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy);
        classification.SecurityConfigurationFailures[0].RelationshipLocalOrder.Should().Be(1);
    }

    [Test]
    public void It_rejects_custom_view_names_with_unknown_basis_resources_as_security_configuration_errors()
    {
        var classification = Classify(CreateMappingSet(_queryResource), "UnknownBasisWithSomething");

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError);
        classification.SecurityConfigurationFailures.Should().ContainSingle();
        classification
            .SecurityConfigurationFailures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource);
        classification
            .SecurityConfigurationFailures[0]
            .Location.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationFailureLocation(AuthorizationObjectName: "UnknownBasis")
            );
    }

    [Test]
    public void It_rejects_non_convention_strategy_names_as_security_configuration_errors()
    {
        var classification = Classify(CreateMappingSet(_queryResource), "CustomAuthorizationStrategy");

        classification
            .Outcome.Should()
            .Be(RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError);
        classification.SecurityConfigurationFailures.Should().ContainSingle();
        classification
            .SecurityConfigurationFailures[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy);
        classification.SecurityConfigurationFailures[0].Hint.Should().Contain("{BasisResource}With...");
    }

    private static RelationshipAuthorizationClassification Classify(
        MappingSet mappingSet,
        params string[] strategyNames
    )
    {
        ConfiguredAuthorizationStrategy[] configuredAuthorizationStrategies =
        [
            .. strategyNames.Select(
                static (strategyName, index) => new ConfiguredAuthorizationStrategy(strategyName, index)
            ),
        ];

        return RelationshipAuthorizationStrategyClassifier.Classify(
            mappingSet,
            _queryResource,
            configuredAuthorizationStrategies
        );
    }

    private static IEnumerable<TestCaseData> PeopleStrategyCases()
    {
        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeople,
            RelationshipAuthorizationHierarchyDirection.Normal,
            new[]
            {
                EdOrgEligibility(),
                PersonEligibility(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                PersonEligibility(
                    SecurableElementKind.Contact,
                    RelationshipAuthorizationPersonAuthViewKind.Contact
                ),
                PersonEligibility(
                    SecurableElementKind.Staff,
                    RelationshipAuthorizationPersonAuthViewKind.Staff
                ),
            }
        ).SetName("RelationshipsWithEdOrgsAndPeople");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
            RelationshipAuthorizationStrategyKind.RelationshipsWithEdOrgsAndPeopleInverted,
            RelationshipAuthorizationHierarchyDirection.Inverted,
            new[]
            {
                EdOrgEligibility(),
                PersonEligibility(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                PersonEligibility(
                    SecurableElementKind.Contact,
                    RelationshipAuthorizationPersonAuthViewKind.Contact
                ),
                PersonEligibility(
                    SecurableElementKind.Staff,
                    RelationshipAuthorizationPersonAuthViewKind.Staff
                ),
            }
        ).SetName("RelationshipsWithEdOrgsAndPeopleInverted");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            RelationshipAuthorizationStrategyKind.RelationshipsWithPeopleOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            new[]
            {
                PersonEligibility(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
                PersonEligibility(
                    SecurableElementKind.Contact,
                    RelationshipAuthorizationPersonAuthViewKind.Contact
                ),
                PersonEligibility(
                    SecurableElementKind.Staff,
                    RelationshipAuthorizationPersonAuthViewKind.Staff
                ),
            }
        ).SetName("RelationshipsWithPeopleOnly");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnly,
            RelationshipAuthorizationHierarchyDirection.Normal,
            new[]
            {
                PersonEligibility(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.Student
                ),
            }
        ).SetName("RelationshipsWithStudentsOnly");

        yield return new TestCaseData(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
            RelationshipAuthorizationStrategyKind.RelationshipsWithStudentsOnlyThroughResponsibility,
            RelationshipAuthorizationHierarchyDirection.Normal,
            new[]
            {
                PersonEligibility(
                    SecurableElementKind.Student,
                    RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility
                ),
            }
        ).SetName("RelationshipsWithStudentsOnlyThroughResponsibility");
    }

    private static RelationshipAuthorizationStrategySubjectEligibility EdOrgEligibility() =>
        new(SecurableElementKind.EducationOrganization);

    private static RelationshipAuthorizationStrategySubjectEligibility PersonEligibility(
        SecurableElementKind kind,
        RelationshipAuthorizationPersonAuthViewKind authViewKind
    ) => new(kind, authViewKind);

    private static MappingSet CreateMappingSet(params QualifiedResourceName[] resources)
    {
        var effectiveResources = resources.Length == 0 ? [_queryResource] : resources;
        List<ProjectSchemaInfo> projectSchemasInEndpointOrder = [];
        List<SchemaComponentInfo> schemaComponentsInEndpointOrder = [];
        HashSet<string> seenProjectNames = [];

        foreach (var resource in effectiveResources)
        {
            if (!seenProjectNames.Add(resource.ProjectName))
            {
                continue;
            }

            var isExtensionProject = !string.Equals(resource.ProjectName, "Ed-Fi", StringComparison.Ordinal);
            var endpointName = resource.ProjectName.Equals("Ed-Fi", StringComparison.Ordinal)
                ? "ed-fi"
                : resource.ProjectName.ToLowerInvariant();

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
            .. effectiveResources.Select(
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
}
