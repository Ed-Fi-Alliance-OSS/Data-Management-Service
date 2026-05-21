// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    public async Task It_authorizes_put_and_updates_document_resource_and_journal_rows()
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
        after.DocumentChangeEventCount.Should().BeGreaterThan(before.DocumentChangeEventCount);
        AssertRootRow(after, proposedSeed);
    }

    [Test]
    public async Task It_denies_stored_value_put_and_leaves_document_resource_identity_and_journal_rows_unchanged()
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
    public async Task It_denies_proposed_value_put_and_leaves_document_resource_identity_and_journal_rows_unchanged()
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
        RelationshipAuthorizationFailureValueSource expectedValueSource
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
        after.DocumentChangeEventCount.Should().BeGreaterThan(before.DocumentChangeEventCount);
        AssertRootRow(after, candidateSeed);
        (await _context.CountDocumentRowsAsync(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    [Test]
    public async Task It_denies_stored_value_post_as_update_and_leaves_document_resource_identity_and_journal_rows_unchanged()
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
    public async Task It_denies_proposed_value_post_as_update_and_leaves_document_resource_identity_and_journal_rows_unchanged()
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
