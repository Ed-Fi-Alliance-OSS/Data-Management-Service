// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using EdFi.DataManagementService.Backend.Cdc;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>One serialized phase owns gates and controller calls. Targets are constructed on demand;
/// no controller can retain the old executor across replacement. Stop failure forbids replacement.</summary>
internal sealed class CdcRuntimeOwner(
    Func<CancellationToken, Task<(ICdcProjectionRuntime Runtime, CdcProjectionGate Gate)>> create
) : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1);
    private ICdcProjectionRuntime _runtime = null!;
    private CdcProjectionGate _gate = null!;
    private CdcRestartObservations _restartObservations = null!;
    private bool _disposed;
    private bool _stopFailed;
    private Task _pendingCleanup = Task.CompletedTask;
    private bool _drainFailed;
    private bool _phaseDrainFailed;
    internal CancellationToken HoldToken { get; private set; }

    public ICdcProjectionRuntime Runtime =>
        _runtime ?? throw new InvalidOperationException("CDC_API_RUNTIME_STOPPED");
    public CdcProjectionGate Gate => _gate ?? throw new InvalidOperationException("CDC_API_RUNTIME_STOPPED");

    public async Task<T> InPhaseAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        await _serial.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_phaseDrainFailed)
            {
                throw new InvalidOperationException("CDC_API_PHASE_DRAIN_FAILED");
            }
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
            HoldToken = phase.Token;
            T result = default!;
            Exception failure = null!;
            try
            {
                // Runtime StartAsync may retain the execution token for its background service.
                // Only holds use the narrower token cancelled when this phase ends.
                result = await action(token);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            try
            {
                // A hold cannot outlive the serialized phase, including a failed scan.
                await phase.CancelAsync();
                if (_gate is not null)
                {
                    await _gate.WaitUntilIdleAsync(CancellationToken.None);
                }
            }
            catch (Exception cleanupFailure)
            {
                _phaseDrainFailed = true;
                if (failure is not null)
                {
                    throw new AggregateException("CDC_API_PHASE_AND_DRAIN_FAILED", failure, cleanupFailure);
                }
                throw;
            }
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return result;
        }
        finally
        {
            HoldToken = CancellationToken.None;
            _serial.Release();
        }
    }

    // Called only inside InPhaseAsync; separate stop/open permits API requests during the outage.
    public async Task OpenAsync(CancellationToken token, CdcRestartObservations restartObservations = null!)
    {
        if (_runtime is not null || _stopFailed)
        {
            throw new InvalidOperationException("CDC_API_RUNTIME_ALREADY_OWNED_OR_STOP_FAILED");
        }

        _restartObservations = restartObservations;
        (_runtime, _gate) = await create(token);
    }

    public async Task StopAsync()
    {
        var runtime = _runtime;
        var gate = _gate;
        var restartObservations = _restartObservations;
        _runtime = null!;
        _gate = null!;
        _restartObservations = null!;
        Exception failure = null!;
        try
        {
            if (gate is not null)
            {
                await gate.DisposeAsync();
            }
        }
        catch (TimeoutException) when (!gate.CleanupCompletion.IsCompleted)
        {
            _stopFailed = true;
            _drainFailed = true;
            _pendingCleanup = FinishPendingStopAsync(gate.CleanupCompletion, runtime, restartObservations);
            throw new CdcPendingCleanupException(_pendingCleanup);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        try
        {
            // The executor can still read through the observed pager until runtime shutdown completes.
            if (restartObservations is not null)
            {
                await restartObservations.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        if (failure is not null)
        {
            _stopFailed = true;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task FinishPendingStopAsync(
        Task gateCleanup,
        ICdcProjectionRuntime runtime,
        CdcRestartObservations observations
    )
    {
        try
        {
            await gateCleanup;
        }
        finally
        {
            try
            {
                if (runtime is not null)
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (observations is not null)
                {
                    await observations.DisposeAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try
        {
            if (_drainFailed)
            {
                throw new CdcPendingCleanupException(_pendingCleanup);
            }
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopAsync();
        }
        finally
        {
            _serial.Release();
        }
    }
}
