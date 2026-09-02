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

    /// <summary>
    /// Both AND strategies plan together for ReadSingle. The ownership check carries its configured index,
    /// and it is not left among the non-namespace strategies the relationship classifier receives, which are
    /// what a 501 would be built from.
    /// </summary>
    [Test]
    public void It_plans_ownership_alongside_namespace_for_read_single()
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

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().HaveCount(1);
        plan.OwnershipCheck.Should().NotBeNull();
        plan.OwnershipCheck!.RawConfiguredIndex.Should().Be(1);
        plan.OwnershipCheck.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
        plan.NonNamespaceConfiguredStrategies.Should().BeEmpty();
    }

    /// <summary>
    /// OwnershipBased alone is a complete plan for ReadSingle. It needs neither a securable element nor a
    /// root namespace column, because its subject is the same document column for every resource.
    /// </summary>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    public void It_plans_ownership_configured_alone_for_an_enforced_operation(
        NamespaceAuthorizationOperation operation
    )
    {
        var resource = ResourceWithoutSecurableElements();

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            operation,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.Plan>().Subject;
        plan.NamespaceChecks.Should().BeEmpty();
        plan.OwnershipCheck.Should().NotBeNull();
        plan.OwnershipCheck!.RawConfiguredIndex.Should().Be(0);
    }

    // OwnershipBased is known-but-not-enabled for GET-many exactly as it is for every other operation:
    // DMS-1410 owns the future GET-many ownership filter and CMS application-context token input.
    // DMS-1060 owns the write-side CreatedByOwnershipTokenId stamping a filter would match against.
    // DMS-1062 ships no ownership token input at all, so there is no token state
    // that could change these outcomes.
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

    [TestCase(NamespaceAuthorizationOperation.ReadSingle, false)]
    [TestCase(NamespaceAuthorizationOperation.ReadSingle, true)]
    [TestCase(NamespaceAuthorizationOperation.Delete, false)]
    [TestCase(NamespaceAuthorizationOperation.Delete, true)]
    [TestCase(NamespaceAuthorizationOperation.Update, false)]
    [TestCase(NamespaceAuthorizationOperation.Update, true)]
    public void It_plans_custom_views_for_a_regular_resource_single_record_operation(
        NamespaceAuthorizationOperation operation,
        bool hasEducationOrganizationClaims
    )
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(2, "PlainResource"), ResourceKey(3, "Student")),
            ResourceWithoutSecurableElements(),
            operation,
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

    [TestCase(NamespaceAuthorizationOperation.ReadMany)]
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    public void It_plans_custom_views_for_a_descriptor_operation(NamespaceAuthorizationOperation operation)
    {
        // Every descriptor path executes custom-view checks now, so storage kind no longer gates any of them.
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(4, "SchoolTypeDescriptor"), ResourceKey(3, "Student")),
            DescriptorResource(),
            operation,
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

    /// <summary>
    /// A still-unsupported outcome carries the strategies the relationship classifier could not support so
    /// the 501 can name them. Uses ReadMany, the only operation that still withholds OwnershipBased: every
    /// single-record operation now plans it instead, which is asserted separately.
    /// </summary>
    [Test]
    public void It_carries_the_non_namespace_strategies_on_a_still_unsupported_outcome()
    {
        var resource = RootNamespaceResource();
        var ownership = Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1);

        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            resource,
            NamespaceAuthorizationOperation.ReadMany,
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

    // ── DMS-1060 ownership enablement gate ──────────────────────────────
    //
    // Every single-record operation is enforced; ReadMany is withheld for the whole story. The predicate is
    // asserted directly as well as through plan outcomes, because a withheld operation produces the same 501
    // whether the gate withheld it or the classifier never saw it, and only the predicate separates those.

    /// <summary>
    /// Every single-record operation, and only those. Each enforcement step added its own operation in the
    /// same commit that wired that operation's executor, so no commit existed in which a planned ownership
    /// check had no executor.
    /// </summary>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    public void It_enforces_ownership_for_the_enabled_operations(NamespaceAuthorizationOperation operation)
    {
        RelationalAuthorizationPlanner
            .EnforcesOwnershipChecks(operation, ResourceStorageKind.RelationalTables)
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// ReadMany is the only operation left withheld, and permanently: GET-many ownership filtering is
    /// DMS-1410's, and it is a page filter rather than a single-record check.
    /// </summary>
    [Test]
    public void It_withholds_ownership_enforcement_for_read_many()
    {
        RelationalAuthorizationPlanner
            .EnforcesOwnershipChecks(
                NamespaceAuthorizationOperation.ReadMany,
                ResourceStorageKind.RelationalTables
            )
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// Descriptor storage is withheld permanently for this story. Before ownership had a bucket of its own,
    /// descriptors were protected only incidentally — by the descriptor guardrail rejecting every
    /// non-namespace strategy — so splitting ownership out would have removed that protection silently.
    /// This is the named replacement.
    /// </summary>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    [TestCase(NamespaceAuthorizationOperation.ReadMany)]
    public void It_withholds_ownership_enforcement_for_descriptor_storage(
        NamespaceAuthorizationOperation operation
    )
    {
        RelationalAuthorizationPlanner
            .EnforcesOwnershipChecks(operation, ResourceStorageKind.SharedDescriptorTable)
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// The descriptor arm must not depend on the operation set: it has to keep withholding even once every
    /// single-record operation has been flipped on for relational resources.
    /// </summary>
    [Test]
    public void It_withholds_descriptor_ownership_enforcement_independently_of_the_operation_set()
    {
        foreach (var operation in Enum.GetValues<NamespaceAuthorizationOperation>())
        {
            RelationalAuthorizationPlanner
                .EnforcesOwnershipChecks(operation, ResourceStorageKind.SharedDescriptorTable)
                .Should()
                .BeFalse($"descriptor storage must never enforce ownership in this story ({operation})");
        }
    }

    /// <summary>
    /// An over-cap ownership-token list must not produce the cap terminal while the operation is withheld:
    /// the strategy is not enforced, so there is nothing for the cap to gate. It keeps its 501.
    /// </summary>
    [Test]
    public void It_does_not_report_the_token_cap_while_the_gate_withholds_read_many()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            OverCapOwnershipContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    /// <summary>
    /// The cap terminal is reachable for every enforced operation: it reports the configured token count so
    /// an operator can see what to reduce, and never a token value.
    /// </summary>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    public void It_reports_the_token_cap_for_an_enforced_operation(NamespaceAuthorizationOperation operation)
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            operation,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            OverCapOwnershipContext()
        );

        var capExceeded = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded>()
            .Subject;
        capExceeded.OwnershipTokenCount.Should().Be(OwnershipTokenLimitExceededException.OwnershipTokenLimit);
        capExceeded.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// The precedence carried since Phase 3 and now observable: a custom view whose basis resource cannot be
    /// resolved is a configuration failure at a position ahead of OwnershipBased, which executes last among
    /// the AND strategies. It must win over the cap terminal, or an over-cap token list would mask a
    /// misconfigured view that a compliant token list would have reported.
    /// </summary>
    [Test]
    public void It_reports_an_unresolved_custom_view_basis_ahead_of_the_read_single_token_cap()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadSingle,
            [
                Strategy("MissingBasisWithCustomAuthorization", 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            OverCapOwnershipContext()
        );

        outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.SecurityConfigurationError>()
            .Which.RelationshipClassification.SecurityConfigurationFailures.Should()
            .Contain(failure =>
                failure.FailureKind == RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource
            );
    }

    /// <summary>
    /// A plan produced for a request with no ownership strategy carries no ownership check — the property
    /// defaults to null rather than to an empty-but-present plan.
    /// </summary>
    [Test]
    public void It_plans_no_ownership_check_when_ownership_is_not_configured()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            RootNamespaceResource(),
            NamespaceAuthorizationOperation.ReadSingle,
            [Strategy(AuthorizationStrategyNameConstants.NamespaceBased, 0)],
            TwoPrefixContext()
        );

        outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.Plan>()
            .Which.OwnershipCheck.Should()
            .BeNull();
    }

    private static RelationalAuthorizationContext OverCapOwnershipContext() =>
        new(
            [],
            ["uri://ed-fi.org/"],
            creatorOwnershipTokenId: null,
            ownershipTokenIds:
            [
                .. Enumerable
                    .Range(1, OwnershipTokenLimitExceededException.OwnershipTokenLimit)
                    .Select(static value => (short)value),
            ]
        );

    // ── Ownership cap versus classifier security-configuration failures ─
    //
    // The classifier's SecurityConfigurationError bucket is not purely relationship failures: it also
    // carries custom view-based strategy-resolution failures. Those are AND-strategy failures that
    // execute ahead of Ownership-based, so the cap must not displace them. Asserted on the predicate
    // because the enablement gate makes it unobservable through a plan outcome; the behavioral
    // assertion belongs to the first gate-flip commit.

    private static RelationshipAuthorizationFailureMetadata Failure(
        RelationshipAuthorizationFailureKind failureKind
    ) => new(failureKind, new QualifiedResourceName("Ed-Fi", "PlainResource"));

    /// <summary>
    /// The regression this pins: a custom-view configuration failure must keep its own 500 rather than
    /// being replaced by the ownership token-cap terminal. Every custom view executes ahead of
    /// Ownership-based among the AND strategies, whatever position CMS gave either.
    /// </summary>
    [TestCase(RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource)]
    [TestCase(RelationshipAuthorizationFailureKind.NoCustomViewJoinPath)]
    [TestCase(RelationshipAuthorizationFailureKind.MissingProposedCustomViewRootBinding)]
    public void It_does_not_let_the_ownership_cap_displace_a_custom_view_configuration_failure(
        RelationshipAuthorizationFailureKind failureKind
    )
    {
        RelationalAuthorizationPlanner
            .OwnershipCapOutranksClassifierFailure(ownershipCapExceeded: true, [Failure(failureKind)])
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// A relationship or otherwise generic failure does yield to the cap: the relationship OR group
    /// executes after every AND strategy.
    /// </summary>
    [TestCase(RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy)]
    [TestCase(RelationshipAuthorizationFailureKind.UnresolvedSecurableElement)]
    [TestCase(RelationshipAuthorizationFailureKind.NoApplicableRootSubject)]
    [TestCase(RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations)]
    public void It_lets_the_ownership_cap_displace_a_relationship_configuration_failure(
        RelationshipAuthorizationFailureKind failureKind
    )
    {
        RelationalAuthorizationPlanner
            .OwnershipCapOutranksClassifierFailure(ownershipCapExceeded: true, [Failure(failureKind)])
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// A single custom-view failure is enough to hold the cap back, even alongside relationship failures:
    /// the earliest-executing failure is the one reported.
    /// </summary>
    [Test]
    public void It_holds_the_cap_back_when_a_custom_view_failure_accompanies_relationship_failures()
    {
        RelationalAuthorizationPlanner
            .OwnershipCapOutranksClassifierFailure(
                ownershipCapExceeded: true,
                [
                    Failure(RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy),
                    Failure(RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource),
                ]
            )
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// With no cap breach there is nothing to displace anything, whatever the failures say.
    /// </summary>
    [Test]
    public void It_never_outranks_anything_when_the_cap_is_not_exceeded()
    {
        RelationalAuthorizationPlanner
            .OwnershipCapOutranksClassifierFailure(
                ownershipCapExceeded: false,
                [Failure(RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy)]
            )
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// An over-cap breach with no classifier failure at all leaves the cap free to be the terminal.
    /// </summary>
    [Test]
    public void It_outranks_an_empty_failure_list_when_the_cap_is_exceeded()
    {
        RelationalAuthorizationPlanner
            .OwnershipCapOutranksClassifierFailure(ownershipCapExceeded: true, [])
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_rejects_a_null_failure_list()
    {
        Action act = () => RelationalAuthorizationPlanner.OwnershipCapOutranksClassifierFailure(true, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Out-of-scope boundaries, asserted rather than inferred from absence ─

    /// <summary>
    /// Descriptor ownership enforcement is out of scope for DMS-1060, so a descriptor configured with
    /// OwnershipBased keeps its known-but-not-enabled 501 on every operation.
    /// </summary>
    /// <remarks>
    /// The behavioral counterpart to the gate-predicate assertions above. Before ownership had a bucket of
    /// its own this held only incidentally, because the descriptor guardrail rejects every non-namespace
    /// strategy; splitting ownership out could have removed that with no failing test.
    /// </remarks>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    [TestCase(NamespaceAuthorizationOperation.ReadMany)]
    public void It_keeps_descriptor_ownership_unsupported_for_every_operation(
        NamespaceAuthorizationOperation operation
    )
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(4, "SchoolTypeDescriptor")),
            DescriptorResource(),
            operation,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            TwoPrefixContext()
        );

        var stillUnsupported = outcome
            .Should()
            .BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>()
            .Subject;
        stillUnsupported
            .RelationshipClassification.KnownButNotEnabledStrategies.Select(static strategy =>
                strategy.ConfiguredStrategy.StrategyName
            )
            .Should()
            .Equal(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// The descriptor boundary must not be reachable through the token cap either: an over-cap list on a
    /// descriptor request keeps the 501 rather than becoming the cap's 500, because ownership is not
    /// enforced there at all and so there is nothing for the cap to gate.
    /// </summary>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    [TestCase(NamespaceAuthorizationOperation.ReadMany)]
    public void It_keeps_descriptor_ownership_unsupported_even_over_the_token_cap(
        NamespaceAuthorizationOperation operation
    )
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(4, "SchoolTypeDescriptor")),
            DescriptorResource(),
            operation,
            [Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 0)],
            OverCapOwnershipContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }

    /// <summary>
    /// A descriptor 501 for ownership still carries the resolved custom views, so a missing or
    /// non-conforming view keeps its own 500 instead of being masked by the 501. Ownership executes last
    /// among the AND strategies whatever its configured position, so every view runs ahead of it.
    /// </summary>
    [Test]
    public void It_carries_resolved_custom_views_on_a_descriptor_ownership_terminal()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(ResourceKey(4, "SchoolTypeDescriptor"), ResourceKey(3, "Student")),
            DescriptorResource(),
            NamespaceAuthorizationOperation.ReadSingle,
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

    /// <summary>
    /// GET-many ownership filtering belongs to DMS-1410. An over-cap token list must not turn that 501 into
    /// the cap's 500 — the strategy is not enforced for ReadMany, so the cap has nothing to gate.
    /// </summary>
    [Test]
    public void It_keeps_read_many_ownership_unsupported_even_over_the_token_cap()
    {
        var outcome = RelationalAuthorizationPlanner.Plan(
            EmptyMappingSet(),
            ResourceWithoutSecurableElements(),
            NamespaceAuthorizationOperation.ReadMany,
            [
                Strategy(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly, 0),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ],
            OverCapOwnershipContext()
        );

        outcome.Should().BeOfType<RelationalAuthorizationPlanOutcome.StillUnsupported>();
    }
}
