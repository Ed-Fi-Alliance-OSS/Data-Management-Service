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

    /// <summary>
    /// A descriptor-storage resource. The descriptor GET-by-id path is wired independently of the
    /// regular-resource one, so the planner distinguishes them by storage kind.
    /// </summary>
    private static ConcreteResourceModel DescriptorResource()
    {
        var root = RootTable("SchoolTypeDescriptor", []);
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"),
            _edfiSchema,
            ResourceStorageKind.SharedDescriptorTable,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            ResourceKey(4, "SchoolTypeDescriptor"),
            ResourceStorageKind.SharedDescriptorTable,
            model
        );
    }

    private static MappingSet EmptyMappingSet(params ResourceKeyEntry[] resourceKeysInIdOrder) =>
        new(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: (short)resourceKeysInIdOrder.Length,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder: [],
                    ResourceKeysInIdOrder: resourceKeysInIdOrder
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

    // OwnershipBased is known-but-not-enabled for GET-many exactly as it is for every other operation:
    // DMS-1060 owns the strategy end to end, including the CMS application-context token source and the
    // write-side CreatedByOwnershipTokenId stamping a filter would match against. DMS-1062 ships no
    // ownership token input at all, so there is no token state that could change these outcomes.
    [Test]
    public void It_returns_still_unsupported_for_ReadMany_when_OwnershipBased_is_configured_alone()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [Test]
    public void It_returns_still_unsupported_for_ReadMany_when_OwnershipBased_is_configured_alongside_relationship_authorization_without_a_custom_view()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [Test]
    public void It_returns_still_unsupported_for_ReadMany_when_a_custom_view_is_configured_with_OwnershipBased()
    {
        // The custom view alone would be supported, but Ownership is an AND term: an unsupported
        // Ownership term fails the whole request closed rather than letting the custom-view filter stand
        // in for it. The carried classification still exposes the custom view so the caller can validate
        // it before reporting the 501.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy("StudentWithCTECourseEnrollments", 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            TwoPrefixContext()
        );

        var stillUnsupported = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>()
            .Subject;
        stillUnsupported
            .RelationshipClassification.SupportedCustomViewStrategies.Should()
            .ContainSingle()
            .Which.ConfiguredStrategy.StrategyName.Should()
            .Be("StudentWithCTECourseEnrollments");
    }

    [Test]
    public void It_returns_no_prefixes_configured_for_ReadMany_ahead_of_OwnershipBased()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            RootNamespaceResource(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            EmptyPrefixContext()
        );

        var noPrefixes = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>()
            .Subject;
        noPrefixes.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
    }

    [Test]
    public void It_returns_no_usable_root_column_for_ReadMany_ahead_of_OwnershipBased()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.NoUsableRootColumn>();
    }

    [TestCase("MadeUpStrategy")]
    [TestCase("MissingBasisWithCustomAuthorization")]
    public void It_returns_security_configuration_error_for_ReadMany_when_an_invalid_strategy_accompanies_OwnershipBased(
        string failingStrategyName
    )
    {
        // An unsupported Ownership term no longer short-circuits ahead of the classifier failure, so
        // the security-configuration 500 remains the reported terminal.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(failingStrategyName, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.SecurityConfigurationError>();
    }

    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    public void It_returns_still_unsupported_for_non_ReadMany_operations(
        NamespaceAuthorizationOperation operation
    )
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            operation,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [TestCase(NamespaceAuthorizationOperation.Update, false)]
    [TestCase(NamespaceAuthorizationOperation.Update, true)]
    [TestCase(NamespaceAuthorizationOperation.Delete, false)]
    [TestCase(NamespaceAuthorizationOperation.Delete, true)]
    public void It_returns_still_unsupported_for_custom_views_on_operations_that_do_not_execute_them(
        NamespaceAuthorizationOperation operation,
        bool hasEducationOrganizationClaims
    )
    {
        // Dropping the checks and serving the request would ignore a configured restriction, so a caller
        // that cannot execute them fails closed instead.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            operation,
            [Strategy("StudentWithCTECourseEnrollments", 0)],
            new RelationalAuthorizationContext(hasEducationOrganizationClaims ? [255901L] : [])
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_plans_custom_views_for_a_regular_resource_read_single(bool hasEducationOrganizationClaims)
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy("StudentWithCTECourseEnrollments", 0)],
            new RelationalAuthorizationContext(hasEducationOrganizationClaims ? [255901L] : [])
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.CustomViewStrategies.Should().ContainSingle();
        plan.CustomViewStrategies[0]
            .ConfiguredStrategy.StrategyName.Should()
            .Be("StudentWithCTECourseEnrollments");
        // Excluded from the relationship bucket so the relationship planner never re-classifies it and
        // downgrades it back to unsupported.
        plan.NonNamespaceConfiguredStrategies.Should().BeEmpty();
    }

    [Test]
    public void It_returns_still_unsupported_for_custom_views_on_a_descriptor_read_single()
    {
        // The descriptor GET-by-id path does not execute custom-view checks yet, so it must keep failing
        // closed even though the regular-resource path for the same operation now enforces them.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(4, "SchoolTypeDescriptor"), ResourceKey(3, "Student")),
            DescriptorResource(),
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy("StudentWithCTECourseEnrollments", 0)],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    [Test]
    public void It_plans_custom_views_for_a_descriptor_read_many()
    {
        // Descriptor GET-many was wired with the rest of GET-many, so storage kind only gates ReadSingle.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(4, "SchoolTypeDescriptor"), ResourceKey(3, "Student")),
            DescriptorResource(),
            NamespaceAuthorizationOperation.ReadMany,
            [Strategy("StudentWithCTECourseEnrollments", 0)],
            TwoPrefixContext()
        );

        outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.Plan>()
            .Subject.CustomViewStrategies.Should()
            .ContainSingle();
    }

    [Test]
    public void It_returns_no_prefixes_for_read_single_ahead_of_a_later_configured_custom_view()
    {
        // Namespace terminals rank ahead of custom-view handling, so the no-prefixes 403 is still the
        // reported outcome. The resolved views are carried on it so a caller can validate the ones
        // configured before the terminal; here the view is configured after it, so none qualify.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(1, "AcademicWeek"), ResourceKey(3, "Student")),
            RootNamespaceResource(),
            NamespaceAuthorizationOperation.ReadSingle,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy("StudentWithCTECourseEnrollments", 1),
            ],
            EmptyPrefixContext()
        );

        var noPrefixes = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>()
            .Subject;
        noPrefixes.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        noPrefixes.RawConfiguredIndex.Should().Be(0);
        noPrefixes.CustomViewStrategies.Should().ContainSingle();
        noPrefixes
            .CustomViewStrategies.Should()
            .AllSatisfy(strategy =>
                strategy
                    .ConfiguredStrategy.RawConfiguredIndex.Should()
                    .BeGreaterThan(noPrefixes.RawConfiguredIndex)
            );
    }

    [Test]
    public void It_returns_no_usable_root_column_for_read_single_ahead_of_a_later_configured_custom_view()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadSingle,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy("StudentWithCTECourseEnrollments", 1),
            ],
            TwoPrefixContext()
        );

        var noUsableRootColumn = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoUsableRootColumn>()
            .Subject;
        noUsableRootColumn.RawConfiguredIndex.Should().Be(0);
        noUsableRootColumn.CustomViewStrategies.Should().ContainSingle();
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
    public void It_carries_supported_custom_view_strategies_on_no_prefixes_configured_outcomes()
    {
        var resource = RootNamespaceResource();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(1, "AcademicWeek"), ResourceKey(2, "Student")),
            resource,
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy("StudentWithCTECourseEnrollments", 1),
            ],
            EmptyPrefixContext()
        );

        var noPrefixes = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>()
            .Subject;
        noPrefixes.CustomViewStrategies.Should().ContainSingle();
        noPrefixes
            .CustomViewStrategies[0]
            .ConfiguredStrategy.StrategyName.Should()
            .Be("StudentWithCTECourseEnrollments");
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
    public void It_returns_no_prefixes_for_ReadMany_when_a_later_custom_view_strategy_has_an_unknown_basis_resource()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(1, "AcademicWeek"), ResourceKey(2, "Student")),
            RootNamespaceResource(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy("MissingBasisWithCustomAuthorization", 1),
            ],
            EmptyPrefixContext()
        );

        var noPrefixes = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>()
            .Subject;
        noPrefixes.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        noPrefixes.RawConfiguredIndex.Should().Be(0);
    }

    [Test]
    public void It_returns_no_prefixes_for_ReadMany_when_a_later_strategy_is_invalid()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(1, "AcademicWeek")),
            RootNamespaceResource(),
            NamespaceAuthorizationOperation.ReadMany,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0), Strategy("MadeUpStrategy", 1)],
            EmptyPrefixContext()
        );

        var noPrefixes = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoPrefixesConfigured>()
            .Subject;
        noPrefixes.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        noPrefixes.RawConfiguredIndex.Should().Be(0);
    }

    [Test]
    public void It_returns_no_usable_root_column_for_ReadMany_when_a_later_custom_view_strategy_has_an_unknown_basis_resource()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0),
                Strategy("MissingBasisWithCustomAuthorization", 1),
            ],
            TwoPrefixContext()
        );

        var noUsableRootColumn = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.NoUsableRootColumn>()
            .Subject;
        noUsableRootColumn.RawConfiguredIndex.Should().Be(0);
    }

    [Test]
    public void It_returns_security_configuration_error_for_ReadMany_when_an_unknown_custom_view_basis_is_configured_before_a_no_usable_root_column_terminal()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy("MissingBasisWithCustomAuthorization", 0),
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 1),
            ],
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.SecurityConfigurationError>();
    }

    [TestCase("MadeUpStrategy")]
    [TestCase("MissingBasisWithCustomAuthorization")]
    public void It_returns_security_configuration_error_for_ReadMany_when_the_failing_strategy_is_configured_before_NamespaceBased(
        string failingStrategyName
    )
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(1, "AcademicWeek"), ResourceKey(2, "Student")),
            RootNamespaceResource(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(failingStrategyName, 0),
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 1),
            ],
            EmptyPrefixContext()
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
