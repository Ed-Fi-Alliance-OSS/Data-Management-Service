// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// <c>OwnershipBased</c> on descriptor POST, PUT and DELETE: the create-side verdict for a POST that creates,
/// the stored-stamp check for a PUT, a DELETE or a POST that updates, and the POST token cap deferred to the
/// branch the resolved target selects.
/// </summary>
/// <remarks>
/// Every denial asserts that no data-modifying statement of any kind reached either command stream — the
/// session executor's or the commands created directly on the session — and that nothing committed and the
/// session was rolled back when one was opened. The one deliberate exception is a create whose verdict has
/// nothing configured ahead of it: it is decided before any session opens, so the assertion is that none was
/// and that no command was issued at all.
/// </remarks>
public partial class Given_Descriptor_Write_Handler_Namespace_Authorization
{
    private const short OwnedToken = 42;
    private const short OtherToken = 7;

    public enum PostTarget
    {
        Create,
        Update,
    }

    public enum OwnershipPolicyShape
    {
        /// <summary>One list for both actions, so one plan serves both branches.</summary>
        Shared,

        /// <summary>Only the branch the target selects carries <c>OwnershipBased</c>.</summary>
        SplitWithOwnershipOnTheSelectedBranch,
    }

    // ── POST that creates ────────────────────────────────────────────────

    /// <summary>
    /// The three verdicts a create can owe: no creator token (§2.14), a creator token outside the client's own
    /// list (§2.13), and the token cap (500). With nothing configured ahead of the verdict it is decided before
    /// any session opens, so not a single command is issued.
    /// </summary>
    [TestCase(CreateDenialKind.NoCreatorToken)]
    [TestCase(CreateDenialKind.CreatorTokenNotHeld)]
    [TestCase(CreateDenialKind.TokenCap)]
    public async Task It_denies_a_descriptor_post_create_the_caller_could_not_own_without_opening_a_session(
        CreateDenialKind denialKind
    )
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, CreateNewTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithCreateDenial(
                CreatePostRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                denialKind
            )
        );

        AssertCreateDenial(result, denialKind, expectedConfiguredIndex: 0);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
        AllSessionCommands(sessionFactory).Should().BeEmpty();
    }

    /// <summary>
    /// With the proposed namespace check configured ahead of it, every create verdict is returned in the
    /// ownership slot once that check authorizes — on the plain create path and on the locked-resolve path an
    /// If-Match create takes, ahead of its 412 — with no data-modifying statement and the opened session rolled
    /// back.
    /// </summary>
    [TestCase(CreateDenialKind.NoCreatorToken, false)]
    [TestCase(CreateDenialKind.CreatorTokenNotHeld, false)]
    [TestCase(CreateDenialKind.TokenCap, false)]
    [TestCase(CreateDenialKind.NoCreatorToken, true)]
    [TestCase(CreateDenialKind.CreatorTokenNotHeld, true)]
    [TestCase(CreateDenialKind.TokenCap, true)]
    public async Task It_denies_a_descriptor_post_create_in_the_ownership_slot_after_the_proposed_namespace_check_authorizes(
        CreateDenialKind denialKind,
        bool withPrecondition
    )
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);

        if (withPrecondition)
        {
            // The in-session lookup finds no row.
            sessionFactory.Session.Executor.ResultSets.Enqueue([InMemoryRelationalResultSet.Create()]);
        }

        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        var sut = CreateSut(sessionFactory, CreateNewTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithCreateDenial(
                CreatePostRequest(
                    namespacePrefixes: ["uri://ed-fi.org/"],
                    authorizationStrategies: [NamespaceStrategy(), OwnershipStrategy()],
                    writePrecondition: withPrecondition
                        ? new WritePrecondition.IfMatch("\"stale-etag\"")
                        : null
                ),
                denialKind
            )
        );

        // Attributed to OwnershipBased's configured position, which the verdict carries from the plan.
        AssertCreateDenial(result, denialKind, expectedConfiguredIndex: 1);
        sessionFactory.Session.Executor.NamespaceResults.Should().BeEmpty("the proposed namespace check ran");
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// Ownership configured first still yields to the proposed namespace denial on a create.
    /// </summary>
    [Test]
    public async Task It_reports_the_proposed_namespace_denial_ahead_of_the_create_ownership_denial()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, CreateNewTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(
                    namespacePrefixes: ["uri://ed-fi.org/"],
                    authorizationStrategies: [OwnershipStrategy(), NamespaceStrategy()],
                    @namespace: "uri://other.org/SchoolTypeDescriptor"
                ),
                creatorOwnershipTokenId: null,
                ownershipTokenIds: [OwnedToken]
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// An If-Match create owes a 412, but the ownership verdict is an authorization answer and precedes it.
    /// </summary>
    [Test]
    public async Task It_reports_the_create_ownership_denial_ahead_of_the_precondition_outcome()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, CreateNewTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(
                    namespacePrefixes: [],
                    authorizationStrategies: [OwnershipStrategy()],
                    writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
                ),
                creatorOwnershipTokenId: null,
                ownershipTokenIds: [OwnedToken]
            )
        );

        AssertUpsertOwnershipDenial(
            result,
            OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized
        );
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// A client whose creator token is among its own tokens creates and stamps as it would without
    /// OwnershipBased. A create has no stored stamp to check, so the stored-stamp check never runs.
    /// </summary>
    [Test]
    public async Task It_creates_and_stamps_a_descriptor_under_ownership_for_a_client_holding_its_creator_token()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        var sut = CreateSut(sessionFactory, CreateNewTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken, OwnedToken]
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        // The control for the no-data-modification assertions: the detector does flag a real insert.
        AllSessionCommands(sessionFactory).Should().Contain(command => IsDataModifying(command));
        sessionFactory
            .Session.Executor.Commands.Should()
            .ContainSingle(command => IsDocumentInsert(command))
            .Which.Parameters.Should()
            .ContainSingle(parameter => parameter.Name == "@createdByOwnershipTokenId")
            .Which.Value.Should()
            .Be(OwnedToken);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    // ── POST that updates, and PUT ──────────────────────────────────────

    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    public async Task It_denies_a_descriptor_post_as_update_the_caller_does_not_own_and_rolls_back(
        OwnershipAuthorizationFailureKind failureKind
    )
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowWithEdFiNamespace());
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(OwnershipDenial(failureKind))
        );
        var sut = CreateSut(sessionFactory, ExistingPostTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        AssertUpsertOwnershipDenial(result, failureKind);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// A read/modify-only client holds the row's token but no creator token. Its update is decided by the
    /// stored stamp alone; the create verdict it would owe applies only to a create.
    /// </summary>
    [Test]
    public async Task It_updates_an_owned_descriptor_through_post_for_a_client_without_a_creator_token()
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowWithEdFiNamespace());
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        var sut = CreateSut(sessionFactory, ExistingPostTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: null,
                ownershipTokenIds: [OwnedToken]
            )
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().Contain(command => IsDescriptorUpdate(command));
        // The control for the no-data-modification assertions: the detector does flag a real update.
        AllSessionCommands(sessionFactory).Should().Contain(command => IsDataModifying(command));
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    /// <summary>
    /// The stored-stamp check is the last stored check: after the stored namespace check authorizes, and
    /// before the proposed namespace check, which would also deny here but never runs.
    /// </summary>
    [Test]
    public async Task It_runs_the_stored_ownership_check_after_the_stored_namespace_check_and_before_the_proposed_one()
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRow());
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, configuredIndex: 1)
            )
        );
        var sut = CreateSut(sessionFactory, ExistingPostTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(
                    namespacePrefixes: ["uri://ed-fi.org/"],
                    authorizationStrategies: [NamespaceStrategy(), OwnershipStrategy()],
                    @namespace: "uri://other.org/SchoolTypeDescriptor"
                ),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        AssertUpsertOwnershipDenial(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 1);
        sessionFactory
            .Session.Executor.NamespaceResults.Should()
            .ContainSingle("the proposed check never ran");
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// A custom view configured after NamespaceBased is the last stored custom-view run, and ownership comes
    /// after it whatever position CMS gave OwnershipBased: the view's nonconforming 500 is reported and the
    /// ownership check, which would deny, never runs. Both locked paths.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_runs_the_stored_ownership_check_after_a_custom_view_configured_after_namespace(
        bool withPrecondition
    )
    {
        var sessionFactory = withPrecondition
            ? PreconditionLockedTargetSessionFactory()
            : LockedTargetSessionFactory(CreatePersistedDescriptorRow());
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
            )
        );
        var validationExecutor = new RecordingCustomViewValidationExecutor(
            new StubDbException("missing authorization view")
        );
        var sut = CreateSut(
            sessionFactory,
            ExistingPutTargetLookup(),
            customViewValidationCommandExecutor: validationExecutor
        );
        var request = WithOwnership(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies:
                [
                    OwnershipStrategy(),
                    NamespaceStrategy(),
                    DeleteCustomViewStrategy(),
                ]
            ),
            creatorOwnershipTokenId: OwnedToken,
            ownershipTokenIds: [OtherToken]
        ) with
        {
            WritePrecondition = withPrecondition
                ? new WritePrecondition.IfMatch("\"stale-etag\"")
                : new WritePrecondition.None(),
        };

        var act = async () => await sut.HandlePutAsync(request);

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_reports_a_stored_namespace_denial_ahead_of_the_stored_ownership_check()
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRow());
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, ExistingPostTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(
                    namespacePrefixes: ["uri://ed-fi.org/"],
                    authorizationStrategies: [OwnershipStrategy(), NamespaceStrategy()]
                ),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// An unchanged body would short-circuit as a no-op success. The ownership denial is reported instead:
    /// telling a non-owner its write changed nothing would disclose the stored representation.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task It_reports_the_ownership_denial_ahead_of_a_no_op_post_as_update(bool denied)
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowMatchingThePostBody());

        if (denied)
        {
            sessionFactory.Session.Executor.OwnershipResults.Enqueue(
                new OwnershipAuthorizationExecutionResult.NotAuthorized(
                    OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
                )
            );
        }

        var sut = CreateSut(sessionFactory, ExistingPostTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        if (denied)
        {
            AssertUpsertOwnershipDenial(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
        }
        else
        {
            // The control: authorized, the same request is the no-op it would otherwise be.
            result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        }

        AssertNoDataModification(sessionFactory);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    public async Task It_denies_a_descriptor_put_the_caller_does_not_own_and_rolls_back(
        OwnershipAuthorizationFailureKind failureKind
    )
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowWithEdFiNamespace());
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(OwnershipDenial(failureKind))
        );
        var sut = CreateSut(sessionFactory, ExistingPutTargetLookup());

        var result = await sut.HandlePutAsync(
            WithOwnership(
                CreatePutRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(failureKind);
        AssertDeniedWithRollback(sessionFactory);
    }

    [Test]
    public async Task It_updates_a_descriptor_through_put_for_its_owner()
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowWithEdFiNamespace());
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        var sut = CreateSut(sessionFactory, ExistingPutTargetLookup());

        var result = await sut.HandlePutAsync(
            WithOwnership(
                CreatePutRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OtherToken,
                ownershipTokenIds: [OwnedToken]
            )
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        AllSessionCommands(sessionFactory).Should().Contain(command => IsDataModifying(command));
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    /// <summary>
    /// A PUT that changes the descriptor's identity owes the immutable-identity failure, but a non-owner is
    /// told only that it does not own the row.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task It_reports_the_ownership_denial_ahead_of_an_immutable_identity_change(bool denied)
    {
        var sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowWithEdFiNamespace());

        if (denied)
        {
            sessionFactory.Session.Executor.OwnershipResults.Enqueue(
                new OwnershipAuthorizationExecutionResult.NotAuthorized(
                    OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
                )
            );
        }

        var sut = CreateSut(sessionFactory, ExistingPutTargetLookup());

        var result = await sut.HandlePutAsync(
            WithOwnership(
                CreatePutRequest(
                    namespacePrefixes: [],
                    authorizationStrategies: [OwnershipStrategy()],
                    codeValue: "ChangedCode"
                ),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        if (denied)
        {
            result.Should().BeOfType<UpdateResult.UpdateFailureOwnershipNotAuthorized>();
        }
        else
        {
            result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }

        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// Under a stale If-Match the stored-stamp check still runs against the locked row first, so a non-owner
    /// gets the 403 rather than a 412 that would disclose the row had changed.
    /// </summary>
    [Test]
    public async Task It_reports_the_put_ownership_denial_ahead_of_a_stale_if_match()
    {
        var sessionFactory = PreconditionLockedTargetSessionFactory();
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
            )
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandlePutAsync(
            WithOwnership(
                CreatePutRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            ) with
            {
                WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
            }
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureOwnershipNotAuthorized>();
        AssertDeniedWithRollback(sessionFactory);
    }

    [Test]
    public async Task It_reports_the_post_as_update_ownership_denial_ahead_of_a_stale_if_match()
    {
        var sessionFactory = PreconditionLockedTargetSessionFactory();
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)
            )
        );
        var sut = CreateSut(sessionFactory, ExistingPostTargetLookup());

        var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(
            WithOwnership(
                CreatePostRequest(
                    namespacePrefixes: [],
                    authorizationStrategies: [OwnershipStrategy()],
                    writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
                ),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: [OtherToken]
            )
        );

        AssertUpsertOwnershipDenial(
            result,
            OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized
        );
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// PUT reports the cap at planning, through the descriptor write resolver's cap arm, before any session.
    /// </summary>
    [Test]
    public async Task It_fails_closed_for_a_descriptor_put_at_the_ownership_token_cap_without_opening_a_session()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, ExistingPutTargetLookup());

        var result = await sut.HandlePutAsync(
            WithOwnership(
                CreatePutRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit)
            )
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .Equal(
                OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                    OwnershipTokenLimitExceededException.OwnershipTokenLimit
                )
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    // ── DELETE ───────────────────────────────────────────────────────────

    /// <summary>
    /// With only OwnershipBased configured there is no namespace or custom-view check to force the resolve and
    /// lock, so the delete must still take the locked path: the target is resolved, locked, and its stamp
    /// checked before the delete statement runs.
    /// </summary>
    [Test]
    public async Task It_deletes_an_owned_descriptor_under_an_ownership_only_configuration()
    {
        var sessionFactory = LockedDeleteTargetSessionFactory();
        EnqueueDescriptorDeleteSuccess(sessionFactory);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            WithOwnership(
                CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                ownershipTokenIds: [OwnedToken]
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        sessionFactory
            .Session.ScalarCommands.Should()
            .ContainSingle("the target is locked before its stamp is checked");
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        AllSessionCommands(sessionFactory).Should().Contain(command => IsDataModifying(command));
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    public async Task It_denies_a_descriptor_delete_the_caller_does_not_own_and_rolls_back(
        OwnershipAuthorizationFailureKind failureKind
    )
    {
        var sessionFactory = LockedDeleteTargetSessionFactory();
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(OwnershipDenial(failureKind))
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            WithOwnership(
                CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                ownershipTokenIds: [OtherToken]
            )
        );

        var failure = result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureOwnershipNotAuthorized>()
            .Subject.OwnershipFailure;
        failure.FailureKind.Should().Be(failureKind);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// A missing target is a 404 before any authorization runs, on both locked paths: there is no stored stamp
    /// to check, and a 403 would claim a row exists.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reports_not_exists_for_a_missing_descriptor_delete_target_before_the_ownership_check(
        bool withPrecondition
    )
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // The target resolve finds no row.
        sessionFactory.Session.Executor.ResultSets.Enqueue([InMemoryRelationalResultSet.Create()]);
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
            )
        );
        var sut = CreateSut(sessionFactory);
        var request = WithOwnership(
            CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
            ownershipTokenIds: [OtherToken]
        ) with
        {
            WritePrecondition = withPrecondition
                ? new WritePrecondition.IfMatch("\"stale-etag\"")
                : new WritePrecondition.None(),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        sessionFactory.Session.ScalarCommands.Should().BeEmpty("nothing was found to lock");
        AssertNoDataModification(sessionFactory);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    /// <summary>
    /// A stored-namespace denial is reported ahead of ownership, whatever position CMS gave OwnershipBased, on
    /// both locked paths.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_reports_a_stored_namespace_denial_ahead_of_the_descriptor_delete_ownership_check(
        bool withPrecondition
    )
    {
        var sessionFactory = withPrecondition
            ? PreconditionLockedTargetSessionFactory()
            : LockedDeleteTargetSessionFactory();
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
            )
        );
        var sut = CreateSut(sessionFactory);
        var request = WithOwnership(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [OwnershipStrategy(), NamespaceStrategy()]
            ),
            ownershipTokenIds: [OtherToken]
        ) with
        {
            WritePrecondition = withPrecondition
                ? new WritePrecondition.IfMatch("\"stale-etag\"")
                : new WritePrecondition.None(),
        };

        var result = await sut.HandleDeleteAsync(request);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// A custom view configured after NamespaceBased runs before ownership: the view's nonconforming 500 is
    /// reported and the ownership check, which would deny, never runs. Both locked paths.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_runs_the_descriptor_delete_ownership_check_after_a_custom_view_configured_after_namespace(
        bool withPrecondition
    )
    {
        var sessionFactory = withPrecondition
            ? PreconditionLockedTargetSessionFactory()
            : LockedDeleteTargetSessionFactory();
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.NotAuthorized(
                OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
            )
        );
        var validationExecutor = new RecordingCustomViewValidationExecutor(
            new StubDbException("missing authorization view")
        );
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);
        var request = WithOwnership(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies:
                [
                    OwnershipStrategy(),
                    NamespaceStrategy(),
                    DeleteCustomViewStrategy(),
                ]
            ),
            ownershipTokenIds: [OtherToken]
        ) with
        {
            WritePrecondition = withPrecondition
                ? new WritePrecondition.IfMatch("\"stale-etag\"")
                : new WritePrecondition.None(),
        };

        var act = async () => await sut.HandleDeleteAsync(request);

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.Session.Executor.NamespaceResults.Should().BeEmpty("the namespace check ran first");
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        AssertNoDataModification(sessionFactory);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    /// <summary>
    /// Under a stale If-Match the stored-stamp check runs against the locked row before the ETag comparison, so
    /// a non-owner gets the 403 rather than a 412 that would disclose the row had changed. The control: the
    /// owner, with the same stale ETag, gets the 412.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task It_reports_the_descriptor_delete_ownership_denial_ahead_of_a_stale_if_match(bool denied)
    {
        var sessionFactory = PreconditionLockedTargetSessionFactory();

        if (denied)
        {
            sessionFactory.Session.Executor.OwnershipResults.Enqueue(
                new OwnershipAuthorizationExecutionResult.NotAuthorized(
                    OwnershipDenial(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)
                )
            );
        }

        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            WithOwnership(
                CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                ownershipTokenIds: [denied ? OtherToken : OwnedToken]
            ) with
            {
                WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
            }
        );

        if (denied)
        {
            result.Should().BeOfType<DeleteResult.DeleteFailureOwnershipNotAuthorized>();
        }
        else
        {
            result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
        }

        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// A provider failure the ownership mapper cannot attribute is a security-configuration 500.
    /// </summary>
    [Test]
    public async Task It_maps_an_invalid_descriptor_delete_ownership_failure_to_a_security_configuration_failure()
    {
        var sessionFactory = LockedDeleteTargetSessionFactory();
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                "Ownership authorization failed, but the failure metadata could not be mapped."
            )
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            WithOwnership(
                CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                ownershipTokenIds: [OtherToken]
            )
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .Equal("Ownership authorization failed, but the failure metadata could not be mapped.");
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// The ownership check reporting the target gone is a 404, never a 403.
    /// </summary>
    [Test]
    public async Task It_reports_not_exists_when_the_descriptor_delete_ownership_check_finds_a_stale_target()
    {
        var sessionFactory = LockedDeleteTargetSessionFactory();
        sessionFactory.Session.Executor.OwnershipResults.Enqueue(
            new OwnershipAuthorizationExecutionResult.StaleTarget()
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            WithOwnership(
                CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                ownershipTokenIds: [OwnedToken]
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        AssertDeniedWithRollback(sessionFactory);
    }

    /// <summary>
    /// DELETE reports the cap at planning, before any session; one below it the delete proceeds.
    /// </summary>
    [TestCase(0)]
    [TestCase(-1)]
    public async Task It_applies_the_ownership_token_cap_to_a_descriptor_delete_before_opening_a_session(
        int offsetFromCap
    )
    {
        var sessionFactory = LockedDeleteTargetSessionFactory();
        EnqueueDescriptorDeleteSuccess(sessionFactory);
        var sut = CreateSut(sessionFactory);
        var tokenCount = OwnershipTokenLimitExceededException.OwnershipTokenLimit + offsetFromCap;

        var result = await sut.HandleDeleteAsync(
            WithOwnership(
                CreateDeleteRequest(namespacePrefixes: [], authorizationStrategies: [OwnershipStrategy()]),
                ownershipTokenIds: TokenRange(tokenCount)
            )
        );

        if (offsetFromCap == 0)
        {
            result
                .Should()
                .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
                .Which.Errors.Should()
                .Equal(
                    OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                        OwnershipTokenLimitExceededException.OwnershipTokenLimit
                    )
                );
            sessionFactory.CreateAsyncCallCount.Should().Be(0);
        }
        else
        {
            result.Should().BeOfType<DeleteResult.DeleteSuccess>();
            sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        }
    }

    // ── POST token cap ───────────────────────────────────────────────────

    /// <summary>
    /// Over the cap, a POST fails with the security-configuration 500 in the ownership slot of whichever branch
    /// its target selects, attributed to that action: before the insert for a create and before the update's
    /// proposed check for an update, with or without a precondition. The list is never sent to SQL.
    /// </summary>
    [TestCase(PostTarget.Create, false, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Create, true, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Create, false, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Create, true, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Update, false, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Update, true, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Update, false, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Update, true, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    public async Task It_fails_a_descriptor_post_at_the_ownership_token_cap_in_the_selected_branch(
        PostTarget target,
        bool withPrecondition,
        OwnershipPolicyShape policyShape
    )
    {
        var (sut, sessionFactory, request) = ArrangeCapPost(
            target,
            withPrecondition,
            OwnershipTokenLimitExceededException.OwnershipTokenLimit
        );

        var result = await sut.HandlePostAsync(request, CapPolicy(target, policyShape));

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                    OwnershipTokenLimitExceededException.OwnershipTokenLimit
                )
            );
        failure
            .TargetAction.Should()
            .Be(target is PostTarget.Create ? UpsertTargetAction.Create : UpsertTargetAction.Update);
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        AssertNoDataModification(sessionFactory);
        sessionFactory.Session.CommitCallCount.Should().Be(0);

        if (target is PostTarget.Create && !withPrecondition)
        {
            // Nothing is configured ahead of the verdict, so it is decided without a session or a command.
            sessionFactory.CreateAsyncCallCount.Should().Be(0);
            AllSessionCommands(sessionFactory).Should().BeEmpty();
        }
        else
        {
            sessionFactory.Session.RollbackCallCount.Should().Be(1);
        }
    }

    /// <summary>
    /// One below the cap, both branches behave normally in every shape: a create inserts and stamps, and an
    /// update runs the stored-stamp check once and applies. A create under an If-Match is not covered here: with
    /// no verdict ahead of it, it owes the 412.
    /// </summary>
    [TestCase(PostTarget.Create, false, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Create, false, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Update, false, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Update, false, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Update, true, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Update, true, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    public async Task It_applies_a_descriptor_post_one_below_the_ownership_token_cap(
        PostTarget target,
        bool withPrecondition,
        OwnershipPolicyShape policyShape
    )
    {
        var (sut, sessionFactory, request) = ArrangeCapPost(
            target,
            withPrecondition,
            OwnershipTokenLimitExceededException.OwnershipTokenLimit - 1
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);

        var result = await sut.HandlePostAsync(request, CapPolicy(target, policyShape));

        if (target is PostTarget.Create)
        {
            result.Should().BeOfType<UpsertResult.InsertSuccess>();
            sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        }
        else
        {
            result.Should().BeOfType<UpsertResult.UpdateSuccess>();
            sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(1);
        }

        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    /// <summary>
    /// One below the cap there is no verdict to precede it, so a create under a stale If-Match owes the 412 and
    /// rolls back without inserting.
    /// </summary>
    [TestCase(OwnershipPolicyShape.Shared)]
    [TestCase(OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    public async Task It_fails_a_descriptor_post_create_one_below_the_ownership_token_cap_under_a_stale_if_match(
        OwnershipPolicyShape policyShape
    )
    {
        var (sut, sessionFactory, request) = ArrangeCapPost(
            PostTarget.Create,
            withPrecondition: true,
            OwnershipTokenLimitExceededException.OwnershipTokenLimit - 1
        );

        var result = await sut.HandlePostAsync(request, CapPolicy(PostTarget.Create, policyShape));

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        AssertNoDataModification(sessionFactory);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    /// <summary>
    /// Under split policies the cap belongs to the branch that carries OwnershipBased: the other branch, over
    /// the same list, is not affected by it.
    /// </summary>
    [TestCase(PostTarget.Create)]
    [TestCase(PostTarget.Update)]
    public async Task It_does_not_fail_the_branch_without_ownership_at_the_ownership_token_cap(
        PostTarget target
    )
    {
        var (sut, sessionFactory, request) = ArrangeCapPost(
            target,
            withPrecondition: false,
            OwnershipTokenLimitExceededException.OwnershipTokenLimit
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);

        // Ownership on the other branch only.
        var policy =
            target is PostTarget.Create
                ? PolicyPair(create: [NoFurtherStrategy()], update: [OwnershipStrategy()])
                : PolicyPair(create: [OwnershipStrategy()], update: [NoFurtherStrategy()]);

        var result = await sut.HandlePostAsync(request, policy);

        result
            .Should()
            .BeOfType(
                target is PostTarget.Create
                    ? typeof(UpsertResult.InsertSuccess)
                    : typeof(UpsertResult.UpdateSuccess)
            );
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    /// <summary>
    /// A denial the branch owes ahead of ownership still wins over the deferred cap: the proposed namespace
    /// check on a create, the stored namespace check on an update. Asserted on both execution paths — the
    /// plain one and the locked-resolve one a precondition takes — and under shared and split policies.
    /// </summary>
    [TestCase(PostTarget.Create, false, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Create, true, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Create, false, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Create, true, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Update, false, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Update, true, OwnershipPolicyShape.Shared)]
    [TestCase(PostTarget.Update, false, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    [TestCase(PostTarget.Update, true, OwnershipPolicyShape.SplitWithOwnershipOnTheSelectedBranch)]
    public async Task It_reports_a_preceding_namespace_denial_ahead_of_the_deferred_ownership_token_cap(
        PostTarget target,
        bool withPrecondition,
        OwnershipPolicyShape policyShape
    )
    {
        RecordingNamespaceWriteSessionFactory sessionFactory;
        WritePrecondition? precondition = null;

        if (target is PostTarget.Create)
        {
            sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);

            if (withPrecondition)
            {
                // The in-session lookup finds no row. The stale tag owes a 412 if no verdict precedes it.
                sessionFactory.Session.Executor.ResultSets.Enqueue([InMemoryRelationalResultSet.Create()]);
                precondition = new WritePrecondition.IfMatch("\"stale-etag\"");
            }
        }
        else if (withPrecondition)
        {
            sessionFactory = PreconditionLockedTargetSessionFactory(CreatePersistedDescriptorRow());
            precondition = new WritePrecondition.IfMatch(ExpectedComposedDescriptorEtag(44L));
        }
        else
        {
            sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRow());
        }

        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(
                target is PostTarget.Create ? ProposedMismatchFailure() : StoredMismatchFailure()
            )
        );
        var sut = CreateSut(
            sessionFactory,
            target is PostTarget.Create ? CreateNewTargetLookup() : ExistingPostTargetLookup()
        );

        // Ownership configured first, so only precedence can put the namespace answer ahead of the cap.
        AuthorizationStrategyEvaluator[] ownershipThenNamespace = [OwnershipStrategy(), NamespaceStrategy()];
        var policy = (policyShape, target) switch
        {
            (OwnershipPolicyShape.Shared, _) => UpsertActionAuthorization.SamePolicyForCreateAndUpdate(
                ownershipThenNamespace
            ),
            (_, PostTarget.Create) => PolicyPair(
                create: ownershipThenNamespace,
                update: [NoFurtherStrategy()]
            ),
            _ => PolicyPair(create: [NoFurtherStrategy()], update: ownershipThenNamespace),
        };

        var result = await sut.HandlePostAsync(
            WithOwnership(
                CreatePostRequest(
                    namespacePrefixes: ["uri://ed-fi.org/"],
                    authorizationStrategies: [],
                    @namespace: "uri://other.org/SchoolTypeDescriptor",
                    writePrecondition: precondition
                ),
                creatorOwnershipTokenId: OwnedToken,
                ownershipTokenIds: TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit)
            ),
            policy
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.ValueSource.Should()
            .Be(
                target is PostTarget.Create
                    ? NamespaceAuthorizationFailureValueSource.Proposed
                    : NamespaceAuthorizationFailureValueSource.Stored
            );
        sessionFactory.Session.Executor.OwnershipCallCount.Should().Be(0);
        AssertDeniedWithRollback(sessionFactory);
    }

    // ── Support ──────────────────────────────────────────────────────────

    public enum CreateDenialKind
    {
        /// <summary>No creator token: §2.14.</summary>
        NoCreatorToken,

        /// <summary>A creator token outside the client's own list: §2.13.</summary>
        CreatorTokenNotHeld,

        /// <summary>2,000 tokens, the creator token among them: the cap 500.</summary>
        TokenCap,
    }

    private static DescriptorWriteRequest WithCreateDenial(
        DescriptorWriteRequest request,
        CreateDenialKind denialKind
    ) =>
        denialKind switch
        {
            CreateDenialKind.NoCreatorToken => WithOwnership(request, null, [OwnedToken]),
            CreateDenialKind.CreatorTokenNotHeld => WithOwnership(request, OwnedToken, [OtherToken]),
            _ => WithOwnership(
                request,
                OwnedToken,
                TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit)
            ),
        };

    private static void AssertCreateDenial(
        UpsertResult result,
        CreateDenialKind denialKind,
        int expectedConfiguredIndex
    )
    {
        switch (denialKind)
        {
            case CreateDenialKind.NoCreatorToken:
                AssertUpsertOwnershipDenial(
                    result,
                    OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized,
                    expectedConfiguredIndex
                );
                break;
            case CreateDenialKind.CreatorTokenNotHeld:
                AssertUpsertOwnershipDenial(
                    result,
                    OwnershipAuthorizationFailureKind.OwnershipTokenMismatch,
                    expectedConfiguredIndex
                );
                break;
            default:
                var failure = result
                    .Should()
                    .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
                    .Subject;
                failure
                    .Errors.Should()
                    .Equal(
                        OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                            OwnershipTokenLimitExceededException.OwnershipTokenLimit
                        )
                    );
                failure.TargetAction.Should().Be(UpsertTargetAction.Create);
                break;
        }
    }

    /// <summary>
    /// Every command the session saw: the ones created directly on it (the target lock) and the ones its
    /// command executor ran.
    /// </summary>
    private static IEnumerable<RelationalCommand> AllSessionCommands(
        RecordingNamespaceWriteSessionFactory sessionFactory
    ) => sessionFactory.Session.ScalarCommands.Concat(sessionFactory.Session.Executor.Commands);

    /// <summary>
    /// Table-independent: any statement that inserts, updates, deletes, merges or truncates, whichever table it
    /// names. <c>UPDATE</c> is matched only as a statement (<c>UPDATE target SET</c>), so a lock's
    /// <c>FOR UPDATE</c> clause is not mistaken for a write.
    /// </summary>
    private static bool IsDataModifying(RelationalCommand command) =>
        _dataModifyingStatement.IsMatch(command.CommandText);

    private static readonly Regex _dataModifyingStatement = new(
        """\bINSERT\s+INTO\b|\bDELETE\s+FROM\b|\bMERGE\b|\bTRUNCATE\b|\bUPDATE\s+[\w.\[\]"]+\s+SET\b""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>The etag a write precondition must carry to match the stubbed locked row's content version.</summary>
    private static string ExpectedComposedDescriptorEtag(long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            DescriptorEtagTestSupport.NoProfileNoLinksJsonVariantKey(
                CreateMappingSet(SqlDialect.Pgsql).Key.EffectiveSchemaHash
            )
        );

    private static void AssertNoDataModification(RecordingNamespaceWriteSessionFactory sessionFactory) =>
        AllSessionCommands(sessionFactory).Should().NotContain(command => IsDataModifying(command));

    /// <summary>
    /// Arranges a POST over the given token list, with its creator token in the list, for either target. With
    /// a precondition the request takes the locked-resolve path: an update under an If-Match the target
    /// satisfies, a create under a stale one it would fail with a 412, so only ownership can stop either.
    /// </summary>
    private static (
        DescriptorWriteHandler Sut,
        RecordingNamespaceWriteSessionFactory SessionFactory,
        DescriptorWriteRequest Request
    ) ArrangeCapPost(PostTarget target, bool withPrecondition, int tokenCount)
    {
        RecordingNamespaceWriteSessionFactory sessionFactory;
        StubRelationalWriteTargetLookupService targetLookup;
        WritePrecondition precondition = new WritePrecondition.None();

        if (target is PostTarget.Create)
        {
            sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
            targetLookup = CreateNewTargetLookup();

            if (withPrecondition)
            {
                // The in-session lookup finds no row. The stale tag owes a 412 if no verdict precedes it.
                sessionFactory.Session.Executor.ResultSets.Enqueue([InMemoryRelationalResultSet.Create()]);
                precondition = new WritePrecondition.IfMatch("\"stale-etag\"");
            }
        }
        else if (withPrecondition)
        {
            sessionFactory = PreconditionLockedTargetSessionFactory(
                CreatePersistedDescriptorRowWithEdFiNamespace()
            );
            targetLookup = ExistingPostTargetLookup();
            precondition = new WritePrecondition.IfMatch(ExpectedComposedDescriptorEtag(44L));
        }
        else
        {
            sessionFactory = LockedTargetSessionFactory(CreatePersistedDescriptorRowWithEdFiNamespace());
            targetLookup = ExistingPostTargetLookup();
        }

        var request = WithOwnership(
            CreatePostRequest(
                namespacePrefixes: [],
                authorizationStrategies: [],
                writePrecondition: precondition
            ),
            creatorOwnershipTokenId: OwnedToken,
            ownershipTokenIds: TokenRange(tokenCount)
        );

        return (CreateSut(sessionFactory, targetLookup), sessionFactory, request);
    }

    private static UpsertActionAuthorization CapPolicy(PostTarget target, OwnershipPolicyShape policyShape) =>
        policyShape switch
        {
            OwnershipPolicyShape.Shared => UpsertActionAuthorization.SamePolicyForCreateAndUpdate([
                OwnershipStrategy(),
            ]),
            _ => target is PostTarget.Create
                ? PolicyPair(create: [OwnershipStrategy()], update: [NoFurtherStrategy()])
                : PolicyPair(create: [NoFurtherStrategy()], update: [OwnershipStrategy()]),
        };

    private static AuthorizationStrategyEvaluator OwnershipStrategy() =>
        new(AuthorizationStrategyNameConstants.OwnershipBased, [], FilterOperator.And);

    private static DescriptorWriteRequest WithOwnership(
        DescriptorWriteRequest request,
        short? creatorOwnershipTokenId,
        IReadOnlyList<short> ownershipTokenIds
    ) =>
        request with
        {
            RelationalAuthorizationContext = new RelationalAuthorizationContext(
                [],
                request.RelationalAuthorizationContext.NamespacePrefixes,
                creatorOwnershipTokenId,
                ownershipTokenIds
            ),
        };

    private static DescriptorDeleteRequest WithOwnership(
        DescriptorDeleteRequest request,
        IReadOnlyList<short> ownershipTokenIds
    ) =>
        request with
        {
            RelationalAuthorizationContext = new RelationalAuthorizationContext(
                [],
                request.RelationalAuthorizationContext.NamespacePrefixes,
                null,
                ownershipTokenIds
            ),
        };

    /// <summary>The no-precondition DELETE path: target resolve, then the lock scalar.</summary>
    private static RecordingNamespaceWriteSessionFactory LockedDeleteTargetSessionFactory()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        return sessionFactory;
    }

    /// <summary>The descriptor delete statement's result: the deleted document id.</summary>
    private static void EnqueueDescriptorDeleteSuccess(
        RecordingNamespaceWriteSessionFactory sessionFactory
    ) =>
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);

    private static short[] TokenRange(int count) =>
        [.. Enumerable.Range(1, count).Select(static tokenId => (short)tokenId)];

    private static OwnershipAuthorizationFailure OwnershipDenial(
        OwnershipAuthorizationFailureKind failureKind,
        int configuredIndex = 0
    ) => new(failureKind, configuredIndex, AuthorizationStrategyNameConstants.OwnershipBased);

    private static void AssertUpsertOwnershipDenial(
        UpsertResult result,
        OwnershipAuthorizationFailureKind expectedKind,
        int expectedConfiguredIndex = 0
    )
    {
        var failure = result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureOwnershipNotAuthorized>()
            .Subject.OwnershipFailure;
        failure.FailureKind.Should().Be(expectedKind);
        failure.ConfiguredStrategyIndex.Should().Be(expectedConfiguredIndex);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// A denial inside an opened session: no data-modifying statement on either command stream, nothing
    /// committed, rolled back.
    /// </summary>
    private static void AssertDeniedWithRollback(RecordingNamespaceWriteSessionFactory sessionFactory)
    {
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        AssertNoDataModification(sessionFactory);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    /// <summary>The no-precondition locked path: lock scalar, then the persisted-row read.</summary>
    private static RecordingNamespaceWriteSessionFactory LockedTargetSessionFactory(
        InMemoryRelationalResultSet persistedRow
    )
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([persistedRow]);
        return sessionFactory;
    }

    /// <summary>The precondition path: in-session target resolve, lock scalar, then the persisted-row read.</summary>
    private static RecordingNamespaceWriteSessionFactory PreconditionLockedTargetSessionFactory(
        InMemoryRelationalResultSet? persistedRow = null
    )
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([persistedRow ?? CreatePersistedDescriptorRow()]);
        return sessionFactory;
    }

    private static StubRelationalWriteTargetLookupService ExistingPostTargetLookup() =>
        new()
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, _documentUuid, 44L),
        };

    private static StubRelationalWriteTargetLookupService ExistingPutTargetLookup() =>
        new()
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, _documentUuid, 44L),
        };

    /// <summary>
    /// The stored state of the POST request body exactly, so an authorized upsert is a no-op.
    /// </summary>
    private static InMemoryRelationalResultSet CreatePersistedDescriptorRowMatchingThePostBody() =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["CodeValue"] = "Charter",
                ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                ["ShortDescription"] = "Charter",
                ["Description"] = null,
                ["EffectiveBeginDate"] = null,
                ["EffectiveEndDate"] = null,
            }
        );
}
