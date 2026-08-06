// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheOfflineActivationCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheOfflineActivationRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheOfflineActivationCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner,
    IDocumentCacheDownstreamPublicationHistoryProvider downstreamPublicationHistoryProvider,
    IDocumentCacheBaselineSeeder baselineSeeder,
    IDocumentCacheAdministrativeDrainer drainer
) : IDocumentCacheOfflineActivationCommand, IDocumentCacheAdministrativeCommandWorkflow
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheOfflineActivationRequest request,
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

        DocumentCacheProviderPrerequisiteValidationResult activationPrerequisites =
            await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    (session, transactionCancellationToken) =>
                        context.Primitives.ValidateActivationPrerequisitesAsync(
                            session,
                            transactionCancellationToken
                        ),
                    commit: true,
                    cancellationToken
                )
                .ConfigureAwait(false);

        DocumentCacheDownstreamPublicationHistoryObservation downstreamPublicationHistory =
            await downstreamPublicationHistoryProvider
                .ObserveAsync(
                    context.TargetContext.TargetKey,
                    context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                    cancellationToken
                )
                .ConfigureAwait(false);

        return DocumentCachePreflightClassifier.ClassifyOfflineActivation(
            Request(context),
            context.LiveTargetObservation,
            new DocumentCacheOfflineActivationPreflightFacts(
                context.TargetContext.Generation,
                activationPrerequisites,
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

        if (lifecycle == DocumentCacheLifecycleState.Disabled)
        {
            DocumentCacheAdministrativeWorkClearance clearance =
                DocumentCacheAdministrativeWorkClearance.Require(
                    context.Request.Command,
                    downstreamPublicationStatus,
                    context.Request.AcceptedOfflineWriterAdmissionConfirmation
                );

            DocumentCacheAdministrativeCommandResult? clearFailure = await ClearCacheAndWorkAsync(
                    context,
                    clearance,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (clearFailure is not null)
            {
                return clearFailure;
            }

            DocumentCacheAdministrativeCommandResult? enterRebuilding = await EnterRebuildingAsync(
                    context,
                    expectedLifecycle: DocumentCacheLifecycleState.Disabled,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (enterRebuilding is not null)
            {
                return enterRebuilding;
            }

            lifecycle = DocumentCacheLifecycleState.Rebuilding;
        }

        if (lifecycle != DocumentCacheLifecycleState.Rebuilding)
        {
            return CreateLifecycleFailure(
                context,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                "Offline activation requires Disabled or Rebuilding with a clear cache-ahead latch."
            );
        }

        DocumentCacheBaselineSeedingResult seedResult = await baselineSeeder
            .SeedAsync(context, effectiveCancellationToken)
            .ConfigureAwait(false);
        if (!seedResult.Completed)
        {
            return seedResult.FailureResult!;
        }

        DocumentCacheAdministrativeDrainToEmptyResult drainResult = await drainer
            .DrainToEmptyAsync(context, effectiveCancellationToken)
            .ConfigureAwait(false);
        if (!drainResult.Completed)
        {
            return drainResult.FailureResult!;
        }

        DocumentCacheAdministrativeCommandResult? enterTracking = await TryTransitionAsync(
                context,
                DocumentCacheAdministrativeCommandPhase.EnterTracking,
                DocumentCacheLifecycleState.Rebuilding,
                DocumentCacheLifecycleState.Tracking,
                effectiveCancellationToken
            )
            .ConfigureAwait(false);

        return enterTracking ?? context.Completed();
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> ClearCacheAndWorkAsync(
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

        return null;
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> EnterRebuildingAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleState expectedLifecycle,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterRebuilding);

        (DocumentCacheAdministrativeCommandResult? Failure, bool Commit) transaction =
            await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context.MutexLease,
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
                                    "Offline activation could not verify empty DocumentCache and DocumentProjectionWork before entering Rebuilding."
                                ),
                                Commit: false
                            );
                        }

                        DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                            .Primitives.TryTransitionLifecycleAsync(
                                session,
                                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                                    expectedLifecycle,
                                    expectedCacheAheadRecoveryRequired: false,
                                    DocumentCacheLifecycleState.Rebuilding,
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

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.EnterRebuilding);
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
                    context.MutexLease,
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
                    "Offline activation encountered a set cache-ahead recovery latch."
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
                "Offline activation requires a live lifecycle observation."
            )
        ).State;

    private static DocumentCacheOfflineActivationRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        new(
            context.Request.TargetKey,
            context.Request.OfflineWriterAdmission,
            context.Request.ExpectedPhysicalSourceFingerprint
        );
}
