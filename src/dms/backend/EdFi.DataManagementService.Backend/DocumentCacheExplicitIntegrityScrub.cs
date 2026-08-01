// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheExplicitIntegrityScrubCommand
{
    Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheExplicitIntegrityScrubRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheExplicitIntegrityScrubCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner
) : IDocumentCacheExplicitIntegrityScrubCommand, IDocumentCacheAdministrativeCommandWorkflow
{
    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheExplicitIntegrityScrubRequest request,
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
            DocumentCachePreflightClassifier.ClassifyExplicitIntegrityScrub(
                Request(context),
                context.LiveTargetObservation,
                new DocumentCacheExplicitIntegrityScrubPreflightFacts(context.TargetContext.Generation)
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

        DocumentCacheLifecycleObservation lifecycle = CurrentLifecycle(context);
        if (
            lifecycle
            is not { State: DocumentCacheLifecycleState.Tracking, CacheAheadRecoveryRequired: false }
        )
        {
            return CreateLifecycleFailure(context, lifecycle);
        }

        DocumentCacheAdministrativeBaselineBoundaryResult boundary = await CaptureBoundaryAsync(
                context,
                effectiveCancellationToken
            )
            .ConfigureAwait(false);

        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ScrubScan);

        if (boundary.BoundaryDocumentId is null)
        {
            context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ScrubScan);
            return context.Completed();
        }

        int pageSize = context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize;
        long afterDocumentId = 0;

        while (afterDocumentId < boundary.BoundaryDocumentId.Value)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();

            DocumentCacheAdministrativeScrubPageResult page = await ExecuteScrubPageAsync(
                    context,
                    boundary.BoundaryDocumentId.Value,
                    afterDocumentId,
                    pageSize,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);

            if (page.Status == DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey)
            {
                continue;
            }

            if (page.Status == DocumentCacheAdministrativeScrubPageStatus.Empty)
            {
                break;
            }

            if (page.Mutated)
            {
                context.MarkMutated(
                    page.LatchSet
                        ? new DocumentCacheLifecycleObservation(
                            DocumentCacheLifecycleState.Tracking,
                            CacheAheadRecoveryRequired: true
                        )
                        : null
                );
            }

            afterDocumentId = page.LastVisitedDocumentId!.Value;

            if (page.LatchSet)
            {
                context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ScrubScan);
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SetCacheAheadLatch);
                context.AddPhaseDiagnostic(
                    DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                    page.Message,
                    retryable: false,
                    page.AffectedDocumentIds
                );
                context.CompletePhase(DocumentCacheAdministrativeCommandPhase.SetCacheAheadLatch);
                return context.Completed();
            }
        }

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.ScrubScan);
        return context.Completed();
    }

    private static async Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBoundaryAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.CaptureBoundary);

        DocumentCacheAdministrativeBaselineBoundaryResult boundary = await DocumentCacheAdministrativeWorkflow
            .ExecuteInTransactionAsync(
                context.MutexLease,
                IsolationLevel.ReadCommitted,
                session => context.Primitives.CaptureBaselineBoundaryAsync(session, cancellationToken),
                commit: true,
                cancellationToken
            )
            .ConfigureAwait(false);

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.CaptureBoundary);
        return boundary;
    }

    private static Task<DocumentCacheAdministrativeScrubPageResult> ExecuteScrubPageAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize,
        CancellationToken cancellationToken
    ) =>
        DocumentCacheAdministrativeWorkflow.ExecuteInTransactionAsync(
            context.MutexLease,
            IsolationLevel.Serializable,
            session =>
                context.Primitives.ScrubPageAsync(
                    session,
                    new DocumentCacheAdministrativeScrubPageRequest(
                        boundaryDocumentId,
                        afterDocumentId,
                        pageSize
                    ),
                    cancellationToken
                ),
            static page =>
                page.Status != DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey,
            cancellationToken
        );

    private static DocumentCacheAdministrativeCommandResult CreateLifecycleFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleObservation lifecycle
    )
    {
        DocumentCacheAdministrativeCommandClassification classification = lifecycle.CacheAheadRecoveryRequired
            ? DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet
            : DocumentCacheAdministrativeCommandClassification.LifecycleMismatch;
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory =
            lifecycle.CacheAheadRecoveryRequired
                ? DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet
                : DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch;

        return context.Failed(
            context.Mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            diagnosticCategory,
            lifecycle.CacheAheadRecoveryRequired
                ? "DocumentCache explicit integrity scrub encountered a set cache-ahead recovery latch."
                : "DocumentCache explicit integrity scrub requires Tracking with a clear cache-ahead latch.",
            retryable: context.Mutated
        );
    }

    private static DocumentCacheLifecycleObservation CurrentLifecycle(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) =>
        context.LiveTargetObservation?.Lifecycle
        ?? context.ObservedLifecycle
        ?? throw new InvalidOperationException(
            "Explicit integrity scrub requires a live lifecycle observation."
        );

    private static DocumentCacheExplicitIntegrityScrubRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) => new(context.Request.TargetKey, context.Request.ExpectedPhysicalSourceFingerprint);
}
