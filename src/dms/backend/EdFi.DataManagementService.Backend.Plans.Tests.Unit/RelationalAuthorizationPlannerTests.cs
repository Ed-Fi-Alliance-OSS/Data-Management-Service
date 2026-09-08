// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalAuthorizationPlanner
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string resource) =>
        new(id, new QualifiedResourceName("Ed-Fi", resource), "1.0", false);

    private static DbTableModel RootTable(string name, IReadOnlyList<DbColumnModel> columns) =>
        new(
            Table(name),
            Path("$"),
            new TableKey("PK_" + name, [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            columns,
            []
        );

    private static ConcreteResourceModel RootNamespaceResource()
    {
        var root = RootTable(
            "AcademicWeek",
            [new DbColumnModel(Col("Namespace"), ColumnKind.Scalar, null, false, Path("$.namespace"), null)]
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "AcademicWeek"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            ResourceKey(1, "AcademicWeek"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], ["$.namespace"], [], [], []),
        };
    }

    private static ConcreteResourceModel ResourceWithoutSecurableElements()
    {
        var root = RootTable("PlainResource", []);
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "PlainResource"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            ResourceKey(2, "PlainResource"),
            ResourceStorageKind.RelationalTables,
            model
        );
    }

    private static MappingSet EmptyMappingSet() =>
        new(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
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
                Dialect: SqlDialect.Pgsql,
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

    private static ConfiguredAuthorizationStrategy Strategy(string name, int index) => new(name, index);

    private static RelationalAuthorizationContext TwoPrefixContext() =>
        new([], ["uri://ed-fi.org/", "uri://gbisd.edu/"]);

    private static RelationalAuthorizationContext EmptyPrefixContext() => new([], []);

    [Test]
    public void It_returns_a_plan_with_namespace_checks_when_only_NamespaceBased_is_configured()
    {
        var resource = RootNamespaceResource();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0)],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().HaveCount(1);
        plan.NonNamespaceConfiguredStrategies.Should().BeEmpty();
    }

    [Test]
    public void It_returns_a_plan_with_relationship_strategies_passed_through_when_no_NamespaceBased_is_configured()
    {
        var resource = ResourceWithoutSecurableElements();
        var relationshipStrategy = Strategy(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            0
        );

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [relationshipStrategy],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().BeEmpty();
        plan.NonNamespaceConfiguredStrategies.Should().Equal(relationshipStrategy);
    }

    [Test]
    public void It_returns_a_plan_when_both_NamespaceBased_and_relationship_strategies_are_configured()
    {
        var resource = RootNamespaceResource();
        var relationshipStrategy = Strategy(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            1
        );

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0), relationshipStrategy],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().HaveCount(1);
        plan.NonNamespaceConfiguredStrategies.Should().Equal(relationshipStrategy);
    }

    [Test]
    public void It_returns_still_unsupported_when_OwnershipBased_is_configured_alongside_NamespaceBased()
    {
        var resource = RootNamespaceResource();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [Test]
    public void It_returns_still_unsupported_when_OwnershipBased_is_configured_alone()
    {
        var resource = ResourceWithoutSecurableElements();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [Test]
    public void It_carries_the_non_namespace_strategies_on_a_still_unsupported_outcome()
    {
        var resource = RootNamespaceResource();
        var ownership = Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1);

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0), ownership],
            TwoPrefixContext()
        );

        var stillUnsupported = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>()
            .Subject;
        stillUnsupported.NonNamespaceConfiguredStrategies.Should().Equal(ownership);
    }

    [Test]
    public void It_carries_the_non_namespace_strategies_on_a_security_configuration_error_outcome()
    {
        var resource = RootNamespaceResource();
        var madeUp = Strategy("MadeUpStrategy", 1);

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0), madeUp],
            TwoPrefixContext()
        );

        var securityError = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.SecurityConfigurationError>()
            .Subject;
        securityError.NonNamespaceConfiguredStrategies.Should().Equal(madeUp);
    }

    [Test]
    public void It_returns_security_configuration_error_when_an_unknown_strategy_is_configured()
    {
        var resource = ResourceWithoutSecurableElements();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy("MadeUpStrategy", 0)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.SecurityConfigurationError>();
    }

    [Test]
    public void It_propagates_no_usable_root_column_when_NamespaceBased_resource_has_only_invalid_metadata()
    {
        var resource = ResourceWithoutSecurableElements();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.NoUsableRootColumn>();
    }

    [Test]
    public void It_returns_no_usable_root_column_before_still_unsupported_when_both_are_present()
    {
        var resource = ResourceWithoutSecurableElements();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.NoUsableRootColumn>();
    }

    [Test]
    public void It_propagates_no_prefixes_configured_when_metadata_is_valid_and_client_prefixes_are_empty()
    {
        var resource = RootNamespaceResource();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0)],
            EmptyPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>();
    }

    [Test]
    public void It_returns_no_prefixes_before_still_unsupported_when_unsupported_strategy_is_also_configured()
    {
        var resource = RootNamespaceResource();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            EmptyPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>();
    }

    [Test]
    public void It_returns_security_configuration_error_before_namespace_outcomes_when_unknown_strategy_is_present()
    {
        var resource = RootNamespaceResource();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0), Strategy("MadeUpStrategy", 1)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.SecurityConfigurationError>();
    }

    [Test]
    public void It_treats_NoFurtherAuthorizationRequired_as_a_non_namespace_strategy_passed_through_to_the_caller()
    {
        var resource = ResourceWithoutSecurableElements();
        var nfar = Strategy(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired, 0);

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [nfar],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().BeEmpty();
        plan.NonNamespaceConfiguredStrategies.Should().Equal(nfar);
    }

    [Test]
    public void It_returns_an_empty_plan_when_no_strategies_are_configured()
    {
        var resource = ResourceWithoutSecurableElements();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().BeEmpty();
        plan.NonNamespaceConfiguredStrategies.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_the_relative_order_of_non_namespace_strategies()
    {
        var resource = RootNamespaceResource();
        var rwedoo = Strategy(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly, 1);
        var rwedooi = Strategy(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted, 2);

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0), rwedoo, rwedooi],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NonNamespaceConfiguredStrategies.Should().Equal(rwedoo, rwedooi);
    }
}
