// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheOfflineDeactivationCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheOfflineDeactivationRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheOfflineDeactivationCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner,
    IDocumentCacheDownstreamPublicationHistoryProvider downstreamPublicationHistoryProvider
) : IDocumentCacheOfflineDeactivationCommand, IDocumentCacheAdministrativeCommandWorkflow
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheOfflineDeactivationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return commandRunner.ExecuteAsync(
            DocumentCacheAdministrativeCommandRunnerRequest.From(request),
            this,
            cancellationToken
        );
    }

    public async Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentCacheDownstreamPublicationHistoryObservation downstreamPublicationHistory =
            await downstreamPublicationHistoryProvider
                .ObserveAsync(
                    context.TargetContext.TargetKey,
                    context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                    cancellationToken
                )
                .ConfigureAwait(false);

        return DocumentCachePreflightClassifier.ClassifyOfflineDeactivation(
            Request(context),
            context.LiveTargetObservation,
            new DocumentCacheOfflineDeactivationPreflightFacts(
                context.TargetContext.Generation,
                downstreamPublicationHistory
            )
        );
    }

    public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
            DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
        CancellationToken effectiveCancellationToken = cancellationScope.Token;
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus =
            context.RequireAcceptedDownstreamPublicationStatus();

        DocumentCacheLifecycleState lifecycle = CurrentLifecycle(context);

        if (lifecycle is DocumentCacheLifecycleState.Tracking or DocumentCacheLifecycleState.Rebuilding)
        {
            DocumentCacheAdministrativeCommandResult? enterResetting = await TryTransitionAsync(
                    context,
                    DocumentCacheAdministrativeCommandPhase.EnterResetting,
                    lifecycle,
                    DocumentCacheLifecycleState.Resetting,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (enterResetting is not null)
            {
                return enterResetting;
            }

            lifecycle = DocumentCacheLifecycleState.Resetting;
        }

        if (lifecycle != DocumentCacheLifecycleState.Resetting)
        {
            return CreateLifecycleFailure(
                context,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                "Offline deactivation requires Tracking, Rebuilding, or Resetting with a clear cache-ahead latch."
            );
        }

        DocumentCacheAdministrativeWorkClearance clearance = DocumentCacheAdministrativeWorkClearance.Require(
            context.Request.Command,
            downstreamPublicationStatus,
            context.Request.AcceptedOfflineWriterAdmissionConfirmation
        );

        await ClearCacheAndWorkAsync(context, clearance, effectiveCancellationToken).ConfigureAwait(false);

        DocumentCacheAdministrativeCommandResult? enterDisabled = await EnterDisabledAsync(
                context,
                effectiveCancellationToken
            )
            .ConfigureAwait(false);

        return enterDisabled ?? context.Completed();
    }

    private static async Task ClearCacheAndWorkAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeWorkClearance clearance,
        CancellationToken cancellationToken
    )
    {
        await DocumentCacheAdministrativeWorkflow
            .ClearDocumentCacheAsync(context, cancellationToken)
            .ConfigureAwait(false);
        await DocumentCacheAdministrativeWorkflow
            .ClearDocumentProjectionWorkAsync(context, clearance, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> EnterDisabledAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterDisabled);

        (DocumentCacheAdministrativeCommandResult? Failure, bool Commit) transaction =
            await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context,
                    IsolationLevel.ReadCommitted,
                    async (session, transactionCancellationToken) =>
                    {
                        DocumentCacheAdministrativeProjectedStateEmptinessResult emptiness = await context
                            .Primitives.ReadProjectedStateEmptinessAsync(
                                session,
                                transactionCancellationToken
                            )
                            .ConfigureAwait(false);
                        if (!emptiness.CacheAndWorkEmpty)
                        {
                            return (
                                Failure: CreateLifecycleFailure(
                                    context,
                                    DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                                    DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                                    "Offline deactivation could not verify empty DocumentCache and DocumentProjectionWork before entering Disabled."
                                ),
                                Commit: false
                            );
                        }

                        DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                            .Primitives.TryTransitionLifecycleAsync(
                                session,
                                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                                    DocumentCacheLifecycleState.Resetting,
                                    expectedCacheAheadRecoveryRequired: false,
                                    DocumentCacheLifecycleState.Disabled,
                                    nextCacheAheadRecoveryRequired: false
                                ),
                                transactionCancellationToken
                            )
                            .ConfigureAwait(false);

                        if (!transition.Mutated)
                        {
                            return (Failure: CreateTransitionFailure(context, transition), Commit: false);
                        }

                        context.MarkMutated(transition.LifecycleReadResult.Lifecycle);
                        return (Failure: (DocumentCacheAdministrativeCommandResult?)null, Commit: true);
                    },
                    static transaction => transaction.Commit,
                    cancellationToken
                )
                .ConfigureAwait(false);

        if (transaction.Failure is not null)
        {
            return transaction.Failure;
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.EnterDisabled);
        return null;
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> TryTransitionAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeCommandPhase phase,
        DocumentCacheLifecycleState expectedLifecycle,
        DocumentCacheLifecycleState nextLifecycle,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(phase);

        (DocumentCacheAdministrativeCommandResult? Failure, bool Commit) transaction =
            await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context,
                    IsolationLevel.ReadCommitted,
                    async (session, transactionCancellationToken) =>
                    {
                        DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                            .Primitives.TryTransitionLifecycleAsync(
                                session,
                                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                                    expectedLifecycle,
                                    expectedCacheAheadRecoveryRequired: false,
                                    nextLifecycle,
                                    nextCacheAheadRecoveryRequired: false
                                ),
                                transactionCancellationToken
                            )
                            .ConfigureAwait(false);

                        if (!transition.Mutated)
                        {
                            return (Failure: CreateTransitionFailure(context, transition), Commit: false);
                        }

                        context.MarkMutated(transition.LifecycleReadResult.Lifecycle);
                        return (Failure: (DocumentCacheAdministrativeCommandResult?)null, Commit: true);
                    },
                    static transaction => transaction.Commit,
                    cancellationToken
                )
                .ConfigureAwait(false);

        if (transaction.Failure is not null)
        {
            return transaction.Failure;
        }

        context.CompletePhase(phase);
        return null;
    }

    private static DocumentCacheAdministrativeCommandResult CreateTransitionFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeLifecycleTransitionResult transition
    )
    {
        if (transition.LifecycleReadResult.Succeeded)
        {
            DocumentCacheLifecycleObservation lifecycle = transition.LifecycleReadResult.Lifecycle!;
            context.SetLiveTargetObservation(
                DocumentCacheAdministrativeLiveTargetObservation.Create(context, lifecycle)
            );

            if (lifecycle.CacheAheadRecoveryRequired)
            {
                return CreateLifecycleFailure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                    DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                    "Offline deactivation encountered a set cache-ahead recovery latch."
                );
            }
        }

        return CreateLifecycleFailure(
            context,
            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
            transition.Message
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateLifecycleFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message
    ) =>
        context.Failed(
            context.Mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            diagnosticCategory,
            message,
            retryable: context.Mutated
        );

    private static DocumentCacheLifecycleState CurrentLifecycle(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        (
            context.LiveTargetObservation?.Lifecycle
            ?? context.LifecycleObservation
            ?? throw new InvalidOperationException(
                "Offline deactivation requires a live lifecycle observation."
            )
        ).State;

    private static DocumentCacheOfflineDeactivationRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        new(
            context.Request.TargetKey,
            context.Request.OfflineWriterAdmission,
            context.Request.ExpectedPhysicalSourceFingerprint
        );
}
