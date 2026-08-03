// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheOnlineCacheRebuildCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheOnlineCacheRebuildRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheOnlineCacheRebuildCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner,
    IDocumentCacheBaselineSeeder baselineSeeder,
    IDocumentCacheAdministrativeDrainer drainer
) : IDocumentCacheOnlineCacheRebuildCommand, IDocumentCacheAdministrativeCommandWorkflow
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheOnlineCacheRebuildRequest request,
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

    public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            DocumentCachePreflightClassifier.ClassifyOnlineCacheRebuild(
                Request(context),
                context.LiveTargetObservation,
                new DocumentCacheOnlineCacheRebuildPreflightFacts(context.TargetContext.Generation)
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

        DocumentCacheLifecycleState lifecycle = CurrentLifecycle(context);

        if (lifecycle == DocumentCacheLifecycleState.Tracking)
        {
            DocumentCacheAdministrativeCommandResult? enterResetting = await TryTransitionAsync(
                    context,
                    DocumentCacheAdministrativeCommandPhase.EnterResetting,
                    DocumentCacheLifecycleState.Tracking,
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

        if (lifecycle == DocumentCacheLifecycleState.Resetting)
        {
            DocumentCacheAdministrativeCommandResult? clearFailure = await ClearCacheAsync(
                    context,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (clearFailure is not null)
            {
                return clearFailure;
            }

            DocumentCacheAdministrativeCommandResult? enterRebuilding = await TryTransitionAsync(
                    context,
                    DocumentCacheAdministrativeCommandPhase.EnterRebuilding,
                    DocumentCacheLifecycleState.Resetting,
                    DocumentCacheLifecycleState.Rebuilding,
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
                "Online cache rebuild requires Tracking, Resetting, or Rebuilding with a clear cache-ahead latch."
            );
        }

        await baselineSeeder.SeedAsync(context, effectiveCancellationToken).ConfigureAwait(false);

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

    private static async Task<DocumentCacheAdministrativeCommandResult?> ClearCacheAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        int pageSize = context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize;

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
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (batch.Mutated)
            {
                context.MarkMutated();
            }

            if (!batch.FilledBatch)
            {
                break;
            }
        }

        DocumentCacheAdministrativeProjectedStateEmptinessResult emptiness =
            await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    (session, transactionCancellationToken) =>
                        context.Primitives.ReadProjectedStateEmptinessAsync(
                            session,
                            transactionCancellationToken
                        ),
                    commit: true,
                    cancellationToken
                )
                .ConfigureAwait(false);

        if (!emptiness.DocumentCacheEmpty)
        {
            return CreateLifecycleFailure(
                context,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                "Online cache rebuild could not verify an empty DocumentCache after bounded clearing."
            );
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
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
            context.SetLiveTargetObservation(CreateTargetObservation(context, lifecycle));

            if (lifecycle.CacheAheadRecoveryRequired)
            {
                return CreateLifecycleFailure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                    DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                    "Online cache rebuild encountered a set cache-ahead recovery latch."
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
            ?? context.ObservedLifecycle
            ?? throw new InvalidOperationException(
                "Online cache rebuild requires a live lifecycle observation."
            )
        ).State;

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

    private static DocumentCacheOnlineCacheRebuildRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) => new(context.Request.TargetKey, context.Request.ExpectedPhysicalSourceFingerprint);
}
