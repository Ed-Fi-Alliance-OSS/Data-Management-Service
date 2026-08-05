// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
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

internal interface IDocumentCacheProjectionRetainedTargetContextReleaser
{
    Task ReleaseRetainedCommandOwnedTargetContextAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
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
    DocumentCacheMaterializationTargetContext MaterializationTargetContext,
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
    private ImmutableDictionary<long, DiagnosticEntry> _diagnostics = ImmutableDictionary<
        long,
        DiagnosticEntry
    >.Empty;
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
            _diagnostics = _diagnostics.Remove(documentId);
        }
    }

    public void RecordDiagnostic(
        long documentId,
        DocumentCacheProjectionDocumentDiagnosticCategory category,
        string message,
        DateTimeOffset observedAt
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

        lock (_sync)
        {
            _entries = _entries.Remove(documentId);
            if (!_diagnostics.ContainsKey(documentId) && _diagnostics.Count >= Capacity)
            {
                DiagnosticEntry entryToEvict = _diagnostics
                    .Values.OrderBy(entry => entry.ObservedAt)
                    .ThenBy(entry => entry.DocumentId)
                    .First();

                _diagnostics = _diagnostics.Remove(entryToEvict.DocumentId);
            }

            _diagnostics = _diagnostics.SetItem(
                documentId,
                new DiagnosticEntry(documentId, category, message, observedAt)
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
            bool removed = _entries.ContainsKey(documentId) || _diagnostics.ContainsKey(documentId);
            _entries = _entries.Remove(documentId);
            _diagnostics = _diagnostics.Remove(documentId);
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
            ImmutableArray<DiagnosticEntry> orderedDiagnostics = _diagnostics
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
                orderedEntries
                    .Select(entry => new DocumentCacheProjectionDocumentDiagnostic(
                        entry.DocumentId,
                        entry.Category,
                        entry.Message,
                        entry.ObservedAt,
                        entry.NextRetryAt
                    ))
                    .Concat(
                        orderedDiagnostics.Select(entry => new DocumentCacheProjectionDocumentDiagnostic(
                            entry.DocumentId,
                            entry.Category,
                            entry.Message,
                            entry.ObservedAt
                        ))
                    )
                    .OrderBy(diagnostic => diagnostic.ObservedAt)
                    .ThenBy(diagnostic => diagnostic.DocumentId)
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

    private sealed record DiagnosticEntry(
        long DocumentId,
        DocumentCacheProjectionDocumentDiagnosticCategory Category,
        string Message,
        DateTimeOffset ObservedAt
    );
}

public sealed record DocumentCacheProjectionCursorPassCompletion(
    bool ProcessedEligibleWork,
    DateTimeOffset? EarliestSuppressedRetryAt
);

public sealed class DocumentCacheProjectionTargetRuntimeContext : IAsyncDisposable
{
    private readonly object _lifetimeSync = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Func<ValueTask>? _disposeScopeAsync;
    private readonly IDocumentCacheSessionBoundWriter? _sessionBoundWriter;
    private TaskCompletionSource? _ordinaryDispatchLeasesReleased;
    private TaskCompletionSource? _administrativeCommandReleased;
    private int _ordinaryDispatchLeaseCount;
    private int _cancelled;
    private bool _disposeStarted;
    private bool _disposed;
    private int _administrativeCommandRetentions;
    private DocumentCacheAdministrativeCommandExecutionContext? _activeAdministrativeCommandContext;
    private DocumentCacheAdministrativeCommandExecutionContext? _administrativeCommandContext;

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
        _sessionBoundWriter = null;
        _disposeScopeAsync = disposeScopeAsync;
    }

    internal DocumentCacheProjectionTargetRuntimeContext(
        DocumentCacheTargetExecutionContext targetExecutionContext,
        DocumentCacheProjectionTargetProviderAdapters providerAdapters,
        IDocumentCacheProjectionObservationSink observationSink,
        IDocumentCacheSessionBoundWriter? sessionBoundWriter,
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
        _sessionBoundWriter = sessionBoundWriter;
        _disposeScopeAsync = disposeScopeAsync;
    }

    public DocumentCacheProjectionTargetContextKey ContextKey { get; }

    public DocumentCacheTargetKey TargetKey => TargetExecutionContext.TargetKey;

    public DocumentCacheTargetContextGeneration Generation => TargetExecutionContext.Generation;

    public DocumentCacheTargetExecutionContext TargetExecutionContext { get; }

    public DocumentCacheProjectionTargetProviderAdapters ProviderAdapters { get; }

    public IDocumentCacheMaterializer Materializer => ProviderAdapters.Materializer;

    public IDocumentCacheWriter Writer => ProviderAdapters.Writer;

    internal IDocumentCacheSessionBoundWriter? SessionBoundWriter => _sessionBoundWriter;

    internal DocumentCacheAdministrativeCommandExecutionContext? AdministrativeCommandContext =>
        Volatile.Read(ref _administrativeCommandContext);

    internal DocumentCacheAdministrativeCommandExecutionContext? ActiveAdministrativeCommandContext
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _activeAdministrativeCommandContext;
            }
        }
    }

    internal bool HasActiveAdministrativeCommand
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _activeAdministrativeCommandContext is not null;
            }
        }
    }

    internal bool HasAdministrativeCommandRetention
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _activeAdministrativeCommandContext is not null
                    || _administrativeCommandRetentions > 0;
            }
        }
    }

    public DocumentCacheMaterializationTargetContext MaterializationTargetContext =>
        ProviderAdapters.MaterializationTargetContext;

    public IDocumentCacheProjectionObservationSink ObservationSink { get; }

    public DocumentCacheProjectionCursorState Cursor { get; }

    public DocumentCacheProjectionFailureBackoffState FailureBackoffState { get; }

    public DocumentCacheProjectionTargetSchedulingState SchedulingState { get; }

    public DocumentCacheProjectionTargetDrainExecutor DrainExecutor { get; }

    public CancellationToken CancellationToken
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _disposed ? new CancellationToken(canceled: true) : _cancellationTokenSource.Token;
            }
        }
    }

    public bool CancellationRequested
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _disposed || _cancellationTokenSource.IsCancellationRequested;
            }
        }
    }

    internal bool HasOrdinaryDispatchLease
    {
        get
        {
            lock (_lifetimeSync)
            {
                return _ordinaryDispatchLeaseCount > 0;
            }
        }
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) == 0)
        {
            lock (_lifetimeSync)
            {
                if (!_disposed)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
        }
    }

    internal IDisposable? TryAcquireOrdinaryDispatchLease()
    {
        lock (_lifetimeSync)
        {
            if (_disposeStarted || _disposed || _cancellationTokenSource.IsCancellationRequested)
            {
                return null;
            }

            if (_ordinaryDispatchLeaseCount == 0)
            {
                _ordinaryDispatchLeasesReleased = null;
            }

            _ordinaryDispatchLeaseCount++;
            return new OrdinaryDispatchLease(this);
        }
    }

    internal IDisposable RetainForAdministrativeCommand()
    {
        lock (_lifetimeSync)
        {
            if (_administrativeCommandRetentions == 0 && _activeAdministrativeCommandContext is null)
            {
                _administrativeCommandReleased = null;
            }

            _administrativeCommandRetentions++;
        }

        return new AdministrativeCommandRetention(this);
    }

    internal IDisposable TrackActiveAdministrativeCommand(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        ArgumentNullException.ThrowIfNull(commandContext);
        if (!ReferenceEquals(commandContext.TargetContext, this))
        {
            throw new ArgumentException(
                "Administrative command context must be pinned to this target context.",
                nameof(commandContext)
            );
        }

        lock (_lifetimeSync)
        {
            if (_activeAdministrativeCommandContext is not null)
            {
                throw new InvalidOperationException(
                    "DocumentCache target context already has an active administrative command."
                );
            }

            if (_administrativeCommandRetentions == 0)
            {
                _administrativeCommandReleased = null;
            }

            _activeAdministrativeCommandContext = commandContext;
        }

        return new ActiveAdministrativeCommandTracking(this, commandContext);
    }

    internal IDisposable BindAdministrativeCommand(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        ArgumentNullException.ThrowIfNull(commandContext);
        if (!ReferenceEquals(commandContext.TargetContext, this))
        {
            throw new ArgumentException(
                "Administrative command context must be pinned to this target context.",
                nameof(commandContext)
            );
        }

        if (Interlocked.CompareExchange(ref _administrativeCommandContext, commandContext, null) is not null)
        {
            throw new InvalidOperationException(
                "DocumentCache target context already has an active administrative command."
            );
        }

        return new AdministrativeCommandBinding(this, commandContext);
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();

        Task? ordinaryDispatchLeasesReleased = null;
        bool shouldDisposeResources = false;
        lock (_lifetimeSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposeStarted = true;
            if (_ordinaryDispatchLeaseCount > 0)
            {
                _ordinaryDispatchLeasesReleased ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                ordinaryDispatchLeasesReleased = _ordinaryDispatchLeasesReleased.Task;
            }
            else
            {
                _disposed = true;
                shouldDisposeResources = true;
            }
        }

        if (ordinaryDispatchLeasesReleased is not null)
        {
            await ordinaryDispatchLeasesReleased.ConfigureAwait(false);

            lock (_lifetimeSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                shouldDisposeResources = true;
            }
        }

        if (!shouldDisposeResources)
        {
            return;
        }

        _cancellationTokenSource.Dispose();

        if (_disposeScopeAsync is not null)
        {
            await _disposeScopeAsync().ConfigureAwait(false);
        }
    }

    internal async Task WaitForRetainedOwnershipReleasedAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task administrativeCommandReleased = WaitForAdministrativeCommandReleasedAsync(cancellationToken);
            Task ordinaryDispatchLeasesReleased = WaitForOrdinaryDispatchLeasesReleasedAsync(
                cancellationToken
            );
            Task drainOwnershipReleased = DrainExecutor.WaitForOwnershipReleasedAsync(cancellationToken);

            if (
                administrativeCommandReleased.IsCompletedSuccessfully
                && ordinaryDispatchLeasesReleased.IsCompletedSuccessfully
                && drainOwnershipReleased.IsCompletedSuccessfully
            )
            {
                return;
            }

            await Task.WhenAll(
                    administrativeCommandReleased,
                    ordinaryDispatchLeasesReleased,
                    drainOwnershipReleased
                )
                .ConfigureAwait(false);
        }
    }

    private void ReleaseOrdinaryDispatchLease()
    {
        TaskCompletionSource? ordinaryDispatchLeasesReleased = null;
        lock (_lifetimeSync)
        {
            if (_ordinaryDispatchLeaseCount <= 0)
            {
                return;
            }

            _ordinaryDispatchLeaseCount--;
            if (_ordinaryDispatchLeaseCount == 0)
            {
                ordinaryDispatchLeasesReleased = _ordinaryDispatchLeasesReleased;
                if (!_disposeStarted)
                {
                    _ordinaryDispatchLeasesReleased = null;
                }
            }
        }

        ordinaryDispatchLeasesReleased?.TrySetResult();
    }

    private Task WaitForOrdinaryDispatchLeasesReleasedAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeSync)
        {
            if (_ordinaryDispatchLeaseCount == 0)
            {
                return Task.CompletedTask;
            }

            _ordinaryDispatchLeasesReleased ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            return _ordinaryDispatchLeasesReleased.Task.WaitAsync(cancellationToken);
        }
    }

    private Task WaitForAdministrativeCommandReleasedAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeSync)
        {
            if (_activeAdministrativeCommandContext is null && _administrativeCommandRetentions == 0)
            {
                return Task.CompletedTask;
            }

            _administrativeCommandReleased ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            return _administrativeCommandReleased.Task.WaitAsync(cancellationToken);
        }
    }

    private void ReleaseAdministrativeCommandRetention()
    {
        TaskCompletionSource? administrativeCommandReleased = null;
        lock (_lifetimeSync)
        {
            if (_administrativeCommandRetentions <= 0)
            {
                return;
            }

            _administrativeCommandRetentions--;
            administrativeCommandReleased = SignalAdministrativeCommandReleasedIfIdleNoLock();
        }

        administrativeCommandReleased?.TrySetResult();
    }

    private void ReleaseActiveAdministrativeCommand(
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    )
    {
        TaskCompletionSource? administrativeCommandReleased = null;
        lock (_lifetimeSync)
        {
            if (ReferenceEquals(_activeAdministrativeCommandContext, commandContext))
            {
                _activeAdministrativeCommandContext = null;
                administrativeCommandReleased = SignalAdministrativeCommandReleasedIfIdleNoLock();
            }
        }

        administrativeCommandReleased?.TrySetResult();
    }

    private TaskCompletionSource? SignalAdministrativeCommandReleasedIfIdleNoLock()
    {
        if (_activeAdministrativeCommandContext is not null || _administrativeCommandRetentions > 0)
        {
            return null;
        }

        TaskCompletionSource? administrativeCommandReleased = _administrativeCommandReleased;
        _administrativeCommandReleased = null;
        return administrativeCommandReleased;
    }

    private sealed class AdministrativeCommandBinding(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    ) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.CompareExchange(
                ref targetContext._administrativeCommandContext,
                null,
                commandContext
            );
        }
    }

    private sealed class ActiveAdministrativeCommandTracking(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheAdministrativeCommandExecutionContext commandContext
    ) : IDisposable
    {
        public void Dispose()
        {
            targetContext.ReleaseActiveAdministrativeCommand(commandContext);
        }
    }

    private sealed class AdministrativeCommandRetention(
        DocumentCacheProjectionTargetRuntimeContext targetContext
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            targetContext.ReleaseAdministrativeCommandRetention();
        }
    }

    private sealed class OrdinaryDispatchLease(DocumentCacheProjectionTargetRuntimeContext targetContext)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            targetContext.ReleaseOrdinaryDispatchLease();
        }
    }
}

public sealed class DocumentCacheProjectionTargetRuntimeContextFactory(
    IServiceScopeFactory serviceScopeFactory,
    IDocumentCacheProjectionObservationSink observationSink
) : IDocumentCacheProjectionTargetRuntimeContextFactory
{
    public async Task<DocumentCacheProjectionTargetRuntimeContext> CreateAsync(
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
            IDocumentCacheSessionBoundWriter? sessionBoundWriter =
                serviceScope.ServiceProvider.GetService<IDocumentCacheSessionBoundWriter>();
            DocumentCacheMaterializationTargetContext materializationTargetContext =
                await CreateMaterializationTargetContextAsync(
                        serviceScope.ServiceProvider,
                        executionContext,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            DocumentCacheProjectionTargetProviderAdapters providerAdapters = new(
                executionContext.ProviderToken,
                materializationTargetContext,
                materializer,
                writer
            );

            return new DocumentCacheProjectionTargetRuntimeContext(
                executionContext,
                providerAdapters,
                observationSink,
                sessionBoundWriter,
                serviceScope.DisposeAsync
            );
        }
        catch
        {
            await serviceScope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<DocumentCacheMaterializationTargetContext> CreateMaterializationTargetContextAsync(
        IServiceProvider serviceProvider,
        DocumentCacheTargetExecutionContext executionContext,
        CancellationToken cancellationToken
    )
    {
        IMappingSetProvider mappingSetProvider = serviceProvider.GetRequiredService<IMappingSetProvider>();
        IRuntimeMappingSetCompiler runtimeCompiler =
            serviceProvider
                .GetServices<IRuntimeMappingSetCompiler>()
                .SingleOrDefault(compiler => compiler.Dialect == ToSqlDialect(executionContext.ProviderToken))
            ?? throw new InvalidOperationException(
                "DocumentCache projection target context creation requires one runtime mapping set "
                    + $"compiler for provider '{executionContext.ProviderToken}'."
            );

        MappingSet mappingSet = await mappingSetProvider
            .GetOrCreateAsync(runtimeCompiler.GetCurrentKey(), cancellationToken)
            .ConfigureAwait(false);

        return new DocumentCacheMaterializationTargetContext(
            new DocumentCacheProjectionTargetKey(
                executionContext.TargetKey.TenantKey,
                new DataStoreId(executionContext.TargetKey.DataStoreId)
            ),
            mappingSet,
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            executionContext.ConnectionInput.Value
        );
    }

    private static SqlDialect ToSqlDialect(RelationalProviderToken providerToken)
    {
        ArgumentNullException.ThrowIfNull(providerToken);

        if (providerToken == RelationalProviderToken.Postgresql)
        {
            return SqlDialect.Pgsql;
        }

        if (providerToken == RelationalProviderToken.SqlServer)
        {
            return SqlDialect.Mssql;
        }

        throw new InvalidOperationException(
            $"Unsupported DocumentCache projection provider token '{providerToken}'."
        );
    }
}

public sealed class DocumentCacheProjectionSupervisor(
    IDocumentCacheTargetRegistry targetRegistry,
    IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory,
    IDocumentCacheProjectionObservationSink observationSink,
    IOptions<DocumentCacheOptions> options,
    IDocumentCacheProjectionScheduler scheduler,
    IDocumentCacheLifecycleReader lifecycleReader,
    TimeProvider timeProvider,
    ILogger<DocumentCacheProjectionSupervisor> logger
)
    : BackgroundService,
        IDocumentCacheProjectionSupervisor,
        IDocumentCacheProjectionRetainedTargetContextReleaser
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetRuntimeContext
    > _targetContexts = ImmutableDictionary<
        DocumentCacheTargetKey,
        DocumentCacheProjectionTargetRuntimeContext
    >.Empty;
    private ImmutableDictionary<
        DocumentCacheProjectionTargetContextKey,
        RetainedTargetContext
    > _retainedCommandOwnedTargetContexts = ImmutableDictionary<
        DocumentCacheProjectionTargetContextKey,
        RetainedTargetContext
    >.Empty;
    private ImmutableDictionary<
        DocumentCacheProjectionTargetContextKey,
        RetainedTargetContext
    > _retainedOrdinaryDrainTargetContexts = ImmutableDictionary<
        DocumentCacheProjectionTargetContextKey,
        RetainedTargetContext
    >.Empty;
    private int _shutdownStarted;

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
            if (ShutdownStarted())
            {
                return targetRegistry.CurrentSnapshot;
            }

            DocumentCacheTargetRegistrySnapshot snapshot = await targetRegistry
                .RefreshAsync(reason, cancellationToken)
                .ConfigureAwait(false);

            if (ShutdownStarted())
            {
                return snapshot;
            }

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

        TimeSpan pollInterval = options.Value.Projector.PollInterval;
        await RefreshAsync(DocumentCacheTargetRefreshReason.Startup, stoppingToken).ConfigureAwait(false);
        DateTimeOffset nextPollTickAt = timeProvider.GetUtcNow() + pollInterval;
        await RunReadyTargetsUntilIdleAsync(nextPollTickAt, stoppingToken).ConfigureAwait(false);

        while (true)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset nextWakeAt = GetNextSupervisorWakeAt(now, nextPollTickAt);
            TimeSpan waitDuration = nextWakeAt > now ? nextWakeAt - now : TimeSpan.Zero;
            if (waitDuration > TimeSpan.Zero)
            {
                await Task.Delay(waitDuration, timeProvider, stoppingToken).ConfigureAwait(false);
            }

            DateTimeOffset wakeObservedAt = timeProvider.GetUtcNow();
            if (wakeObservedAt >= nextPollTickAt)
            {
                await RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered, stoppingToken)
                    .ConfigureAwait(false);
                nextPollTickAt = timeProvider.GetUtcNow() + pollInterval;
            }

            await RunReadyTargetsUntilIdleAsync(nextPollTickAt, stoppingToken).ConfigureAwait(false);
        }
    }

    private DateTimeOffset GetNextSupervisorWakeAt(DateTimeOffset now, DateTimeOffset nextPollTickAt)
    {
        DateTimeOffset? nextSchedulingWakeAt = CurrentTargetContexts
            .Select(context =>
                context.SchedulingState.GetNextSchedulingWakeAt(
                    now,
                    context.CancellationRequested,
                    context.DrainExecutor
                )
            )
            .Where(wakeAt => wakeAt is not null)
            .Min();

        return nextSchedulingWakeAt is not null && nextSchedulingWakeAt < nextPollTickAt
            ? nextSchedulingWakeAt.Value
            : nextPollTickAt;
    }

    private async Task RunReadyTargetsUntilIdleAsync(
        DateTimeOffset nextPollTickAt,
        CancellationToken stoppingToken
    )
    {
        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (timeProvider.GetUtcNow() >= nextPollTickAt)
            {
                return;
            }

            ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results = await scheduler
                .RunReadyTargetsOnceAsync(CurrentTargetContexts, stoppingToken)
                .ConfigureAwait(false);

            await EndCompletedRetainedTargetContextsAsync(stoppingToken).ConfigureAwait(false);

            if (!AnyPageProcessed(results))
            {
                return;
            }
        }
    }

    private static bool AnyPageProcessed(
        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results
    ) =>
        results.Any(result =>
            result.Status == DocumentCacheProjectionSchedulerDispatchStatus.Dispatched
            && result.DrainResult?.Outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed
        );

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _shutdownStarted, 1);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _refreshLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await EndAllTargetContextsAsync(DocumentCacheProjectionTargetEndReason.Shutdown)
                .ConfigureAwait(false);
            await EndAllRetainedTargetContextsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool ShutdownStarted() => Volatile.Read(ref _shutdownStarted) != 0;

    public async Task ReleaseRetainedCommandOwnedTargetContextAsync(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (
                _retainedCommandOwnedTargetContexts.TryGetValue(
                    targetContext.ContextKey,
                    out RetainedTargetContext? retainedContext
                )
                && ReferenceEquals(retainedContext.TargetContext, targetContext)
                && !IsRetainedCommandOwnedTargetContextActive(targetContext)
            )
            {
                _retainedCommandOwnedTargetContexts = _retainedCommandOwnedTargetContexts.Remove(
                    targetContext.ContextKey
                );
                await EndTargetContextAsync(targetContext, retainedContext.EndReason).ConfigureAwait(false);
            }

            await EndCompletedRetainedTargetContextsNoLockAsync().ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
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
            if (IsCommandOwned(currentContext))
            {
                RetainCommandOwnedTargetContext(currentContext, endReason);
            }
            else if (IsOrdinaryDrainOwned(currentContext))
            {
                RetainOrdinaryDrainTargetContext(currentContext, endReason);
            }
            else
            {
                await EndTargetContextAsync(currentContext, endReason).ConfigureAwait(false);
            }

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
                await ObserveLifecycleFenceAsync(existing, cancellationToken).ConfigureAwait(false);
                ObserveTarget(existing);
                continue;
            }

            try
            {
                DocumentCacheProjectionTargetRuntimeContext createdContext = await targetContextFactory
                    .CreateAsync(desiredContext, cancellationToken)
                    .ConfigureAwait(false);
                nextContexts[desiredContext.TargetKey] = createdContext;
                await ObserveLifecycleFenceAsync(createdContext, cancellationToken).ConfigureAwait(false);
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
        await EndCompletedRetainedTargetContextsNoLockAsync().ConfigureAwait(false);
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
            await EndOrRetainTargetContextAsync(context, endReason).ConfigureAwait(false);
        }
    }

    private async Task EndAllRetainedTargetContextsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await EndCompletedRetainedTargetContextsNoLockAsync().ConfigureAwait(false);

            ImmutableArray<RetainedTargetContext> activeRetainedContexts = _retainedCommandOwnedTargetContexts
                .Values.Concat(_retainedOrdinaryDrainTargetContexts.Values)
                .GroupBy(retainedContext => retainedContext.TargetContext.ContextKey)
                .Select(group => group.First())
                .ToImmutableArray();
            if (activeRetainedContexts.IsEmpty)
            {
                return;
            }

            await Task.WhenAll(
                    activeRetainedContexts.Select(retainedContext =>
                        retainedContext.TargetContext.WaitForRetainedOwnershipReleasedAsync(cancellationToken)
                    )
                )
                .ConfigureAwait(false);
        }
    }

    private async Task EndOrRetainTargetContextAsync(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionTargetEndReason endReason
    )
    {
        if (IsCommandOwned(context))
        {
            RetainCommandOwnedTargetContext(context, endReason);
            return;
        }

        if (IsOrdinaryDrainOwned(context))
        {
            RetainOrdinaryDrainTargetContext(context, endReason);
            return;
        }

        await EndTargetContextAsync(context, endReason).ConfigureAwait(false);
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

    private void RetainCommandOwnedTargetContext(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionTargetEndReason endReason
    )
    {
        _retainedCommandOwnedTargetContexts = _retainedCommandOwnedTargetContexts.SetItem(
            context.ContextKey,
            new RetainedTargetContext(context, endReason)
        );

        MarkTargetContextNoncurrent(context);
    }

    private void RetainOrdinaryDrainTargetContext(
        DocumentCacheProjectionTargetRuntimeContext context,
        DocumentCacheProjectionTargetEndReason endReason
    )
    {
        context.Cancel();
        _retainedOrdinaryDrainTargetContexts = _retainedOrdinaryDrainTargetContexts.SetItem(
            context.ContextKey,
            new RetainedTargetContext(context, endReason)
        );

        MarkTargetContextNoncurrent(context);
    }

    private void MarkTargetContextNoncurrent(DocumentCacheProjectionTargetRuntimeContext context)
    {
        if (observationSink is IDocumentCacheProjectionCurrentTargetHealthSink currentTargetHealthSink)
        {
            currentTargetHealthSink.MarkTargetContextNoncurrent(context.ContextKey, timeProvider.GetUtcNow());
        }
    }

    private async Task EndCompletedRetainedTargetContextsAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EndCompletedRetainedTargetContextsNoLockAsync().ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task EndCompletedRetainedTargetContextsNoLockAsync()
    {
        ImmutableArray<RetainedTargetContext> completedCommandOwnedRetainedContexts =
            _retainedCommandOwnedTargetContexts
                .Values.Where(retainedContext =>
                    !IsRetainedCommandOwnedTargetContextActive(retainedContext.TargetContext)
                )
                .ToImmutableArray();

        foreach (RetainedTargetContext retainedContext in completedCommandOwnedRetainedContexts)
        {
            _retainedCommandOwnedTargetContexts = _retainedCommandOwnedTargetContexts.Remove(
                retainedContext.TargetContext.ContextKey
            );
            await EndTargetContextAsync(retainedContext.TargetContext, retainedContext.EndReason)
                .ConfigureAwait(false);
        }

        ImmutableArray<RetainedTargetContext> completedOrdinaryDrainRetainedContexts =
            _retainedOrdinaryDrainTargetContexts
                .Values.Where(retainedContext =>
                    IsRetainedOrdinaryDrainTargetContextComplete(retainedContext.TargetContext)
                )
                .ToImmutableArray();

        foreach (RetainedTargetContext retainedContext in completedOrdinaryDrainRetainedContexts)
        {
            _retainedOrdinaryDrainTargetContexts = _retainedOrdinaryDrainTargetContexts.Remove(
                retainedContext.TargetContext.ContextKey
            );
            await EndTargetContextAsync(retainedContext.TargetContext, retainedContext.EndReason)
                .ConfigureAwait(false);
        }
    }

    private static bool IsCommandOwned(DocumentCacheProjectionTargetRuntimeContext context) =>
        context.HasAdministrativeCommandRetention || context.DrainExecutor.IsCommandOwned;

    private static bool IsOrdinaryDrainOwned(DocumentCacheProjectionTargetRuntimeContext context) =>
        context.HasOrdinaryDispatchLease
        || context.DrainExecutor.CurrentOwner == DocumentCacheProjectionDrainInvocationKind.Ordinary;

    private static bool IsRetainedCommandOwnedTargetContextActive(
        DocumentCacheProjectionTargetRuntimeContext context
    ) => context.HasAdministrativeCommandRetention || context.DrainExecutor.IsOwned;

    private static bool IsRetainedOrdinaryDrainTargetContextComplete(
        DocumentCacheProjectionTargetRuntimeContext context
    ) =>
        !context.HasOrdinaryDispatchLease
        && !context.DrainExecutor.IsOwned
        && !context.HasAdministrativeCommandRetention;

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

    private async Task ObserveLifecycleFenceAsync(
        DocumentCacheProjectionTargetRuntimeContext context,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        DocumentCacheProjectionLifecycleFenceSnapshot lifecycleFenceSnapshot;

        try
        {
            if (lifecycleReader.ProviderToken != context.TargetExecutionContext.ProviderToken)
            {
                lifecycleFenceSnapshot =
                    DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycleReadFailure(
                        observedAt,
                        DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                        "DocumentCache lifecycle reader provider does not match target provider."
                    );
            }
            else
            {
                DocumentCacheLifecycleReadResult lifecycleReadResult = await lifecycleReader
                    .ReadLifecycleAsync(
                        context.TargetExecutionContext.ConnectionInput.Value,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                lifecycleFenceSnapshot =
                    DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycleReadResult(
                        lifecycleReadResult,
                        observedAt
                    );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "DocumentCache projection lifecycle observation failed for target {TargetKey}; ordinary processing remains fenced.",
                context.TargetKey
            );
            lifecycleFenceSnapshot =
                DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycleReadFailure(
                    observedAt,
                    DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                    "DocumentCache lifecycle is unreadable."
                );
        }

        context.SchedulingState.ObserveLifecycleFence(lifecycleFenceSnapshot);
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

    private sealed record RetainedTargetContext(
        DocumentCacheProjectionTargetRuntimeContext TargetContext,
        DocumentCacheProjectionTargetEndReason EndReason
    );
}
