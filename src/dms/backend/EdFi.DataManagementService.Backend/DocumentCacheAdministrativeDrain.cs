// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
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
        var unproductiveRetryPassCount = 0;

        while (true)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();

            DocumentCacheProjectionDrainPageResult drainResult = await RunDrainSliceAsync(
                    context,
                    effectiveCancellationToken
                )
                .ConfigureAwait(false);

            stats.Record(drainResult);
            currentPass.Record(drainResult);

            if (drainResult.AdministrativeFailure is not null)
            {
                return Fail(context, stats, drainResult.AdministrativeFailure);
            }

            switch (drainResult.Outcome)
            {
                case DocumentCacheProjectionDrainPageOutcome.PageProcessed:
                    if (drainResult.AcknowledgedOrRemovedItemCount > 0)
                    {
                        unproductiveRetryPassCount = 0;
                    }

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
                        if (currentPass.IsUnproductivePoisonPass)
                        {
                            unproductiveRetryPassCount++;
                            if (unproductiveRetryPassCount >= 2)
                            {
                                return PersistentPoison(context, stats, currentPass.DiagnosticDocumentIds);
                            }
                        }
                        else
                        {
                            unproductiveRetryPassCount = 0;
                        }

                        await DelayUntilAsync(drainResult.NextRetryAt.Value, effectiveCancellationToken)
                            .ConfigureAwait(false);
                        currentPass = NewPass(context);
                        continue;
                    }

                    unproductiveRetryPassCount = 0;
                    currentPass = NewPass(context);
                    logger.LogDebug(
                        "DocumentCache administrative drain for target {TargetKey} found durable work after an empty page and will poll again.",
                        LoggingSanitizer.SanitizeForLogging(context.TargetContext.TargetKey.ToString())
                    );
                    await delay
                        .DelayAsync(
                            context
                                .TargetContext
                                .TargetExecutionContext
                                .EffectiveSettings
                                .ProjectorPollInterval,
                            timeProvider,
                            effectiveCancellationToken
                        )
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

    private static DocumentCacheAdministrativeDrainPass NewPass(
        DocumentCacheAdministrativeCommandExecutionContext context
    ) => new(context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize);

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

    private static DocumentCacheAdministrativeDrainToEmptyResult PersistentPoison(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeDrainStats stats,
        ImmutableArray<long> diagnosticDocumentIds
    )
    {
        ImmutableArray<long> boundedDocumentIds = diagnosticDocumentIds.IsDefaultOrEmpty
            ? context.TargetContext.FailureBackoffState.CreateFailureDiagnosticsSnapshot().DocumentIds
            : diagnosticDocumentIds;

        DocumentCacheAdministrativeCommandResult failureResult = context.Failed(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.PersistentPoison,
            DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison,
            "Administrative drain observed durable work that repeatedly produced only document-scoped failures after retry was due.",
            retryable: true,
            affectedDocumentIds: boundedDocumentIds
                .Take(context.TargetContext.TargetExecutionContext.EffectiveSettings.ProjectorPageSize)
                .ToImmutableArray()
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
    }
}

internal sealed class DocumentCacheAdministrativeDrainPass(int diagnosticCapacity)
{
    private ImmutableArray<long> _diagnosticDocumentIds = [];

    public int ProcessedItemCount { get; private set; }

    public int AcknowledgedOrRemovedItemCount { get; private set; }

    public int DocumentScopedFailureCount { get; private set; }

    public ImmutableArray<long> DiagnosticDocumentIds => _diagnosticDocumentIds;

    public bool IsUnproductivePoisonPass =>
        ProcessedItemCount > 0
        && AcknowledgedOrRemovedItemCount == 0
        && DocumentScopedFailureCount == ProcessedItemCount;

    public void Record(DocumentCacheProjectionDrainPageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        ProcessedItemCount += result.ProcessedItemCount;
        AcknowledgedOrRemovedItemCount += result.AcknowledgedOrRemovedItemCount;
        DocumentScopedFailureCount += result.DocumentScopedFailureCount;

        if (result.DocumentScopedFailureIds.IsDefaultOrEmpty)
        {
            return;
        }

        _diagnosticDocumentIds = _diagnosticDocumentIds
            .AddRange(result.DocumentScopedFailureIds)
            .Distinct()
            .Take(diagnosticCapacity)
            .ToImmutableArray();
    }
}
