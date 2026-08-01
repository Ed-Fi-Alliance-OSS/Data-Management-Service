// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend;

public interface IDocumentCacheProjectionSupervisor
{
    ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; }

    Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
        DocumentCacheTargetRefreshReason reason,
        CancellationToken cancellationToken = default
    );
}

public interface IDocumentCacheProjectionTargetRuntimeContextFactory
{
    Task<DocumentCacheProjectionTargetRuntimeContext> CreateAsync(
        DocumentCacheTargetExecutionContext executionContext,
        CancellationToken cancellationToken = default
    );
}

public sealed record DocumentCacheProjectionTargetProviderAdapters(
    RelationalProviderToken ProviderToken,
    IDocumentCacheMaterializer Materializer,
    IDocumentCacheWriter Writer
);

public sealed class DocumentCacheProjectionCursorState
{
    public DateTimeOffset? LastFirstEnqueuedAt { get; private set; }

    public long? LastDocumentId { get; private set; }

    public bool HasValue => LastFirstEnqueuedAt is not null && LastDocumentId is not null;

    public void Advance(DateTimeOffset firstEnqueuedAt, long documentId)
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        LastFirstEnqueuedAt = firstEnqueuedAt;
        LastDocumentId = documentId;
    }

    public void Clear()
    {
        LastFirstEnqueuedAt = null;
        LastDocumentId = null;
    }
}

public sealed class DocumentCacheProjectionFailureBackoffState
{
    private readonly object _sync = new();
    private ImmutableDictionary<long, FailureEntry> _entries = ImmutableDictionary<long, FailureEntry>.Empty;
    private ImmutableArray<long> _lastSuppressedDocumentIds = [];
    private int _lastSuppressedDocumentCount;
    private DateTimeOffset? _lastSuppressedEarliestRetryAt;
    private long _evictionCount;
    private bool _processedEligibleWorkSinceCursorWrap;

    public DocumentCacheProjectionFailureBackoffState(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "Failure state capacity must be positive."
            );
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public long EvictionCount
    {
        get
        {
            lock (_sync)
            {
                return _evictionCount;
            }
        }
    }

    public void RecordFailure(
        long documentId,
        DocumentCacheProjectionDocumentDiagnosticCategory category,
        string message,
        DateTimeOffset observedAt,
        TimeSpan failureBackoff
    )
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                "Unsupported projection document diagnostic category."
            );
        }

        if (failureBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureBackoff),
                "Failure backoff must be positive."
            );
        }

        lock (_sync)
        {
            if (!_entries.ContainsKey(documentId) && _entries.Count >= Capacity)
            {
                FailureEntry entryToEvict = _entries
                    .Values.OrderBy(entry => entry.ObservedAt)
                    .ThenBy(entry => entry.DocumentId)
                    .First();

                _entries = _entries.Remove(entryToEvict.DocumentId);
                _evictionCount++;
            }

            _entries = _entries.SetItem(
                documentId,
                new FailureEntry(documentId, category, message, observedAt, observedAt + failureBackoff)
            );
        }
    }

    public bool ClearFailure(long documentId)
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        lock (_sync)
        {
            bool removed = _entries.ContainsKey(documentId);
            _entries = _entries.Remove(documentId);
            return removed;
        }
    }

    public bool IsSuppressed(long documentId, DateTimeOffset observedAt)
    {
        if (documentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentId), "Document id must be positive.");
        }

        lock (_sync)
        {
            return _entries.TryGetValue(documentId, out FailureEntry? entry)
                && entry.NextRetryAt > observedAt;
        }
    }

    public void RecordEligibleWorkProcessed()
    {
        lock (_sync)
        {
            _processedEligibleWorkSinceCursorWrap = true;
        }
    }

    public void RecordSuppressedTraversal(IEnumerable<long> suppressedDocumentIds, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(suppressedDocumentIds);

        ImmutableArray<long> materializedDocumentIds = suppressedDocumentIds.ToImmutableArray();
        if (materializedDocumentIds.Any(documentId => documentId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(suppressedDocumentIds),
                "Document ids must be positive."
            );
        }

        lock (_sync)
        {
            _lastSuppressedDocumentIds = materializedDocumentIds.Take(Capacity).ToImmutableArray();
            _lastSuppressedDocumentCount = materializedDocumentIds.Length;
            _lastSuppressedEarliestRetryAt = materializedDocumentIds
                .Select(documentId =>
                    _entries.TryGetValue(documentId, out FailureEntry? entry)
                    && entry.NextRetryAt > observedAt
                        ? entry.NextRetryAt
                        : (DateTimeOffset?)null
                )
                .Where(nextRetryAt => nextRetryAt is not null)
                .Min();
        }
    }

    public DocumentCacheProjectionCursorPassCompletion CompleteCursorPass(DateTimeOffset observedAt)
    {
        lock (_sync)
        {
            DocumentCacheProjectionCursorPassCompletion completion = new(
                _processedEligibleWorkSinceCursorWrap,
                EarliestSuppressedRetryAt(observedAt)
            );
            _processedEligibleWorkSinceCursorWrap = false;
            return completion;
        }
    }

    public DocumentCacheProjectionFailureDiagnostics CreateFailureDiagnosticsSnapshot()
    {
        lock (_sync)
        {
            ImmutableArray<FailureEntry> orderedEntries = _entries
                .Values.OrderBy(entry => entry.ObservedAt)
                .ThenBy(entry => entry.DocumentId)
                .ToImmutableArray();

            DateTimeOffset? earliestRetryAt = orderedEntries.IsEmpty
                ? null
                : orderedEntries.Min(entry => entry.NextRetryAt);

            return new DocumentCacheProjectionFailureDiagnostics(
                Capacity,
                orderedEntries.Length,
                earliestRetryAt,
                _evictionCount,
                orderedEntries.Select(entry => new DocumentCacheProjectionDocumentDiagnostic(
                    entry.DocumentId,
                    entry.Category,
                    entry.Message,
                    entry.ObservedAt,
                    entry.NextRetryAt
                ))
            );
        }
    }

    public DocumentCacheProjectionPoisonTraversalSnapshot CreatePoisonTraversalSnapshot()
    {
        lock (_sync)
        {
            return new DocumentCacheProjectionPoisonTraversalSnapshot(
                Capacity,
                _lastSuppressedDocumentCount,
                _lastSuppressedEarliestRetryAt,
                _lastSuppressedDocumentIds
            );
        }
    }

    private DateTimeOffset? EarliestSuppressedRetryAt(DateTimeOffset observedAt)
    {
        ImmutableArray<DateTimeOffset> retryTimes = _entries
            .Values.Where(entry => entry.NextRetryAt > observedAt)
            .Select(entry => entry.NextRetryAt)
            .ToImmutableArray();

        return retryTimes.IsEmpty ? null : retryTimes.Min();
    }

    private sealed record FailureEntry(
        long DocumentId,
        DocumentCacheProjectionDocumentDiagnosticCategory Category,
        string Message,
        DateTimeOffset ObservedAt,
        DateTimeOffset NextRetryAt
    );
}

public sealed record DocumentCacheProjectionCursorPassCompletion(
    bool ProcessedEligibleWork,
    DateTimeOffset? EarliestSuppressedRetryAt
);

public sealed class DocumentCacheProjectionTargetRuntimeContext : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Func<ValueTask>? _disposeScopeAsync;
    private int _cancelled;

    public DocumentCacheProjectionTargetRuntimeContext(
        DocumentCacheTargetExecutionContext targetExecutionContext,
        DocumentCacheProjectionTargetProviderAdapters providerAdapters,
        IDocumentCacheProjectionObservationSink observationSink,
        Func<ValueTask>? disposeScopeAsync = null
    )
    {
        TargetExecutionContext =
            targetExecutionContext ?? throw new ArgumentNullException(nameof(targetExecutionContext));
        ProviderAdapters = providerAdapters ?? throw new ArgumentNullException(nameof(providerAdapters));
        ObservationSink = observationSink ?? throw new ArgumentNullException(nameof(observationSink));
        Cursor = new DocumentCacheProjectionCursorState();
        FailureBackoffState = new DocumentCacheProjectionFailureBackoffState(
            TargetExecutionContext.EffectiveSettings.ProjectorPageSize
        );
        SchedulingState = new DocumentCacheProjectionTargetSchedulingState();
        DrainExecutor = new DocumentCacheProjectionTargetDrainExecutor();
        ContextKey = new DocumentCacheProjectionTargetContextKey(
            TargetExecutionContext.TargetKey,
            TargetExecutionContext.Generation
        );
        _disposeScopeAsync = disposeScopeAsync;
    }

    public DocumentCacheProjectionTargetContextKey ContextKey { get; }

    public DocumentCacheTargetKey TargetKey => TargetExecutionContext.TargetKey;

    public DocumentCacheTargetContextGeneration Generation => TargetExecutionContext.Generation;

    public DocumentCacheTargetExecutionContext TargetExecutionContext { get; }

    public DocumentCacheProjectionTargetProviderAdapters ProviderAdapters { get; }

    public IDocumentCacheMaterializer Materializer => ProviderAdapters.Materializer;

    public IDocumentCacheWriter Writer => ProviderAdapters.Writer;

    public IDocumentCacheProjectionObservationSink ObservationSink { get; }

    public DocumentCacheProjectionCursorState Cursor { get; }

    public DocumentCacheProjectionFailureBackoffState FailureBackoffState { get; }

    public DocumentCacheProjectionTargetSchedulingState SchedulingState { get; }

    public DocumentCacheProjectionTargetDrainExecutor DrainExecutor { get; }

    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    public bool CancellationRequested => _cancellationTokenSource.IsCancellationRequested;

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) == 0)
        {
            _cancellationTokenSource.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        _cancellationTokenSource.Dispose();

        if (_disposeScopeAsync is not null)
        {
            await _disposeScopeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class DocumentCacheProjectionTargetRuntimeContextFactory(
    IServiceScopeFactory serviceScopeFactory,
    IDocumentCacheProjectionObservationSink observationSink
) : IDocumentCacheProjectionTargetRuntimeContextFactory
{
    public Task<DocumentCacheProjectionTargetRuntimeContext> CreateAsync(
        DocumentCacheTargetExecutionContext executionContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        cancellationToken.ThrowIfCancellationRequested();

        AsyncServiceScope serviceScope = serviceScopeFactory.CreateAsyncScope();
        try
        {
            IDocumentCacheMaterializer materializer =
                serviceScope.ServiceProvider.GetRequiredService<IDocumentCacheMaterializer>();
            IDocumentCacheWriter writer =
                serviceScope.ServiceProvider.GetRequiredService<IDocumentCacheWriter>();

            DocumentCacheProjectionTargetProviderAdapters providerAdapters = new(
                executionContext.ProviderToken,
                materializer,
                writer
            );

            return Task.FromResult(
                new DocumentCacheProjectionTargetRuntimeContext(
                    executionContext,
                    providerAdapters,
                    observationSink,
                    serviceScope.DisposeAsync
                )
            );
        }
        catch
        {
            serviceScope.Dispose();
            throw;
        }
    }
}

public sealed class DocumentCacheProjectionSupervisor(
    IDocumentCacheTargetRegistry targetRegistry,
    IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory,
    IDocumentCacheProjectionObservationSink observationSink,
    IOptions<DocumentCacheOptions> options,
    IDocumentCacheProjectionScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<DocumentCacheProjectionSupervisor> logger
) : BackgroundService, IDocumentCacheProjectionSupervisor
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetRuntimeContext
    > _targetContexts = ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetRuntimeContext
    >.Empty;

    public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts =>
        _targetContexts.Values.ToImmutableArray();

    public async Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
        DocumentCacheTargetRefreshReason reason,
        CancellationToken cancellationToken = default
    )
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DocumentCacheTargetRegistrySnapshot snapshot = await targetRegistry
                .RefreshAsync(reason, cancellationToken)
                .ConfigureAwait(false);

            await ReconcileTargetContextsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.GetTargetKeys().Count == 0)
        {
            logger.LogInformation(
                "DocumentCache projection supervisor started with no configured targets; no projection workers were started."
            );
            return;
        }

        await RefreshAsync(DocumentCacheTargetRefreshReason.Startup, stoppingToken).ConfigureAwait(false);
        await scheduler.RunReadyTargetsOnceAsync(CurrentTargetContexts, stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(options.Value.Projector.PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered, stoppingToken)
                .ConfigureAwait(false);
            await scheduler
                .RunReadyTargetsOnceAsync(CurrentTargetContexts, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await EndAllTargetContextsAsync(DocumentCacheProjectionTargetEndReason.Shutdown)
            .ConfigureAwait(false);
    }

    private async Task ReconcileTargetContextsAsync(
        DocumentCacheTargetRegistrySnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = targetRegistry.CurrentRuntimeSnapshot;
        ImmutableDictionary<DocumentCacheTargetKey, DocumentCacheTargetExecutionContext> desiredContexts =
            runtimeSnapshot.ExecutionContexts.ToImmutableDictionary(context => context.TargetKey);

        ImmutableDictionary<
            DocumentCacheTargetKey,
            DocumentCacheProjectionTargetRuntimeContext
        > currentContexts = _targetContexts;

        foreach (DocumentCacheProjectionTargetRuntimeContext currentContext in currentContexts.Values)
        {
            if (
                desiredContexts.TryGetValue(
                    currentContext.TargetKey,
                    out DocumentCacheTargetExecutionContext? desiredContext
                )
                && desiredContext.Generation == currentContext.Generation
            )
            {
                continue;
            }

            DocumentCacheProjectionTargetEndReason endReason = DetermineEndReason(
                currentContext,
                desiredContext,
                snapshot
            );
            await EndTargetContextAsync(currentContext, endReason).ConfigureAwait(false);
            currentContexts = currentContexts.Remove(currentContext.TargetKey);
        }

        ImmutableDictionary<
            DocumentCacheTargetKey,
            DocumentCacheProjectionTargetRuntimeContext
        >.Builder nextContexts = currentContexts.ToBuilder();

        foreach (DocumentCacheTargetExecutionContext desiredContext in desiredContexts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (
                nextContexts.TryGetValue(
                    desiredContext.TargetKey,
                    out DocumentCacheProjectionTargetRuntimeContext? existing
                )
            )
            {
                ObserveTarget(existing);
                continue;
            }

            try
            {
                DocumentCacheProjectionTargetRuntimeContext createdContext = await targetContextFactory
                    .CreateAsync(desiredContext, cancellationToken)
                    .ConfigureAwait(false);
                nextContexts[desiredContext.TargetKey] = createdContext;
                ObserveTarget(createdContext);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "DocumentCache projection target context creation failed for target {TargetKey}; peer targets continue.",
                    desiredContext.TargetKey
                );
            }
        }

        _targetContexts = nextContexts.ToImmutable();
    }

    private async Task EndAllTargetContextsAsync(DocumentCacheProjectionTargetEndReason endReason)
    {
        ImmutableDictionary<DocumentCacheTargetKey, DocumentCacheProjectionTargetRuntimeContext> contexts =
            _targetContexts;
        _targetContexts = ImmutableDictionary<
            DocumentCacheTargetKey,
            DocumentCacheProjectionTargetRuntimeContext
        >.Empty;

        foreach (DocumentCacheProjectionTargetRuntimeContext context in contexts.Values)
        {
            await EndTargetContextAsync(context, endReason).ConfigureAwait(false);
        }
    }

    private async Task EndTargetContextAsync(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionTargetEndReason endReason
    )
    {
        context.Cancel();
        observationSink.EndTargetContext(context.ContextKey, endReason, timeProvider.GetUtcNow());
        await context.DisposeAsync().ConfigureAwait(false);
    }

    private static DocumentCacheProjectionTargetEndReason DetermineEndReason(
        DocumentCacheProjectionTargetRuntimeContext currentContext,
        DocumentCacheTargetExecutionContext? desiredContext,
        DocumentCacheTargetRegistrySnapshot snapshot
    )
    {
        if (desiredContext is not null && desiredContext.Generation != currentContext.Generation)
        {
            return DocumentCacheProjectionTargetEndReason.Replaced;
        }

        DocumentCacheTargetObservation? targetObservation = snapshot.GetTarget(currentContext.TargetKey);
        return targetObservation?.EligibilityState == DocumentCacheTargetEligibilityState.Ineligible
            ? DocumentCacheProjectionTargetEndReason.Ineligible
            : DocumentCacheProjectionTargetEndReason.Removed;
    }

    private void ObserveTarget(DocumentCacheProjectionTargetRuntimeContext context)
    {
        DateTimeOffset observedAt = timeProvider.GetUtcNow();

        observationSink.ObserveTarget(
            DocumentCacheProjectionTargetHealthSnapshotFactory.Create(
                context,
                observedAt,
                executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                    isRunning: true,
                    isActivelyProcessing: false,
                    isWaitingForWorkerGate: false,
                    isInBackoff: false,
                    backoffUntil: null,
                    cancellationRequested: context.CancellationRequested,
                    cancellationObservedAt: context.CancellationRequested ? observedAt : null
                )
            )
        );
    }
}
