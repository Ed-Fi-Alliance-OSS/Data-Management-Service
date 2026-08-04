// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

public interface IDocumentCacheProjectionScheduler
{
    Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        CancellationToken cancellationToken = default
    );
}

public interface IDocumentCacheProjectionDrainPageProcessor
{
    Task<DocumentCacheProjectionDrainPageResult> ProcessPageAsync(
        DocumentCacheProjectionDrainPageRequest request,
        CancellationToken cancellationToken = default
    );
}

public enum DocumentCacheProjectionDrainInvocationKind
{
    Ordinary = 1,
    Administrative = 2,
}

public sealed record DocumentCacheProjectionDrainPageRequest
{
    public DocumentCacheProjectionDrainPageRequest(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    )
    {
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        InvocationKind = DocumentCacheProjectionSchedulingGuard.RequireDefined(
            invocationKind,
            nameof(invocationKind),
            "Unsupported DocumentCache projection drain invocation kind."
        );
    }

    public DocumentCacheProjectionTargetRuntimeContext TargetContext { get; }

    public DocumentCacheProjectionDrainInvocationKind InvocationKind { get; }
}

public enum DocumentCacheProjectionDrainPageOutcome
{
    PageProcessed = 1,
    NoEligibleWork = 2,
    TargetBackoff = 3,
    LifecycleFenced = 4,
    TargetPaused = 5,
    AdministrativeFailure = 6,
}

public sealed record DocumentCacheAdministrativeDrainFailure
{
    public DocumentCacheAdministrativeDrainFailure(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        DocumentCacheAdministrativeDiagnosticCategory diagnosticCategory,
        string message,
        bool retryable,
        ImmutableArray<long> affectedDocumentIds = default
    )
    {
        Status = DocumentCacheProjectionSchedulingGuard.RequireDefined(
            status,
            nameof(status),
            "Unsupported administrative command status."
        );
        Classification = DocumentCacheProjectionSchedulingGuard.RequireDefined(
            classification,
            nameof(classification),
            "Unsupported administrative command classification."
        );
        DiagnosticCategory = DocumentCacheProjectionSchedulingGuard.RequireDefined(
            diagnosticCategory,
            nameof(diagnosticCategory),
            "Unsupported administrative diagnostic category."
        );
        if (!affectedDocumentIds.IsDefaultOrEmpty && affectedDocumentIds.Any(documentId => documentId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(affectedDocumentIds),
                "Affected document ids must be positive."
            );
        }

        Status = status;
        Classification = classification;
        DiagnosticCategory = diagnosticCategory;
        Message = string.IsNullOrWhiteSpace(message) ? diagnosticCategory.ToString() : message;
        Retryable = retryable;
        AffectedDocumentIds = affectedDocumentIds.IsDefault ? [] : affectedDocumentIds;
    }

    public DocumentCacheAdministrativeCommandStatus Status { get; }

    public DocumentCacheAdministrativeCommandClassification Classification { get; }

    public DocumentCacheAdministrativeDiagnosticCategory DiagnosticCategory { get; }

    public string Message { get; }

    public bool Retryable { get; }

    public ImmutableArray<long> AffectedDocumentIds { get; }
}

public sealed record DocumentCacheProjectionDrainPageResult
{
    private DocumentCacheProjectionDrainPageResult(
        DocumentCacheProjectionDrainPageOutcome outcome,
        int processedItemCount,
        DateTimeOffset? backoffUntil,
        DateTimeOffset? nextRetryAt,
        int acknowledgedOrRemovedItemCount,
        int documentScopedFailureCount,
        ImmutableArray<long> documentScopedFailureIds,
        DocumentCacheAdministrativeDrainFailure? administrativeFailure
    )
    {
        Outcome = DocumentCacheProjectionSchedulingGuard.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported DocumentCache projection drain page outcome."
        );
        ProcessedItemCount = DocumentCacheProjectionSchedulingGuard.RequireNonNegative(
            processedItemCount,
            nameof(processedItemCount)
        );
        if (outcome == DocumentCacheProjectionDrainPageOutcome.TargetBackoff && backoffUntil is null)
        {
            throw new ArgumentException("Target backoff drain results require a backoff boundary.");
        }

        if (outcome != DocumentCacheProjectionDrainPageOutcome.TargetBackoff && backoffUntil is not null)
        {
            throw new ArgumentException("Only target backoff drain results may carry a backoff boundary.");
        }

        if (outcome != DocumentCacheProjectionDrainPageOutcome.NoEligibleWork && nextRetryAt is not null)
        {
            throw new ArgumentException("Only no-work drain results may carry a next retry boundary.");
        }

        if (
            outcome == DocumentCacheProjectionDrainPageOutcome.AdministrativeFailure
            && administrativeFailure is null
        )
        {
            throw new ArgumentException("Administrative failure drain results require failure details.");
        }

        if (
            outcome != DocumentCacheProjectionDrainPageOutcome.AdministrativeFailure
            && administrativeFailure is not null
        )
        {
            throw new ArgumentException(
                "Only administrative failure drain results may carry failure details."
            );
        }

        if (acknowledgedOrRemovedItemCount < 0 || acknowledgedOrRemovedItemCount > processedItemCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acknowledgedOrRemovedItemCount),
                "Acknowledged work count must be within the processed item count."
            );
        }

        if (documentScopedFailureCount < 0 || documentScopedFailureCount > processedItemCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentScopedFailureCount),
                "Document-scoped failure count must be within the processed item count."
            );
        }

        if (
            !documentScopedFailureIds.IsDefaultOrEmpty
            && documentScopedFailureIds.Any(documentId => documentId <= 0)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentScopedFailureIds),
                "Document-scoped failure ids must be positive."
            );
        }

        if (
            !documentScopedFailureIds.IsDefaultOrEmpty
            && documentScopedFailureIds.Length > documentScopedFailureCount
        )
        {
            throw new ArgumentException(
                "Document-scoped failure diagnostics cannot exceed the failure count.",
                nameof(documentScopedFailureIds)
            );
        }

        BackoffUntil = backoffUntil;
        NextRetryAt = nextRetryAt;
        AcknowledgedOrRemovedItemCount = acknowledgedOrRemovedItemCount;
        DocumentScopedFailureCount = documentScopedFailureCount;
        DocumentScopedFailureIds = documentScopedFailureIds.IsDefault ? [] : documentScopedFailureIds;
        AdministrativeFailure = administrativeFailure;
    }

    public DocumentCacheProjectionDrainPageOutcome Outcome { get; }

    public int ProcessedItemCount { get; }

    public DateTimeOffset? BackoffUntil { get; }

    public DateTimeOffset? NextRetryAt { get; }

    public int AcknowledgedOrRemovedItemCount { get; }

    public int DocumentScopedFailureCount { get; }

    public ImmutableArray<long> DocumentScopedFailureIds { get; }

    public DocumentCacheAdministrativeDrainFailure? AdministrativeFailure { get; }

    public static DocumentCacheProjectionDrainPageResult PageProcessed(
        int processedItemCount,
        int acknowledgedOrRemovedItemCount = 0,
        int documentScopedFailureCount = 0,
        ImmutableArray<long> documentScopedFailureIds = default
    ) =>
        new(
            DocumentCacheProjectionDrainPageOutcome.PageProcessed,
            processedItemCount,
            backoffUntil: null,
            nextRetryAt: null,
            acknowledgedOrRemovedItemCount,
            documentScopedFailureCount,
            documentScopedFailureIds,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionDrainPageResult NoEligibleWork { get; } =
        new(
            DocumentCacheProjectionDrainPageOutcome.NoEligibleWork,
            processedItemCount: 0,
            backoffUntil: null,
            nextRetryAt: null,
            acknowledgedOrRemovedItemCount: 0,
            documentScopedFailureCount: 0,
            documentScopedFailureIds: [],
            administrativeFailure: null
        );

    public static DocumentCacheProjectionDrainPageResult NoEligibleWorkWithRetry(
        DateTimeOffset nextRetryAt
    ) =>
        new(
            DocumentCacheProjectionDrainPageOutcome.NoEligibleWork,
            processedItemCount: 0,
            backoffUntil: null,
            nextRetryAt,
            acknowledgedOrRemovedItemCount: 0,
            documentScopedFailureCount: 0,
            documentScopedFailureIds: [],
            administrativeFailure: null
        );

    public static DocumentCacheProjectionDrainPageResult LifecycleFenced { get; } =
        new(
            DocumentCacheProjectionDrainPageOutcome.LifecycleFenced,
            processedItemCount: 0,
            backoffUntil: null,
            nextRetryAt: null,
            acknowledgedOrRemovedItemCount: 0,
            documentScopedFailureCount: 0,
            documentScopedFailureIds: [],
            administrativeFailure: null
        );

    public static DocumentCacheProjectionDrainPageResult TargetBackoff(DateTimeOffset backoffUntil) =>
        new(
            DocumentCacheProjectionDrainPageOutcome.TargetBackoff,
            processedItemCount: 0,
            backoffUntil,
            nextRetryAt: null,
            acknowledgedOrRemovedItemCount: 0,
            documentScopedFailureCount: 0,
            documentScopedFailureIds: [],
            administrativeFailure: null
        );

    public static DocumentCacheProjectionDrainPageResult TargetPaused(
        int processedItemCount,
        int acknowledgedOrRemovedItemCount = 0,
        int documentScopedFailureCount = 0,
        ImmutableArray<long> documentScopedFailureIds = default
    ) =>
        new(
            DocumentCacheProjectionDrainPageOutcome.TargetPaused,
            processedItemCount,
            backoffUntil: null,
            nextRetryAt: null,
            acknowledgedOrRemovedItemCount,
            documentScopedFailureCount,
            documentScopedFailureIds,
            administrativeFailure: null
        );

    public static DocumentCacheProjectionDrainPageResult AdministrativeFailureResult(
        int processedItemCount,
        int acknowledgedOrRemovedItemCount,
        int documentScopedFailureCount,
        ImmutableArray<long> documentScopedFailureIds,
        DocumentCacheAdministrativeDrainFailure administrativeFailure
    ) =>
        new(
            DocumentCacheProjectionDrainPageOutcome.AdministrativeFailure,
            processedItemCount,
            backoffUntil: null,
            nextRetryAt: null,
            acknowledgedOrRemovedItemCount,
            documentScopedFailureCount,
            documentScopedFailureIds,
            administrativeFailure
        );
}

public enum DocumentCacheProjectionSchedulerDispatchStatus
{
    Dispatched = 1,
    Skipped = 2,
    Faulted = 3,
}

public enum DocumentCacheProjectionTargetReadinessBlockReason
{
    CancellationPending = 1,
    TargetBackoff = 2,
    PollSleep = 3,
    CommandOwned = 4,
    LocalDrainActive = 5,
    TargetPaused = 6,
}

public sealed record DocumentCacheProjectionSchedulerDispatchResult
{
    private DocumentCacheProjectionSchedulerDispatchResult(
        DocumentCacheProjectionTargetContextKey contextKey,
        DocumentCacheProjectionSchedulerDispatchStatus status,
        DocumentCacheProjectionTargetReadinessBlockReason? blockReason,
        DocumentCacheProjectionDrainPageResult? drainResult,
        DateTimeOffset observedAt,
        DateTimeOffset? completedAt
    )
    {
        ContextKey = contextKey ?? throw new ArgumentNullException(nameof(contextKey));
        Status = DocumentCacheProjectionSchedulingGuard.RequireDefined(
            status,
            nameof(status),
            "Unsupported DocumentCache projection scheduler dispatch status."
        );
        if (status == DocumentCacheProjectionSchedulerDispatchStatus.Skipped && blockReason is null)
        {
            throw new ArgumentException("Skipped dispatch results require a block reason.");
        }

        if (status != DocumentCacheProjectionSchedulerDispatchStatus.Skipped && blockReason is not null)
        {
            throw new ArgumentException("Only skipped dispatch results may carry a block reason.");
        }

        if (status == DocumentCacheProjectionSchedulerDispatchStatus.Dispatched && drainResult is null)
        {
            throw new ArgumentException("Dispatched results require a drain result.");
        }

        ContextKey = contextKey;
        BlockReason = blockReason;
        DrainResult = drainResult;
        ObservedAt = observedAt;
        CompletedAt = completedAt;
    }

    public DocumentCacheProjectionTargetContextKey ContextKey { get; }

    public DocumentCacheTargetKey TargetKey => ContextKey.TargetKey;

    public DocumentCacheTargetContextGeneration Generation => ContextKey.Generation;

    public DocumentCacheProjectionSchedulerDispatchStatus Status { get; }

    public DocumentCacheProjectionTargetReadinessBlockReason? BlockReason { get; }

    public DocumentCacheProjectionDrainPageResult? DrainResult { get; }

    public DateTimeOffset ObservedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public static DocumentCacheProjectionSchedulerDispatchResult Dispatched(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionDrainPageResult drainResult,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt
    ) =>
        new(
            context.ContextKey,
            DocumentCacheProjectionSchedulerDispatchStatus.Dispatched,
            blockReason: null,
            drainResult,
            startedAt,
            completedAt
        );

    public static DocumentCacheProjectionSchedulerDispatchResult Skipped(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionTargetReadinessBlockReason blockReason,
        DateTimeOffset observedAt
    ) =>
        new(
            context.ContextKey,
            DocumentCacheProjectionSchedulerDispatchStatus.Skipped,
            blockReason,
            drainResult: null,
            observedAt,
            completedAt: null
        );

    public static DocumentCacheProjectionSchedulerDispatchResult Faulted(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionDrainPageResult drainResult,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt
    ) =>
        new(
            context.ContextKey,
            DocumentCacheProjectionSchedulerDispatchStatus.Faulted,
            blockReason: null,
            drainResult,
            startedAt,
            completedAt
        );
}

public sealed class DocumentCacheProjectionTargetDrainExecutor
{
    private static readonly AsyncLocal<object?> CurrentAdministrativeOwner = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _administrativeOwnerToken = new();
    private int _owner;

    public DocumentCacheProjectionDrainInvocationKind? CurrentOwner =>
        Volatile.Read(ref _owner) switch
        {
            (int)DocumentCacheProjectionDrainInvocationKind.Ordinary =>
                DocumentCacheProjectionDrainInvocationKind.Ordinary,
            (int)DocumentCacheProjectionDrainInvocationKind.Administrative =>
                DocumentCacheProjectionDrainInvocationKind.Administrative,
            _ => null,
        };

    public bool IsOwned => Volatile.Read(ref _owner) != 0;

    public bool IsCommandOwned =>
        Volatile.Read(ref _owner) == (int)DocumentCacheProjectionDrainInvocationKind.Administrative;

    public bool IsOwnedByCurrentAdministrativeFlow =>
        ReferenceEquals(CurrentAdministrativeOwner.Value, _administrativeOwnerToken);

    public async Task<DocumentCacheProjectionDrainPageResult?> TryRunOrdinaryDrainSliceAsync(
        Func<CancellationToken, Task<DocumentCacheProjectionDrainPageResult>> drainSlice,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(drainSlice);
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Volatile.Write(ref _owner, (int)DocumentCacheProjectionDrainInvocationKind.Ordinary);
        try
        {
            return await drainSlice(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _owner, 0);
            _gate.Release();
        }
    }

    public async Task<DocumentCacheProjectionDrainPageResult> RunAdministrativeDrainSliceAsync(
        Func<CancellationToken, Task<DocumentCacheProjectionDrainPageResult>> drainSlice,
        CancellationToken cancellationToken = default
    )
    {
        if (IsOwnedByCurrentAdministrativeFlow)
        {
            return await drainSlice(cancellationToken).ConfigureAwait(false);
        }

        return await RunAdministrativeCommandAsync(drainSlice, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> RunAdministrativeCommandAsync<T>(
        Func<CancellationToken, Task<T>> command,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _owner, (int)DocumentCacheProjectionDrainInvocationKind.Administrative);
        object? previousAdministrativeOwner = CurrentAdministrativeOwner.Value;
        CurrentAdministrativeOwner.Value = _administrativeOwnerToken;
        try
        {
            return await command(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CurrentAdministrativeOwner.Value = previousAdministrativeOwner;
            Volatile.Write(ref _owner, 0);
            _gate.Release();
        }
    }
}

public sealed class DocumentCacheProjectionTargetSchedulingState
{
    private readonly object _sync = new();
    private ProjectionThroughputCounter _ordinaryPageThroughput;
    private ProjectionThroughputCounter _administrativeDrainThroughput;
    private bool _targetPaused;
    private bool _lifecycleFencePaused;
    private DocumentCacheProjectionLifecycleFenceSnapshot _lifecycleFenceSnapshot =
        DocumentCacheProjectionLifecycleFenceSnapshot.Unknown;

    public DateTimeOffset? TargetBackoffUntil { get; private set; }

    public DateTimeOffset? PollSleepUntil { get; private set; }

    public bool IsTargetPaused
    {
        get
        {
            lock (_sync)
            {
                return _targetPaused || _lifecycleFencePaused;
            }
        }
    }

    public DocumentCacheProjectionLifecycleFenceSnapshot LifecycleFenceSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _lifecycleFenceSnapshot;
            }
        }
    }

    public void PauseTarget()
    {
        lock (_sync)
        {
            _targetPaused = true;
            TargetBackoffUntil = null;
            PollSleepUntil = null;
        }
    }

    public void ObserveLifecycleFence(DocumentCacheProjectionLifecycleFenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            _lifecycleFenceSnapshot = snapshot;
            if (snapshot.State == DocumentCacheProjectionLifecycleFenceState.Eligible)
            {
                _lifecycleFencePaused = false;
                return;
            }

            _lifecycleFencePaused = true;
            TargetBackoffUntil = null;
            PollSleepUntil = null;
        }
    }

    public void SetTargetBackoffUntil(DateTimeOffset backoffUntil)
    {
        lock (_sync)
        {
            TargetBackoffUntil = backoffUntil;
            PollSleepUntil = null;
        }
    }

    public void SetPollSleepUntil(DateTimeOffset pollSleepUntil)
    {
        lock (_sync)
        {
            PollSleepUntil = pollSleepUntil;
        }
    }

    public DocumentCacheProjectionTargetReadinessBlockReason? GetReadinessBlock(
        DateTimeOffset now,
        bool cancellationRequested,
        DocumentCacheProjectionTargetDrainExecutor drainExecutor
    )
    {
        ArgumentNullException.ThrowIfNull(drainExecutor);

        if (cancellationRequested)
        {
            return DocumentCacheProjectionTargetReadinessBlockReason.CancellationPending;
        }

        if (drainExecutor.IsCommandOwned)
        {
            return DocumentCacheProjectionTargetReadinessBlockReason.CommandOwned;
        }

        if (drainExecutor.IsOwned)
        {
            return DocumentCacheProjectionTargetReadinessBlockReason.LocalDrainActive;
        }

        lock (_sync)
        {
            if (_targetPaused || _lifecycleFencePaused)
            {
                return DocumentCacheProjectionTargetReadinessBlockReason.TargetPaused;
            }

            if (TargetBackoffUntil is not null && TargetBackoffUntil > now)
            {
                return DocumentCacheProjectionTargetReadinessBlockReason.TargetBackoff;
            }

            if (TargetBackoffUntil is not null && TargetBackoffUntil <= now)
            {
                TargetBackoffUntil = null;
            }

            if (PollSleepUntil is not null && PollSleepUntil > now)
            {
                return DocumentCacheProjectionTargetReadinessBlockReason.PollSleep;
            }

            if (PollSleepUntil is not null && PollSleepUntil <= now)
            {
                PollSleepUntil = null;
            }

            return null;
        }
    }

    public DateTimeOffset? GetNextSchedulingWakeAt(
        DateTimeOffset now,
        bool cancellationRequested,
        DocumentCacheProjectionTargetDrainExecutor drainExecutor
    )
    {
        ArgumentNullException.ThrowIfNull(drainExecutor);

        if (cancellationRequested || drainExecutor.IsOwned)
        {
            return null;
        }

        lock (_sync)
        {
            if (_targetPaused || _lifecycleFencePaused)
            {
                return null;
            }

            DateTimeOffset? wakeAt = null;
            if (TargetBackoffUntil is not null)
            {
                wakeAt = TargetBackoffUntil > now ? TargetBackoffUntil : now;
            }

            if (PollSleepUntil is not null)
            {
                DateTimeOffset pollSleepWakeAt = PollSleepUntil > now ? PollSleepUntil.Value : now;
                if (wakeAt is null || pollSleepWakeAt < wakeAt)
                {
                    wakeAt = pollSleepWakeAt;
                }
            }

            return wakeAt;
        }
    }

    public void RecordOrdinaryDrainStarted(DateTimeOffset startedAt)
    {
        lock (_sync)
        {
            _ordinaryPageThroughput = _ordinaryPageThroughput.Started(startedAt);
        }
    }

    public void RecordOrdinaryDrainCompleted(
        DocumentCacheProjectionDrainPageResult result,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        TimeSpan pollInterval
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            _ordinaryPageThroughput = _ordinaryPageThroughput.Completed(result, startedAt, completedAt);

            switch (result.Outcome)
            {
                case DocumentCacheProjectionDrainPageOutcome.PageProcessed:
                    PollSleepUntil = null;
                    TargetBackoffUntil = null;
                    break;

                case DocumentCacheProjectionDrainPageOutcome.NoEligibleWork:
                    DateTimeOffset noWorkSleepUntil = completedAt + pollInterval;
                    if (result.NextRetryAt is not null && result.NextRetryAt < noWorkSleepUntil)
                    {
                        noWorkSleepUntil = result.NextRetryAt.Value;
                    }

                    PollSleepUntil = noWorkSleepUntil;
                    break;

                case DocumentCacheProjectionDrainPageOutcome.TargetBackoff:
                    TargetBackoffUntil = result.BackoffUntil;
                    PollSleepUntil = null;
                    break;

                case DocumentCacheProjectionDrainPageOutcome.LifecycleFenced:
                    _lifecycleFencePaused = true;
                    _lifecycleFenceSnapshot =
                        DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromWriterFenceObserved(
                            completedAt
                        );
                    TargetBackoffUntil = null;
                    PollSleepUntil = null;
                    break;

                case DocumentCacheProjectionDrainPageOutcome.TargetPaused:
                    _targetPaused = true;
                    TargetBackoffUntil = null;
                    PollSleepUntil = null;
                    break;
            }
        }
    }

    public void RecordAdministrativeDrainStarted(DateTimeOffset startedAt)
    {
        lock (_sync)
        {
            _administrativeDrainThroughput = _administrativeDrainThroughput.Started(startedAt);
        }
    }

    public void RecordAdministrativeDrainCompleted(
        DocumentCacheProjectionDrainPageResult result,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            _administrativeDrainThroughput = _administrativeDrainThroughput.Completed(
                result,
                startedAt,
                completedAt
            );
        }
    }

    public DocumentCacheProjectionThroughputSnapshot OrdinaryPageThroughputSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _ordinaryPageThroughput.ToSnapshot();
            }
        }
    }

    public DocumentCacheProjectionThroughputSnapshot AdministrativeDrainThroughputSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _administrativeDrainThroughput.ToSnapshot();
            }
        }
    }

    private readonly record struct ProjectionThroughputCounter(
        long StartedCount,
        long CompletedCount,
        long ItemCount,
        long FailureCount,
        DateTimeOffset? LastStartedAt,
        DateTimeOffset? LastCompletedAt,
        TimeSpan? LastDuration
    )
    {
        public ProjectionThroughputCounter Started(DateTimeOffset startedAt) =>
            this with
            {
                StartedCount = StartedCount + 1,
                LastStartedAt = startedAt,
            };

        public ProjectionThroughputCounter Completed(
            DocumentCacheProjectionDrainPageResult result,
            DateTimeOffset startedAt,
            DateTimeOffset completedAt
        ) =>
            this with
            {
                CompletedCount = CompletedCount + 1,
                ItemCount = ItemCount + result.ProcessedItemCount,
                FailureCount = result.Outcome
                    is DocumentCacheProjectionDrainPageOutcome.TargetBackoff
                        or DocumentCacheProjectionDrainPageOutcome.TargetPaused
                        or DocumentCacheProjectionDrainPageOutcome.AdministrativeFailure
                    ? FailureCount + 1
                    : FailureCount,
                LastCompletedAt = completedAt,
                LastDuration = completedAt - startedAt,
            };

        public DocumentCacheProjectionThroughputSnapshot ToSnapshot() =>
            new(
                StartedCount,
                CompletedCount,
                ItemCount,
                FailureCount,
                LastStartedAt,
                LastCompletedAt,
                LastDuration
            );
    }
}

public sealed class DocumentCacheProjectionScheduler(
    IOptions<DocumentCacheOptions> options,
    IDocumentCacheProjectionDrainPageProcessor drainPageProcessor,
    IDocumentCacheProjectionObservationSink observationSink,
    TimeProvider timeProvider,
    ILogger<DocumentCacheProjectionScheduler> logger,
    IDocumentCacheProjectionTelemetry? telemetry = null
) : IDocumentCacheProjectionScheduler
{
    private readonly object _rotationSync = new();
    private readonly SemaphoreSlim _workerGate = new(options.Value.Projector.MaxConcurrentTargets);
    private readonly IDocumentCacheProjectionTelemetry _telemetry =
        telemetry ?? NoOpDocumentCacheProjectionTelemetry.Instance;
    private ImmutableArray<DocumentCacheProjectionTargetContextKey> _rotation = [];

    public async Task<
        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>
    > RunReadyTargetsOnceAsync(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetContexts);

        ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> plannedContexts =
            PlanReadyOrdinaryTargets(targetContexts, timeProvider.GetUtcNow());
        if (plannedContexts.IsEmpty)
        {
            return [];
        }

        Task<DocumentCacheProjectionSchedulerDispatchResult>[] dispatchTasks = plannedContexts
            .Select(context => DispatchOrdinaryTargetAsync(context, cancellationToken))
            .ToArray();

        return (await Task.WhenAll(dispatchTasks).ConfigureAwait(false)).ToImmutableArray();
    }

    public async Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);

        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        targetContext.SchedulingState.RecordAdministrativeDrainStarted(startedAt);
        ObserveTarget(
            targetContext,
            new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: true,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: targetContext.CancellationRequested,
                cancellationObservedAt: targetContext.CancellationRequested ? startedAt : null
            )
        );

        DocumentCacheProjectionDrainPageResult drainResult = await targetContext
            .DrainExecutor.RunAdministrativeDrainSliceAsync(
                async drainCancellationToken =>
                {
                    await _workerGate.WaitAsync(drainCancellationToken).ConfigureAwait(false);
                    try
                    {
                        ObserveTarget(
                            targetContext,
                            new DocumentCacheProjectionExecutionStateSnapshot(
                                isRunning: true,
                                isActivelyProcessing: true,
                                isWaitingForWorkerGate: false,
                                isInBackoff: false,
                                backoffUntil: null,
                                cancellationRequested: targetContext.CancellationRequested,
                                cancellationObservedAt: targetContext.CancellationRequested
                                    ? timeProvider.GetUtcNow()
                                    : null
                            )
                        );

                        return await drainPageProcessor
                            .ProcessPageAsync(
                                new DocumentCacheProjectionDrainPageRequest(
                                    targetContext,
                                    DocumentCacheProjectionDrainInvocationKind.Administrative
                                ),
                                drainCancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        _workerGate.Release();
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        DateTimeOffset completedAt = timeProvider.GetUtcNow();
        targetContext.SchedulingState.RecordAdministrativeDrainCompleted(drainResult, startedAt, completedAt);
        ObserveIdleOrBackoff(targetContext, completedAt);

        return RecordDispatchResult(
            targetContext,
            DocumentCacheProjectionSchedulerDispatchResult.Dispatched(
                targetContext,
                drainResult,
                startedAt,
                completedAt
            ),
            DocumentCacheProjectionDrainInvocationKind.Administrative
        );
    }

    private ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> PlanReadyOrdinaryTargets(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
        DateTimeOffset now
    )
    {
        ImmutableDictionary<
            DocumentCacheProjectionTargetContextKey,
            DocumentCacheProjectionTargetRuntimeContext
        > contextsByKey = targetContexts
            .GroupBy(context => context.ContextKey)
            .ToImmutableDictionary(group => group.Key, group => group.First());

        lock (_rotationSync)
        {
            HashSet<DocumentCacheProjectionTargetContextKey> existingKeys = contextsByKey.Keys.ToHashSet();
            _rotation = _rotation.Where(existingKeys.Contains).ToImmutableArray();

            ImmutableArray<DocumentCacheProjectionTargetContextKey> keysToAdd = contextsByKey
                .Keys.Where(key => !_rotation.Contains(key))
                .Order(DocumentCacheProjectionTargetContextKeyComparer.Instance)
                .ToImmutableArray();
            _rotation = _rotation.AddRange(keysToAdd);

            ImmutableArray<DocumentCacheProjectionTargetRuntimeContext>.Builder plannedContexts =
                ImmutableArray.CreateBuilder<DocumentCacheProjectionTargetRuntimeContext>();
            ImmutableArray<DocumentCacheProjectionTargetContextKey>.Builder skippedKeys =
                ImmutableArray.CreateBuilder<DocumentCacheProjectionTargetContextKey>();
            ImmutableArray<DocumentCacheProjectionTargetContextKey>.Builder selectedKeys =
                ImmutableArray.CreateBuilder<DocumentCacheProjectionTargetContextKey>();
            int rotationCount = _rotation.Length;

            for (int index = 0; index < rotationCount; index++)
            {
                DocumentCacheProjectionTargetContextKey contextKey = _rotation[index];
                if (
                    !contextsByKey.TryGetValue(
                        contextKey,
                        out DocumentCacheProjectionTargetRuntimeContext? context
                    )
                )
                {
                    continue;
                }

                DocumentCacheProjectionTargetReadinessBlockReason? blockReason =
                    context.SchedulingState.GetReadinessBlock(
                        now,
                        context.CancellationRequested,
                        context.DrainExecutor
                    );
                if (blockReason is null)
                {
                    plannedContexts.Add(context);
                    selectedKeys.Add(contextKey);
                    continue;
                }

                RecordDispatchResult(
                    context,
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(context, blockReason.Value, now),
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                );
                skippedKeys.Add(contextKey);
            }

            _rotation = skippedKeys.ToImmutable().AddRange(selectedKeys);
            return plannedContexts.ToImmutable();
        }
    }

    private async Task<DocumentCacheProjectionSchedulerDispatchResult> DispatchOrdinaryTargetAsync(
        DocumentCacheProjectionTargetRuntimeContext context,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset waitStartedAt = timeProvider.GetUtcNow();
        DateTimeOffset? startedAt = null;
        bool drainStarted = false;
        ObserveTarget(
            context,
            new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: true,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: context.CancellationRequested,
                cancellationObservedAt: context.CancellationRequested ? waitStartedAt : null
            )
        );

        using CancellationTokenSource dispatchCancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
        bool workerGateHeld = false;
        try
        {
            await _workerGate.WaitAsync(dispatchCancellationSource.Token).ConfigureAwait(false);
            workerGateHeld = true;

            DateTimeOffset dispatchStartedAt = timeProvider.GetUtcNow();
            startedAt = dispatchStartedAt;
            DocumentCacheProjectionTargetReadinessBlockReason? blockReason =
                context.SchedulingState.GetReadinessBlock(
                    dispatchStartedAt,
                    context.CancellationRequested,
                    context.DrainExecutor
                );
            if (blockReason is not null)
            {
                return RecordDispatchResult(
                    context,
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(
                        context,
                        blockReason.Value,
                        dispatchStartedAt
                    ),
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                );
            }

            DocumentCacheProjectionDrainPageResult? drainResult = await context
                .DrainExecutor.TryRunOrdinaryDrainSliceAsync(
                    drainCancellationToken =>
                    {
                        DateTimeOffset drainStartedAt = timeProvider.GetUtcNow();
                        startedAt = drainStartedAt;
                        context.SchedulingState.RecordOrdinaryDrainStarted(drainStartedAt);
                        drainStarted = true;
                        ObserveTarget(
                            context,
                            new DocumentCacheProjectionExecutionStateSnapshot(
                                isRunning: true,
                                isActivelyProcessing: true,
                                isWaitingForWorkerGate: false,
                                isInBackoff: false,
                                backoffUntil: null,
                                cancellationRequested: context.CancellationRequested,
                                cancellationObservedAt: context.CancellationRequested ? drainStartedAt : null
                            )
                        );

                        return drainPageProcessor.ProcessPageAsync(
                            new DocumentCacheProjectionDrainPageRequest(
                                context,
                                DocumentCacheProjectionDrainInvocationKind.Ordinary
                            ),
                            drainCancellationToken
                        );
                    },
                    dispatchCancellationSource.Token
                )
                .ConfigureAwait(false);

            DateTimeOffset completedAt = timeProvider.GetUtcNow();
            if (drainResult is null)
            {
                DocumentCacheProjectionTargetReadinessBlockReason reason = context
                    .DrainExecutor
                    .IsCommandOwned
                    ? DocumentCacheProjectionTargetReadinessBlockReason.CommandOwned
                    : DocumentCacheProjectionTargetReadinessBlockReason.LocalDrainActive;
                return RecordDispatchResult(
                    context,
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(context, reason, completedAt),
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                );
            }

            context.SchedulingState.RecordOrdinaryDrainCompleted(
                drainResult,
                startedAt!.Value,
                completedAt,
                context.TargetExecutionContext.EffectiveSettings.ProjectorPollInterval
            );
            ObserveIdleOrBackoff(context, completedAt);

            return RecordDispatchResult(
                context,
                DocumentCacheProjectionSchedulerDispatchResult.Dispatched(
                    context,
                    drainResult,
                    startedAt.Value,
                    completedAt
                ),
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            );
        }
        catch (OperationCanceledException)
        {
            if (context.CancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
                ObserveTarget(
                    context,
                    new DocumentCacheProjectionExecutionStateSnapshot(
                        isRunning: true,
                        isActivelyProcessing: false,
                        isWaitingForWorkerGate: false,
                        isInBackoff: false,
                        backoffUntil: null,
                        cancellationRequested: true,
                        cancellationObservedAt: cancelledAt
                    )
                );

                return RecordDispatchResult(
                    context,
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(
                        context,
                        DocumentCacheProjectionTargetReadinessBlockReason.CancellationPending,
                        cancelledAt
                    ),
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                );
            }

            throw;
        }
        catch (Exception exception)
        {
            DateTimeOffset completedAt = timeProvider.GetUtcNow();
            DateTimeOffset faultStartedAt = startedAt ?? waitStartedAt;
            DateTimeOffset backoffUntil =
                completedAt + context.TargetExecutionContext.EffectiveSettings.ProjectorFailureBackoff;
            DocumentCacheProjectionDrainPageResult faultResult =
                DocumentCacheProjectionDrainPageResult.TargetBackoff(backoffUntil);
            if (!drainStarted)
            {
                context.SchedulingState.RecordOrdinaryDrainStarted(faultStartedAt);
            }

            context.SchedulingState.RecordOrdinaryDrainCompleted(
                faultResult,
                faultStartedAt,
                completedAt,
                context.TargetExecutionContext.EffectiveSettings.ProjectorPollInterval
            );

            logger.LogError(
                exception,
                "DocumentCache projection scheduler page dispatch failed for target {TargetKey}; peer targets continue.",
                LoggingSanitizer.SanitizeForLogging(context.TargetKey.ToString())
            );
            ObserveIdleOrBackoff(context, completedAt);
            return RecordDispatchResult(
                context,
                DocumentCacheProjectionSchedulerDispatchResult.Faulted(
                    context,
                    faultResult,
                    faultStartedAt,
                    completedAt
                ),
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            );
        }
        finally
        {
            if (workerGateHeld)
            {
                _workerGate.Release();
            }
        }
    }

    private void ObserveIdleOrBackoff(
        DocumentCacheProjectionTargetRuntimeContext context,
        DateTimeOffset observedAt
    )
    {
        DateTimeOffset? backoffUntil = context.SchedulingState.TargetBackoffUntil;
        bool isInBackoff = backoffUntil is not null && backoffUntil > observedAt;
        ObserveTarget(
            context,
            new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: false,
                isInBackoff: isInBackoff,
                backoffUntil: isInBackoff ? backoffUntil : null,
                cancellationRequested: context.CancellationRequested,
                cancellationObservedAt: context.CancellationRequested ? observedAt : null
            )
        );
    }

    private void ObserveTarget(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionExecutionStateSnapshot executionState
    ) =>
        observationSink.ObserveTarget(
            DocumentCacheProjectionTargetHealthSnapshotFactory.Create(
                context,
                timeProvider.GetUtcNow(),
                executionState
            )
        );

    private DocumentCacheProjectionSchedulerDispatchResult RecordDispatchResult(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionSchedulerDispatchResult result,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    )
    {
        _telemetry.RecordSchedulerDispatch(context, result, invocationKind);
        return result;
    }
}

internal static class DocumentCacheProjectionTargetHealthSnapshotFactory
{
    public static DocumentCacheProjectionTargetHealthSnapshot Create(
        DocumentCacheProjectionTargetRuntimeContext context,
        DateTimeOffset observedAt,
        DocumentCacheProjectionExecutionStateSnapshot? executionState = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        DocumentCacheTargetExecutionContext executionContext = context.TargetExecutionContext;
        return new DocumentCacheProjectionTargetHealthSnapshot(
            executionContext.TargetKey,
            executionContext.Generation,
            executionContext.EffectiveSettings.ProjectorPageSize,
            observedAt,
            providerToken: executionContext.ProviderToken,
            physicalSourceFingerprint: executionContext.PhysicalSourceFingerprint,
            executionState: executionState,
            pageThroughput: context.SchedulingState.OrdinaryPageThroughputSnapshot,
            drainThroughput: context.SchedulingState.AdministrativeDrainThroughputSnapshot,
            lifecycleFence: SelectLifecycleFenceSnapshot(context, observedAt),
            poisonTraversal: context.FailureBackoffState.CreatePoisonTraversalSnapshot(),
            failureDiagnostics: context.FailureBackoffState.CreateFailureDiagnosticsSnapshot()
        );
    }

    private static DocumentCacheProjectionLifecycleFenceSnapshot SelectLifecycleFenceSnapshot(
        DocumentCacheProjectionTargetRuntimeContext context,
        DateTimeOffset observedAt
    )
    {
        DocumentCacheProjectionLifecycleFenceSnapshot lifecycleFence = context
            .SchedulingState
            .LifecycleFenceSnapshot;

        return lifecycleFence.State == DocumentCacheProjectionLifecycleFenceState.Unknown
            ? DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycle(
                context.TargetExecutionContext.Lifecycle,
                observedAt
            )
            : lifecycleFence;
    }
}

internal static class DocumentCacheProjectionLifecycleFenceSnapshotFactory
{
    public static DocumentCacheProjectionLifecycleFenceSnapshot FromLifecycleReadResult(
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycleReadResult);

        return lifecycleReadResult.Lifecycle is not null
            ? FromLifecycle(lifecycleReadResult.Lifecycle, observedAt)
            : FromLifecycleReadFailure(
                observedAt,
                DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                lifecycleReadResult.Message
            );
    }

    public static DocumentCacheProjectionLifecycleFenceSnapshot FromLifecycle(
        DocumentCacheLifecycleObservation lifecycle,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        bool eligible =
            lifecycle.State is DocumentCacheLifecycleState.Tracking or DocumentCacheLifecycleState.Rebuilding
            && !lifecycle.CacheAheadRecoveryRequired;

        DocumentCacheProjectionLifecycleFenceState fenceState = eligible
            ? DocumentCacheProjectionLifecycleFenceState.Eligible
            : DocumentCacheProjectionLifecycleFenceState.Fenced;
        DocumentCacheTargetDiagnosticCategory? diagnosticCategory = null;
        if (!eligible)
        {
            diagnosticCategory = lifecycle.CacheAheadRecoveryRequired
                ? DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet
                : DocumentCacheTargetDiagnosticCategory.LifecycleMismatch;
        }

        return new DocumentCacheProjectionLifecycleFenceSnapshot(
            fenceState,
            lifecycle,
            observedAt,
            diagnosticCategory,
            eligible
                ? "Target lifecycle permits ordinary projection processing."
                : "Target lifecycle or cache-ahead latch fences ordinary projection processing."
        );
    }

    public static DocumentCacheProjectionLifecycleFenceSnapshot FromLifecycleReadFailure(
        DateTimeOffset observedAt,
        DocumentCacheTargetDiagnosticCategory diagnosticCategory,
        string message
    ) =>
        new(
            DocumentCacheProjectionLifecycleFenceState.Fenced,
            lifecycle: null,
            observedAt,
            diagnosticCategory,
            message
        );

    public static DocumentCacheProjectionLifecycleFenceSnapshot FromWriterFenceObserved(
        DateTimeOffset observedAt
    ) =>
        new(
            DocumentCacheProjectionLifecycleFenceState.Fenced,
            lifecycle: null,
            observedAt,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
            "Cache writer observed a lifecycle or cache-ahead latch fence; supervisor lifecycle observation is pending."
        );
}

internal sealed class DocumentCacheProjectionTargetContextKeyComparer
    : IComparer<DocumentCacheProjectionTargetContextKey>
{
    public static DocumentCacheProjectionTargetContextKeyComparer Instance { get; } = new();

    private DocumentCacheProjectionTargetContextKeyComparer() { }

    public int Compare(DocumentCacheProjectionTargetContextKey? x, DocumentCacheProjectionTargetContextKey? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int tenantComparison = StringComparer.OrdinalIgnoreCase.Compare(
            x.TargetKey.TenantKey,
            y.TargetKey.TenantKey
        );
        if (tenantComparison != 0)
        {
            return tenantComparison;
        }

        int dataStoreComparison = x.TargetKey.DataStoreId.CompareTo(y.TargetKey.DataStoreId);
        return dataStoreComparison != 0
            ? dataStoreComparison
            : x.Generation.Value.CompareTo(y.Generation.Value);
    }
}

file static class DocumentCacheProjectionSchedulingGuard
{
    public static int RequireNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must not be negative.");
        }

        return value;
    }

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
