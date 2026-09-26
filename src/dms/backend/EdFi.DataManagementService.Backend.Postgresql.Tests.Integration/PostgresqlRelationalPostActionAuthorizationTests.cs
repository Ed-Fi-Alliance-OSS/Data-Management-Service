// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// A POST applies Create authorization when its target is new and Update authorization when its target exists,
/// on real PostgreSQL, for an ordinary resource and a descriptor. Every denial asserts the persisted state did
/// not move, and every race holds a real write uncommitted and waits until the POST under test is observed
/// blocked on it before letting it commit.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationalPost")]
public class Given_A_Postgresql_Post_With_Distinct_Create_And_Update_Authorization
{
    private const string NullableProject = "authz";
    private const string NullableResource = "AuthorizationNullableResource";
    private const string ContentProject = "ed-fi";
    private const string ContentResource = "EducationContent";
    private const string DescriptorProject = "ed-fi";
    private const string DescriptorResource = "SchoolTypeDescriptor";
    private const string UnauthorizedNamespace = "uri://other.example/PostAction";
    private const string SchoolProject = "ed-fi";
    private const string SchoolResource = "School";
    private const string MissingCustomViewStrategyName = "SchoolWithMissingPostActionView";
    private const short CreatorToken = 11;
    private const string RootChildProject = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string RootChildResource =
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName;
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const int AuthorizedSchoolId = (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId;
    private const int UnauthorizedSchoolId = (int)
        RelationshipAuthorizationCrudTestSupport.UnauthorizedSchoolId;
    private const short ForeignToken = 22;

    private static readonly TimeSpan _blockTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<string> _prefixes =
        RelationshipAuthorizationCrudTestSupport.ConfiguredNamespacePrefixes;

    private static readonly DocumentUuid _firstUuid = new(Guid.Parse("c5c5c5c5-0000-0000-0000-000000000001"));
    private static readonly DocumentUuid _secondUuid = new(
        Guid.Parse("c5c5c5c5-0000-0000-0000-000000000002")
    );

    private static readonly UpsertActionPolicy _noFurther = Permitted(
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
    );
    private static readonly UpsertActionPolicy _namespaceBased = Permitted(
        AuthorizationStrategyNameConstants.NamespaceBased
    );
    private static readonly UpsertActionPolicy _ownershipBased = Permitted(
        AuthorizationStrategyNameConstants.OwnershipBased
    );
    private static readonly UpsertActionPolicy _notPermitted = UpsertActionPolicy.NotPermitted.Instance;
    private static readonly UpsertActionPolicy _edOrgsOnly = Permitted(
        RelationshipAuthorizationCrudTestSupport.RelationshipsWithEdOrgsOnly
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
    public async Task SetUp() => await _context.Database.ResetAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    // ── Action difference ────────────────────────────────────────────────

    [TestCase(NullableProject, NullableResource)]
    [TestCase(DescriptorProject, DescriptorResource)]
    public async Task It_creates_under_create_only_and_refuses_every_update_without_moving_anything(
        string project,
        string resource
    )
    {
        var createOnly = Pair(_noFurther, _notPermitted);
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await PostAsync(project, resource, Body(resource, "Original"), _firstUuid, createOnly)
        );
        var before = await _context.ReadSideEffectStateAsync(project, resource, _firstUuid);

        var changed = await PostAsync(project, resource, Body(resource, "Changed"), _secondUuid, createOnly);
        var identical = await PostAsync(
            project,
            resource,
            Body(resource, "Original"),
            _secondUuid,
            createOnly
        );

        changed
            .Should()
            .Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Update));
        identical
            .Should()
            .Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Update));
        (await _context.ReadSideEffectStateAsync(project, resource, _firstUuid))
            .Should()
            .BeEquivalentTo(before);
        (await _context.CountDocumentsAsync(project, resource)).Should().Be(1);
    }

    [TestCase(NullableProject, NullableResource)]
    [TestCase(DescriptorProject, DescriptorResource)]
    public async Task It_refuses_a_create_under_update_only_and_updates_an_existing_record(
        string project,
        string resource
    )
    {
        var updateOnly = Pair(_notPermitted, _noFurther);

        var refused = await PostAsync(project, resource, Body(resource, "Original"), _firstUuid, updateOnly);

        refused
            .Should()
            .Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Create));
        (await _context.CountDocumentsAsync(project, resource)).Should().Be(0);

        await SeedAsync(project, resource, Body(resource, "Original"), _firstUuid);
        var seeded = await _context.ReadSideEffectStateAsync(project, resource, _firstUuid);

        var updated = await PostAsync(project, resource, Body(resource, "Changed"), _secondUuid, updateOnly);

        updated.Should().BeOfType<UpsertResult.UpdateSuccess>();
        var after = await _context.ReadSideEffectStateAsync(project, resource, _firstUuid);
        after.Document.ContentVersion.Should().BeGreaterThan(seeded.Document.ContentVersion);
        (await _context.CountDocumentsAsync(project, resource)).Should().Be(1);
    }

    // ── Strategy difference: namespace ───────────────────────────────────

    [TestCase(ContentProject, ContentResource)]
    [TestCase(DescriptorProject, DescriptorResource)]
    public async Task It_applies_only_the_update_namespace_check_to_an_existing_record(
        string project,
        string resource
    )
    {
        var updateChecksNamespace = Pair(_noFurther, _namespaceBased);
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await PostAsync(
                project,
                resource,
                Body(resource, "Original", UnauthorizedNamespace),
                _firstUuid,
                updateChecksNamespace
            )
        );
        var before = await _context.ReadSideEffectStateAsync(project, resource, _firstUuid);

        var changed = await PostAsync(
            project,
            resource,
            Body(resource, "Changed", UnauthorizedNamespace),
            _secondUuid,
            updateChecksNamespace
        );

        changed
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        (await _context.ReadSideEffectStateAsync(project, resource, _firstUuid))
            .Should()
            .BeEquivalentTo(before);
    }

    [TestCase(ContentProject, ContentResource)]
    [TestCase(DescriptorProject, DescriptorResource)]
    public async Task It_applies_only_the_create_namespace_check_to_a_new_record(
        string project,
        string resource
    )
    {
        var createChecksNamespace = Pair(_namespaceBased, _noFurther);

        var refused = await PostAsync(
            project,
            resource,
            Body(resource, "Original", UnauthorizedNamespace),
            _firstUuid,
            createChecksNamespace
        );

        refused
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        (await _context.CountDocumentsAsync(project, resource)).Should().Be(0);

        await SeedAsync(project, resource, Body(resource, "Original", UnauthorizedNamespace), _firstUuid);

        var updated = await PostAsync(
            project,
            resource,
            Body(resource, "Changed", UnauthorizedNamespace),
            _secondUuid,
            createChecksNamespace
        );

        updated.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    // ── Strategy difference: ownership ───────────────────────────────────

    [Test]
    public async Task It_applies_only_the_update_ownership_check_to_an_existing_record()
    {
        var updateChecksOwnership = Pair(_noFurther, _ownershipBased);
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await PostAsync(
                NullableProject,
                NullableResource,
                Body(NullableResource, "Original"),
                _firstUuid,
                updateChecksOwnership,
                creatorOwnershipTokenId: CreatorToken,
                ownershipTokenIds: [ForeignToken]
            )
        );
        var before = await _context.ReadSideEffectStateAsync(NullableProject, NullableResource, _firstUuid);

        var foreign = await PostAsync(
            NullableProject,
            NullableResource,
            Body(NullableResource, "Changed"),
            _secondUuid,
            updateChecksOwnership,
            creatorOwnershipTokenId: ForeignToken,
            ownershipTokenIds: [ForeignToken]
        );

        foreign.Should().BeOfType<UpsertResult.UpsertFailureOwnershipNotAuthorized>();
        (await _context.ReadSideEffectStateAsync(NullableProject, NullableResource, _firstUuid))
            .Should()
            .BeEquivalentTo(before);

        var owner = await PostAsync(
            NullableProject,
            NullableResource,
            Body(NullableResource, "Changed"),
            _secondUuid,
            updateChecksOwnership,
            creatorOwnershipTokenId: CreatorToken,
            ownershipTokenIds: [CreatorToken]
        );

        owner.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    // ── Strategy difference: relationship ────────────────────────────────

    [Test]
    public async Task It_applies_only_the_update_relationship_check_to_an_existing_record()
    {
        await SeedRelationshipSchoolsAsync();
        var updateChecksRelationship = Pair(_noFurther, _edOrgsOnly);

        // The school is outside the claim, and only Update carries a relationship strategy.
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await PostRelationshipAsync(
                RootChildBody("Original", UnauthorizedSchoolId),
                _firstUuid,
                updateChecksRelationship
            )
        );
        var before = await _context.ReadSideEffectStateAsync(RootChildProject, RootChildResource, _firstUuid);

        // The proposed school is authorized, so only the stored school can refuse this update.
        var changed = await PostRelationshipAsync(
            RootChildBody("Changed", AuthorizedSchoolId),
            _secondUuid,
            updateChecksRelationship
        );

        changed
            .Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Stored);
        (await _context.ReadSideEffectStateAsync(RootChildProject, RootChildResource, _firstUuid))
            .Should()
            .BeEquivalentTo(before);
        (await _context.CountDocumentsAsync(RootChildProject, RootChildResource)).Should().Be(1);
    }

    [Test]
    public async Task It_applies_only_the_create_relationship_check_to_a_new_record()
    {
        await SeedRelationshipSchoolsAsync();
        var createChecksRelationship = Pair(_edOrgsOnly, _noFurther);

        var refused = await PostRelationshipAsync(
            RootChildBody("Original", UnauthorizedSchoolId),
            _firstUuid,
            createChecksRelationship
        );

        refused
            .Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        (await _context.CountDocumentsAsync(RootChildProject, RootChildResource)).Should().Be(0);

        await SeedAsync(
            RootChildProject,
            RootChildResource,
            RootChildBody("Original", UnauthorizedSchoolId),
            _firstUuid
        );
        var seeded = await _context.ReadSideEffectStateAsync(RootChildProject, RootChildResource, _firstUuid);

        var updated = await PostRelationshipAsync(
            RootChildBody("Changed", UnauthorizedSchoolId),
            _secondUuid,
            createChecksRelationship
        );

        updated.Should().BeOfType<UpsertResult.UpdateSuccess>();
        (await _context.ReadSideEffectStateAsync(RootChildProject, RootChildResource, _firstUuid))
            .Document.ContentVersion.Should()
            .BeGreaterThan(seeded.Document.ContentVersion);
    }

    // ── Strategy difference: custom view ─────────────────────────────────

    [Test]
    public async Task It_creates_without_validating_an_update_only_custom_view_and_validates_it_for_an_existing_record()
    {
        await _context.SeedSchoolDescriptorDataAsync();
        var updateNamesAMissingView = Pair(_noFurther, Permitted(MissingCustomViewStrategyName));

        // The update policy's view does not exist. Validated or sent ahead of the capture, it would fail this
        // create under configuration the create policy does not carry.
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await PostAsync(
                SchoolProject,
                SchoolResource,
                SchoolBody("Original"),
                _firstUuid,
                updateNamesAMissingView
            )
        );
        (await _context.CountDocumentsAsync(SchoolProject, SchoolResource)).Should().Be(1);
        var before = await _context.ReadSideEffectStateAsync(SchoolProject, SchoolResource, _firstUuid);

        Func<Task> updateExisting = () =>
            PostAsync(
                SchoolProject,
                SchoolResource,
                SchoolBody("Changed"),
                _secondUuid,
                updateNamesAMissingView
            );

        var validation = await updateExisting
            .Should()
            .ThrowAsync<CustomViewAuthorizationValidationException>();
        var providerError = validation.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
        providerError.SqlState.Should().Be(PostgresErrorCodes.UndefinedTable);
        providerError.Message.Should().Contain(MissingCustomViewStrategyName);
        (await _context.ReadSideEffectStateAsync(SchoolProject, SchoolResource, _firstUuid))
            .Should()
            .BeEquivalentTo(before);
    }

    // ── Differential: only the list selection changes ────────────────────

    [TestCase(NullableProject, NullableResource)]
    [TestCase(DescriptorProject, DescriptorResource)]
    public async Task It_persists_the_same_outcome_as_the_shared_list_for_the_selected_action(
        string project,
        string resource
    )
    {
        // An existing record under (M, L) against the shared L, and a new one under (L, M) against the shared L.
        await SeedAsync(project, resource, Body(resource, "Original", identity: "A"), _firstUuid);
        await SeedAsync(project, resource, Body(resource, "Original", identity: "B"), _secondUuid);
        var sharedUpdate = await PostAsync(
            project,
            resource,
            Body(resource, "Changed", identity: "A"),
            NewUuid(),
            UpsertActionAuthorization.SamePolicyForCreateAndUpdate(Evaluators(_noFurther))
        );
        var distinctUpdate = await PostAsync(
            project,
            resource,
            Body(resource, "Changed", identity: "B"),
            NewUuid(),
            Pair(_notPermitted, _noFurther)
        );

        var sharedCreateUuid = NewUuid();
        var distinctCreateUuid = NewUuid();
        var sharedCreate = await PostAsync(
            project,
            resource,
            Body(resource, "Original", identity: "C"),
            sharedCreateUuid,
            UpsertActionAuthorization.SamePolicyForCreateAndUpdate(Evaluators(_noFurther))
        );
        var distinctCreate = await PostAsync(
            project,
            resource,
            Body(resource, "Original", identity: "D"),
            distinctCreateUuid,
            Pair(_noFurther, _notPermitted)
        );

        sharedUpdate.Should().BeOfType<UpsertResult.UpdateSuccess>();
        distinctUpdate.Should().BeOfType<UpsertResult.UpdateSuccess>();
        sharedCreate.Should().BeOfType<UpsertResult.InsertSuccess>();
        distinctCreate.Should().BeOfType<UpsertResult.InsertSuccess>();
        await AssertSameShapeAsync(project, resource, _firstUuid, _secondUuid);
        await AssertSameShapeAsync(project, resource, sharedCreateUuid, distinctCreateUuid);
    }

    // ── Races ────────────────────────────────────────────────────────────

    [Test]
    public async Task It_applies_the_create_policy_when_an_existing_record_is_deleted_under_the_capture()
    {
        await SeedAsync(NullableProject, NullableResource, Body(NullableResource, "Original"), _firstUuid);
        var updateOnly = Pair(_notPermitted, _noFurther);

        var (deleteResult, postResult) = await RaceAgainstHeldWriteAsync(
            () =>
                _context.DeleteByIdAsync(
                    NullableProject,
                    NullableResource,
                    _firstUuid,
                    [],
                    [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired]
                ),
            () =>
                PostAsync(
                    NullableProject,
                    NullableResource,
                    Body(NullableResource, "Changed"),
                    _secondUuid,
                    updateOnly
                )
        );

        deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        // The capture waited on the delete, found no row, and so selected Create. Reusing the stale Update
        // policy would have created the record instead.
        postResult
            .Should()
            .Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Create));
        (await _context.CountDocumentsAsync(NullableProject, NullableResource)).Should().Be(0);
    }

    [Test]
    public async Task It_never_commits_a_create_that_loses_to_a_concurrent_create()
    {
        var createOnly = Pair(_noFurther, _notPermitted);

        var (blockerResult, postResult) = await RaceAgainstHeldWriteAsync(
            () => SeedAsync(NullableProject, NullableResource, Body(NullableResource, "Blocker"), _firstUuid),
            () =>
                PostAsync(
                    NullableProject,
                    NullableResource,
                    Body(NullableResource, "Loser"),
                    _secondUuid,
                    createOnly
                )
        );

        blockerResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        // The capture could not see the uncommitted row, so the insert waited on its identity and then lost. A
        // POST without If-None-Match reports that as an identity conflict, which is not retried.
        postResult.Should().BeOfType<UpsertResult.UpsertFailureIdentityConflict>();
        var afterRace = await AssertOnlyTheBlockerPersistedAsync(
            NullableProject,
            NullableResource,
            "Blocker"
        );

        var nextAttempt = await PostAsync(
            NullableProject,
            NullableResource,
            Body(NullableResource, "Loser"),
            NewUuid(),
            createOnly
        );

        nextAttempt
            .Should()
            .Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Update));
        (await _context.ReadSideEffectStateAsync(NullableProject, NullableResource, _firstUuid))
            .Should()
            .BeEquivalentTo(afterRace);
    }

    [Test]
    public async Task It_reselects_the_update_policy_after_an_if_none_match_create_loses_to_a_concurrent_create()
    {
        var createOnly = Pair(_noFurther, _notPermitted);
        var ifNoneMatch = new Dictionary<string, string> { ["If-None-Match"] = "*" };

        var (blockerResult, postResult) = await RaceAgainstHeldWriteAsync(
            () => SeedAsync(NullableProject, NullableResource, Body(NullableResource, "Blocker"), _firstUuid),
            () =>
                PostAsync(
                    NullableProject,
                    NullableResource,
                    Body(NullableResource, "Loser"),
                    _secondUuid,
                    createOnly,
                    headers: ifNoneMatch
                )
        );

        blockerResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var afterRace = await AssertOnlyTheBlockerPersistedAsync(
            NullableProject,
            NullableResource,
            "Blocker"
        );
        postResult.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();

        // The retry the conflict earns captures the committed row and so applies Update, ahead of the 412
        // If-None-Match would otherwise answer.
        var retry = await PostAsync(
            NullableProject,
            NullableResource,
            Body(NullableResource, "Loser"),
            NewUuid(),
            createOnly,
            headers: ifNoneMatch
        );

        retry.Should().Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Update));
        (await _context.ReadSideEffectStateAsync(NullableProject, NullableResource, _firstUuid))
            .Should()
            .BeEquivalentTo(afterRace);
    }

    [Test]
    public async Task It_reselects_the_update_policy_after_a_descriptor_create_loses_to_a_concurrent_create()
    {
        var createOnly = Pair(_noFurther, _notPermitted);

        var (blockerResult, postResult) = await RaceAgainstHeldWriteAsync(
            () =>
                SeedAsync(
                    DescriptorProject,
                    DescriptorResource,
                    Body(DescriptorResource, "Blocker"),
                    _firstUuid
                ),
            () =>
                PostAsync(
                    DescriptorProject,
                    DescriptorResource,
                    Body(DescriptorResource, "Loser"),
                    _secondUuid,
                    createOnly
                )
        );

        blockerResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var afterRace = await AssertOnlyTheBlockerPersistedAsync(
            DescriptorProject,
            DescriptorResource,
            "Blocker"
        );
        // The lookup ran before the blocker committed and chose CreateNew; the insert then lost the identity to
        // the committed row, which fails closed to a retryable conflict.
        postResult.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();

        var retry = await PostAsync(
            DescriptorProject,
            DescriptorResource,
            Body(DescriptorResource, "Loser"),
            NewUuid(),
            createOnly
        );

        retry.Should().Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Update));
        (await _context.ReadSideEffectStateAsync(DescriptorProject, DescriptorResource, _firstUuid))
            .Should()
            .BeEquivalentTo(afterRace);
    }

    [Test]
    public async Task It_reselects_the_create_policy_after_a_descriptor_is_deleted_under_the_lock()
    {
        await SeedAsync(
            DescriptorProject,
            DescriptorResource,
            Body(DescriptorResource, "Original"),
            _firstUuid
        );
        var updateOnly = Pair(_notPermitted, _noFurther);

        var (deleteResult, postResult) = await RaceAgainstHeldWriteAsync(
            () =>
                _context.DeleteByIdAsync(
                    DescriptorProject,
                    DescriptorResource,
                    _firstUuid,
                    [],
                    [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired]
                ),
            () =>
                PostAsync(
                    DescriptorProject,
                    DescriptorResource,
                    Body(DescriptorResource, "Changed"),
                    _secondUuid,
                    updateOnly
                )
        );

        deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        // The lookup saw the row before the delete committed; the lock then found it gone, which fails closed
        // to a retryable conflict rather than updating under the stale Update policy.
        postResult.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();

        var retry = await PostAsync(
            DescriptorProject,
            DescriptorResource,
            Body(DescriptorResource, "Changed"),
            NewUuid(),
            updateOnly
        );

        retry.Should().Be(new UpsertResult.UpsertFailureTargetActionNotPermitted(UpsertTargetAction.Create));
        (await _context.CountDocumentsAsync(DescriptorProject, DescriptorResource)).Should().Be(0);
    }

    // ── Support ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts <paramref name="heldWrite"/> and holds its transaction open at commit, so its insert or delete
    /// stays uncommitted with its locks held. Then starts <paramref name="post"/>, waits until the database
    /// reports a session blocked, and only then lets the held write commit.
    /// </summary>
    private async Task<(TResult HeldResult, UpsertResult PostResult)> RaceAgainstHeldWriteAsync<TResult>(
        Func<Task<TResult>> heldWrite,
        Func<Task<UpsertResult>> post
    )
    {
        var heldAtCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.HoldNextCommit(async cancellationToken =>
        {
            heldAtCommit.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });

        var heldTask = Task.Run(heldWrite);
        await heldAtCommit.Task.WaitAsync(_blockTimeout);

        var postTask = Task.Run(post);

        try
        {
            await _context.WaitUntilASessionIsBlockedAsync(_blockTimeout);
            postTask.IsCompleted.Should().BeFalse("the POST has to be waiting on the held write");
        }
        finally
        {
            release.TrySetResult();
        }

        return (await heldTask.WaitAsync(_blockTimeout), await postTask.WaitAsync(_blockTimeout));
    }

    private Task<UpsertResult> PostAsync(
        string project,
        string resource,
        JsonNode body,
        DocumentUuid documentUuid,
        UpsertActionAuthorization actionAuthorization,
        short? creatorOwnershipTokenId = null,
        IReadOnlyList<short>? ownershipTokenIds = null,
        Dictionary<string, string>? headers = null
    ) =>
        _context.UpsertWithActionAuthorizationAsync(
            project,
            resource,
            body,
            documentUuid,
            actionAuthorization,
            _prefixes,
            creatorOwnershipTokenId,
            ownershipTokenIds,
            headers
        );

    /// <summary>
    /// Seeds one school the claim reaches through the EdOrg hierarchy and one it does not.
    /// </summary>
    private async Task SeedRelationshipSchoolsAsync()
    {
        await _context.SeedSchoolDescriptorDataAsync();
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(
                new QuerySchoolSeed(NewUuid(), AuthorizedSchoolId, "Authorized School")
            )
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(
                new QuerySchoolSeed(NewUuid(), UnauthorizedSchoolId, "Unauthorized School")
            )
        );
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, AuthorizedSchoolId);
    }

    private Task<UpsertResult> PostRelationshipAsync(
        JsonNode body,
        DocumentUuid documentUuid,
        UpsertActionAuthorization actionAuthorization
    ) =>
        _context.UpsertWithActionAuthorizationAsync(
            RootChildProject,
            RootChildResource,
            body,
            documentUuid,
            actionAuthorization,
            _prefixes,
            claimEducationOrganizationIds: [ClaimEducationOrganizationId]
        );

    private static JsonNode RootChildBody(string name, int schoolId) =>
        RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            new AuthorizationRootChildSeed(_firstUuid, 2200, name, schoolId, [])
        );

    private Task<UpsertResult> SeedAsync(
        string project,
        string resource,
        JsonNode body,
        DocumentUuid documentUuid
    ) =>
        PostAsync(
            project,
            resource,
            body,
            documentUuid,
            UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate
        );

    /// <summary>
    /// Asserts the only document of the resource is the blocker's, under the blocker's document uuid and carrying
    /// its value and none of the loser's, and returns its full state so a later attempt can be shown to leave the
    /// document row, version stamps, resource and child tables, and referential identities exactly as they are.
    /// </summary>
    private async Task<AuthorizationWriteSideEffectState> AssertOnlyTheBlockerPersistedAsync(
        string project,
        string resource,
        string expectedText
    )
    {
        (await _context.CountDocumentsAsync(project, resource)).Should().Be(1);
        var state = await _context.ReadSideEffectStateAsync(project, resource, _firstUuid);
        state
            .ResourceTables.SelectMany(static table => table.Rows)
            .SelectMany(static row => row.Values)
            .Should()
            .Contain(expectedText)
            .And.NotContain("Loser");
        state.ReferentialIdentities.Should().NotBeEmpty();
        return state;
    }

    /// <summary>
    /// Two documents written the same way persist the same tables, row counts, and request-supplied values,
    /// which is what the selected branch's outcome matching the shared list's comes down to. Identities,
    /// document ids, and the per-write stamps necessarily differ between two documents, so they are set aside.
    /// </summary>
    private async Task AssertSameShapeAsync(
        string project,
        string resource,
        DocumentUuid shared,
        DocumentUuid distinct
    )
    {
        var sharedState = await _context.ReadSideEffectStateAsync(project, resource, shared);
        var distinctState = await _context.ReadSideEffectStateAsync(project, resource, distinct);

        Shape(distinctState).Should().BeEquivalentTo(Shape(sharedState));
        Shape(sharedState)
            .Should()
            .Contain(static table => table.Values.Contains("Changed") || table.Values.Contains("Original"));

        static IEnumerable<(string Table, int RowCount, string Values)> Shape(
            AuthorizationWriteSideEffectState state
        ) =>
            state.ResourceTables.Select(static table =>
                (
                    table.TableName,
                    table.Rows.Count,
                    string.Join(
                        "|",
                        table.Rows.SelectMany(static row =>
                            row.Where(static column => _requestSuppliedColumns.Contains(column.Key))
                                .OrderBy(static column => column.Key, StringComparer.Ordinal)
                                .Select(static column => $"{column.Key}={column.Value}")
                        )
                    )
                )
            );
    }

    private static readonly HashSet<string> _requestSuppliedColumns =
    [
        "Name",
        "NullableSchoolId",
        "Namespace",
        "ShortDescription",
        "Description",
        "Discriminator",
    ];

    private static JsonNode Body(
        string resource,
        string text,
        string? @namespace = null,
        string identity = "PA"
    ) =>
        resource switch
        {
            NullableResource => new JsonObject
            {
                ["authorizationNullableId"] = identity switch
                {
                    "A" => 2101,
                    "B" => 2102,
                    "C" => 2103,
                    "D" => 2104,
                    _ => 2100,
                },
                ["name"] = text,
            },
            ContentResource => new JsonObject
            {
                ["contentIdentifier"] = $"post-action-{identity}",
                ["namespace"] = @namespace ?? "uri://ns1.org/PostAction",
                ["shortDescription"] = text,
            },
            _ => new JsonObject
            {
                ["namespace"] = @namespace ?? "uri://ns1.org/SchoolTypeDescriptor",
                ["codeValue"] = $"PostAction{identity}",
                ["shortDescription"] = text,
            },
        };

    private static JsonNode SchoolBody(string nameOfInstitution) =>
        RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(255901, nameOfInstitution);

    private static UpsertActionPolicy Permitted(string strategyName) =>
        new UpsertActionPolicy.Permitted([
            new AuthorizationStrategyEvaluator(strategyName, [], FilterOperator.And),
        ]);

    private static AuthorizationStrategyEvaluator[] Evaluators(UpsertActionPolicy policy) =>
        ((UpsertActionPolicy.Permitted)policy).Evaluators;

    private static UpsertActionAuthorization Pair(UpsertActionPolicy create, UpsertActionPolicy update) =>
        new(create, update);

    private static DocumentUuid NewUuid() => new(Guid.NewGuid());
}
