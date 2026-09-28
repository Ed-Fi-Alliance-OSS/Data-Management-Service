// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>
/// Fixture-owned pause before the real processor's first writer call (including fast-path
/// acknowledgement). Pass ConfigureServices to the internal CDC runtime factory overload.
/// Arm/release between serialized fixture phases; dispose the runtime and this gate at teardown.
/// </summary>
internal sealed class CdcProjectionGate(DocumentCacheTargetKey targetKey, TimeSpan timeout) : IAsyncDisposable
{
    private readonly DocumentCacheTargetKey _targetKey =
        targetKey ?? throw new ArgumentNullException(nameof(targetKey));
    private readonly TimeSpan _timeout =
        timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(5)
            ? timeout
            : throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Gate deadline must be positive and at most five minutes."
            );
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _released = NewSignal();
    private TaskCompletionSource<CdcProjectionPauseObservation> _arrived = NewArrival();
    private TaskCompletionSource _drained = NewSignal();
    private Task _disposal = Task.CompletedTask;
    private int _active;
    private long _documentId;
    private bool _paused;
    private bool _heldProcessingFailed;
    private bool _disposed;
    private bool _overlap;
    private bool _overlapStarted;
    private TaskCompletionSource _candidateReleased = NewSignal();
    private TaskCompletionSource<CdcProjectionPauseObservation> _materialized = NewArrival();
    private TaskCompletionSource<CdcProjectionWriteObservation> _written = NewWrite();

    public void ConfigureServices(IServiceCollection services)
    {
        // Both production providers register this scoped alias through a factory. Preserve that
        // factory so the provider retains ownership of its concrete writer/session-bound alias.
        var writerFactory =
            services.Last(d => d.ServiceType == typeof(IDocumentCacheWriter)).ImplementationFactory
            ?? throw new InvalidOperationException("Expected the provider's scoped writer factory.");
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentCacheWriter>(provider => new HeldCandidateWriter(
                (IDocumentCacheWriter)writerFactory(provider),
                this
            ))
        );
        services.AddSingleton<DocumentCacheProjectionItemProcessor>();
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentCacheProjectionItemProcessor>(
                provider => new PausedProcessor(
                    provider.GetRequiredService<DocumentCacheProjectionItemProcessor>(),
                    this
                )
            )
        );
    }

    /// <summary>Zero selects all work for the target, including a not-yet-created API document.</summary>
    public void Pause(long documentId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentId);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paused || _overlap || _active != 0)
            {
                throw new InvalidOperationException("Arm the gate only between completed processing phases.");
            }
            _documentId = documentId;
            _released = NewSignal();
            _arrived = NewArrival();
            _paused = true;
            _heldProcessingFailed = false;
        }
    }

    /// <summary>Hold the first real candidate at the writer boundary, then block subsequent
    /// processor calls before even their fast-path acknowledgement. Zero binds the first document.</summary>
    public void PauseForOverlap(long documentId = 0)
    {
        lock (_sync)
        {
            Pause(documentId);
            _paused = false;
            _overlap = true;
            _overlapStarted = false;
            _candidateReleased = NewSignal();
            _materialized = NewArrival();
            _written = NewWrite();
        }
    }

    public Task<CdcProjectionPauseObservation> WaitUntilMaterializedAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_overlap)
            {
                throw new InvalidOperationException("Arm overlap before awaiting materialization.");
            }
            return _materialized.Task.WaitAsync(_timeout, token);
        }
    }

    public void ReleaseCandidate()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_overlap || !_materialized.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("Await the materialized candidate before releasing it.");
            }
            _candidateReleased.TrySetResult();
        }
    }

    public Task<CdcProjectionWriteObservation> WaitUntilWrittenAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_overlap)
            {
                throw new InvalidOperationException("Arm overlap before awaiting completion.");
            }
            return _written.Task.WaitAsync(_timeout, token);
        }
    }

    public Task<CdcProjectionPauseObservation> WaitUntilPausedAsync(
        CancellationToken cancellationToken = default
    )
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paused && !_overlap)
            {
                throw new InvalidOperationException("Arm the gate before awaiting an arrival.");
            }
            return _arrived.Task.WaitAsync(_timeout, cancellationToken);
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _paused = false;
            _overlap = false;
            _candidateReleased.TrySetResult();
            _released.TrySetResult();
        }
    }

    /// <summary>Resume an arrived pause and require the held real processor call to finish normally.
    /// An expired/cancelled hold or a provider backoff cannot count as a successful resumed drain.</summary>
    public async Task ResumeHeldProcessingAsync(CancellationToken token)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paused || !_arrived.Task.IsCompletedSuccessfully || _active == 0 || _heldProcessingFailed)
            {
                throw new InvalidOperationException(
                    "A live held processor call is required before resuming."
                );
            }
            Release();
        }
        await WaitUntilIdleAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            if (_heldProcessingFailed)
            {
                throw new InvalidOperationException("The held processor call did not complete normally.");
            }
        }
    }

    public Task WaitUntilIdleAsync(CancellationToken token)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_active == 0 ? Task.CompletedTask : _drained.Task).WaitAsync(_timeout, token);
        }
    }

    internal async Task<DocumentCacheProjectionItemProcessResult> ProcessAsync(
        IDocumentCacheProjectionItemProcessor inner,
        DocumentCacheProjectionItemProcessRequest request,
        CancellationToken cancellationToken
    )
    {
        Task release;
        bool hold;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active++ == 0)
            {
                _drained = NewSignal();
            }
            bool selected =
                request.TargetContext.TargetKey.Equals(_targetKey)
                && (_documentId == 0 || request.WorkItem.DocumentId == _documentId);
            hold = selected && (_paused || (_overlap && _overlapStarted));
            if (selected && _overlap && !_overlapStarted)
            {
                _documentId = request.WorkItem.DocumentId;
                _overlapStarted = true;
            }
            release = hold ? _released.Task : Task.CompletedTask;
            if (hold)
            {
                _arrived.TrySetResult(
                    new(request.WorkItem.DocumentId, request.WorkItem.RequiredContentVersion)
                );
            }
        }
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                request.TargetContext.CancellationToken,
                _shutdown.Token
            );
            await release.WaitAsync(_timeout, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var result = await inner.ProcessItemAsync(request, linked.Token).ConfigureAwait(false);
            if (hold && result.Outcome != DocumentCacheProjectionItemProcessOutcome.Continue)
            {
                lock (_sync)
                {
                    _heldProcessingFailed = true;
                }
            }
            return result;
        }
        catch
        {
            if (hold)
            {
                lock (_sync)
                {
                    _heldProcessingFailed = true;
                }
            }
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (--_active == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _arrived.TrySetCanceled();
                _materialized.TrySetCanceled();
                _written.TrySetCanceled();
                _disposal = StopAsync(_active == 0 ? Task.CompletedTask : _drained.Task);
            }
            return new(_disposal);
        }
    }

    private async Task StopAsync(Task drained)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await drained.WaitAsync(_timeout).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task<DocumentCacheWriterResult> WriteAsync(
        IDocumentCacheWriter inner,
        DocumentCacheWriterRequest request
    )
    {
        bool hold;
        Task release;
        lock (_sync)
        {
            hold =
                _overlap
                && _overlapStarted
                && request.DocumentId == _documentId
                && request.TargetContext.TargetKey.TenantKey == _targetKey.TenantKey
                && request.TargetContext.TargetKey.DataStoreId.Value == _targetKey.DataStoreId
                && request.Candidate is not null
                && !_materialized.Task.IsCompleted;
            release = _candidateReleased.Task;
            if (hold)
            {
                _materialized.TrySetResult(new(request.DocumentId, request.Candidate!.ContentVersion));
            }
        }
        if (hold)
        {
            // The candidate stays on the real processor's stack. No copy or re-materialization.
            await release.WaitAsync(_timeout, request.CancellationToken).ConfigureAwait(false);
            request.CancellationToken.ThrowIfCancellationRequested();
        }
        var result = await inner.WriteAsync(request).ConfigureAwait(false);
        if (hold)
        {
            lock (_sync)
            {
                _written.TrySetResult(
                    new(request.DocumentId, request.Candidate!.ContentVersion, result.Outcome)
                );
            }
        }
        return result;
    }

    private static TaskCompletionSource<CdcProjectionWriteObservation> NewWrite() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HeldCandidateWriter(IDocumentCacheWriter inner, CdcProjectionGate gate)
        : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            gate.WriteAsync(inner, request);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<CdcProjectionPauseObservation> NewArrival() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PausedProcessor(IDocumentCacheProjectionItemProcessor inner, CdcProjectionGate gate)
        : IDocumentCacheProjectionItemProcessor
    {
        public Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
            DocumentCacheProjectionItemProcessRequest request,
            CancellationToken cancellationToken = default
        ) => gate.ProcessAsync(inner, request, cancellationToken);
    }
}

/// <summary>One bounded checkpoint, with no body, connection string or materialization result.</summary>
internal sealed record CdcProjectionPauseObservation(long DocumentId, long RequiredContentVersion);

internal sealed record CdcProjectionWriteObservation(
    long DocumentId,
    long ContentVersion,
    DocumentCacheWriterOutcome Outcome
);
