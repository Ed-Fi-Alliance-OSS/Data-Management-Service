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
    private bool _disposed;
    private bool _stopFailed;

    public ICdcProjectionRuntime Runtime =>
        _runtime ?? throw new InvalidOperationException("CDC_API_RUNTIME_STOPPED");
    public CdcProjectionGate Gate => _gate ?? throw new InvalidOperationException("CDC_API_RUNTIME_STOPPED");

    public async Task<T> InPhaseAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        await _serial.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await action(token);
        }
        finally
        {
            _serial.Release();
        }
    }

    // Called only inside InPhaseAsync; separate stop/open permits API requests during the outage.
    public async Task OpenAsync(CancellationToken token)
    {
        if (_runtime is not null || _stopFailed)
        {
            throw new InvalidOperationException("CDC_API_RUNTIME_ALREADY_OWNED_OR_STOP_FAILED");
        }

        (_runtime, _gate) = await create(token);
    }

    public async Task StopAsync()
    {
        var runtime = _runtime;
        var gate = _gate;
        _runtime = null!;
        _gate = null!;
        Exception failure = null!;
        try
        {
            if (gate is not null)
            {
                await gate.DisposeAsync();
            }
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
        if (failure is not null)
        {
            _stopFailed = true;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try
        {
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
