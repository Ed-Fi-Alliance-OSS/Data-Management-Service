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
    private bool _disposed;

    public void ConfigureServices(IServiceCollection services)
    {
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
            if (_paused || _active != 0)
            {
                throw new InvalidOperationException("Arm the gate only between completed processing phases.");
            }
            _documentId = documentId;
            _released = NewSignal();
            _arrived = NewArrival();
            _paused = true;
        }
    }

    public Task<CdcProjectionPauseObservation> WaitUntilPausedAsync(
        CancellationToken cancellationToken = default
    )
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paused)
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
            _released.TrySetResult();
        }
    }

    internal async Task<DocumentCacheProjectionItemProcessResult> ProcessAsync(
        IDocumentCacheProjectionItemProcessor inner,
        DocumentCacheProjectionItemProcessRequest request,
        CancellationToken cancellationToken
    )
    {
        Task release;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active++ == 0)
            {
                _drained = NewSignal();
            }
            bool selected =
                _paused
                && request.TargetContext.TargetKey == _targetKey
                && (_documentId == 0 || request.WorkItem.DocumentId == _documentId);
            release = selected ? _released.Task : Task.CompletedTask;
            if (selected)
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
            return await inner.ProcessItemAsync(request, linked.Token).ConfigureAwait(false);
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
