// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheAdministrativeDrainer
{
    Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken = default
    );
}

internal interface IDocumentCacheAdministrativeDrainDelay
{
    Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken);
}

internal sealed class DocumentCacheAdministrativeDrainDelay : IDocumentCacheAdministrativeDrainDelay
{
    public Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Task.Delay(delay, timeProvider, cancellationToken);
    }
}

internal sealed record DocumentCacheAdministrativeDrainToEmptyResult
{
    private DocumentCacheAdministrativeDrainToEmptyResult(
        bool completed,
        int drainSliceCount,
        int processedItemCount,
        int acknowledgedOrRemovedItemCount,
        int documentScopedFailureCount,
        DocumentCacheAdministrativeCommandResult? failureResult
    )
    {
        if (!completed && failureResult is null)
        {
            throw new ArgumentException("Incomplete administrative drain results require a failure result.");
        }

        if (completed && failureResult is not null)
        {
            throw new ArgumentException(
                "Completed administrative drain results cannot carry a failure result."
            );
        }

        Completed = completed;
        DrainSliceCount = RequireNonNegative(drainSliceCount, nameof(drainSliceCount));
        ProcessedItemCount = RequireNonNegative(processedItemCount, nameof(processedItemCount));
        AcknowledgedOrRemovedItemCount = RequireNonNegative(
            acknowledgedOrRemovedItemCount,
            nameof(acknowledgedOrRemovedItemCount)
        );
        DocumentScopedFailureCount = RequireNonNegative(
            documentScopedFailureCount,
            nameof(documentScopedFailureCount)
        );
        FailureResult = failureResult;
    }

    public bool Completed { get; }

    public int DrainSliceCount { get; }

    public int ProcessedItemCount { get; }

    public int AcknowledgedOrRemovedItemCount { get; }

    public int DocumentScopedFailureCount { get; }

    public DocumentCacheAdministrativeCommandResult? FailureResult { get; }

    public static DocumentCacheAdministrativeDrainToEmptyResult Succeeded(
        DocumentCacheAdministrativeDrainStats stats
    )
    {
        ArgumentNullException.ThrowIfNull(stats);

        return new(
            completed: true,
            stats.DrainSliceCount,
            stats.ProcessedItemCount,
            stats.AcknowledgedOrRemovedItemCount,
            stats.DocumentScopedFailureCount,
            failureResult: null
        );
    }

    public static DocumentCacheAdministrativeDrainToEmptyResult Failed(
        DocumentCacheAdministrativeDrainStats stats,
        DocumentCacheAdministrativeCommandResult failureResult
    )
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(failureResult);

        return new(
            completed: false,
            stats.DrainSliceCount,
            stats.ProcessedItemCount,
            stats.AcknowledgedOrRemovedItemCount,
            stats.DocumentScopedFailureCount,
            failureResult
        );
    }

    private static int RequireNonNegative(int value, string parameterName) =>
        value < 0
            ? throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative.")
            : value;
}

internal sealed class DocumentCacheAdministrativeDrainStats
{
    public int DrainSliceCount { get; private set; }

    public int ProcessedItemCount { get; private set; }

    public int AcknowledgedOrRemovedItemCount { get; private set; }

    public int DocumentScopedFailureCount { get; private set; }

    public void Record(DocumentCacheProjectionDrainPageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        DrainSliceCount++;
        ProcessedItemCount += result.ProcessedItemCount;
        AcknowledgedOrRemovedItemCount += result.AcknowledgedOrRemovedItemCount;
        DocumentScopedFailureCount += result.DocumentScopedFailureCount;
    }
}

internal sealed record DocumentCacheAdministrativeDrainSliceResult
{
    private DocumentCacheAdministrativeDrainSliceResult(
        bool completed,
        int drainSliceCount,
        int processedItemCount,
        int acknowledgedOrRemovedItemCount,
        int documentScopedFailureCount,
        DateTimeOffset? nextRetryAt,
        DocumentCacheAdministrativeCommandResult? failureResult
    )
    {
        if (!completed && failureResult is null)
        {
            throw new ArgumentException(
                "Incomplete administrative drain slice results require a failure result."
            );
        }

        if (completed && failureResult is not null)
        {
            throw new ArgumentException(
                "Completed administrative drain slice results cannot carry a failure result."
            );
        }

        Completed = completed;
        DrainSliceCount = RequireNonNegative(drainSliceCount, nameof(drainSliceCount));
        ProcessedItemCount = RequireNonNegative(processedItemCount, nameof(processedItemCount));
        AcknowledgedOrRemovedItemCount = RequireNonNegative(
            acknowledgedOrRemovedItemCount,
            nameof(acknowledgedOrRemovedItemCount)
        );
        DocumentScopedFailureCount = RequireNonNegative(
            documentScopedFailureCount,
            nameof(documentScopedFailureCount)
        );
        NextRetryAt = nextRetryAt;
        FailureResult = failureResult;
    }

    public bool Completed { get; }

    public int DrainSliceCount { get; }

    public int ProcessedItemCount { get; }

    public int AcknowledgedOrRemovedItemCount { get; }

    public int DocumentScopedFailureCount { get; }

    public DateTimeOffset? NextRetryAt { get; }

    public DocumentCacheAdministrativeCommandResult? FailureResult { get; }

    public static DocumentCacheAdministrativeDrainSliceResult Succeeded(
        DocumentCacheProjectionDrainPageResult drainResult
    )
    {
        ArgumentNullException.ThrowIfNull(drainResult);

        var stats = new DocumentCacheAdministrativeDrainStats();
        stats.Record(drainResult);

        return Succeeded(stats, drainResult);
    }

    public static DocumentCacheAdministrativeDrainSliceResult Succeeded(
        DocumentCacheAdministrativeDrainStats stats,
        DocumentCacheProjectionDrainPageResult drainResult
    )
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(drainResult);

        return new(
            completed: true,
            stats.DrainSliceCount,
            stats.ProcessedItemCount,
            stats.AcknowledgedOrRemovedItemCount,
            stats.DocumentScopedFailureCount,
            drainResult.NextRetryAt,
            failureResult: null
        );
    }

    public static DocumentCacheAdministrativeDrainSliceResult Failed(
        DocumentCacheAdministrativeDrainStats stats,
        DocumentCacheAdministrativeCommandResult failureResult
    )
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(failureResult);

        return new(
            completed: false,
            stats.DrainSliceCount,
            stats.ProcessedItemCount,
            stats.AcknowledgedOrRemovedItemCount,
            stats.DocumentScopedFailureCount,
            nextRetryAt: null,
            failureResult
        );
    }

    private static int RequireNonNegative(int value, string parameterName) =>
        value < 0
            ? throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative.")
            : value;
}

internal sealed class DocumentCacheAdministrativeDrainer(
    IDocumentCacheProjectionScheduler scheduler,
    IDocumentCacheAdministrativeDrainDelay delay,
    TimeProvider timeProvider,
    ILogger<DocumentCacheAdministrativeDrainer> logger
) : IDocumentCacheAdministrativeDrainer
{
    public async Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
            DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
        CancellationToken effectiveCancellationToken = cancellationScope.Token;

        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);

        var stats = new DocumentCacheAdministrativeDrainStats();

        DocumentCacheProjectionDrainPageResult drainResult = await RunDrainSliceAsync(
                context,
                effectiveCancellationToken
            )
            .ConfigureAwait(false);

        stats.Record(drainResult);

        if (drainResult.AdministrativeFailure is not null)
        {
            return FailSlice(context, stats, drainResult.AdministrativeFailure);
        }

        switch (drainResult.Outcome)
        {
            case DocumentCacheProjectionDrainPageOutcome.PageProcessed:
            case DocumentCacheProjectionDrainPageOutcome.NoEligibleWork:
                context.CompletePhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                return DocumentCacheAdministrativeDrainSliceResult.Succeeded(stats, drainResult);

            case DocumentCacheProjectionDrainPageOutcome.LifecycleFenced:
                return FailSlice(
                    context,
                    stats,
                    CreateFailure(
                        context,
                        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                        DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                        "Administrative drain encountered a lifecycle or latch fence.",
                        retryable: context.Mutated
                    )
                );

            case DocumentCacheProjectionDrainPageOutcome.TargetBackoff:
            case DocumentCacheProjectionDrainPageOutcome.TargetPaused:
                return FailSlice(
                    context,
                    stats,
                    CreateFailure(
                        context,
                        DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                        DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                        "Administrative drain encountered a target-scoped projector failure.",
                        retryable: context.Mutated
                    )
                );

            case DocumentCacheProjectionDrainPageOutcome.AdministrativeFailure:
                throw new InvalidOperationException(
                    "Administrative drain failure results require failure details."
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported administrative drain page outcome '{drainResult.Outcome}'."
                );
        }
    }

    public async Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        using DocumentCacheAdministrativeWorkflowCancellationScope cancellationScope =
            DocumentCacheAdministrativeWorkflow.CreateCancellationScope(context, cancellationToken);
        CancellationToken effectiveCancellationToken = cancellationScope.Token;

        context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);

        var stats = new DocumentCacheAdministrativeDrainStats();
        var currentPass = new DocumentCacheAdministrativeDrainPass(
            context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize
        );
        var unproductivePoisonPassCount = 0;
        var observedMixedPoisonFailures = false;
        var unproductiveOrdinaryFailurePassCount = 0;
        ImmutableArray<long> nativeLimitDocumentIds = [];

        while (true)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();

            DocumentCacheAdministrativeDrainCursorPosition? startingCursor =
                DocumentCacheAdministrativeDrainCursorPosition.From(context.TargetContext.Cursor);
            DocumentCacheProjectionDrainPageResult drainResult = await RunDrainSliceAsync(
                    context,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);
            DocumentCacheAdministrativeDrainCursorPosition? endingCursor =
                DocumentCacheAdministrativeDrainCursorPosition.From(context.TargetContext.Cursor);

            stats.Record(drainResult);

            if (drainResult.AdministrativeFailure is not null)
            {
                return Fail(context, stats, drainResult.AdministrativeFailure);
            }

            ImmutableArray<long> pageNativeLimitDocumentIds = GetNativeLimitFailureIds(
                context,
                drainResult.DocumentScopedFailureIds
            );
            ImmutableArray<long> suppressedNativeLimitDocumentIds = GetNativeLimitFailureIds(
                context,
                drainResult.SuppressedDocumentIds
            );

            if (CompletedCursorPass(startingCursor, endingCursor, drainResult))
            {
                if (observedMixedPoisonFailures && currentPass.IsNativeLimitOnlyTraversal)
                {
                    // A source deletion can remove ordinary work without an acknowledgement.
                    // Retire its allowance only after a full traversal sees native work alone.
                    observedMixedPoisonFailures = false;
                    unproductivePoisonPassCount = 0;
                    unproductiveOrdinaryFailurePassCount = 0;
                }
                if (
                    currentPass.IsUnproductivePoisonPass
                    && currentPass.NativeLimitFailureCount < currentPass.DocumentScopedFailureCount
                    && currentPass.SuppressedOrdinaryFailureCount == 0
                    && drainResult.AcknowledgedOrRemovedItemCount == 0
                    && drainResult.DocumentScopedFailureCount == drainResult.ProcessedItemCount
                )
                {
                    // Count only traversals where every ordinary pending row got an attempt.
                    // Native-only retries and suppressed ordinary work consume no allowance.
                    unproductiveOrdinaryFailurePassCount++;
                }
                // Native JSON limit failures can outnumber the bounded suppression cache and
                // become eligible again on every wrap. Classify the entire completed pass,
                // using the categories captured before later pages can evict the failures.
                // Acknowledgement on the wrapping page is still progress.
                if (
                    currentPass.IsUnproductivePoisonPass
                    && currentPass.NativeLimitFailureCount > 0
                    && drainResult.AcknowledgedOrRemovedItemCount == 0
                    && drainResult.DocumentScopedFailureCount == drainResult.ProcessedItemCount
                )
                {
                    observedMixedPoisonFailures |=
                        !currentPass.IsUnproductiveNativeLimitPass
                        || pageNativeLimitDocumentIds.Length != drainResult.DocumentScopedFailureCount;
                    // Use the latest completed pass, since earlier work rows can disappear
                    // when their source documents are deleted between passes.
                    nativeLimitDocumentIds = currentPass.NativeLimitDocumentIds;
                    // A fully suppressed wrapping page is followed by NoEligibleWorkWithRetry.
                    // Classify the pass now, but count that retry boundary only once below.
                    if (drainResult.ProcessedItemCount > 0)
                    {
                        unproductivePoisonPassCount++;
                    }
                    if (
                        drainResult.ProcessedItemCount > 0
                        && unproductivePoisonPassCount >= (observedMixedPoisonFailures ? 4 : 2)
                        && (!observedMixedPoisonFailures || unproductiveOrdinaryFailurePassCount >= 4)
                    )
                    {
                        return PersistentPoison(
                            context,
                            stats,
                            pageNativeLimitDocumentIds.IsEmpty
                                ? drainResult.DocumentScopedFailureIds
                                : pageNativeLimitDocumentIds,
                            nativeLimitConfirmed: !pageNativeLimitDocumentIds.IsEmpty
                        );
                    }

                    // After establishing repeated native failures, give mixed work two more
                    // complete passes with backoff so ordinary failures can recover. Keep this
                    // allowance until progress, even if a later pass has only native failures.
                    if (
                        drainResult.ProcessedItemCount > 0
                        && observedMixedPoisonFailures
                        && unproductivePoisonPassCount >= 2
                    )
                    {
                        await delay
                            .DelayAsync(
                                context
                                    .TargetContext
                                    .TargetExecutionContext
                                    .EffectiveSettings
                                    .ProjectorFailureBackoff,
                                timeProvider,
                                effectiveCancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                }
                else if (
                    !observedMixedPoisonFailures
                    || currentPass.AcknowledgedOrRemovedItemCount > 0
                    || drainResult.AcknowledgedOrRemovedItemCount > 0
                )
                {
                    unproductivePoisonPassCount = 0;
                    observedMixedPoisonFailures = false;
                    unproductiveOrdinaryFailurePassCount = 0;
                    nativeLimitDocumentIds = [];
                }

                if (
                    currentPass.IsUnproductiveNoProgressPass
                    && drainResult.AcknowledgedOrRemovedItemCount == 0
                    && drainResult.DocumentScopedFailureCount == 0
                )
                {
                    logger.LogDebug(
                        "DocumentCache administrative drain for target {TargetKey} completed a cursor pass without acknowledging, removing, or recording failures and will poll again.",
                        LoggingSanitizer.SanitizeInternalValueForLogging(
                            context.TargetContext.TargetKey.ToString()
                        )
                    );
                    await DelayForPollIntervalAsync(context, effectiveCancellationToken)
                        .ConfigureAwait(false);
                }
                currentPass = NewPass(context);
            }

            currentPass.Record(drainResult, pageNativeLimitDocumentIds, suppressedNativeLimitDocumentIds);

            switch (drainResult.Outcome)
            {
                case DocumentCacheProjectionDrainPageOutcome.PageProcessed:
                    continue;

                case DocumentCacheProjectionDrainPageOutcome.NoEligibleWork:
                    DocumentCacheAdministrativeProjectedStateEmptinessResult emptiness =
                        await ReadProjectedStateEmptinessAsync(context, effectiveCancellationToken)
                            .ConfigureAwait(false);

                    if (emptiness.DocumentProjectionWorkEmpty)
                    {
                        context.CompletePhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                        return DocumentCacheAdministrativeDrainToEmptyResult.Succeeded(stats);
                    }

                    if (drainResult.NextRetryAt is not null)
                    {
                        if (currentPass.IsUnproductivePoisonPass || currentPass.ProcessedItemCount == 0)
                        {
                            unproductivePoisonPassCount++;
                            if (
                                unproductivePoisonPassCount >= (observedMixedPoisonFailures ? 4 : 2)
                                && (!observedMixedPoisonFailures || unproductiveOrdinaryFailurePassCount >= 4)
                            )
                            {
                                // The terminal suppressed scan observes the remaining work.
                                // Older native failures may have disappeared via source deletion.
                                ImmutableArray<long> pendingNativeLimitDocumentIds = nativeLimitDocumentIds
                                    .Intersect(currentPass.DiagnosticDocumentIds)
                                    .ToImmutableArray();
                                return pendingNativeLimitDocumentIds.IsEmpty
                                    ? PersistentPoison(context, stats, currentPass.DiagnosticDocumentIds)
                                    : PersistentPoison(
                                        context,
                                        stats,
                                        pendingNativeLimitDocumentIds,
                                        nativeLimitConfirmed: true
                                    );
                            }
                        }
                        else if (
                            !observedMixedPoisonFailures
                            || currentPass.AcknowledgedOrRemovedItemCount > 0
                        )
                        {
                            unproductivePoisonPassCount = 0;
                            observedMixedPoisonFailures = false;
                            unproductiveOrdinaryFailurePassCount = 0;
                            nativeLimitDocumentIds = [];
                        }

                        await DelayUntilAsync(drainResult.NextRetryAt.Value, effectiveCancellationToken)
                            .ConfigureAwait(false);
                        currentPass = NewPass(context);
                        continue;
                    }

                    unproductivePoisonPassCount = 0;
                    observedMixedPoisonFailures = false;
                    unproductiveOrdinaryFailurePassCount = 0;
                    nativeLimitDocumentIds = [];
                    currentPass = NewPass(context);
                    logger.LogDebug(
                        "DocumentCache administrative drain for target {TargetKey} found durable work after an empty page and will poll again.",
                        LoggingSanitizer.SanitizeInternalValueForLogging(
                            context.TargetContext.TargetKey.ToString()
                        )
                    );
                    await DelayForPollIntervalAsync(context, effectiveCancellationToken)
                        .ConfigureAwait(false);
                    continue;

                case DocumentCacheProjectionDrainPageOutcome.LifecycleFenced:
                    return Fail(
                        context,
                        stats,
                        CreateFailure(
                            context,
                            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                            DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                            "Administrative drain encountered a lifecycle or latch fence.",
                            retryable: context.Mutated
                        )
                    );

                case DocumentCacheProjectionDrainPageOutcome.TargetBackoff:
                case DocumentCacheProjectionDrainPageOutcome.TargetPaused:
                    return Fail(
                        context,
                        stats,
                        CreateFailure(
                            context,
                            DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                            DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                            "Administrative drain encountered a target-scoped projector failure.",
                            retryable: context.Mutated
                        )
                    );

                case DocumentCacheProjectionDrainPageOutcome.AdministrativeFailure:
                    throw new InvalidOperationException(
                        "Administrative drain failure results require failure details."
                    );

                default:
                    throw new InvalidOperationException(
                        $"Unsupported administrative drain page outcome '{drainResult.Outcome}'."
                    );
            }
        }
    }

    private async Task DelayUntilAsync(DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        TimeSpan delayDuration = retryAt - timeProvider.GetUtcNow();
        if (delayDuration <= TimeSpan.Zero)
        {
            return;
        }

        await delay.DelayAsync(delayDuration, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private Task DelayForPollIntervalAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    ) =>
        delay.DelayAsync(
            context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPollInterval,
            timeProvider,
            cancellationToken
        );

    private static DocumentCacheAdministrativeDrainPass NewPass(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) => new(context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize);

    private static bool CompletedCursorPass(
        DocumentCacheAdministrativeDrainCursorPosition? startingCursor,
        DocumentCacheAdministrativeDrainCursorPosition? endingCursor,
        DocumentCacheProjectionDrainPageResult drainResult
    ) =>
        drainResult.Outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed
        && startingCursor is not null
        && endingCursor is not null
        && endingCursor.Value.CompareTo(startingCursor.Value) <= 0;

    private async Task<DocumentCacheProjectionDrainPageResult> RunDrainSliceAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheProjectionSchedulerDispatchResult dispatch = await scheduler
            .RunAdministrativeDrainSliceAsync(context.TargetContext, cancellationToken)
            .ConfigureAwait(false);

        return dispatch.DrainResult
            ?? throw new InvalidOperationException(
                "Administrative drain scheduler dispatch did not return a drain result."
            );
    }

    private static DocumentCacheAdministrativeDrainSliceResult FailSlice(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeDrainStats stats,
        DocumentCacheAdministrativeDrainFailure failure
    )
    {
        DocumentCacheAdministrativeCommandResult failureResult = context.Failed(
            failure.Status,
            failure.Classification,
            failure.DiagnosticCategory,
            failure.Message,
            failure.Retryable,
            failure.AffectedDocumentIds
        );

        return DocumentCacheAdministrativeDrainSliceResult.Failed(stats, failureResult);
    }

    private static DocumentCacheAdministrativeDrainToEmptyResult Fail(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeDrainStats stats,
        DocumentCacheAdministrativeDrainFailure failure
    )
    {
        DocumentCacheAdministrativeCommandResult failureResult = context.Failed(
            failure.Status,
            failure.Classification,
            failure.DiagnosticCategory,
            failure.Message,
            failure.Retryable,
            failure.AffectedDocumentIds
        );

        return DocumentCacheAdministrativeDrainToEmptyResult.Failed(stats, failureResult);
    }

    // Must run while the page's failures are still in the bounded suppression state; later
    // pages can evict them, so the pass captures the classification when it records a page.
    private static ImmutableArray<long> GetNativeLimitFailureIds(
        DocumentCacheAdministrativeCommandExecutionContext context,
        ImmutableArray<long> documentIds
    )
    {
        if (
            context.TargetContext.TargetExecutionContext.ProviderToken != RelationalProviderToken.SqlServer
            || documentIds.IsDefaultOrEmpty
        )
        {
            return [];
        }

        return context
            .TargetContext.FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Where(diagnostic =>
                diagnostic.Category
                    == DocumentCacheProjectionDocumentDiagnosticCategory.NativeJsonLimitExceeded
                && documentIds.Contains(diagnostic.DocumentId)
            )
            .Select(diagnostic => diagnostic.DocumentId)
            .ToImmutableArray();
    }

    private static DocumentCacheAdministrativeDrainToEmptyResult PersistentPoison(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeDrainStats stats,
        ImmutableArray<long> diagnosticDocumentIds,
        bool nativeLimitConfirmed = false
    )
    {
        ImmutableArray<long> boundedDocumentIds = diagnosticDocumentIds
            .Take(context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize)
            .ToImmutableArray();
        var nativeLimitDocumentIds = context
            .TargetContext.FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Where(diagnostic =>
                diagnostic.Category
                == DocumentCacheProjectionDocumentDiagnosticCategory.NativeJsonLimitExceeded
            )
            .Select(diagnostic => diagnostic.DocumentId)
            .ToHashSet();

        DocumentCacheAdministrativeCommandResult failureResult = context.Failed(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.PersistentPoison,
            DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison,
            context.TargetContext.TargetExecutionContext.ProviderToken == RelationalProviderToken.SqlServer
            && !boundedDocumentIds.IsEmpty
            && (nativeLimitConfirmed || boundedDocumentIds.All(nativeLimitDocumentIds.Contains))
                ? "A document exceeds SQL Server native JSON storage limits. Its projection work is still pending. Update or remove the source document before retrying."
                : "Administrative drain observed durable work that repeatedly produced only document-scoped failures after retry was due.",
            retryable: true,
            affectedDocumentIds: boundedDocumentIds
        );

        return DocumentCacheAdministrativeDrainToEmptyResult.Failed(stats, failureResult);
    }

    private static DocumentCacheAdministrativeDrainFailure CreateFailure(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        bool retryable
    ) =>
        new(
            context.Mutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            classification,
            diagnosticCategory,
            message,
            retryable
        );

    private static async Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        return await DocumentCacheAdministrativeWorkflow
            .ExecuteInTransactionAsync(
                context,
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
    }
}

internal readonly record struct DocumentCacheAdministrativeDrainCursorPosition(
    DateTimeOffset FirstEnqueuedAt,
    long DocumentId
) : IComparable<DocumentCacheAdministrativeDrainCursorPosition>
{
    public static DocumentCacheAdministrativeDrainCursorPosition? From(
        DocumentCacheProjectionCursorState cursor
    )
    {
        if (!cursor.HasValue)
        {
            return null;
        }

        return new(cursor.LastFirstEnqueuedAt!.Value, cursor.LastDocumentId!.Value);
    }

    public int CompareTo(DocumentCacheAdministrativeDrainCursorPosition other)
    {
        int firstEnqueuedAtComparison = FirstEnqueuedAt.CompareTo(other.FirstEnqueuedAt);
        if (firstEnqueuedAtComparison != 0)
        {
            return firstEnqueuedAtComparison;
        }

        return DocumentId.CompareTo(other.DocumentId);
    }
}

internal sealed class DocumentCacheAdministrativeDrainPass(int diagnosticCapacity)
{
    private ImmutableArray<long> _diagnosticDocumentIds = [];
    private ImmutableArray<long> _nativeLimitDocumentIds = [];
    private int _suppressedFailureCount;

    public int ProcessedItemCount { get; private set; }

    public int AcknowledgedOrRemovedItemCount { get; private set; }

    public int DocumentScopedFailureCount { get; private set; }

    public ImmutableArray<long> DiagnosticDocumentIds => _diagnosticDocumentIds;

    public bool IsUnproductiveNoProgressPass =>
        ProcessedItemCount > 0 && AcknowledgedOrRemovedItemCount == 0 && DocumentScopedFailureCount == 0;

    public int NativeLimitFailureCount { get; private set; }

    public int SuppressedOrdinaryFailureCount { get; private set; }

    public bool IsNativeLimitOnlyTraversal =>
        ProcessedItemCount + _suppressedFailureCount > 0
        && ProcessedItemCount == NativeLimitFailureCount
        && SuppressedOrdinaryFailureCount == 0;

    public ImmutableArray<long> NativeLimitDocumentIds => _nativeLimitDocumentIds;

    public bool IsUnproductiveNativeLimitPass =>
        IsUnproductivePoisonPass && NativeLimitFailureCount == DocumentScopedFailureCount;

    public bool IsUnproductivePoisonPass =>
        ProcessedItemCount > 0
        && AcknowledgedOrRemovedItemCount == 0
        && DocumentScopedFailureCount == ProcessedItemCount;

    public void Record(
        DocumentCacheProjectionDrainPageResult result,
        ImmutableArray<long> nativeLimitFailureIds = default,
        ImmutableArray<long> suppressedNativeLimitFailureIds = default
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        _suppressedFailureCount += result.SuppressedDocumentIds.Length;
        SuppressedOrdinaryFailureCount +=
            result.SuppressedDocumentIds.Length
            - (suppressedNativeLimitFailureIds.IsDefault ? 0 : suppressedNativeLimitFailureIds.Length);

        if (!nativeLimitFailureIds.IsDefaultOrEmpty)
        {
            NativeLimitFailureCount += nativeLimitFailureIds.Length;
            _nativeLimitDocumentIds = _nativeLimitDocumentIds
                .AddRange(nativeLimitFailureIds)
                .Distinct()
                .Take(diagnosticCapacity)
                .ToImmutableArray();
        }
        ProcessedItemCount += result.ProcessedItemCount;
        AcknowledgedOrRemovedItemCount += result.AcknowledgedOrRemovedItemCount;
        DocumentScopedFailureCount += result.DocumentScopedFailureCount;

        _diagnosticDocumentIds = _diagnosticDocumentIds
            .AddRange(result.DocumentScopedFailureIds)
            .AddRange(result.SuppressedDocumentIds)
            .Distinct()
            .Take(diagnosticCapacity)
            .ToImmutableArray();
    }
}
