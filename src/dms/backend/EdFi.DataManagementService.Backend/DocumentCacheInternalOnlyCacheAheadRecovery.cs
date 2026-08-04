// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheInternalOnlyCacheAheadRecoveryCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheInternalOnlyCacheAheadRecoveryRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheInternalOnlyCacheAheadRecoveryCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner,
    IDocumentCacheDownstreamPublicationHistoryProvider downstreamPublicationHistoryProvider,
    IDocumentCacheBaselineSeeder baselineSeeder,
    IDocumentCacheAdministrativeDrainer drainer
) : IDocumentCacheInternalOnlyCacheAheadRecoveryCommand, IDocumentCacheAdministrativeCommandWorkflow
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheInternalOnlyCacheAheadRecoveryRequest request,
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

        return DocumentCachePreflightClassifier.ClassifyInternalOnlyCacheAheadRecovery(
            Request(context),
            context.LiveTargetObservation,
            new DocumentCacheInternalOnlyCacheAheadRecoveryPreflightFacts(
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

        DocumentCacheDownstreamPublicationHistoryProofResult downstreamProof =
            await ObserveAcceptedDownstreamProofAsync(context, effectiveCancellationToken)
                .ConfigureAwait(false);
        if (!downstreamProof.IsAccepted)
        {
            return CreateDownstreamProofFailure(context, downstreamProof);
        }

        DocumentCacheLifecycleObservation lifecycle = CurrentLifecycle(context);

        if (
            lifecycle.CacheAheadRecoveryRequired
            && lifecycle.State
                is DocumentCacheLifecycleState.Tracking
                    or DocumentCacheLifecycleState.Rebuilding
        )
        {
            DocumentCacheAdministrativeCommandResult? enterResetting = await TryTransitionAsync(
                    context,
                    DocumentCacheAdministrativeCommandPhase.EnterResetting,
                    lifecycle.State,
                    expectedCacheAheadRecoveryRequired: true,
                    DocumentCacheLifecycleState.Resetting,
                    nextCacheAheadRecoveryRequired: true,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (enterResetting is not null)
            {
                return enterResetting;
            }

            lifecycle = new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Resetting,
                CacheAheadRecoveryRequired: true
            );
        }

        if (lifecycle is { State: DocumentCacheLifecycleState.Resetting, CacheAheadRecoveryRequired: true })
        {
            DocumentCacheAdministrativeWorkClearance clearance =
                DocumentCacheAdministrativeWorkClearance.Require(
                    context.Request.Command,
                    downstreamProof.DownstreamPublicationStatus,
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

            DocumentCacheAdministrativeCommandResult? enterRebuilding =
                await EnterRebuildingAndClearLatchAsync(context, effectiveCancellationToken)
                    .ConfigureAwait(false);
            if (enterRebuilding is not null)
            {
                return enterRebuilding;
            }

            lifecycle = new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Rebuilding,
                CacheAheadRecoveryRequired: false
            );
        }

        if (
            lifecycle
            is not { State: DocumentCacheLifecycleState.Rebuilding, CacheAheadRecoveryRequired: false }
        )
        {
            return CreateLifecycleFailure(
                context,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                "Internal-only cache-ahead recovery requires Tracking, Resetting, or Rebuilding with a set cache-ahead latch, or Rebuilding with a clear latch as the supported resume state."
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
                expectedCacheAheadRecoveryRequired: false,
                DocumentCacheLifecycleState.Tracking,
                nextCacheAheadRecoveryRequired: false,
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
        int pageSize = context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize;

        DocumentCacheAdministrativeCommandResult? clearCacheFailure = await ClearCacheAsync(
                context,
                pageSize,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (clearCacheFailure is not null)
        {
            return clearCacheFailure;
        }

        return await ClearWorkAsync(context, pageSize, clearance, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> ClearCacheAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearCache);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DocumentCacheAdministrativeClearBatchResult batch = await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    (session, transactionCancellationToken) =>
                        context.Primitives.ClearDocumentCacheBatchAsync(
                            session,
                            new DocumentCacheAdministrativeClearBatchRequest(pageSize),
                            transactionCancellationToken
                        ),
                    commit: true,
                    cancellationToken,
                    beforeCommit: batch =>
                    {
                        if (batch.Mutated)
                        {
                            context.MarkMutated();
                        }
                    }
                )
                .ConfigureAwait(false);

            if (!batch.FilledBatch)
            {
                break;
            }
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
        return null;
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> ClearWorkAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        int pageSize,
        DocumentCacheAdministrativeWorkClearance clearance,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearWork);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DocumentCacheAdministrativeClearBatchResult batch = await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    (session, transactionCancellationToken) =>
                        context.Primitives.ClearDocumentProjectionWorkBatchAsync(
                            session,
                            new DocumentCacheAdministrativeClearBatchRequest(pageSize),
                            clearance,
                            transactionCancellationToken
                        ),
                    commit: true,
                    cancellationToken,
                    beforeCommit: batch =>
                    {
                        if (batch.Mutated)
                        {
                            context.MarkMutated();
                        }
                    }
                )
                .ConfigureAwait(false);

            if (!batch.FilledBatch)
            {
                break;
            }
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ClearWork);
        return null;
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> EnterRebuildingAndClearLatchAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
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
                                    "Internal-only cache-ahead recovery could not verify empty DocumentCache and DocumentProjectionWork before entering Rebuilding and clearing the latch."
                                ),
                                Commit: false
                            );
                        }

                        DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                            .Primitives.TryTransitionLifecycleAsync(
                                session,
                                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                                    DocumentCacheLifecycleState.Resetting,
                                    expectedCacheAheadRecoveryRequired: true,
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
        bool expectedCacheAheadRecoveryRequired,
        DocumentCacheLifecycleState nextLifecycle,
        bool nextCacheAheadRecoveryRequired,
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
                                    expectedCacheAheadRecoveryRequired,
                                    nextLifecycle,
                                    nextCacheAheadRecoveryRequired
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

    private async Task<DocumentCacheDownstreamPublicationHistoryProofResult> ObserveAcceptedDownstreamProofAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation =
            await downstreamPublicationHistoryProvider
                .ObserveAsync(
                    context.TargetContext.TargetKey,
                    context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                    cancellationToken
                )
                .ConfigureAwait(false);

        return DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
            context.TargetContext.TargetKey,
            context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            observation,
            context.Request.ExpectedPhysicalSourceFingerprint
        );
    }

    private static DocumentCacheAdministrativeCommandResult CreateDownstreamProofFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheDownstreamPublicationHistoryProofResult proof
    )
    {
        DocumentCacheAdministrativeDiagnostic diagnostic = proof.Diagnostics[0];
        return CreateLifecycleFailure(context, proof.Classification, diagnostic.Category, diagnostic.Message);
    }

    private static DocumentCacheAdministrativeCommandResult CreateTransitionFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeLifecycleTransitionResult transition
    )
    {
        if (transition.LifecycleReadResult.Succeeded)
        {
            DocumentCacheLifecycleObservation lifecycle = transition.LifecycleReadResult.Lifecycle!;
            context.SetLiveTargetObservation(CreateTargetObservation(context, lifecycle));

            if (
                lifecycle.CacheAheadRecoveryRequired
                && lifecycle.State is not DocumentCacheLifecycleState.Resetting
            )
            {
                return CreateLifecycleFailure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                    DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                    "Internal-only cache-ahead recovery encountered a set cache-ahead recovery latch outside Resetting."
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

    private static DocumentCacheLifecycleObservation CurrentLifecycle(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        context.LiveTargetObservation?.Lifecycle
        ?? context.ObservedLifecycle
        ?? throw new InvalidOperationException(
            "Internal-only cache-ahead recovery requires a live lifecycle observation."
        );

    private static DocumentCacheTargetObservation CreateTargetObservation(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleObservation lifecycle
    )
    {
        DocumentCacheTargetExecutionContext executionContext = context.TargetContext.TargetExecutionContext;

        return DocumentCacheTargetObservation.ResolvedEligible(
            executionContext.TargetKey,
            executionContext.EffectiveSettings,
            executionContext.Generation,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint,
            lifecycle,
            executionContext.Inventory,
            executionContext.EnqueueTrigger,
            executionContext.SqlServerPrerequisites
        );
    }

    private static DocumentCacheInternalOnlyCacheAheadRecoveryRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        new(
            context.Request.TargetKey,
            context.Request.OfflineWriterAdmission,
            context.Request.ExpectedPhysicalSourceFingerprint
        );
}
