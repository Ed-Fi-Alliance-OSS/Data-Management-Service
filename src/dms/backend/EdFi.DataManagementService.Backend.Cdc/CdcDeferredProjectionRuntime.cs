// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Invocation-owned, sequential runtime. A failed preparation may be retried by the next watch pass;
/// disposal never initializes unused resources, and initialization never starts the supervisor.
/// </summary>
public sealed class CdcDeferredProjectionRuntime(
    Func<CancellationToken, Task<CdcTransportResult<ICdcProjectionRuntime>>> prepare
) : ICdcProjectionRuntime
{
    private ICdcProjectionRuntime _runtime = null!;
    private bool _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime is not null)
        {
            return;
        }
        var result = await prepare(cancellationToken);
        if (result is CdcTransportResult<ICdcProjectionRuntime>.Observed observed)
        {
            _runtime = observed.Value;
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new CdcEstablishedValidation.EvidenceException(
            result.Diagnostics.FirstOrDefault()
                ?? new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.Unavailable)
        );
    }

    public async Task<DocumentCacheAdministrativeCommandResult> ActivateAsync(
        DocumentCacheGuardedNewEmptyActivationRequest request,
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ActivateAsync(request, cancellationToken);
    }

    public async Task<CdcInitialDatabaseObservation> ObserveInitialDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ObserveInitialDatabaseAsync(cancellationToken);
    }

    public async Task<CdcInitialDatabaseObservation> ObserveEstablishedDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ObserveEstablishedDatabaseAsync(cancellationToken);
    }

    public async Task<CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        CdcDeploymentRequest request,
        ICdcProviderSourcePositionAdapter adapter,
        CancellationToken cancellationToken
    )
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.CaptureBarrierAsync(request, adapter, cancellationToken);
    }

    public async Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _runtime.StartProcessingAsync(cancellationToken);
    }

    public async Task<DocumentCacheStatusResponse> ObserveAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ObserveAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }
    }
}
