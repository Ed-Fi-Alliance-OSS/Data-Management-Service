// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal sealed class RepresentationRestampCommand(
    IDocumentCacheAdministrativeCommandRunner commandRunner,
    IRepresentationRestampStore store,
    TimeProvider timeProvider
) : IDocumentCacheRepresentationRestampCommand
{
    private const int ContractVersion = 1;

    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheRepresentationRestampPreviewRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return commandRunner.ExecuteAsync(
            DocumentCacheAdministrativeCommandRunnerRequest.From(request),
            new PreviewWorkflow(request, store, timeProvider),
            cancellationToken
        );
    }

    public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
        DocumentCacheRepresentationRestampExecuteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return commandRunner.ExecuteAsync(
            DocumentCacheAdministrativeCommandRunnerRequest.From(request),
            new ExecuteWorkflow(request, store),
            cancellationToken
        );
    }

    private sealed class PreviewWorkflow(
        DocumentCacheRepresentationRestampPreviewRequest request,
        IRepresentationRestampStore store,
        TimeProvider timeProvider
    ) : IDocumentCacheAdministrativeCommandWorkflow, IDocumentCacheAdministrativeCommandResultAugmenter
    {
        private DocumentCacheRepresentationRestampOperation? _knownOperation;

        public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        )
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context.EligiblePreflightResult());
        }

        public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        )
        {
            if (
                !request.Scope.TryCanonicalize(
                    PageSize(context),
                    out DocumentCacheRepresentationRestampScope canonicalScope
                )
            )
            {
                return Failure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampScope,
                    DocumentCacheAdministrativeDiagnosticCategory.InvalidRepresentationRestampScope,
                    "Representation restamp scope is invalid."
                );
            }

            using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
                DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
            context.EnterPhase(DocumentCacheAdministrativeCommandPhase.CreateManifest);
            PreviewTransaction transaction = await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context,
                    IsolationLevel.Serializable,
                    async (session, transactionCancellationToken) =>
                    {
                        try
                        {
                            DocumentCacheAdministrativeCommandResult? lifecycleFailure =
                                await RequireLifecycleAsync(
                                        context,
                                        request.Mode,
                                        session,
                                        transactionCancellationToken
                                    )
                                    .ConfigureAwait(false);
                            if (lifecycleFailure is not null)
                            {
                                return PreviewTransaction.Failed(lifecycleFailure);
                            }

                            long boundary = await store
                                .GetMaxChangeVersionAsync(session, transactionCancellationToken)
                                .ConfigureAwait(false);
                            RepresentationRestampSelection selection = await store
                                .ResolveSelectionAsync(
                                    session,
                                    canonicalScope,
                                    PageSize(context),
                                    transactionCancellationToken
                                )
                                .ConfigureAwait(false);
                            DocumentCacheRepresentationRestampOperation operation = new(
                                Guid.NewGuid(),
                                ContractVersion,
                                request.TargetKey,
                                context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                                selection.CanonicalScope,
                                request.Reason,
                                request.Mode,
                                Math.Max(0, boundary),
                                selection.PreviewDocumentCount,
                                0,
                                DocumentCacheRepresentationRestampOperationState.Draft,
                                timeProvider.GetUtcNow(),
                                timeProvider.GetUtcNow()
                            );
                            await store
                                .CreateDraftAsync(session, operation, transactionCancellationToken)
                                .ConfigureAwait(false);
                            return PreviewTransaction.Succeeded(operation);
                        }
                        catch (RepresentationRestampValidationException exception)
                        {
                            return PreviewTransaction.Failed(Failure(context, exception));
                        }
                    },
                    result => result.Commit,
                    cancellationScope.Token,
                    result =>
                    {
                        if (result.DraftOperation is not null)
                        {
                            _knownOperation = result.DraftOperation;
                            context.MarkMutated();
                        }
                    }
                )
                .ConfigureAwait(false);

            if (transaction.FailureResult is not null)
            {
                return transaction.FailureResult;
            }

            context.CompletePhase(DocumentCacheAdministrativeCommandPhase.CreateManifest);
            return Result(
                context,
                DocumentCacheAdministrativeCommandStatus.Completed,
                DocumentCacheAdministrativeCommandClassification.Succeeded,
                transaction.DraftOperation!,
                remaining: null
            );
        }

        public DocumentCacheAdministrativeCommandResult AugmentResult(
            DocumentCacheAdministrativeCommandExecutionContext context,
            DocumentCacheAdministrativeCommandResult result
        ) => ResultWithKnownOperation(result, _knownOperation, remaining: null);
    }

    private sealed class ExecuteWorkflow(
        DocumentCacheRepresentationRestampExecuteRequest request,
        IRepresentationRestampStore store
    ) : IDocumentCacheAdministrativeCommandWorkflow, IDocumentCacheAdministrativeCommandResultAugmenter
    {
        private DocumentCacheRepresentationRestampOperation? _knownOperation;
        private long? _knownRemaining;

        public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        )
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context.EligiblePreflightResult());
        }

        public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        )
        {
            using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
                DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
            OperationLoadTransaction load = await DocumentCacheAdministrativeWorkflow
                .ExecuteInTransactionAsync(
                    context,
                    IsolationLevel.ReadCommitted,
                    async (session, transactionCancellationToken) =>
                    {
                        DocumentCacheRepresentationRestampOperation? operation = await store
                            .LoadAsync(session, request.OperationId, transactionCancellationToken)
                            .ConfigureAwait(false);
                        return operation is null
                            ? OperationLoadTransaction.NotFound
                            : OperationLoadTransaction.Loaded(operation);
                    },
                    commit: true,
                    cancellationScope.Token
                )
                .ConfigureAwait(false);

            if (load.LoadedOperation is null)
            {
                return Failure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationNotFound,
                    DocumentCacheAdministrativeDiagnosticCategory.RepresentationRestampOperationNotFound,
                    "Representation restamp operation was not found."
                );
            }

            DocumentCacheRepresentationRestampOperation operation = load.LoadedOperation;
            _knownOperation = operation;
            if (
                operation.ContractVersion != ContractVersion
                || !operation.TargetKey.Equals(request.TargetKey)
                || !operation.PhysicalSourceFingerprint.Equals(
                    context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint
                )
                || !RepresentationRestampOperationAdmission.CanExecute(operation.State)
            )
            {
                return Failure(
                    context,
                    DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationStateMismatch,
                    DocumentCacheAdministrativeDiagnosticCategory.RepresentationRestampOperationStateMismatch,
                    "Representation restamp operation does not match the current command target, source, contract, or executable state."
                );
            }

            while (true)
            {
                cancellationScope.Token.ThrowIfCancellationRequested();
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SelectDocuments);
                PageTransaction pageTransaction = await DocumentCacheAdministrativeWorkflow
                    .ExecuteInTransactionWithProviderConcurrencyRetryAsync(
                        context,
                        IsolationLevel.Serializable,
                        async (session, transactionCancellationToken) =>
                        {
                            try
                            {
                                DocumentCacheAdministrativeCommandResult? lifecycleFailure =
                                    await RequireLifecycleAsync(
                                            context,
                                            operation.Mode,
                                            session,
                                            transactionCancellationToken
                                        )
                                        .ConfigureAwait(false);
                                if (lifecycleFailure is not null)
                                {
                                    return PageTransaction.Failed(lifecycleFailure);
                                }

                                RepresentationRestampPage page = await store
                                    .SelectNextPageAsync(
                                        session,
                                        operation,
                                        PageSize(context),
                                        transactionCancellationToken
                                    )
                                    .ConfigureAwait(false);
                                if (page.IsEmpty)
                                {
                                    long remaining = await store
                                        .CountRemainingEligibleAsync(
                                            session,
                                            operation,
                                            transactionCancellationToken
                                        )
                                        .ConfigureAwait(false);
                                    if (
                                        operation.CommittedDocumentCount + remaining
                                        != operation.PreviewDocumentCount
                                    )
                                    {
                                        return PageTransaction.FromReconciliationFailure(remaining);
                                    }

                                    await store
                                        .MarkCompletedAsync(
                                            session,
                                            operation.OperationId,
                                            transactionCancellationToken
                                        )
                                        .ConfigureAwait(false);
                                    return PageTransaction.FromCompleted(remaining);
                                }

                                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.StampDocuments);
                                RepresentationRestampPageCommit commit = await store
                                    .StampPageAsync(session, page, transactionCancellationToken)
                                    .ConfigureAwait(false);
                                commit.RequireSelectedPage(page);
                                long committed = checked(
                                    operation.CommittedDocumentCount + commit.Page.Count
                                );
                                await store
                                    .UpdateProgressAsync(
                                        session,
                                        operation.OperationId,
                                        committed,
                                        DocumentCacheRepresentationRestampOperationState.Incomplete,
                                        transactionCancellationToken
                                    )
                                    .ConfigureAwait(false);
                                return PageTransaction.Stamped(committed);
                            }
                            catch (RepresentationRestampValidationException exception)
                            {
                                _knownOperation = operation with
                                {
                                    State = DocumentCacheRepresentationRestampOperationState.Incomplete,
                                };
                                return PageTransaction.Failed(Failure(context, exception));
                            }
                        },
                        result => result.Commit,
                        cancellationScope.Token,
                        result =>
                        {
                            if (result.Mutated)
                            {
                                context.MarkMutated();
                            }
                        }
                    )
                    .ConfigureAwait(false);

                if (pageTransaction.FailureResult is not null)
                {
                    return pageTransaction.FailureResult;
                }

                if (pageTransaction.ReconciliationFailed)
                {
                    _knownRemaining = pageTransaction.Remaining;
                    return Failure(
                        context,
                        DocumentCacheAdministrativeCommandClassification.RepresentationRestampCountReconciliationFailure,
                        DocumentCacheAdministrativeDiagnosticCategory.RepresentationRestampCountReconciliationFailure,
                        "Representation restamp preview count no longer reconciles with committed and remaining documents."
                    );
                }

                if (pageTransaction.Completed)
                {
                    DocumentCacheRepresentationRestampOperation completed = operation with
                    {
                        State = DocumentCacheRepresentationRestampOperationState.Completed,
                    };
                    return Result(
                        context,
                        DocumentCacheAdministrativeCommandStatus.Completed,
                        DocumentCacheAdministrativeCommandClassification.Succeeded,
                        completed,
                        pageTransaction.Remaining
                    );
                }

                operation = operation with
                {
                    CommittedDocumentCount = pageTransaction.CommittedDocumentCount,
                    State = DocumentCacheRepresentationRestampOperationState.Incomplete,
                };
                _knownOperation = operation;
                context.CompletePhase(DocumentCacheAdministrativeCommandPhase.StampDocuments);
            }
        }

        public DocumentCacheAdministrativeCommandResult AugmentResult(
            DocumentCacheAdministrativeCommandExecutionContext context,
            DocumentCacheAdministrativeCommandResult result
        ) => ResultWithKnownOperation(result, _knownOperation, _knownRemaining);
    }

    private static async Task<DocumentCacheAdministrativeCommandResult?> RequireLifecycleAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheRepresentationRestampMode mode,
        IRelationalWriteSession session,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheLifecycleReadResult lifecycleRead = await context
            .Primitives.ReadLifecycleAsync(
                session,
                DocumentCacheAdministrativeStateLockMode.Shared,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!lifecycleRead.Succeeded)
        {
            return Failure(
                context,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleObservationFailure,
                lifecycleRead.Message
            );
        }

        DocumentCacheLifecycleObservation lifecycle = lifecycleRead.Lifecycle!;
        context.SetLiveTargetObservation(
            DocumentCacheAdministrativeLiveTargetObservation.Create(context, lifecycle)
        );
        if (lifecycle.CacheAheadRecoveryRequired)
        {
            return Failure(
                context,
                DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                "Representation restamp requires a clear cache-ahead recovery latch."
            );
        }

        bool matches = mode switch
        {
            DocumentCacheRepresentationRestampMode.Tracking => lifecycle.State
                == DocumentCacheLifecycleState.Tracking,
            DocumentCacheRepresentationRestampMode.Disabled => lifecycle.State
                == DocumentCacheLifecycleState.Disabled,
            _ => false,
        };
        return matches
            ? null
            : Failure(
                context,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                "Representation restamp lifecycle does not match the operation mode."
            );
    }

    private static int PageSize(DocumentCacheAdministrativeCommandExecutionContext context) =>
        context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize;

    private static DocumentCacheAdministrativeCommandResult Failure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message
    ) =>
        context.Failed(
            context.Mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : StatusBeforeMutation(classification),
            classification,
            diagnosticCategory,
            message,
            context.Mutated
        );

    private static DocumentCacheAdministrativeCommandStatus StatusBeforeMutation(
        DocumentCacheAdministrativeCommandClassification classification
    ) =>
        classification
            is DocumentCacheAdministrativeCommandClassification.LifecycleMismatch
                or DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet
                or DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampScope
                or DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMapping
                or DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampMirror
                or DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationNotFound
                or DocumentCacheAdministrativeCommandClassification.RepresentationRestampOperationStateMismatch
                or DocumentCacheAdministrativeCommandClassification.RepresentationRestampCountReconciliationFailure
            ? DocumentCacheAdministrativeCommandStatus.RejectedNoMutation
            : DocumentCacheAdministrativeCommandStatus.FailedNoMutation;

    private static DocumentCacheAdministrativeCommandResult Failure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        RepresentationRestampValidationException exception
    ) => Failure(context, exception.Classification, exception.DiagnosticCategory, exception.Message);

    private static DocumentCacheAdministrativeCommandResult Result(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheRepresentationRestampOperation operation,
        long? remaining
    )
    {
        return new(
            context.Request.Command,
            context.Request.TargetKey,
            status,
            classification,
            context.Mutated,
            context.TargetContext.Generation.Value,
            context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
            context.LifecycleObservation?.State,
            context.LifecycleObservation?.CacheAheadRecoveryRequired,
            context.PhaseDiagnostics,
            context.Request.AcceptedOfflineWriterAdmissionConfirmation,
            context.ElapsedCommandTime,
            new DocumentCacheRepresentationRestampResult(
                operation.OperationId,
                operation.State,
                operation.PreRestampBoundary,
                operation.PreviewDocumentCount,
                operation.CommittedDocumentCount,
                remaining,
                operation.Scope,
                operation.Reason,
                operation.Mode,
                operation.PhysicalSourceFingerprint,
                ClaimLevel(operation)
            )
        );
    }

    private static DocumentCacheAdministrativeCommandResult ResultWithKnownOperation(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheRepresentationRestampOperation? operation,
        long? remaining
    )
    {
        if (operation is null || result.RepresentationRestampResult is not null)
        {
            return result;
        }

        return new(
            result.Command,
            result.TargetKey,
            result.Status,
            result.Classification,
            result.Mutated,
            result.TargetGeneration,
            result.PhysicalSourceFingerprint,
            result.Lifecycle,
            result.CacheAheadRecoveryRequired,
            result.PhaseDiagnostics,
            result.OfflineWriterAdmission,
            result.ElapsedCommandTime,
            new DocumentCacheRepresentationRestampResult(
                operation.OperationId,
                operation.State,
                operation.PreRestampBoundary,
                operation.PreviewDocumentCount,
                operation.CommittedDocumentCount,
                remaining,
                operation.Scope,
                operation.Reason,
                operation.Mode,
                operation.PhysicalSourceFingerprint,
                ClaimLevel(operation)
            )
        );
    }

    private static DocumentCacheRepresentationRestampClaimLevel ClaimLevel(
        DocumentCacheRepresentationRestampOperation operation
    )
    {
        if (operation.State is not DocumentCacheRepresentationRestampOperationState.Completed)
        {
            return DocumentCacheRepresentationRestampClaimLevel.Incomplete;
        }

        return operation.Mode == DocumentCacheRepresentationRestampMode.Tracking
            ? DocumentCacheRepresentationRestampClaimLevel.ProjectionWorkQueued
            : DocumentCacheRepresentationRestampClaimLevel.CanonicalOnlyComplete;
    }

    private sealed record PreviewTransaction(
        DocumentCacheRepresentationRestampOperation? DraftOperation,
        DocumentCacheAdministrativeCommandResult? FailureResult
    )
    {
        public bool Commit => DraftOperation is not null;

        public static PreviewTransaction Failed(DocumentCacheAdministrativeCommandResult failure) =>
            new(null, failure);

        public static PreviewTransaction Succeeded(DocumentCacheRepresentationRestampOperation operation) =>
            new(operation, null);
    }

    private sealed record OperationLoadTransaction(
        DocumentCacheRepresentationRestampOperation? LoadedOperation
    )
    {
        public static OperationLoadTransaction NotFound { get; } =
            new((DocumentCacheRepresentationRestampOperation?)null);

        public static OperationLoadTransaction Loaded(
            DocumentCacheRepresentationRestampOperation operation
        ) => new(operation);
    }

    private sealed record PageTransaction(
        long CommittedDocumentCount,
        long? Remaining,
        bool Completed,
        bool ReconciliationFailed,
        DocumentCacheAdministrativeCommandResult? FailureResult
    )
    {
        public bool Mutated => Completed || CommittedDocumentCount > 0;
        public bool Commit => FailureResult is null && !ReconciliationFailed;

        public static PageTransaction Failed(DocumentCacheAdministrativeCommandResult failure) =>
            new(0, null, false, false, failure);

        public static PageTransaction FromReconciliationFailure(long remaining) =>
            new(0, remaining, false, true, null);

        public static PageTransaction FromCompleted(long remaining) => new(0, remaining, true, false, null);

        public static PageTransaction Stamped(long committedDocumentCount) =>
            new(committedDocumentCount, null, false, false, null);
    }
}
