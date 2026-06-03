// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class AuthorizationRootChildStoredProjectionInvoker(ImmutableArray<string> hiddenMemberPaths)
    : IStoredStateProjectionInvoker
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
                    HiddenMemberPaths: hiddenMemberPaths
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
}

file static class PostgresqlAuthorizationRootChildProfileTestSupport
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
    ) => CreateRootProfileContext(mappingSet, writableRequestBody, ["schoolReference"]);

    public static BackendProfileWriteContext CreateRootSchoolVisibleProfileContext(
        MappingSet mappingSet,
        JsonNode writableRequestBody
    ) => CreateRootProfileContext(mappingSet, writableRequestBody, []);

    private static BackendProfileWriteContext CreateRootProfileContext(
        MappingSet mappingSet,
        JsonNode writableRequestBody,
        ImmutableArray<string> hiddenMemberPaths
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
            StoredStateProjectionInvoker: new AuthorizationRootChildStoredProjectionInvoker(hiddenMemberPaths)
        );
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationalPut")]
public class Given_A_PostgresqlRelationalPutAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-1111-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-1111-0000-0000-000000000002")), 200, "P2"),
    ];

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
    public async Task It_authorizes_put_and_updates_document_resource()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000004",
            404,
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
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_stored_value_put_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000001",
            401,
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
            "cccccccc-1111-0000-0000-000000000005",
            405,
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
            "cccccccc-1111-0000-0000-000000000006",
            406,
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
            "cccccccc-1111-0000-0000-000000000007",
            407,
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
            "cccccccc-1111-0000-0000-000000000008",
            408,
            "profile-hidden-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "profile-hidden-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var writeBody = PostgresqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenBody(
            proposedSeed
        );
        var profileContext =
            PostgresqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
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
    public async Task It_denies_profiled_put_with_unauthorized_finalized_proposed_school_and_leaves_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000009",
            409,
            "profile-proposed-denied-existing",
            100,
            []
        );
        var proposedSeed = existingSeed with { Name = "profile-proposed-denied-change", SchoolId = 300 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            proposedSeed
        );
        var profileContext =
            PostgresqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
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

        AssertUpdateRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_maps_stored_null_put_failure_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("cccccccc-1111-0000-0000-000000000010")),
            410,
            "stored-null-existing"
        );
        var proposedSeed = existingSeed with { Name = "stored-null-change", NullableSchoolId = 100 };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

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
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_maps_proposed_missing_put_failure_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("cccccccc-1111-0000-0000-000000000011")),
            411,
            "proposed-missing-existing",
            100
        );
        var proposedSeed = existingSeed with { Name = "proposed-missing-change", NullableSchoolId = null };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

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
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_proposed_value_put_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000002",
            402,
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
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

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
[Category("PostgresqlIntegration")]
[Category("RelationalPost")]
public class Given_A_PostgresqlRelationalPostAsUpdateAuthorizationTests_With_A_Synthetic_EdOrg_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-2222-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-2222-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-2222-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("bbbbbbbb-2222-0000-0000-000000000002")), 200, "P2"),
    ];

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
            "cccccccc-2222-0000-0000-000000000004",
            504,
            "authorized-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000004")),
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
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_stored_value_post_as_update_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000001",
            501,
            "stored-denied-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000001")),
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
            "cccccccc-2222-0000-0000-000000000005",
            505,
            "proposed-denied-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000005")),
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
            "cccccccc-2222-0000-0000-000000000006",
            506,
            "authorized-if-match-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000006")),
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
            "cccccccc-2222-0000-0000-000000000007",
            507,
            "inverted-existing",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000007")),
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
            "cccccccc-2222-0000-0000-000000000008",
            508,
            "profile-hidden-existing",
            100,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000008")),
            Name = "profile-hidden-change",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var writeBody = PostgresqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenBody(
            candidateSeed
        );
        var profileContext =
            PostgresqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
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
    public async Task It_denies_profiled_post_as_update_with_unauthorized_finalized_proposed_school_and_leaves_rows_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000009",
            509,
            "profile-proposed-denied-existing",
            100,
            []
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000009")),
            Name = "profile-proposed-denied-change",
            SchoolId = 300,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            candidateSeed
        );
        var profileContext =
            PostgresqlAuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
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

        AssertUpsertRelationshipDenied(result, RelationshipAuthorizationFailureValueSource.Proposed);
        var after = await _context.ReadAuthorizationRootChildSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_maps_stored_null_post_as_update_failure_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("cccccccc-2222-0000-0000-000000000010")),
            510,
            "stored-null-existing"
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000010")),
            Name = "stored-null-change",
            NullableSchoolId = 100,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationNullableAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.StoredValueNull
        );
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_maps_proposed_missing_post_as_update_failure_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = new AuthorizationNullableSeed(
            new DocumentUuid(Guid.Parse("cccccccc-2222-0000-0000-000000000011")),
            511,
            "proposed-missing-existing",
            100
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000011")),
            Name = "proposed-missing-change",
            NullableSchoolId = null,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationNullableAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);

        var result = await _context.UpsertAuthorizationNullableAsync(
            candidateSeed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertUpsertRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
        );
        var after = await _context.ReadAuthorizationNullableSideEffectStateAsync(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_proposed_value_post_as_update_and_leaves_document_resource_identity_unchanged()
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000002",
            502,
            "proposed-denied-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000002")),
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
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind =
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
    )
    {
        if (result is UpsertResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

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
[Category("PostgresqlIntegration")]
[Category("RelationalPut")]
[Category("RelationalPost")]
public class Given_A_PostgresqlRelationalPutAuthorizationTests_With_A_Synthetic_People_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-5555-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("aaaaaaaa-5555-0000-0000-000000000002")), 300, "West School"),
    ];

    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000001")),
        2026,
        true,
        "2026"
    );

    private static readonly StudentSeed _authorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000101")),
        "put-10001",
        "Ari",
        "Able"
    );

    private static readonly StudentSeed _unauthorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000102")),
        "put-10002",
        "Blake",
        "Baker"
    );

    private static readonly StudentSchoolAssociationSeed _authorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000201")),
        _authorizedStudentSeed.StudentUniqueId,
        100,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );

    private static readonly StudentSchoolAssociationSeed _unauthorizedStudentSchoolAssociationSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000202")),
        _unauthorizedStudentSeed.StudentUniqueId,
        300,
        _schoolYearSeed.SchoolYear,
        EntryGradeLevelDescriptor,
        new DateOnly(2026, 8, 15)
    );

    private static readonly StudentAcademicRecordSeed _authorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000301")),
        100,
        _schoolYearSeed.SchoolYear,
        _authorizedStudentSeed.StudentUniqueId,
        TermDescriptor
    );

    private static readonly StudentAcademicRecordSeed _unauthorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("bbbbbbbb-5555-0000-0000-000000000302")),
        300,
        _schoolYearSeed.SchoolYear,
        _unauthorizedStudentSeed.StudentUniqueId,
        TermDescriptor
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

        await _context.SeedTermDescriptorAsync(
            Guid.Parse("bbbbbbbb-5555-0000-0000-000000000501"),
            TermDescriptor
        );
        await _context.SeedSchoolYearTypeAsync(_schoolYearSeed);
        await _context.SeedStudentAsync(_authorizedStudentSeed);
        await _context.SeedStudentAsync(_unauthorizedStudentSeed);
        await _context.SeedStudentSchoolAssociationAsync(_authorizedStudentSchoolAssociationSeed);
        await _context.SeedStudentSchoolAssociationAsync(_unauthorizedStudentSchoolAssociationSeed);
        await _context.SeedStudentAcademicRecordAsync(_authorizedStudentAcademicRecordSeed);
        await _context.SeedStudentAcademicRecordAsync(_unauthorizedStudentAcademicRecordSeed);

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
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
    public async Task It_authorizes_people_put_and_updates_the_existing_document()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000001",
            801,
            "people-put-authorized-existing",
            _authorizedStudentAcademicRecordSeed
        );
        var proposedSeed = existingSeed with { Name = "people-put-authorized-change" };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(proposedSeed, existingSeed.DocumentUuid);

        var success = result.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingSeed.DocumentUuid);
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Document.ContentVersion.Should().BeGreaterThan(before.Document.ContentVersion);
        AssertPeopleRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_people_put_on_stored_values_before_request_reference_resolution()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000002",
            802,
            "people-put-stored-denied-existing",
            _unauthorizedStudentAcademicRecordSeed
        );
        var proposedSeed = existingSeed with
        {
            Name = "people-put-stored-denied-missing-reference",
            StudentUniqueId = "missing-student",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(proposedSeed, existingSeed.DocumentUuid);

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_returns_people_403_before_stale_if_match_when_put_proposed_values_are_unauthorized()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000003",
            803,
            "people-put-proposed-denied-existing",
            _authorizedStudentAcademicRecordSeed
        );
        var proposedSeed = existingSeed with
        {
            Name = "people-put-proposed-denied-change",
            EducationOrganizationId = _unauthorizedStudentAcademicRecordSeed.EducationOrganizationId,
            StudentUniqueId = _unauthorizedStudentAcademicRecordSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            ifMatch: "\"stale-etag\""
        );

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_put_for_unauthorized_people_only_proposed_value()
    {
        var existingSeed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-5555-0000-0000-000000000008")),
            808,
            "people-only-direct-put-existing",
            100,
            _authorizedStudentSeed.StudentUniqueId
        );
        var proposedSeed = existingSeed with
        {
            Name = "people-only-direct-put-denied-change",
            StudentUniqueId = _unauthorizedStudentSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentSchoolAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentSchoolAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        AssertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithPeopleOnly
        );
        var after = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_put_for_unauthorized_edorgs_and_people_proposed_value()
    {
        var existingSeed = new AuthorizationStudentSchoolSeed(
            new DocumentUuid(Guid.Parse("cccccccc-5555-0000-0000-000000000009")),
            809,
            "mixed-direct-put-existing",
            100,
            _authorizedStudentSeed.StudentUniqueId
        );
        var proposedSeed = existingSeed with
        {
            Name = "mixed-direct-put-denied-change",
            StudentUniqueId = _unauthorizedStudentSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentSchoolAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentSchoolAsync(
            proposedSeed,
            existingSeed.DocumentUuid,
            RelationshipAuthorizationCrudTestSupport.EdOrgAndPeopleStrategyNames
        );

        AssertDirectStudentRelationshipDenied(
            result,
            RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsAndPeople
        );
        var after = await _context.ReadAuthorizationStudentSchoolSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_authorizes_people_put_proposed_values_before_guarded_no_op_success()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000004",
            804,
            "people-put-guarded-no-op",
            _authorizedStudentAcademicRecordSeed
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PutStudentAcademicRecordAsync(existingSeed, existingSeed.DocumentUuid);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _context.AssertPeopleUpdateRunsStoredThenProposedRelationshipAuthorization();
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_denies_people_post_as_update_on_stored_values_before_request_reference_resolution()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000005",
            805,
            "people-post-update-stored-denied-existing",
            _unauthorizedStudentAcademicRecordSeed
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-5555-0000-0000-000000000005")),
            Name = "people-post-update-stored-denied-missing-reference",
            StudentUniqueId = "missing-student",
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PostStudentAcademicRecordAsync(candidateSeed);

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Stored,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_people_403_before_stale_if_match_when_post_as_update_proposed_values_are_unauthorized()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000006",
            806,
            "people-post-update-proposed-denied-existing",
            _authorizedStudentAcademicRecordSeed
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-5555-0000-0000-000000000006")),
            Name = "people-post-update-proposed-denied-change",
            EducationOrganizationId = _unauthorizedStudentAcademicRecordSeed.EducationOrganizationId,
            StudentUniqueId = _unauthorizedStudentAcademicRecordSeed.StudentUniqueId,
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PostStudentAcademicRecordAsync(candidateSeed, ifMatch: "\"stale-etag\"");

        AssertPeopleRelationshipDenied(
            result,
            RelationshipAuthorizationFailureValueSource.Proposed,
            RelationshipAuthorizationSubjectFailureKind.NoRelationship
        );
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_people_post_as_update_proposed_values_before_guarded_no_op_success()
    {
        var existingSeed = CreateAuthorizationStudentAcademicRecordSeed(
            "cccccccc-5555-0000-0000-000000000007",
            807,
            "people-post-update-guarded-no-op",
            _authorizedStudentAcademicRecordSeed
        );
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-5555-0000-0000-000000000007")),
        };

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(existingSeed)
        );
        var before = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );

        var result = await PostStudentAcademicRecordAsync(candidateSeed);

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _context.AssertPeopleUpdateRunsStoredThenProposedRelationshipAuthorization();
        var after = await _context.ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
            existingSeed.DocumentUuid
        );
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    private async Task<UpdateResult> PutStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        DocumentUuid documentUuid,
        string? ifMatch = null
    )
    {
        return await _context.UpdateAuthorizationStudentAcademicRecordByIdAsync(
            seed,
            documentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames,
            ifMatch
        );
    }

    private async Task<UpdateResult> PutStudentSchoolAsync(
        AuthorizationStudentSchoolSeed seed,
        DocumentUuid documentUuid,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await _context.UpdateAuthorizationStudentSchoolByIdAsync(
            seed,
            documentUuid,
            [ClaimEducationOrganizationId],
            strategyNames,
            ifMatch
        );
    }

    private async Task<UpsertResult> PostStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        string? ifMatch = null
    )
    {
        return await _context.UpsertAuthorizationStudentAcademicRecordAsync(
            seed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames,
            ifMatch
        );
    }

    private static void AssertPeopleRelationshipDenied(
        UpdateResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        AssertPeopleRelationshipFailure(
            failure.RelationshipFailure,
            expectedValueSource,
            expectedFailureKind
        );
    }

    private static void AssertDirectStudentRelationshipDenied(
        UpdateResult result,
        string expectedStrategyName
    )
    {
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        AssertDirectStudentRelationshipFailure(failure.RelationshipFailure, expectedStrategyName);
    }

    private static void AssertPeopleRelationshipDenied(
        UpsertResult result,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        if (result is UpsertResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        AssertPeopleRelationshipFailure(
            failure.RelationshipFailure,
            expectedValueSource,
            expectedFailureKind
        );
    }

    private static void AssertDirectStudentRelationshipFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        string expectedStrategyName
    )
    {
        relationshipFailure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Proposed);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);

        var failedStrategy = relationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy.StrategyName.Should().Be(expectedStrategyName);
        failedStrategy.StrategyKind.Should().Be(expectedStrategyName);

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.RootBinding.TableName.Should().Be("authz.AuthorizationStudentSchoolResource");
        failedSubject.RootBinding.ColumnName.Should().Be("Student_DocumentId");
        failedSubject
            .SecurableElements.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSecurableElement(
                    "Student",
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                )
            );
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.PathKind.Should().Be("DirectRootColumn");
        failedSubject.PersonSubject.ProposedAnchor.Should().NotBeNull();
        failedSubject.PersonSubject.ProposedAnchor!.Kind.Should().Be("RootRow");
        failedSubject.PersonSubject.ProposedAnchor.Binding.ColumnName.Should().Be("Student_DocumentId");
        failedSubject.PersonSubject.Hint.Should().Contain("StudentSchoolAssociation");
    }

    private static void AssertPeopleRelationshipFailure(
        RelationshipAuthorizationFailure relationshipFailure,
        RelationshipAuthorizationFailureValueSource expectedValueSource,
        RelationshipAuthorizationSubjectFailureKind expectedFailureKind
    )
    {
        relationshipFailure.ValueSource.Should().Be(expectedValueSource);
        relationshipFailure
            .ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(ClaimEducationOrganizationId);

        var failedStrategy = relationshipFailure.FailedStrategies.Should().ContainSingle().Subject;
        failedStrategy
            .StrategyName.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.RelationshipsWithStudentsOnly);
        failedStrategy
            .StrategyKind.Should()
            .Be(RelationshipAuthorizationCrudTestSupport.RelationshipsWithStudentsOnly);

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Subject;
        failedSubject.FailureKind.Should().Be(expectedFailureKind);
        failedSubject.RootBinding.TableName.Should().Be("authz.AuthorizationStudentAcademicRecordResource");
        failedSubject
            .SecurableElements.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RelationshipAuthorizationSecurableElement(
                    "Student",
                    "$.studentAcademicRecordReference.studentUniqueId",
                    "StudentUniqueId"
                )
            );
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.PathKind.Should().Be("TransitiveJoinPath");
        failedSubject.PersonSubject.Hint.Should().Contain("StudentSchoolAssociation");

        if (expectedValueSource is RelationshipAuthorizationFailureValueSource.Proposed)
        {
            failedSubject.PersonSubject.ProposedAnchor.Should().NotBeNull();
            failedSubject
                .PersonSubject.ProposedAnchor!.Binding.ColumnName.Should()
                .Be("StudentAcademicRecord_DocumentId");
        }
    }

    private static void AssertPeopleRootRow(
        AuthorizationWriteSideEffectState state,
        AuthorizationStudentAcademicRecordSeed expectedSeed
    )
    {
        var rootRow = state
            .ResourceTables.Single(static table =>
                table.TableName == "authz.AuthorizationStudentAcademicRecordResource"
            )
            .Rows.Should()
            .ContainSingle()
            .Subject;

        rootRow["AuthorizationStudentAcademicRecordId"]
            .Should()
            .Be(expectedSeed.AuthorizationStudentAcademicRecordId.ToString());
        rootRow["Name"].Should().Be(expectedSeed.Name);
        rootRow["StudentAcademicRecord_EducationOrganizationId"]
            .Should()
            .Be(expectedSeed.EducationOrganizationId.ToString());
        rootRow["StudentAcademicRecord_SchoolYear"].Should().Be(expectedSeed.SchoolYear.ToString());
        rootRow["StudentAcademicRecord_StudentUniqueId"].Should().Be(expectedSeed.StudentUniqueId);
    }

    private static AuthorizationStudentAcademicRecordSeed CreateAuthorizationStudentAcademicRecordSeed(
        string documentUuid,
        int authorizationStudentAcademicRecordId,
        string name,
        StudentAcademicRecordSeed studentAcademicRecordSeed
    ) =>
        new(
            new DocumentUuid(Guid.Parse(documentUuid)),
            authorizationStudentAcademicRecordId,
            name,
            studentAcademicRecordSeed.EducationOrganizationId,
            studentAcademicRecordSeed.SchoolYear,
            studentAcademicRecordSeed.StudentUniqueId,
            studentAcademicRecordSeed.TermDescriptor
        );
}
