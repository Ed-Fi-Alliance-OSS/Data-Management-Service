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

        DocumentCacheAdministrativeCommandResult? admissionFailure = await VerifyAdmissionAsync(
                context,
                effectiveCancellationToken
            )
            .ConfigureAwait(false);
        if (admissionFailure is not null)
        {
            return admissionFailure;
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

            DocumentCacheExplicitIntegrityScrubPageExecutionResult pageExecution =
                await ExecuteScrubPageAsync(
                        context,
                        boundary.BoundaryDocumentId.Value,
                        afterDocumentId,
                        pageSize,
                        effectiveCancellationToken
                    )
                    .ConfigureAwait(false);

            if (pageExecution.LifecycleReadResult is not null)
            {
                DocumentCacheLifecycleReadResult lifecycleReadResult = pageExecution.LifecycleReadResult;
                if (!lifecycleReadResult.Succeeded)
                {
                    return CreateLifecycleReadFailure(context, lifecycleReadResult);
                }

                DocumentCacheLifecycleObservation lifecycle = lifecycleReadResult.Lifecycle!;
                context.SetLiveTargetObservation(CreateTargetObservation(context, lifecycle));
                return CreateLifecycleFailure(context, lifecycle);
            }

            DocumentCacheAdministrativeScrubPageResult page =
                pageExecution.Page
                ?? throw new InvalidOperationException("Explicit integrity scrub page result was missing.");

            if (page.Status == DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey)
            {
                continue;
            }

            if (page.Status == DocumentCacheAdministrativeScrubPageStatus.Empty)
            {
                break;
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
                return CompletedWithCacheAheadLatchSet(context);
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
                (session, transactionCancellationToken) =>
                    context.Primitives.CaptureBaselineBoundaryAsync(session, transactionCancellationToken),
                commit: true,
                cancellationToken
            )
            .ConfigureAwait(false);

        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.CaptureBoundary);
        return boundary;
    }

    private static Task<DocumentCacheExplicitIntegrityScrubPageExecutionResult> ExecuteScrubPageAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize,
        CancellationToken cancellationToken
    ) =>
        DocumentCacheAdministrativeWorkflow.ExecuteInTransactionWithProviderConcurrencyRetryAsync(
            context,
            IsolationLevel.Serializable,
            async (session, transactionCancellationToken) =>
            {
                DocumentCacheLifecycleReadResult lifecycleReadResult = await context
                    .Primitives.ReadLifecycleAsync(
                        session,
                        DocumentCacheAdministrativeStateLockMode.Shared,
                        transactionCancellationToken
                    )
                    .ConfigureAwait(false);

                if (!lifecycleReadResult.Succeeded || !IsAdmittedLifecycle(lifecycleReadResult.Lifecycle))
                {
                    return DocumentCacheExplicitIntegrityScrubPageExecutionResult.FromLifecycleRead(
                        lifecycleReadResult
                    );
                }

                DocumentCacheAdministrativeScrubPageResult page = await context
                    .Primitives.ScrubPageAsync(
                        session,
                        new DocumentCacheAdministrativeScrubPageRequest(
                            boundaryDocumentId,
                            afterDocumentId,
                            pageSize
                        ),
                        transactionCancellationToken
                    )
                    .ConfigureAwait(false);

                return DocumentCacheExplicitIntegrityScrubPageExecutionResult.FromPage(page);
            },
            static page =>
                page.Page is null
                || page.Page.Status != DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey,
            cancellationToken,
            beforeCommit: pageExecution =>
            {
                DocumentCacheAdministrativeScrubPageResult? page = pageExecution.Page;
                if (page is not null && page.Mutated)
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
            }
        );

    private static DocumentCacheAdministrativeCommandResult CreateLifecycleReadFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleReadResult lifecycleReadResult
    ) =>
        context.Failed(
            context.Mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
            DocumentCacheAdministrativeDiagnosticCategory.LifecycleObservationFailure,
            lifecycleReadResult.Message,
            retryable: context.Mutated
        );

    private static bool IsAdmittedLifecycle(DocumentCacheLifecycleObservation? lifecycle) =>
        lifecycle is { State: DocumentCacheLifecycleState.Tracking, CacheAheadRecoveryRequired: false };

    private static async Task<DocumentCacheAdministrativeCommandResult?> VerifyAdmissionAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheLifecycleReadResult lifecycleReadResult = await DocumentCacheAdministrativeWorkflow
            .ExecuteInTransactionAsync(
                context.MutexLease,
                IsolationLevel.ReadCommitted,
                (session, transactionCancellationToken) =>
                    context.Primitives.ReadLifecycleAsync(
                        session,
                        DocumentCacheAdministrativeStateLockMode.Shared,
                        transactionCancellationToken
                    ),
                commit: true,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!lifecycleReadResult.Succeeded)
        {
            return context.Failed(
                DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleObservationFailure,
                lifecycleReadResult.Message,
                retryable: false
            );
        }

        DocumentCacheLifecycleObservation lifecycle = lifecycleReadResult.Lifecycle!;
        context.SetLiveTargetObservation(CreateTargetObservation(context, lifecycle));

        return lifecycle is { State: DocumentCacheLifecycleState.Tracking, CacheAheadRecoveryRequired: false }
            ? null
            : CreateLifecycleFailure(context, lifecycle);
    }

    private static DocumentCacheAdministrativeCommandResult CompletedWithCacheAheadLatchSet(
        DocumentCacheAdministrativeCommandExecutionContext context
    )
    {
        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.Complete);

        return new(
            context.Request.Command,
            context.Request.TargetKey,
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
            context.Mutated,
            context.TargetContext.Generation.Value,
            context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            context.ObservedLifecycle?.State,
            context.ObservedLifecycle?.CacheAheadRecoveryRequired,
            context.PhaseDiagnostics,
            context.Request.AcceptedOfflineWriterAdmissionConfirmation,
            context.ElapsedCommandTime
        );
    }

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

    private static DocumentCacheExplicitIntegrityScrubRequest Request(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) => new(context.Request.TargetKey, context.Request.ExpectedPhysicalSourceFingerprint);

    private sealed record DocumentCacheExplicitIntegrityScrubPageExecutionResult(
        DocumentCacheAdministrativeScrubPageResult? Page,
        DocumentCacheLifecycleReadResult? LifecycleReadResult
    )
    {
        public static DocumentCacheExplicitIntegrityScrubPageExecutionResult FromPage(
            DocumentCacheAdministrativeScrubPageResult page
        )
        {
            ArgumentNullException.ThrowIfNull(page);
            return new(page, LifecycleReadResult: null);
        }

        public static DocumentCacheExplicitIntegrityScrubPageExecutionResult FromLifecycleRead(
            DocumentCacheLifecycleReadResult lifecycleReadResult
        )
        {
            ArgumentNullException.ThrowIfNull(lifecycleReadResult);
            return new(Page: null, lifecycleReadResult);
        }
    }
}
