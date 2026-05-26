// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Delete_Authorization_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string PhysicalSchema = "authz";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000003")), 300, "West School"),
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
            new DocumentUuid(Guid.Parse("88888888-1000-0000-0000-000000000001")),
            101,
            "requires-both",
            100,
            200
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-1000-0000-0000-000000000002")),
            102,
            "missing-secondary-auth",
            100,
            300
        ),
    ];

    private static readonly AuthorizationRootChildSeed[] _authorizationRootChildSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000001")),
            201,
            "delete-authorized",
            100,
            [new ClassPeriodReferenceSeed("P3", 300)]
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000002")),
            202,
            "delete-unauthorized",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000003")),
            203,
            "delete-inverted",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000004")),
            204,
            "delete-or",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000005")),
            205,
            "delete-no-op-only",
            300,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000006")),
            206,
            "delete-supported-no-op-authorized",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000007")),
            207,
            "delete-supported-no-op-unauthorized",
            300,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000008")),
            208,
            "delete-known-unsupported",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000009")),
            209,
            "delete-empty-claims",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000010")),
            210,
            "delete-authorization-before-if-match",
            300,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("88888888-2000-0000-0000-000000000011")),
            211,
            "delete-current-if-match",
            100,
            []
        ),
    ];

    private static readonly AuthorizationChildOnlySeed _authorizationChildOnlySeed = new(
        new DocumentUuid(Guid.Parse("88888888-3000-0000-0000-000000000001")),
        301,
        "child-only",
        [new ClassPeriodReferenceSeed("P1", 100)]
    );

    private static readonly AuthorizationNullableSeed _authorizationNullableSeed = new(
        new DocumentUuid(Guid.Parse("88888888-4000-0000-0000-000000000001")),
        401,
        "stored-null"
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
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
            await _context.CreateAuthorizationChildOnlyAsync(_authorizationChildOnlySeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(_authorizationNullableSeed)
        );

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
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
    public async Task It_authorizes_delete_by_id_and_removes_document_and_resource_rows()
    {
        var seed = _authorizationRootChildSeeds[0];

        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            1
        );

        var result = await DeleteRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            0
        );
    }

    [Test]
    public async Task It_returns_403_and_preserves_rows_when_the_relationship_is_missing()
    {
        var seed = _authorizationRootChildSeeds[1];

        var result = await DeleteRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            1
        );
    }

    [Test]
    public async Task It_authorizes_inverted_and_or_composed_relationship_strategies()
    {
        var invertedSeed = _authorizationRootChildSeeds[2];
        var orSeed = _authorizationRootChildSeeds[3];

        var invertedResult = await DeleteRootChildAsync(
            invertedSeed,
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );
        var orResult = await DeleteRootChildAsync(
            orSeed,
            [
                RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsOnly,
                RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsOnlyInverted,
            ]
        );

        invertedResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        orResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            invertedSeed.DocumentUuid,
            0
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            orSeed.DocumentUuid,
            0
        );
    }

    [Test]
    public async Task It_ands_multiple_root_edorg_subjects_within_one_strategy()
    {
        var authorizedSeed = _authorizationAndSeeds[0];
        var unauthorizedSeed = _authorizationAndSeeds[1];

        var authorizedResult = await _context.DeleteByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            authorizedSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );
        var unauthorizedResult = await _context.DeleteByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            unauthorizedSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        authorizedResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        AssertRelationshipDenied(
            unauthorizedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            authorizedSeed.DocumentUuid,
            0
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName,
            unauthorizedSeed.DocumentUuid,
            1
        );
    }

    [Test]
    public async Task It_treats_no_further_authorization_required_as_a_bypass_only_when_it_is_the_only_strategy()
    {
        var noOpOnlySeed = _authorizationRootChildSeeds[4];
        var mixedAuthorizedSeed = _authorizationRootChildSeeds[5];
        var mixedUnauthorizedSeed = _authorizationRootChildSeeds[6];

        var noOpOnlyResult = await DeleteRootChildAsync(
            noOpOnlySeed,
            RelationshipAuthorizationCrudTestSupport.NoFurtherAuthorizationRequiredOnlyStrategyNames,
            []
        );
        var mixedAuthorizedResult = await DeleteRootChildAsync(
            mixedAuthorizedSeed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyPlusNoFurtherAuthorizationRequiredStrategyNames
        );
        var mixedUnauthorizedResult = await DeleteRootChildAsync(
            mixedUnauthorizedSeed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyPlusNoFurtherAuthorizationRequiredStrategyNames
        );

        noOpOnlyResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        mixedAuthorizedResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        AssertRelationshipDenied(
            mixedUnauthorizedResult,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            noOpOnlySeed.DocumentUuid,
            0
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            mixedAuthorizedSeed.DocumentUuid,
            0
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            mixedUnauthorizedSeed.DocumentUuid,
            1
        );
    }

    [Test]
    public async Task It_returns_501_and_security_configuration_failures_before_deleting()
    {
        var knownUnsupportedSeed = _authorizationRootChildSeeds[7];

        var knownUnsupportedResult = await DeleteRootChildAsync(
            knownUnsupportedSeed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyPlusKnownUnsupportedStrategyNames
        );
        var securityConfigurationResult = await _context.DeleteByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.ChildOnlyEdOrgResourceName,
            _authorizationChildOnlySeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyPlusKnownUnsupportedStrategyNames
        );

        var notImplemented = knownUnsupportedResult
            .Should()
            .BeOfType<DeleteResult.DeleteFailureNotImplemented>()
            .Subject;
        notImplemented
            .FailureMessage.Should()
            .Contain(RelationshipAuthorizationCrudTestSupport.NamespaceBased);
        var securityConfiguration = securityConfigurationResult
            .Should()
            .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Subject;
        securityConfiguration
            .Errors.Should()
            .Contain(error => error.Contains("$.classPeriods[*].classPeriodReference.schoolId"));
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            knownUnsupportedSeed.DocumentUuid,
            1
        );
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.ChildOnlyEdOrgResourceName,
            _authorizationChildOnlySeed.DocumentUuid,
            1
        );
    }

    [Test]
    public async Task It_returns_single_record_403_when_claim_edorgs_are_empty()
    {
        var seed = _authorizationRootChildSeeds[8];

        var result = await DeleteRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            []
        );

        var failure = AssertRelationshipDenied(
            result,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            1
        );
    }

    [Test]
    public async Task It_returns_403_before_if_match_and_preserves_rows_when_both_fail()
    {
        var seed = _authorizationRootChildSeeds[9];

        var result = await DeleteRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            1
        );
    }

    [Test]
    public async Task It_deletes_after_authorization_and_current_if_match_share_the_guarded_session()
    {
        var seed = _authorizationRootChildSeeds[10];
        var getResult = await GetRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );
        var etag = getResult.Should().BeOfType<GetResult.GetSuccess>().Subject.EdfiDoc[
            "_etag"
        ]!.GetValue<string>();

        var result = await DeleteRootChildAsync(
            seed,
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: etag
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _context.AssertDeleteWithIfMatchSharedGuardedSession();
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            0
        );
    }

    [Test]
    public async Task It_surfaces_stored_null_invalid_data_without_deleting_rows()
    {
        var result = await _context.DeleteByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.NullableRootEdOrgResourceName,
            _authorizationNullableSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertRelationshipDenied(result, RelationshipAuthorizationSubjectFailureKind.StoredValueNull);
        await AssertRowsAsync(
            RelationshipAuthorizationCrudTestSupport.NullableRootEdOrgResourceName,
            _authorizationNullableSeed.DocumentUuid,
            1
        );
    }

    private async Task<DeleteResult> DeleteRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<string> strategyNames,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        string? ifMatch = null
    )
    {
        return await _context.DeleteByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            claimEducationOrganizationIds ?? [ClaimEducationOrganizationId],
            strategyNames,
            ifMatch
        );
    }

    private async Task<GetResult> GetRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<string> strategyNames
    )
    {
        return await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            seed.DocumentUuid,
            [ClaimEducationOrganizationId],
            strategyNames
        );
    }

    private async Task AssertRowsAsync(string resourceName, DocumentUuid documentUuid, long expectedCount)
    {
        (await _context.CountDocumentRowsAsync(documentUuid)).Should().Be(expectedCount);
        (await _context.CountResourceRootRowsAsync(PhysicalSchema, resourceName, documentUuid))
            .Should()
            .Be(expectedCount);
    }

    private static DeleteResult.DeleteFailureRelationshipNotAuthorized AssertRelationshipDenied(
        DeleteResult result,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(expectedFailureKind);
        return failure;
    }
}
