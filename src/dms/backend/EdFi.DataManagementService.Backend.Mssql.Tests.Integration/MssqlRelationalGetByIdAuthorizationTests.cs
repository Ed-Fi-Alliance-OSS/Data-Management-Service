// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Relational_Get_By_Id_Authorization_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId = 900;
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly IReadOnlyList<string> _invertedStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly IReadOnlyList<string> _normalAndInvertedStrategies =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly IReadOnlyList<string> _noFurtherAuthorizationRequiredOnlyStrategy =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];
    private static readonly IReadOnlyList<string> _normalPlusNoFurtherAuthorizationRequiredStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];
    private static readonly IReadOnlyList<string> _normalPlusKnownUnsupportedStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.NamespaceBased,
    ];

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];
    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000002")), 200, "P2"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000003")), 300, "P3"),
    ];
    private static readonly AuthorizationAndSeed[] _authorizationAndSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000001")),
            1,
            "requires-both",
            100,
            200
        ),
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000002")),
            2,
            "missing-secondary-auth",
            100,
            300
        ),
    ];
    private static readonly AuthorizationRootChildSeed[] _authorizationRootChildSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000001")),
            1,
            "authorized-by-root",
            100,
            [new ClassPeriodReferenceSeed("P3", 300)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000002")),
            2,
            "child-would-match-but-root-does-not",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000003")),
            3,
            "authorized-with-empty-child-collection",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000004")),
            4,
            "read-boundary-probe",
            100,
            []
        ),
    ];
    private static readonly AuthorizationChildOnlySeed _authorizationChildOnlySeed = new(
        new DocumentUuid(Guid.Parse("66666666-0000-0000-0000-000000000001")),
        1,
        "child-only",
        [new ClassPeriodReferenceSeed("P1", 100)]
    );
    private static readonly AuthorizationNullableSeed _authorizationNullableSeed = new(
        new DocumentUuid(Guid.Parse("77777777-0000-0000-0000-000000000001")),
        1,
        "stored-null"
    );
    private static readonly AuthorizationRootChildSeed _directClaimRootChildSeed = new(
        new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000005")),
        5,
        "get-direct-claim",
        (int)ClaimEducationOrganizationId,
        []
    );

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateClassPeriodAsync(classPeriodSeed)
            );
        }

        foreach (var authorizationAndSeed in _authorizationAndSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationAndAsync(authorizationAndSeed)
            );
        }

        foreach (var authorizationRootChildSeed in _authorizationRootChildSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationRootChildAsync(authorizationRootChildSeed)
            );
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_directClaimRootChildSeed)
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationChildOnlyAsync(_authorizationChildOnlySeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(_authorizationNullableSeed)
        );

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_authorizes_get_by_id_before_reconstituting()
    {
        var result = await GetRootChildAsync(_authorizationRootChildSeeds[0], _normalStrategy);

        var success = AssertSuccess(result, _authorizationRootChildSeeds[0].DocumentUuid);
        success.EdfiDoc["schoolReference"]!["schoolId"]!.GetValue<long>().Should().Be(100);
        _context.AssertSingleDocumentHydration();
        _context.AssertSingleDocumentMaterialized();
    }

    [Test]
    public async Task It_returns_403_without_hydration_when_the_relationship_is_missing()
    {
        var result = await GetRootChildAsync(_authorizationRootChildSeeds[1], _normalStrategy);

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_authorizes_inverted_and_or_composed_relationship_strategies()
    {
        var invertedResult = await GetRootChildAsync(_authorizationRootChildSeeds[1], _invertedStrategy);
        AssertSuccess(invertedResult, _authorizationRootChildSeeds[1].DocumentUuid);
        _context.AssertSingleDocumentHydration();

        var orResult = await GetRootChildAsync(_authorizationRootChildSeeds[1], _normalAndInvertedStrategies);
        AssertSuccess(orResult, _authorizationRootChildSeeds[1].DocumentUuid);
        _context.AssertSingleDocumentHydration();
    }

    [Test]
    public async Task It_authorizes_get_by_id_by_direct_claim_match_without_a_hierarchy_edge()
    {
        (await _context.CountAuthEdgesAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId))
            .Should()
            .Be(0);

        var result = await GetRootChildAsync(_directClaimRootChildSeed, _normalStrategy);

        AssertSuccess(result, _directClaimRootChildSeed.DocumentUuid);
        _context.AssertSingleDocumentHydration();
    }

    [Test]
    public async Task It_ands_multiple_root_edorg_subjects_within_one_strategy()
    {
        var authorizedResult = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            _authorizationAndSeeds[0].DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );
        AssertSuccess(authorizedResult, _authorizationAndSeeds[0].DocumentUuid);

        var unauthorizedResult = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            _authorizationAndSeeds[1].DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );
        AssertRelationshipDenied(
            unauthorizedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_treats_no_further_authorization_required_as_a_bypass_only_when_it_is_the_only_strategy()
    {
        var noOpOnlyResult = await GetRootChildAsync(
            _authorizationRootChildSeeds[1],
            _noFurtherAuthorizationRequiredOnlyStrategy,
            []
        );
        AssertSuccess(noOpOnlyResult, _authorizationRootChildSeeds[1].DocumentUuid);

        var mixedAuthorizedResult = await GetRootChildAsync(
            _authorizationRootChildSeeds[0],
            _normalPlusNoFurtherAuthorizationRequiredStrategy
        );
        AssertSuccess(mixedAuthorizedResult, _authorizationRootChildSeeds[0].DocumentUuid);

        var mixedUnauthorizedResult = await GetRootChildAsync(
            _authorizationRootChildSeeds[1],
            _normalPlusNoFurtherAuthorizationRequiredStrategy
        );
        AssertRelationshipDenied(
            mixedUnauthorizedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_501_for_known_but_not_enabled_mixed_strategies()
    {
        var result = await GetRootChildAsync(
            _authorizationRootChildSeeds[0],
            _normalPlusKnownUnsupportedStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureNotImplemented>().Subject;
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.NamespaceBased);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_security_configuration_failure_before_known_unsupported_strategy_failures()
    {
        var result = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.ChildOnlyEdOrgResourceName,
            _authorizationChildOnlySeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalPlusKnownUnsupportedStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Contain(error => error.Contains("$.classPeriods[*].classPeriodReference.schoolId"));
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_single_record_403_when_claim_edorgs_are_empty()
    {
        var result = await GetRootChildAsync(_authorizationRootChildSeeds[0], _normalStrategy, []);

        var failure = AssertRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_surfaces_stored_null_invalid_data_without_reconstitution()
    {
        var result = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.NullableRootEdOrgResourceName,
            _authorizationNullableSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.StoredValueNull);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_retries_when_authorization_and_hydration_observe_different_content_versions()
    {
        _context.BeforeNextHydration(ct =>
            _context.MutateAuthorizationRootChildSchoolAsync(
                _authorizationRootChildSeeds[3].DocumentUuid,
                300,
                ct
            )
        );

        var result = await GetRootChildAsync(_authorizationRootChildSeeds[3], _normalStrategy);

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _context.AssertHydratedWithoutMaterialization(expectedHydrationCount: 1);
    }

    private async Task<GetResult> GetRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<string> strategyNames,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    )
    {
        return await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            claimEducationOrganizationIds ?? [ClaimEducationOrganizationId],
            strategyNames
        );
    }

    private static GetResult.GetSuccess AssertSuccess(GetResult result, DocumentUuid expectedDocumentUuid)
    {
        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(expectedDocumentUuid);
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(expectedDocumentUuid.Value.ToString());
        return success;
    }

    private static GetResult.GetFailureRelationshipNotAuthorized AssertRelationshipDenied(
        GetResult result,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        var failure = result.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(expectedFailureKind);
        return failure;
    }
}
