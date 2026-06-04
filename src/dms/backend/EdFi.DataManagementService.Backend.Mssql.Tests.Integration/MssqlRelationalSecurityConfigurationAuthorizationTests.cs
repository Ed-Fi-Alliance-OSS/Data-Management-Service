// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("SecurityConfiguration")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Relational_Backend_Security_Configuration_Authorization_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string ResourceName =
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName;
    private const string UnknownStrategyName = "SecurityConfigurationUnknownStrategy";
    private const string ResourceFullName = "Authz.AuthorizationRootChildResource";
    private const int ParameterBudgetClaimEdOrgCount = 1999;
    private const int ParameterBudgetQueryFilterCount = 150;

    private static readonly IReadOnlyList<string> _unknownStrategy = [UnknownStrategyName];
    private static readonly QuerySchoolSeed _schoolSeed = new(
        new DocumentUuid(Guid.Parse("94949494-0000-0000-0000-000000000001")),
        (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId,
        "Unknown Strategy School"
    );
    private static readonly AuthorizationRootChildSeed _seededRootChild = new(
        new DocumentUuid(Guid.Parse("94949494-0000-0000-0000-000000000101")),
        101,
        "seeded-root-child",
        (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId,
        []
    );

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
        await _context.SeedSchoolDescriptorDataAsync();
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(_schoolSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_seededRootChild)
        );
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, _schoolSeed.SchoolId);
        _context.ResetRecorder();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Test]
    public async Task It_returns_typed_security_configuration_for_query_unknown_strategy()
    {
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            ResourceName,
            [ClaimEducationOrganizationId],
            _unknownStrategy,
            totalCount: false
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertUnknownStrategySecurityConfiguration(
            failure.Errors,
            failure.Diagnostics,
            UnknownStrategyName,
            ResourceFullName
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_typed_security_configuration_for_get_by_id_unknown_strategy()
    {
        var result = await _context.GetByIdAsync(
            ProjectEndpointName,
            ResourceName,
            _seededRootChild.DocumentUuid,
            [ClaimEducationOrganizationId],
            _unknownStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertUnknownStrategySecurityConfiguration(
            failure.Errors,
            failure.Diagnostics,
            UnknownStrategyName,
            ResourceFullName
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_typed_security_configuration_for_post_unknown_strategy_without_writing()
    {
        var newSeed = new AuthorizationRootChildSeed(
            new DocumentUuid(Guid.Parse("94949494-0000-0000-0000-000000000201")),
            201,
            "post-rejected-root-child",
            (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId,
            []
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            newSeed,
            [ClaimEducationOrganizationId],
            _unknownStrategy
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertUnknownStrategySecurityConfiguration(
            failure.Errors,
            failure.Diagnostics,
            UnknownStrategyName,
            ResourceFullName
        );
        (await _context.CountDocumentRowsAsync(newSeed.DocumentUuid)).Should().Be(0);
        (await _context.CountResourceRootRowsAsync(ProjectEndpointName, ResourceName, newSeed.DocumentUuid))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_returns_typed_security_configuration_when_relationship_query_exceeds_mssql_parameter_limit()
    {
        long[] claimEducationOrganizationIds =
        [
            .. Enumerable.Range(0, ParameterBudgetClaimEdOrgCount).Select(static index => 100000L + index),
        ];
        QueryElement[] queryElements = CreateNameFilters(ParameterBudgetQueryFilterCount);

        var result = await _context.QueryAsync(
            ProjectEndpointName,
            ResourceName,
            claimEducationOrganizationIds,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            totalCount: false,
            queryElements: queryElements,
            mappingSetTransform: AddBudgetQueryFields
        );

        if (result is QueryResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(unknownFailure.FailureMessage);
        }

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixCount: 0,
                    claimEducationOrganizationIdCount: ParameterBudgetClaimEdOrgCount,
                    nonAuthorizationParameterCount: ParameterBudgetQueryFilterCount + 2
                )
            );
        var diagnostic = failure.Diagnostics.Should().ContainSingle().Subject;
        diagnostic
            .ProviderOrPlannerFailureKind.Should()
            .Be("AuthorizationParameterBudget.CommandParameterCapExceeded");
        diagnostic.ResourceFullName.Should().Be(ResourceFullName);
        _context.AssertNoHydration();
    }

    private static QueryElement[] CreateNameFilters(int count) =>
        [
            .. Enumerable
                .Range(0, count)
                .Select(static index => new QueryElement(
                    $"nameBudget{index}",
                    [new JsonPath($"$.nameBudget{index}")],
                    $"budget-filter-{index}",
                    "string"
                )),
        ];

    private static MappingSet AddBudgetQueryFields(MappingSet mappingSet)
    {
        var resource = new QualifiedResourceName("Authz", ResourceName);
        var queryCapability = mappingSet.QueryCapabilitiesByResource[resource];
        var readPlan = mappingSet.ReadPlansByResource[resource];
        DbColumnModel[] budgetColumns = CreateBudgetColumns(ParameterBudgetQueryFilterCount);
        var rootWithBudgetColumns = readPlan.Model.Root with
        {
            Columns = [.. readPlan.Model.Root.Columns, .. budgetColumns],
        };
        var modelWithBudgetColumns = readPlan.Model with
        {
            Root = rootWithBudgetColumns,
            TablesInDependencyOrder =
            [
                rootWithBudgetColumns,
                .. readPlan.Model.TablesInDependencyOrder.Skip(1),
            ],
        };
        Dictionary<string, SupportedRelationalQueryField> supportedFields = new(
            queryCapability.SupportedFieldsByQueryField,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var index in Enumerable.Range(0, ParameterBudgetQueryFilterCount))
        {
            var queryFieldName = $"nameBudget{index}";
            supportedFields[queryFieldName] = new SupportedRelationalQueryField(
                queryFieldName,
                new RelationalQueryFieldPath(new JsonPathExpression($"$.nameBudget{index}", []), "string"),
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName($"NameBudget{index}"))
            );
        }

        Dictionary<QualifiedResourceName, RelationalQueryCapability> queryCapabilities = new(
            mappingSet.QueryCapabilitiesByResource
        )
        {
            [resource] = queryCapability with { SupportedFieldsByQueryField = supportedFields },
        };
        Dictionary<QualifiedResourceName, ResourceReadPlan> readPlans = new(mappingSet.ReadPlansByResource)
        {
            [resource] = readPlan with { Model = modelWithBudgetColumns },
        };
        var modelSet = mappingSet.Model with
        {
            ConcreteResourcesInNameOrder =
            [
                .. mappingSet.Model.ConcreteResourcesInNameOrder.Select(concreteResource =>
                    concreteResource.ResourceKey.Resource == resource
                        ? concreteResource with
                        {
                            RelationalModel = modelWithBudgetColumns,
                        }
                        : concreteResource
                ),
            ],
        };

        return mappingSet with
        {
            Model = modelSet,
            ReadPlansByResource = readPlans,
            QueryCapabilitiesByResource = queryCapabilities,
        };
    }

    private static DbColumnModel[] CreateBudgetColumns(int count) =>
        [
            .. Enumerable
                .Range(0, count)
                .Select(static index => new DbColumnModel(
                    new DbColumnName($"NameBudget{index}"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    IsNullable: true,
                    new JsonPathExpression($"$.nameBudget{index}", []),
                    TargetResource: null
                )),
        ];
}
