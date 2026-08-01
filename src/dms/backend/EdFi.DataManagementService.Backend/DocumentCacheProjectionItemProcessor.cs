// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheProjectionItemProcessor
{
    Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
        DocumentCacheProjectionItemProcessRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed record DocumentCacheProjectionItemProcessRequest
{
    public DocumentCacheProjectionItemProcessRequest(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    )
    {
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        WorkItem = workItem ?? throw new ArgumentNullException(nameof(workItem));
        InvocationKind = DocumentCacheProjectionItemProcessingGuard.RequireDefined(
            invocationKind,
            nameof(invocationKind),
            "Unsupported DocumentCache projection drain invocation kind."
        );
    }

    public DocumentCacheProjectionTargetRuntimeContext TargetContext { get; }

    public DocumentProjectionWorkPageItem WorkItem { get; }

    public DocumentCacheProjectionDrainInvocationKind InvocationKind { get; }
}

internal enum DocumentCacheProjectionItemProcessOutcome
{
    Continue = 1,
    LifecycleFenced = 2,
    TargetBackoff = 3,
    TargetPaused = 4,
}

internal sealed record DocumentCacheProjectionItemProcessResult
{
    private DocumentCacheProjectionItemProcessResult(
        DocumentCacheProjectionItemProcessOutcome outcome,
        DateTimeOffset? backoffUntil,
        bool acknowledgedOrRemovedDurableWork,
        bool documentScopedFailureRecorded,
        DocumentCacheAdministrativeDrainFailure? administrativeFailure
    )
    {
        Outcome = DocumentCacheProjectionItemProcessingGuard.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported DocumentCache projection item process outcome."
        );
        if (outcome == DocumentCacheProjectionItemProcessOutcome.TargetBackoff && backoffUntil is null)
        {
            throw new ArgumentException("Target backoff item results require a backoff boundary.");
        }

        if (outcome != DocumentCacheProjectionItemProcessOutcome.TargetBackoff && backoffUntil is not null)
        {
            throw new ArgumentException("Only target backoff item results may carry a backoff boundary.");
        }

        BackoffUntil = backoffUntil;
        AcknowledgedOrRemovedDurableWork = acknowledgedOrRemovedDurableWork;
        DocumentScopedFailureRecorded = documentScopedFailureRecorded;
        AdministrativeFailure = administrativeFailure;
    }

    public DocumentCacheProjectionItemProcessOutcome Outcome { get; }

    public DateTimeOffset? BackoffUntil { get; }

    public bool AcknowledgedOrRemovedDurableWork { get; }

    public bool DocumentScopedFailureRecorded { get; }

    public DocumentCacheAdministrativeDrainFailure? AdministrativeFailure { get; }

    public static DocumentCacheProjectionItemProcessResult Continue { get; } =
        new(
            DocumentCacheProjectionItemProcessOutcome.Continue,
            backoffUntil: null,
            acknowledgedOrRemovedDurableWork: false,
            documentScopedFailureRecorded: false,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionItemProcessResult AcknowledgedOrRemoved { get; } =
        new(
            DocumentCacheProjectionItemProcessOutcome.Continue,
            backoffUntil: null,
            acknowledgedOrRemovedDurableWork: true,
            documentScopedFailureRecorded: false,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionItemProcessResult DocumentScopedFailure { get; } =
        new(
            DocumentCacheProjectionItemProcessOutcome.Continue,
            backoffUntil: null,
            acknowledgedOrRemovedDurableWork: false,
            documentScopedFailureRecorded: true,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionItemProcessResult LifecycleFenced { get; } =
        new(
            DocumentCacheProjectionItemProcessOutcome.LifecycleFenced,
            backoffUntil: null,
            acknowledgedOrRemovedDurableWork: false,
            documentScopedFailureRecorded: false,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionItemProcessResult TargetPaused { get; } =
        new(
            DocumentCacheProjectionItemProcessOutcome.TargetPaused,
            backoffUntil: null,
            acknowledgedOrRemovedDurableWork: false,
            documentScopedFailureRecorded: false,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionItemProcessResult TargetBackoff(DateTimeOffset backoffUntil) =>
        new(
            DocumentCacheProjectionItemProcessOutcome.TargetBackoff,
            backoffUntil,
            acknowledgedOrRemovedDurableWork: false,
            documentScopedFailureRecorded: false,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionItemProcessResult FromAdministrativeFailure(
        DocumentCacheAdministrativeDrainFailure administrativeFailure
    )
    {
        ArgumentNullException.ThrowIfNull(administrativeFailure);

        return new(
            DocumentCacheProjectionItemProcessOutcome.Continue,
            backoffUntil: null,
            acknowledgedOrRemovedDurableWork: false,
            documentScopedFailureRecorded: false,
            administrativeFailure
        );
    }
}

internal sealed class DocumentCacheProjectionItemProcessor(
    TimeProvider timeProvider,
    ILogger<DocumentCacheProjectionItemProcessor> logger
) : IDocumentCacheProjectionItemProcessor
{
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<DocumentCacheProjectionItemProcessor> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
        DocumentCacheProjectionItemProcessRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheProjectionTargetRuntimeContext targetContext = request.TargetContext;
        DocumentProjectionWorkPageItem workItem = request.WorkItem;
        using CancellationTokenSource linkedCancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                targetContext.CancellationToken
            );
        CancellationToken effectiveCancellationToken = linkedCancellationSource.Token;
        effectiveCancellationToken.ThrowIfCancellationRequested();

        try
        {
            DocumentCacheProjectionWriterInvocationResult fastPathResult = await WriteCacheAsync(
                    request,
                    candidate: null,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            if (fastPathResult.AdministrativeFailure is not null)
            {
                return DocumentCacheProjectionItemProcessResult.FromAdministrativeFailure(
                    fastPathResult.AdministrativeFailure
                );
            }

            return await HandleWriterResultAsync(
                    targetContext,
                    workItem,
                    fastPathResult.WriterResult!,
                    materializationAllowed: true,
                    request.InvocationKind,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentCacheProjectionProcessingException exception)
        {
            return PauseTargetForDeterministicFailure(targetContext, workItem, exception);
        }
        catch (DocumentCacheTargetMappingException exception)
        {
            return PauseTargetForDeterministicFailure(targetContext, workItem, exception);
        }
        catch (Exception exception)
        {
            return TargetBackoffForProviderFailure(targetContext, exception);
        }
    }

    private async Task<DocumentCacheProjectionItemProcessResult> HandleWriterResultAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        DocumentCacheWriterResult writerResult,
        bool materializationAllowed,
        DocumentCacheProjectionDrainInvocationKind invocationKind,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        switch (writerResult)
        {
            case DocumentCacheWriterResult.AlreadyCurrentAcknowledged:
            case DocumentCacheWriterResult.CandidateWrittenAcknowledged:
                targetContext.FailureBackoffState.ClearFailure(workItem.DocumentId);
                return DocumentCacheProjectionItemProcessResult.AcknowledgedOrRemoved;

            case DocumentCacheWriterResult.NeedsMaterialization when materializationAllowed:
                return await MaterializeAndWriteAsync(
                        targetContext,
                        workItem,
                        invocationKind,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            case DocumentCacheWriterResult.NeedsMaterialization:
                return PauseTargetForUnexpectedWriterResult(
                    targetContext,
                    workItem,
                    writerResult,
                    observedAt
                );

            case DocumentCacheWriterResult.SourceMissingOrDeleted:
            case DocumentCacheWriterResult.StaleCandidateSuppressed:
            case DocumentCacheWriterResult.CacheAheadDisappeared:
            case DocumentCacheWriterResult.RacingWriterLost:
                LogContinuingWriterOutcome(targetContext, writerResult);
                return DocumentCacheProjectionItemProcessResult.Continue;

            case DocumentCacheWriterResult.WorkAnomaly:
                RecordDocumentFailure(
                    targetContext,
                    workItem,
                    DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
                    $"Cache writer outcome {writerResult.Outcome}.",
                    observedAt
                );
                return DocumentCacheProjectionItemProcessResult.DocumentScopedFailure;

            case DocumentCacheWriterResult.RetryBudgetExhausted:
            case DocumentCacheWriterResult.CallerAbortedRetry:
            case DocumentCacheWriterResult.DeleteRaceRetryExhausted:
            case DocumentCacheWriterResult.CacheAheadUnconfirmedCallerAbort:
                RecordDocumentFailure(
                    targetContext,
                    workItem,
                    DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome,
                    $"Cache writer outcome {writerResult.Outcome}.",
                    observedAt
                );
                return DocumentCacheProjectionItemProcessResult.DocumentScopedFailure;

            case DocumentCacheWriterResult.LifecycleOrLatchFenced:
                LogContinuingWriterOutcome(targetContext, writerResult);
                if (targetContext.AdministrativeCommandContext is not null)
                {
                    return AdministrativeFailureForLifecycleFence(
                        targetContext.AdministrativeCommandContext,
                        (DocumentCacheWriterResult.LifecycleOrLatchFenced)writerResult,
                        workItem.DocumentId
                    );
                }

                return DocumentCacheProjectionItemProcessResult.LifecycleFenced;

            case DocumentCacheWriterResult.CacheAheadLatchSet:
                RecordDocumentFailure(
                    targetContext,
                    workItem,
                    DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome,
                    "Cache writer set the cache-ahead recovery latch.",
                    observedAt
                );
                targetContext.SchedulingState.PauseTarget();
                if (targetContext.AdministrativeCommandContext is not null)
                {
                    return AdministrativeFailureForCommandState(
                        targetContext.AdministrativeCommandContext,
                        DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
                        DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                        "Session-bound DocumentCache writer set the cache-ahead recovery latch during administrative drain.",
                        workItem.DocumentId,
                        retryable: true
                    );
                }

                return DocumentCacheProjectionItemProcessResult.TargetPaused;

            case DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure:
                RecordDocumentFailure(
                    targetContext,
                    workItem,
                    DocumentCacheProjectionDocumentDiagnosticCategory.DeterministicInvariantFailure,
                    $"Cache writer outcome {writerResult.Outcome}.",
                    observedAt
                );
                targetContext.SchedulingState.PauseTarget();
                if (targetContext.AdministrativeCommandContext is not null)
                {
                    return AdministrativeFailureForCommandState(
                        targetContext.AdministrativeCommandContext,
                        DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                        DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                        $"Cache writer outcome {writerResult.Outcome} paused the administrative drain target.",
                        workItem.DocumentId,
                        retryable: targetContext.AdministrativeCommandContext.Mutated
                    );
                }

                return DocumentCacheProjectionItemProcessResult.TargetPaused;

            default:
                return PauseTargetForUnexpectedWriterResult(
                    targetContext,
                    workItem,
                    writerResult,
                    observedAt
                );
        }
    }

    private async Task<DocumentCacheProjectionItemProcessResult> MaterializeAndWriteAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        DocumentCacheProjectionDrainInvocationKind invocationKind,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            DocumentCacheMaterializationResult materializationResult = await targetContext
                .Materializer.MaterializeAsync(
                    new DocumentCacheMaterializationRequest(
                        targetContext.MaterializationTargetContext,
                        workItem.DocumentId,
                        workItem.RequiredContentVersion,
                        DocumentCacheMaterializationPurpose.DurableWorkProjection,
                        cancellationToken
                    )
                )
                .ConfigureAwait(false);

            if (materializationResult is not DocumentCacheMaterializationResult.Success success)
            {
                LogContinuingMaterializerOutcome(targetContext, materializationResult);
                return DocumentCacheProjectionItemProcessResult.Continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            DocumentCacheProjectionWriterInvocationResult candidateResult = await WriteCacheAsync(
                    new DocumentCacheProjectionItemProcessRequest(targetContext, workItem, invocationKind),
                    success.Candidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (candidateResult.AdministrativeFailure is not null)
            {
                return DocumentCacheProjectionItemProcessResult.FromAdministrativeFailure(
                    candidateResult.AdministrativeFailure
                );
            }

            return await HandleWriterResultAsync(
                    targetContext,
                    workItem,
                    candidateResult.WriterResult!,
                    materializationAllowed: false,
                    invocationKind,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentCacheProjectionProcessingException exception)
        {
            return PauseTargetForDeterministicFailure(targetContext, workItem, exception);
        }
        catch (DocumentCacheTargetMappingException exception)
        {
            return PauseTargetForDeterministicFailure(targetContext, workItem, exception);
        }
        catch (Exception exception)
        {
            return TargetBackoffForProviderFailure(targetContext, exception);
        }
    }

    private DocumentCacheProjectionItemProcessResult PauseTargetForDeterministicFailure(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        Exception exception
    )
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        RecordDocumentFailure(
            targetContext,
            workItem,
            DocumentCacheProjectionDocumentDiagnosticCategory.DeterministicInvariantFailure,
            exception.Message,
            observedAt
        );
        targetContext.SchedulingState.PauseTarget();
        _logger.LogError(
            exception,
            "DocumentCache projection paused target {TargetKey} after deterministic item processing failure.",
            LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString())
        );

        if (targetContext.AdministrativeCommandContext is not null)
        {
            return AdministrativeFailureForCommandState(
                targetContext.AdministrativeCommandContext,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                exception.Message,
                workItem.DocumentId,
                retryable: targetContext.AdministrativeCommandContext.Mutated
            );
        }

        return DocumentCacheProjectionItemProcessResult.TargetPaused;
    }

    private DocumentCacheProjectionItemProcessResult PauseTargetForUnexpectedWriterResult(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        DocumentCacheWriterResult writerResult,
        DateTimeOffset observedAt
    )
    {
        RecordDocumentFailure(
            targetContext,
            workItem,
            DocumentCacheProjectionDocumentDiagnosticCategory.DeterministicInvariantFailure,
            $"Unexpected cache writer outcome {writerResult.Outcome}.",
            observedAt
        );
        targetContext.SchedulingState.PauseTarget();
        _logger.LogError(
            "DocumentCache projection paused target {TargetKey} after unexpected cache writer outcome {Outcome}.",
            LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString()),
            writerResult.Outcome
        );

        if (targetContext.AdministrativeCommandContext is not null)
        {
            return AdministrativeFailureForCommandState(
                targetContext.AdministrativeCommandContext,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                $"Unexpected cache writer outcome {writerResult.Outcome}.",
                workItem.DocumentId,
                retryable: targetContext.AdministrativeCommandContext.Mutated
            );
        }

        return DocumentCacheProjectionItemProcessResult.TargetPaused;
    }

    private DocumentCacheProjectionItemProcessResult TargetBackoffForProviderFailure(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        Exception exception
    )
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        DateTimeOffset backoffUntil =
            observedAt + targetContext.TargetExecutionContext.EffectiveSettings.ProjectorFailureBackoff;
        _logger.LogError(
            exception,
            "DocumentCache projection target {TargetKey} hit a provider/runtime failure while processing an item; target backoff applies.",
            LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString())
        );

        if (targetContext.AdministrativeCommandContext is not null)
        {
            return AdministrativeFailureForCommandState(
                targetContext.AdministrativeCommandContext,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                exception.Message,
                documentId: null,
                retryable: targetContext.AdministrativeCommandContext.Mutated
            );
        }

        return DocumentCacheProjectionItemProcessResult.TargetBackoff(backoffUntil);
    }

    private static async Task<DocumentCacheProjectionWriterInvocationResult> WriteCacheAsync(
        DocumentCacheProjectionItemProcessRequest request,
        DocumentCacheMaterializationCandidate? candidate,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheWriterRequest writerRequest = CreateWriterRequest(
            request.TargetContext,
            request.WorkItem,
            candidate,
            cancellationToken
        );

        DocumentCacheAdministrativeCommandExecutionContext? commandContext = request
            .TargetContext
            .AdministrativeCommandContext;
        if (request.InvocationKind == DocumentCacheProjectionDrainInvocationKind.Ordinary)
        {
            DocumentCacheWriterResult ordinaryResult = await request
                .TargetContext.Writer.WriteAsync(writerRequest)
                .ConfigureAwait(false);

            return DocumentCacheProjectionWriterInvocationResult.Success(ordinaryResult);
        }

        if (commandContext is null)
        {
            return DocumentCacheProjectionWriterInvocationResult.FromAdministrativeFailure(
                AdministrativeFailureForMissingCommandContext(request.WorkItem.DocumentId)
            );
        }

        IDocumentCacheSessionBoundWriter? sessionBoundWriter = request.TargetContext.SessionBoundWriter;
        if (sessionBoundWriter is null)
        {
            return DocumentCacheProjectionWriterInvocationResult.FromAdministrativeFailure(
                AdministrativeFailureForCommandStateDetails(
                    commandContext,
                    DocumentCacheAdministrativeCommandClassification.ProviderIneligible,
                    DocumentCacheAdministrativeDiagnosticCategory.ProviderIneligible,
                    "Administrative drain requires a provider session-bound DocumentCache writer.",
                    request.WorkItem.DocumentId,
                    retryable: false
                )
            );
        }

        DocumentCacheSessionBoundWriterResult sessionBoundResult = await sessionBoundWriter
            .WriteAsync(
                new DocumentCacheSessionBoundWriterRequest(
                    commandContext.MutexLease,
                    writerRequest,
                    commandContext.Mutated
                )
            )
            .ConfigureAwait(false);

        if (sessionBoundResult.Mutated && !commandContext.Mutated)
        {
            commandContext.MarkMutated();
        }

        if (sessionBoundResult.Classification != DocumentCacheAdministrativeCommandClassification.Succeeded)
        {
            return DocumentCacheProjectionWriterInvocationResult.FromAdministrativeFailure(
                AdministrativeFailureForSessionBoundWriter(sessionBoundResult, request.WorkItem.DocumentId)
            );
        }

        return DocumentCacheProjectionWriterInvocationResult.Success(sessionBoundResult.WriterResult!);
    }

    private static DocumentCacheProjectionItemProcessResult AdministrativeFailureForLifecycleFence(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        DocumentCacheWriterResult.LifecycleOrLatchFenced writerResult,
        long documentId
    )
    {
        bool latchSet = writerResult.Reason == DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired;

        return AdministrativeFailureForCommandState(
            commandContext,
            latchSet
                ? DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet
                : DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
            latchSet
                ? DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet
                : DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
            latchSet
                ? "Session-bound DocumentCache writer observed a cache-ahead recovery latch during administrative drain."
                : "Session-bound DocumentCache writer observed a lifecycle fence during administrative drain.",
            documentId,
            retryable: commandContext.Mutated
        );
    }

    private static DocumentCacheProjectionItemProcessResult AdministrativeFailureForCommandState(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        long? documentId,
        bool retryable
    ) =>
        DocumentCacheProjectionItemProcessResult.FromAdministrativeFailure(
            AdministrativeFailureForCommandStateDetails(
                commandContext,
                classification,
                diagnosticCategory,
                message,
                documentId,
                retryable
            )
        );

    private static DocumentCacheAdministrativeDrainFailure AdministrativeFailureForCommandStateDetails(
        DocumentCacheAdministrativeCommandExecutionContext commandContext,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        long? documentId,
        bool retryable
    )
    {
        ImmutableArray<long> affectedDocumentIds = documentId is null ? [] : [documentId.Value];

        return new DocumentCacheAdministrativeDrainFailure(
            commandContext.Mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            diagnosticCategory,
            message,
            retryable,
            affectedDocumentIds
        );
    }

    private static DocumentCacheAdministrativeDrainFailure AdministrativeFailureForMissingCommandContext(
        long documentId
    ) =>
        new(
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
            DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
            "Administrative drain item processing requires a pinned command context.",
            retryable: false,
            affectedDocumentIds: [documentId]
        );

    private static DocumentCacheAdministrativeDrainFailure AdministrativeFailureForSessionBoundWriter(
        DocumentCacheSessionBoundWriterResult result,
        long documentId
    )
    {
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory =
            result.DiagnosticCategory
            ?? DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure;

        return new DocumentCacheAdministrativeDrainFailure(
            result.Status,
            result.Classification,
            diagnosticCategory,
            result.Message,
            retryable: result.Status == DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            affectedDocumentIds: [documentId]
        );
    }

    private static DocumentCacheWriterRequest CreateWriterRequest(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        DocumentCacheMaterializationCandidate? candidate,
        CancellationToken cancellationToken
    ) =>
        new(
            targetContext.MaterializationTargetContext,
            workItem.DocumentId,
            workItem.RequiredContentVersion,
            DocumentCacheWriterPurpose.DurableWorkProjection,
            candidate,
            cancellationToken
        );

    private static void RecordDocumentFailure(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem,
        DocumentCacheProjectionDocumentDiagnosticCategory category,
        string message,
        DateTimeOffset observedAt
    ) =>
        targetContext.FailureBackoffState.RecordFailure(
            workItem.DocumentId,
            category,
            message,
            observedAt,
            targetContext.TargetExecutionContext.EffectiveSettings.ProjectorFailureBackoff
        );

    private void LogContinuingWriterOutcome(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheWriterResult writerResult
    ) =>
        _logger.LogDebug(
            "DocumentCache projection observed cache writer outcome {Outcome} for target {TargetKey}; durable work remains visible when not acknowledged by the writer.",
            writerResult.Outcome,
            LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString())
        );

    private void LogContinuingMaterializerOutcome(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheMaterializationResult materializationResult
    ) =>
        _logger.LogDebug(
            "DocumentCache projection observed materializer outcome {Outcome} for target {TargetKey}; no projector acknowledgement or repair was attempted.",
            materializationResult.GetType().Name,
            LoggingSanitizer.SanitizeForLogging(targetContext.TargetKey.ToString())
        );
}

internal sealed record DocumentCacheProjectionWriterInvocationResult(
    DocumentCacheWriterResult? WriterResult,
    DocumentCacheAdministrativeDrainFailure? AdministrativeFailure
)
{
    public static DocumentCacheProjectionWriterInvocationResult Success(
        DocumentCacheWriterResult writerResult
    )
    {
        ArgumentNullException.ThrowIfNull(writerResult);

        return new(writerResult, AdministrativeFailure: null);
    }

    public static DocumentCacheProjectionWriterInvocationResult FromAdministrativeFailure(
        DocumentCacheAdministrativeDrainFailure administrativeFailure
    )
    {
        ArgumentNullException.ThrowIfNull(administrativeFailure);

        return new(WriterResult: null, AdministrativeFailure: administrativeFailure);
    }
}

file static class DocumentCacheProjectionItemProcessingGuard
{
    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, message);
        }

        return value;
    }
}
