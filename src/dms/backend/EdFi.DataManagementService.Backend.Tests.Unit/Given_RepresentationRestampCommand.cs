// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Reflection;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RepresentationRestampCommand
{
    private static readonly DocumentCacheAdministrativeTargetKey _target =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(DocumentCacheTargetKey.Create("TenantA", 7));

    [Test]
    public async Task It_creates_a_zero_selection_draft_through_the_real_runner_without_stamping()
    {
        Harness harness = CreateHarness();
        DocumentCacheRepresentationRestampPreviewRequest request = PreviewRequest();
        A.CallTo(() =>
                harness.Store.GetMaxChangeVersionAsync(A<IRelationalWriteSession>._, A<CancellationToken>._)
            )
            .Returns(41);
        A.CallTo(() =>
                harness.Store.ResolveSelectionAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampScope>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new RepresentationRestampSelection(request.Scope, 0));

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(request);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        A.CallTo(() =>
                harness.Store.CreateDraftAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>.That.Matches(operation =>
                        operation.State == DocumentCacheRepresentationRestampOperationState.Draft
                        && operation.PreviewDocumentCount == 0
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                harness.Store.StampPageAsync(
                    A<IRelationalWriteSession>._,
                    A<RepresentationRestampPage>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_a_completed_operation_through_the_real_runner_before_selecting_a_page()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(Operation(operationId, DocumentCacheRepresentationRestampOperationState.Completed));

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationStateMismatch);
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(DocumentCacheRepresentationRestampOperationState.Completed)]
    [TestCase((DocumentCacheRepresentationRestampOperationState)99)]
    public async Task It_rejects_each_nonexecutable_operation_state_before_stamping(
        DocumentCacheRepresentationRestampOperationState state
    )
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(Operation(operationId, state));

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationStateMismatch);
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Store.StampPageAsync(
                    A<IRelationalWriteSession>._,
                    A<RepresentationRestampPage>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase("target")]
    [TestCase("fingerprint")]
    [TestCase("contract")]
    public async Task It_rejects_an_operation_that_does_not_match_the_real_runner_target_contract_or_fingerprint(
        string mismatch
    )
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        DocumentCacheRepresentationRestampOperation operation = Operation(
            operationId,
            DocumentCacheRepresentationRestampOperationState.Draft
        );
        operation = mismatch switch
        {
            "target" => operation with
            {
                TargetKey = new DocumentCacheAdministrativeTargetKey("OtherTenant", 8),
            },
            "fingerprint" => operation with { PhysicalSourceFingerprint = OtherFingerprint },
            "contract" => operation with { ContractVersion = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(operation);

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationStateMismatch);
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(
        DocumentCacheLifecycleState.Disabled,
        false,
        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch
    )]
    [TestCase(
        DocumentCacheLifecycleState.Tracking,
        true,
        DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet
    )]
    public async Task It_rejects_lifecycle_and_recovery_latch_before_selecting_a_page(
        DocumentCacheLifecycleState state,
        bool latchSet,
        DocumentCacheAdministrativeCommandClassification expectedClassification
    )
    {
        Harness harness = CreateHarness(new DocumentCacheLifecycleObservation(state, latchSet));
        Guid operationId = Guid.NewGuid();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    previewDocumentCount: 2,
                    committedDocumentCount: 1
                )
            );

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(expectedClassification);
        AssertIncompleteRestampResult(
            result,
            operationId,
            committedDocumentCount: 1,
            remaining: null,
            previewDocumentCount: 2
        );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_preview_scope_validation_failures_to_the_typed_restamp_classification()
    {
        Harness harness = CreateHarness();
        DocumentCacheRepresentationRestampPreviewRequest request = PreviewRequest();
        A.CallTo(() =>
                harness.Store.GetMaxChangeVersionAsync(A<IRelationalWriteSession>._, A<CancellationToken>._)
            )
            .Returns(41);
        A.CallTo(() =>
                harness.Store.ResolveSelectionAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampScope>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromException<RepresentationRestampSelection>(
                    new RepresentationRestampValidationException(
                        DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampScope,
                        DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampScope,
                        "Representation restamp resource scope did not resolve to a compiled resource key."
                    )
                )
            );

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(request);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampScope);
        result.RepresentationRestampResult.Should().BeNull();
        A.CallTo(() =>
                harness.Store.CreateDraftAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_execute_mapping_validation_failures_to_the_typed_restamp_classification()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    previewDocumentCount: 2,
                    committedDocumentCount: 1
                )
            );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromException<RepresentationRestampPage>(
                    new RepresentationRestampValidationException(
                        DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMapping,
                        DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampMapping,
                        "Representation restamp document resource is missing compiled metadata."
                    )
                )
            );

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMapping);
        AssertIncompleteRestampResult(
            result,
            operationId,
            committedDocumentCount: 1,
            remaining: null,
            previewDocumentCount: 2
        );
        A.CallTo(() =>
                harness.Store.StampPageAsync(
                    A<IRelationalWriteSession>._,
                    A<RepresentationRestampPage>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_execute_mirror_validation_failures_to_the_typed_restamp_classification()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        RepresentationRestampPage page = Page(1);
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Draft,
                    previewDocumentCount: 1
                )
            );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(page);
        A.CallTo(() =>
                harness.Store.StampPageAsync(A<IRelationalWriteSession>._, page, A<CancellationToken>._)
            )
            .Returns(
                Task.FromException<RepresentationRestampPageCommit>(
                    new RepresentationRestampValidationException(
                        DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMirror,
                        DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampMirror,
                        "Representation restamp document resource has no unique compiled document-stamping mirror route."
                    )
                )
            );

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMirror);
        AssertIncompleteRestampResult(result, operationId, committedDocumentCount: 0, remaining: null);
        A.CallTo(() =>
                harness.Store.UpdateProgressAsync(
                    A<IRelationalWriteSession>._,
                    operationId,
                    A<long>._,
                    A<DocumentCacheRepresentationRestampOperationState>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_mode_mismatch_before_selecting_a_page()
    {
        Harness harness = CreateHarness(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false)
        );
        Guid operationId = Guid.NewGuid();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    previewDocumentCount: 2,
                    committedDocumentCount: 1
                )
            );

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.LifecycleMismatch);
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_a_page_commit_for_another_selected_page_before_updating_progress()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        RepresentationRestampPage selected = Page(1);
        RepresentationRestampPage other = Page(2);
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Draft,
                    previewDocumentCount: 1
                )
            );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(selected);
        A.CallTo(() =>
                harness.Store.StampPageAsync(A<IRelationalWriteSession>._, selected, A<CancellationToken>._)
            )
            .Returns(Commit(other));

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure);
        A.CallTo(() =>
                harness.Store.UpdateProgressAsync(
                    A<IRelationalWriteSession>._,
                    operationId,
                    A<long>._,
                    A<DocumentCacheRepresentationRestampOperationState>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_classifies_cancellation_after_a_committed_page_through_the_real_workflow()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        RepresentationRestampPage page = Page(1);
        using var cancellation = new CancellationTokenSource();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Draft,
                    previewDocumentCount: 1
                )
            );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(page);
        A.CallTo(() =>
                harness.Store.StampPageAsync(A<IRelationalWriteSession>._, page, A<CancellationToken>._)
            )
            .Returns(Commit(page));
        A.CallTo(() =>
                harness.Store.UpdateProgressAsync(
                    A<IRelationalWriteSession>._,
                    operationId,
                    1,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    A<CancellationToken>._
                )
            )
            .Invokes(cancellation.Cancel);

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId),
            cancellation.Token
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation);
        AssertIncompleteRestampResult(result, operationId, committedDocumentCount: 1, remaining: null);
        A.CallTo(() =>
                harness.Store.StampPageAsync(A<IRelationalWriteSession>._, page, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_classifies_mutex_session_loss_without_updating_progress()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        RepresentationRestampPage page = Page(1);
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Draft,
                    previewDocumentCount: 1
                )
            );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(page);
        A.CallTo(() =>
                harness.Store.StampPageAsync(A<IRelationalWriteSession>._, page, A<CancellationToken>._)
            )
            .ReturnsLazily(() =>
            {
                harness.Mutex.Lease.LoseSession();
                return Task.FromException<RepresentationRestampPageCommit>(
                    new InvalidOperationException("Connection lost.")
                );
            });

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossNoMutation);
        A.CallTo(() =>
                harness.Store.UpdateProgressAsync(
                    A<IRelationalWriteSession>._,
                    operationId,
                    A<long>._,
                    A<DocumentCacheRepresentationRestampOperationState>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_classifies_provider_retry_exhaustion_without_stamping()
    {
        Harness harness = CreateHarness(
            retryAttempts: 0,
            writeExceptionClassifier: new TransientWriteExceptionClassifier()
        );
        Guid operationId = Guid.NewGuid();
        A.CallTo(() =>
                harness.Store.LoadAsync(A<IRelationalWriteSession>._, operationId, A<CancellationToken>._)
            )
            .Returns(
                Operation(
                    operationId,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    previewDocumentCount: 2,
                    committedDocumentCount: 1
                )
            );
        A.CallTo(() =>
                harness.Store.SelectNextPageAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheRepresentationRestampOperation>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromException<RepresentationRestampPage>(new TransientDbException()));

        DocumentCacheAdministrativeCommandResult result = await harness.Command.ExecuteAsync(
            ExecuteRequest(operationId)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderConcurrencyRetryExhausted);
        AssertIncompleteRestampResult(
            result,
            operationId,
            previewDocumentCount: 2,
            committedDocumentCount: 1,
            remaining: null
        );
        A.CallTo(() =>
                harness.Store.StampPageAsync(
                    A<IRelationalWriteSession>._,
                    A<RepresentationRestampPage>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_uses_the_existing_runner_for_preview_without_a_command_confirmation()
    {
        var runner = A.Fake<IDocumentCacheAdministrativeCommandRunner>();
        var store = A.Fake<IRepresentationRestampStore>();
        var target = new DocumentCacheAdministrativeTargetKey("tenant", 1);
        var request = new DocumentCacheRepresentationRestampPreviewRequest(
            target,
            new DocumentCacheOfflineWriterAdmission(
                confirmed: true,
                DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
            ),
            DocumentCacheRepresentationRestampMode.Tracking,
            "representation correction",
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student")
        );
        DocumentCacheAdministrativeCommandResult expected = Result(target);
        A.CallTo(() =>
                runner.ExecuteAsync(
                    A<DocumentCacheAdministrativeCommandRunnerRequest>._,
                    A<IDocumentCacheAdministrativeCommandWorkflow>._,
                    A<CancellationToken>._
                )
            )
            .Returns(expected);
        var command = new RepresentationRestampCommand(runner, store, TimeProvider.System);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(request);

        result.Should().BeSameAs(expected);
        A.CallTo(() =>
                runner.ExecuteAsync(
                    A<DocumentCacheAdministrativeCommandRunnerRequest>.That.Matches(value =>
                        value.Command == DocumentCacheAdministrativeCommand.RepresentationRestamp
                        && !value.RequiresCommandConfirmation
                    ),
                    A<IDocumentCacheAdministrativeCommandWorkflow>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_uses_the_existing_runner_for_execute_with_the_exact_confirmation_contract()
    {
        var runner = A.Fake<IDocumentCacheAdministrativeCommandRunner>();
        var store = A.Fake<IRepresentationRestampStore>();
        var target = new DocumentCacheAdministrativeTargetKey("tenant", 1);
        var request = new DocumentCacheRepresentationRestampExecuteRequest(
            target,
            Guid.NewGuid(),
            new DocumentCacheOfflineWriterAdmission(
                confirmed: true,
                DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
            ),
            DocumentCacheAdministrativeCommandConfirmation.RepresentationRestamp
        );
        A.CallTo(() =>
                runner.ExecuteAsync(
                    A<DocumentCacheAdministrativeCommandRunnerRequest>._,
                    A<IDocumentCacheAdministrativeCommandWorkflow>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Result(target));
        var command = new RepresentationRestampCommand(runner, store, TimeProvider.System);

        await command.ExecuteAsync(request);

        A.CallTo(() =>
                runner.ExecuteAsync(
                    A<DocumentCacheAdministrativeCommandRunnerRequest>.That.Matches(value =>
                        value.RequiresCommandConfirmation
                        && value.Confirmation
                            == DocumentCacheAdministrativeCommandConfirmation.RepresentationRestamp
                    ),
                    A<IDocumentCacheAdministrativeCommandWorkflow>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    private static DocumentCacheAdministrativeCommandResult Result(
        DocumentCacheAdministrativeTargetKey target
    ) =>
        new(
            DocumentCacheAdministrativeCommand.RepresentationRestamp,
            target,
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            mutated: false
        );

    private static readonly DocumentCachePhysicalSourceFingerprint OtherFingerprint = new(
        "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
    );

    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private static Harness CreateHarness(
        DocumentCacheLifecycleObservation? lifecycle = null,
        int? retryAttempts = null,
        IRelationalWriteExceptionClassifier? writeExceptionClassifier = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext =
            FixtureHelper<DocumentCacheTargetExecutionContext>(
                "ExecutionContext",
                1L,
                null,
                null,
                TrackingLifecycle
            );
        DocumentCacheTargetObservation observation = FixtureHelper<DocumentCacheTargetObservation>(
            "EligibleObservation",
            executionContext
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext =
            FixtureHelper<DocumentCacheProjectionTargetRuntimeContext>(
                "RuntimeContext",
                executionContext,
                null
            );
        var registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() => registry.CurrentSnapshot)
            .Returns(new DocumentCacheTargetRegistrySnapshot([observation], DateTimeOffset.UtcNow));
        A.CallTo(() => registry.CurrentRuntimeSnapshot)
            .Returns(new DocumentCacheTargetRuntimeSnapshot([executionContext], DateTimeOffset.UtcNow));
        var supervisor = A.Fake<IDocumentCacheProjectionSupervisor>();
        A.CallTo(() => supervisor.CurrentTargetContexts).Returns([runtimeContext]);
        var mutex = new TestMutex();
        var primitives = A.Fake<IDocumentCacheAdministrativePrimitives>();
        A.CallTo(() => primitives.ProviderToken).Returns(RelationalProviderToken.Postgresql);
        A.CallTo(() =>
                primitives.ReadLifecycleAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheAdministrativeStateLockMode>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
                Task.FromResult(DocumentCacheLifecycleReadResult.Success(lifecycle ?? TrackingLifecycle))
            );
        DocumentCacheAdministrativeCommandRunner runner =
            FixtureHelper<DocumentCacheAdministrativeCommandRunner>(
                "CreateRunner",
                registry,
                supervisor,
                mutex,
                null,
                primitives,
                null,
                retryAttempts is null
                    ? null
                    : FixtureHelper<DeadlockRetrySettings>(
                        "ProviderConcurrencyRetrySettings",
                        retryAttempts.Value
                    ),
                writeExceptionClassifier,
                null
            );
        IRepresentationRestampStore store = A.Fake<IRepresentationRestampStore>();
        return new Harness(
            new RepresentationRestampCommand(runner, store, TimeProvider.System),
            store,
            mutex
        );
    }

    private static T FixtureHelper<T>(string name, params object?[] arguments)
    {
        MethodInfo method =
            typeof(Given_DocumentCacheAdministrativeCommandRunner).GetMethod(
                name,
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException($"Fixture helper '{name}' was not found.");
        try
        {
            return (T)(
                method.Invoke(null, arguments)
                ?? throw new InvalidOperationException($"Fixture helper '{name}' returned null.")
            );
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static DocumentCacheRepresentationRestampPreviewRequest PreviewRequest() =>
        new(
            _target,
            Admission(),
            DocumentCacheRepresentationRestampMode.Tracking,
            "representation correction",
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student")
        );

    private static DocumentCacheRepresentationRestampExecuteRequest ExecuteRequest(Guid operationId) =>
        new(
            _target,
            operationId,
            Admission(),
            DocumentCacheAdministrativeCommandConfirmation.RepresentationRestamp
        );

    private static DocumentCacheOfflineWriterAdmission Admission() =>
        new(
            confirmed: true,
            DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
        );

    private static DocumentCacheRepresentationRestampOperation Operation(
        Guid operationId,
        DocumentCacheRepresentationRestampOperationState state,
        long previewDocumentCount = 0,
        long committedDocumentCount = 0
    ) =>
        new(
            operationId,
            1,
            _target,
            Fingerprint,
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            "representation correction",
            DocumentCacheRepresentationRestampMode.Tracking,
            41,
            previewDocumentCount,
            committedDocumentCount,
            state,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

    private static RepresentationRestampPage Page(long documentId) =>
        new([
            new RepresentationRestampDocument(
                documentId,
                Guid.Parse($"00000000-0000-0000-0000-{documentId:D12}"),
                new RepresentationRestampMirrorRoute(1, "dms", "Student")
            ),
        ]);

    private static RepresentationRestampPageCommit Commit(RepresentationRestampPage page) =>
        new(page, [new RepresentationRestampStamp(page.Documents[0].DocumentId, 1, DateTimeOffset.UtcNow)]);

    private static void AssertIncompleteRestampResult(
        DocumentCacheAdministrativeCommandResult result,
        Guid operationId,
        long committedDocumentCount,
        long? remaining,
        long previewDocumentCount = 1
    )
    {
        result.RepresentationRestampResult.Should().NotBeNull();
        result.RepresentationRestampResult!.OperationId.Should().Be(operationId);
        result
            .RepresentationRestampResult.State.Should()
            .Be(DocumentCacheRepresentationRestampOperationState.Incomplete);
        result.RepresentationRestampResult.PreRestampBoundary.Should().Be(41);
        result.RepresentationRestampResult.PreviewDocumentCount.Should().Be(previewDocumentCount);
        result.RepresentationRestampResult.CommittedDocumentCount.Should().Be(committedDocumentCount);
        result.RepresentationRestampResult.RemainingEligibleDocumentCount.Should().Be(remaining);
        result
            .RepresentationRestampResult.Scope.Should()
            .Be(new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"));
        result.RepresentationRestampResult.Reason.Should().Be("representation correction");
        result.RepresentationRestampResult.Mode.Should().Be(DocumentCacheRepresentationRestampMode.Tracking);
        result.RepresentationRestampResult.PhysicalSourceFingerprint.Should().Be(Fingerprint);
        result
            .RepresentationRestampResult.ClaimLevel.Should()
            .Be(DocumentCacheRepresentationRestampClaimLevel.Incomplete);
    }

    private sealed record Harness(
        RepresentationRestampCommand Command,
        IRepresentationRestampStore Store,
        TestMutex Mutex
    );

    private sealed class TestMutex : IDocumentCacheAdministrativeMutex
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public TestMutexLease Lease { get; } = new();

        public Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
            DocumentCacheTargetConnectionInput connectionInput,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IDocumentCacheAdministrativeMutexLease>(Lease);
    }

    private sealed class TestMutexLease : IDocumentCacheAdministrativeMutexLease
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen { get; private set; } = true;

        public void LoseSession() => IsSessionOpen = false;

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        ) =>
            IsSessionOpen
                ? Task.FromResult<IRelationalWriteSession>(new TestWriteSession(isolationLevel))
                : Task.FromException<IRelationalWriteSession>(
                    new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken)
                );

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestWriteSession(IsolationLevel isolationLevel) : IRelationalWriteSession
    {
        public IsolationLevel IsolationLevel { get; } = isolationLevel;

        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransientDbException : DbException;

    private sealed class TransientWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public bool IsTransientFailure(DbException exception) => exception is TransientDbException;

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

        public bool TryClassify(
            DbException exception,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
                out RelationalWriteExceptionClassification? classification
        )
        {
            classification = null;
            return false;
        }
    }
}
