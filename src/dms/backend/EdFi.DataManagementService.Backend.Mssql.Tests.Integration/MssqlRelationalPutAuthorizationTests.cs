// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class AuthorizationRootChildStoredProjectionInvoker : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        _ = scopeCatalog;

        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["schoolReference"]
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
}

file static class MssqlAuthorizationRootChildProfileTestSupport
{
    private static readonly QualifiedResourceName AuthorizationRootChildResource = new(
        "Authz",
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName
    );

    public static JsonNode CreateRootSchoolHiddenBody(AuthorizationRootChildSeed seed) =>
        new JsonObject { ["authorizationRootChildId"] = seed.AuthorizationRootChildId, ["name"] = seed.Name };

    public static BackendProfileWriteContext CreateRootSchoolHiddenProfileContext(
        MappingSet mappingSet,
        JsonNode writableRequestBody
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            mappingSet.WritePlansByResource[AuthorizationRootChildResource]
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableRequestBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "authorization-root-child-hidden-school-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new AuthorizationRootChildStoredProjectionInvoker()
        );
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalPut")]
public class Given_A_MssqlRelationalPutAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("eeeeeeee-1111-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("ffffffff-1111-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("ffffffff-1111-0000-0000-000000000002")), 200, "P2"),
    ];

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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _context.Database.ResetAsync();
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

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, 300);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
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
    public async Task It_authorizes_put_and_updates_document_resource_and_journal_rows()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000004",
            604,
            "authorized-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "authorized-change",
            SchoolId = 200,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        var success = result.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        after.Document.DocumentUuid.Should().Be(existingSeed.DocumentUuid.Value);
        after.DocumentChangeEventCount.Should().BeGreaterThan(before.DocumentChangeEventCount);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_stored_value_put_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000001",
            601,
            "stored-denied-no-op",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            existingSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Stored);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_returns_403_before_stale_if_match_when_proposed_put_values_are_unauthorized()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000005",
            605,
            "proposed-denied-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "proposed-denied-if-match-change",
            SchoolId = 300,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_returns_412_after_authorized_put_values_with_stale_if_match()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000006",
            606,
            "authorized-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "authorized-if-match-change",
            SchoolId = 200,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_authorizes_put_with_inverted_relationship_filtering()
    {
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(100, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(200, ClaimEducationOrganizationId);

        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000007",
            607,
            "inverted-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "inverted-change",
            SchoolId = 100,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_authorizes_profiled_put_with_hidden_stored_school_in_the_finalized_proposed_row()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000008",
            608,
            "profile-hidden-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "profile-hidden-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var writeBody = MssqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenBody(
            proposedSeed
        );
        var profileContext =
            MssqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
                _context.MappingSet,
                writeBody.DeepClone()
            );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: profileContext,
            requestBody: writeBody
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_authorizes_put_with_mssql_scalar_claim_parameters_below_the_tvp_threshold()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000009",
            609,
            "scalar-claims-existing",
            (int)ClaimEducationOrganizationId,
            []
        );
        var proposedSeed = existingSeed with { Name = "scalar-claims-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            CreateUniqueClaimEducationOrganizationIds(1999),
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _context.AssertUpdateRelationshipAuthorizationUsesScalarClaimParameters(1999);
    }

    [Test]
    public async Task It_maps_mssql_auth1_stored_null_failure_for_put()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("aaaaaaaa-3333-0000-0000-000000000010")),
            610,
            "stored-null-existing"
        );
        var proposedSeed = existingSeed with { Name = "stored-null-change", NullableSchoolId = 100 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationNullableByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.StoredValueNull
        );
    }

    [Test]
    public async Task It_maps_mssql_auth1_proposed_missing_failure_for_put()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("aaaaaaaa-3333-0000-0000-000000000011")),
            611,
            "proposed-missing-existing",
            100
        );
        var proposedSeed = existingSeed with { Name = "proposed-missing-change", NullableSchoolId = null };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );

        var result = await _context.UpdateAuthorizationNullableByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
        );
    }

    [Test]
    public async Task It_denies_proposed_value_put_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-3333-0000-0000-000000000002",
            602,
            "proposed-denied-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var proposedSeed = existingSeed with
        {
            Name = "proposed-denied-change",
            SchoolId = 300,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private static void AssertUpdateRelationshipDenied(
        UpdateResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind =
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
    )
    {
        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.ValueSource.Should().Be(expectedValueSource);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(expectedFailureKind);
    }

    private static void AssertRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationRootChildSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table => table.TableName == "authz.AuthorizationRootChildResource")
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationRootChildId"].Should().Be(expectedSeed.AuthorizationRootChildId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["School_SchoolId"].Should().Be(expectedSeed.SchoolId.ToString());
    }

    private static IReadOnlyList<long> CreateUniqueClaimEducationOrganizationIds(int uniqueCount)
    {
        if (uniqueCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(uniqueCount));
        }

        return
        [
            ClaimEducationOrganizationId,
            .. Enumerable.Range(0, uniqueCount - 1).Select(static index => 1000L + index),
        ];
    }

    private static AuthorizationRootChildSeed CreateRootChildSeed(
        string documentUuid,
        int authorizationRootChildId,
        string name,
        int schoolId,
        IReadOnlyList<ClassPeriodReferenceSeed> classPeriods
    ) =>
        new(
            new DocumentUuid(Guid.Parse(documentUuid)),
            authorizationRootChildId,
            name,
            schoolId,
            classPeriods
        );
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalPost")]
public class Given_A_MssqlRelationalPostAsUpdateAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000003")), 300, "West School"),
        new(
            new DocumentUuid(Guid.Parse("eeeeeeee-2222-0000-0000-000000000004")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("ffffffff-2222-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("ffffffff-2222-0000-0000-000000000002")), 200, "P2"),
    ];

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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _context.Database.ResetAsync();
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

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, 300);
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
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
    public async Task It_authorizes_post_as_update_and_updates_the_existing_document()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000004",
            704,
            "authorized-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000004")),
            Name = "authorized-change",
            SchoolId = 200,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        var success = result.Should().BeOfType<UpsertResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        after.Document.DocumentUuid.Should().Be(existingSeed.DocumentUuid.Value);
        after.DocumentChangeEventCount.Should().BeGreaterThan(before.DocumentChangeEventCount);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_stored_value_post_as_update_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000001",
            701,
            "stored-denied-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000001")),
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Stored);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_403_before_stale_if_match_when_proposed_post_as_update_values_are_unauthorized()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000005",
            705,
            "proposed-denied-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000005")),
            Name = "proposed-denied-if-match-change",
            SchoolId = 300,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_412_after_authorized_post_as_update_values_with_stale_if_match()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000006",
            706,
            "authorized-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000006")),
            Name = "authorized-if-match-change",
            SchoolId = 200,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            ifMatch: "\"stale-etag\""
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_post_as_update_with_inverted_relationship_filtering()
    {
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(100, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(200, ClaimEducationOrganizationId);

        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000007",
            707,
            "inverted-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000007")),
            Name = "inverted-change",
            SchoolId = 100,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.InvertedEdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_profiled_post_as_update_with_hidden_stored_school_in_the_finalized_proposed_row()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000008",
            708,
            "profile-hidden-existing",
            100,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000008")),
            Name = "profile-hidden-change",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var writeBody = MssqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenBody(
            candidateSeed
        );
        var profileContext =
            MssqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
                _context.MappingSet,
                writeBody.DeepClone()
            );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: profileContext,
            requestBody: writeBody
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_post_as_update_with_mssql_structured_tvp_claim_parameters_at_the_threshold()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000009",
            709,
            "structured-claims-existing",
            (int)ClaimEducationOrganizationId,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000009")),
            Name = "structured-claims-change",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            CreateUniqueClaimEducationOrganizationIds(2000),
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _context.AssertUpdateRelationshipAuthorizationUsesStructuredClaimParameter();
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_proposed_value_post_as_update_and_leaves_document_resource_identity_and_journal_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "aaaaaaaa-4444-0000-0000-000000000002",
            702,
            "proposed-denied-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-4444-0000-0000-000000000002")),
            Name = "proposed-denied-change",
            SchoolId = 300,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationRootChildAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    private static void AssertUpsertRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource
    )
    {
        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.ValueSource.Should().Be(expectedValueSource);
        failure
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Contain(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
    }

    private static void AssertRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationRootChildSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table => table.TableName == "authz.AuthorizationRootChildResource")
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationRootChildId"].Should().Be(expectedSeed.AuthorizationRootChildId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["School_SchoolId"].Should().Be(expectedSeed.SchoolId.ToString());
    }

    private static IReadOnlyList<long> CreateUniqueClaimEducationOrganizationIds(int uniqueCount)
    {
        if (uniqueCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(uniqueCount));
        }

        return
        [
            ClaimEducationOrganizationId,
            .. Enumerable.Range(0, uniqueCount - 1).Select(static index => 1000L + index),
        ];
    }

    private static AuthorizationRootChildSeed CreateRootChildSeed(
        string documentUuid,
        int authorizationRootChildId,
        string name,
        int schoolId,
        IReadOnlyList<ClassPeriodReferenceSeed> classPeriods
    ) =>
        new(
            new DocumentUuid(Guid.Parse(documentUuid)),
            authorizationRootChildId,
            name,
            schoolId,
            classPeriods
        );
}
