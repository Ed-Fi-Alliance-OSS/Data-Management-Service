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
            await ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    session =>
                        context.Primitives.ValidateActivationPrerequisitesAsync(session, cancellationToken),
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

        using CancellationTokenSource? linkedCancellationSource = CreateLinkedCancellationSource(
            context,
            cancellationToken
        );
        CancellationToken effectiveCancellationToken =
            linkedCancellationSource?.Token ?? SelectEffectiveCancellationToken(context, cancellationToken);

        DocumentCacheDownstreamPublicationHistoryProofResult downstreamProof =
            await ObserveAcceptedDownstreamProofAsync(context, effectiveCancellationToken)
                .ConfigureAwait(false);
        if (!downstreamProof.IsAccepted)
        {
            return CreateDownstreamProofFailure(context, downstreamProof);
        }

        DocumentCacheLifecycleState lifecycle = CurrentLifecycle(context);

        if (lifecycle == DocumentCacheLifecycleState.Disabled)
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

            DocumentCacheAdministrativeClearBatchResult batch = await ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    session =>
                        context.Primitives.ClearDocumentCacheBatchAsync(
                            session,
                            new DocumentCacheAdministrativeClearBatchRequest(pageSize),
                            cancellationToken
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

            DocumentCacheAdministrativeClearBatchResult batch = await ExecuteInTransactionAsync(
                    context.MutexLease,
                    IsolationLevel.ReadCommitted,
                    session =>
                        context.Primitives.ClearDocumentProjectionWorkBatchAsync(
                            session,
                            new DocumentCacheAdministrativeClearBatchRequest(pageSize),
                            clearance,
                            cancellationToken
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

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ClearWork);
        return null;
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> EnterRebuildingAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleState expectedLifecycle,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterRebuilding);

        await using IRelationalWriteSession session = await context
            .MutexLease.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            DocumentCacheAdministrativeProjectedStateEmptinessResult emptiness = await context
                .Primitives.ReadProjectedStateEmptinessAsync(session, cancellationToken)
                .ConfigureAwait(false);
            if (!emptiness.CacheAndWorkEmpty)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return CreateLifecycleFailure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                    DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                    "Offline activation could not verify empty DocumentCache and DocumentProjectionWork before entering Rebuilding."
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
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (!transition.Mutated)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return CreateTransitionFailure(context, transition);
            }

            context.MarkMutated(transition.LifecycleReadResult.Lifecycle);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            context.CompletePhase(DocumentCacheAdministrativeCommandPhase.EnterRebuilding);
            return null;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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

        await using IRelationalWriteSession session = await context
            .MutexLease.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
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
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (!transition.Mutated)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return CreateTransitionFailure(context, transition);
            }

            context.MarkMutated(transition.LifecycleReadResult.Lifecycle);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            context.CompletePhase(phase);
            return null;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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
            ?? context.ObservedLifecycle
            ?? throw new InvalidOperationException(
                "Offline activation requires a live lifecycle observation."
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

    private static DocumentCacheOfflineActivationRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        new(
            context.Request.TargetKey,
            context.Request.OfflineWriterAdmission,
            context.Request.ExpectedPhysicalSourceFingerprint
        );

    private static CancellationTokenSource? CreateLinkedCancellationSource(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        if (!cancellationToken.CanBeCanceled || !context.WorkflowCancellationToken.CanBeCanceled)
        {
            return null;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.WorkflowCancellationToken
        );
    }

    private static CancellationToken SelectEffectiveCancellationToken(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    ) => cancellationToken.CanBeCanceled ? cancellationToken : context.WorkflowCancellationToken;

    private static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        IsolationLevel isolationLevel,
        Func<IRelationalWriteSession, Task<TResult>> executeAsync,
        bool commit,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(mutexLease);
        ArgumentNullException.ThrowIfNull(executeAsync);

        await using IRelationalWriteSession session = await mutexLease
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            TResult result = await executeAsync(session).ConfigureAwait(false);
            if (commit)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
