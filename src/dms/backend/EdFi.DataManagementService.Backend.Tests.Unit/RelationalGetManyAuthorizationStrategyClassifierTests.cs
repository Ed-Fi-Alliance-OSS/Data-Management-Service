// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalGetManyAuthorizationStrategyClassifier
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
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.SupportedRelationshipStrategies);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(
                RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly,
                RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsOnlyInverted
            );
        classification.KnownButNotImplementedStrategies.Should().BeEmpty();
        classification.FailureMessage.Should().BeNull();
    }

    [Test]
    public void It_treats_no_further_authorization_required_as_a_no_op_when_supported_strategies_exist()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        classification
            .Outcome.Should()
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.SupportedRelationshipStrategies);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Evaluator.AuthorizationStrategyName)
            .Should()
            .Equal(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        classification.KnownButNotImplementedStrategies.Should().BeEmpty();
    }

    [Test]
    public void It_classifies_known_out_of_scope_mixed_strategies_as_not_implemented()
    {
        var classification = Classify(
            CreateMappingSet(_queryResource),
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            AuthorizationStrategyNameConstants.NamespaceBased,
            AuthorizationStrategyNameConstants.OwnershipBased,
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
        );

        classification
            .Outcome.Should()
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.KnownButNotImplemented);
        classification
            .SupportedStrategies.Select(static strategy => strategy.Kind)
            .Should()
            .Equal(RelationalGetManyAuthorizationStrategyKind.RelationshipsWithEdOrgsOnly);
        classification
            .KnownButNotImplementedStrategies.Select(static strategy => strategy.StrategyName)
            .Should()
            .Equal(
                AuthorizationStrategyNameConstants.NamespaceBased,
                AuthorizationStrategyNameConstants.OwnershipBased
            );
        classification.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.NamespaceBased);
        classification.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        classification
            .FailureMessage.Should()
            .Contain($"{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' as a no-op");
    }

    [Test]
    public void It_prefers_the_standard_edfi_basis_resource_when_custom_view_homographs_exist()
    {
        var classification = Classify(
            CreateMappingSet(new("Sample", "School"), new("Ed-Fi", "School")),
            "SchoolWithSomething"
        );

        classification
            .Outcome.Should()
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.KnownButNotImplemented);
        classification.KnownButNotImplementedStrategies.Should().ContainSingle();
        classification
            .KnownButNotImplementedStrategies[0]
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

        classification
            .Outcome.Should()
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.KnownButNotImplemented);
        classification.KnownButNotImplementedStrategies.Should().ContainSingle();
        classification
            .KnownButNotImplementedStrategies[0]
            .BasisResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor"));
    }

    [Test]
    public void It_rejects_custom_view_names_with_unknown_basis_resources_as_security_configuration_errors()
    {
        var classification = Classify(CreateMappingSet(_queryResource), "UnknownBasisWithSomething");

        classification
            .Outcome.Should()
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.SecurityConfigurationError);
        classification.FailureMessage.Should().Contain("UnknownBasis");
        classification.FailureMessage.Should().Contain("schema-hash/Pgsql/v1");
    }

    [Test]
    public void It_rejects_non_convention_strategy_names_as_security_configuration_errors()
    {
        var classification = Classify(CreateMappingSet(_queryResource), "CustomAuthorizationStrategy");

        classification
            .Outcome.Should()
            .Be(RelationalGetManyAuthorizationStrategyClassificationOutcome.SecurityConfigurationError);
        classification.FailureMessage.Should().Contain("CustomAuthorizationStrategy");
        classification.FailureMessage.Should().Contain("{BasisResource}With...");
    }

    private static RelationalGetManyAuthorizationStrategyClassification Classify(
        MappingSet mappingSet,
        params string[] strategyNames
    )
    {
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators =
        [
            .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                strategyName,
                [],
                FilterOperator.And
            )),
        ];

        return RelationalGetManyAuthorizationStrategyClassifier.Classify(
            mappingSet,
            _queryResource,
            authorizationStrategyEvaluators
        );
    }

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
