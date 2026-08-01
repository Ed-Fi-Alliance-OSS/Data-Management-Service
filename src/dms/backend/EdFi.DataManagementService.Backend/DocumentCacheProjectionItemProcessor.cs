// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        DateTimeOffset? backoffUntil
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
    }

    public DocumentCacheProjectionItemProcessOutcome Outcome { get; }

    public DateTimeOffset? BackoffUntil { get; }

    public static DocumentCacheProjectionItemProcessResult Continue { get; } =
        new(DocumentCacheProjectionItemProcessOutcome.Continue, backoffUntil: null);

    public static DocumentCacheProjectionItemProcessResult LifecycleFenced { get; } =
        new(DocumentCacheProjectionItemProcessOutcome.LifecycleFenced, backoffUntil: null);

    public static DocumentCacheProjectionItemProcessResult TargetPaused { get; } =
        new(DocumentCacheProjectionItemProcessOutcome.TargetPaused, backoffUntil: null);

    public static DocumentCacheProjectionItemProcessResult TargetBackoff(DateTimeOffset backoffUntil) =>
        new(DocumentCacheProjectionItemProcessOutcome.TargetBackoff, backoffUntil);
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
            DocumentCacheWriterResult fastPathResult = await targetContext
                .Writer.WriteAsync(
                    CreateWriterRequest(targetContext, workItem, candidate: null, effectiveCancellationToken)
                )
                .ConfigureAwait(false);

            return await HandleWriterResultAsync(
                    targetContext,
                    workItem,
                    fastPathResult,
                    materializationAllowed: true,
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
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        switch (writerResult)
        {
            case DocumentCacheWriterResult.AlreadyCurrentAcknowledged:
            case DocumentCacheWriterResult.CandidateWrittenAcknowledged:
                targetContext.FailureBackoffState.ClearFailure(workItem.DocumentId);
                return DocumentCacheProjectionItemProcessResult.Continue;

            case DocumentCacheWriterResult.NeedsMaterialization when materializationAllowed:
                return await MaterializeAndWriteAsync(targetContext, workItem, cancellationToken)
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
                return DocumentCacheProjectionItemProcessResult.Continue;

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
                return DocumentCacheProjectionItemProcessResult.Continue;

            case DocumentCacheWriterResult.LifecycleOrLatchFenced:
                LogContinuingWriterOutcome(targetContext, writerResult);
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
            DocumentCacheWriterResult candidateResult = await targetContext
                .Writer.WriteAsync(
                    CreateWriterRequest(targetContext, workItem, success.Candidate, cancellationToken)
                )
                .ConfigureAwait(false);

            return await HandleWriterResultAsync(
                    targetContext,
                    workItem,
                    candidateResult,
                    materializationAllowed: false,
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

        return DocumentCacheProjectionItemProcessResult.TargetBackoff(backoffUntil);
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
