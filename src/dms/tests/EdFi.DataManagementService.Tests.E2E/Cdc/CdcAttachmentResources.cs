// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Partial attachment must release every local resource; never mask the scenario failure.</summary>
internal sealed class CdcAttachmentResources : IAsyncDisposable
{
    private readonly Stack<Func<ValueTask>> _cleanup = new();
    private bool _pendingFailure;
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;

    public void Add(IDisposable resource) =>
        _cleanup.Push(() =>
        {
            resource.Dispose();
            return ValueTask.CompletedTask;
        });

    public void Add(IAsyncDisposable resource) => _cleanup.Push(resource.DisposeAsync);

    public async ValueTask DisposeAsync()
    {
        if (_pendingFailure)
        {
            throw new InvalidOperationException("CDC_API_ATTACHMENT_DISPOSAL");
        }
        bool failed = false;
        while (_cleanup.TryPop(out var dispose))
        {
            try
            {
                await dispose();
            }
            catch (CdcPendingCleanupException pending)
            {
                // The outstanding processor still owns the transports, service provider and settings
                // below its runtime in this stack. Retain them until it actually leaves those resources.
                _pendingFailure = true;
                CleanupCompletion = FinishPendingAsync(pending.Completion);
                throw new InvalidOperationException("CDC_API_ATTACHMENT_DISPOSAL");
            }
            catch
            {
                failed = true;
            }
        }
        if (failed)
        {
            throw new InvalidOperationException("CDC_API_ATTACHMENT_DISPOSAL");
        }
    }

    private async Task FinishPendingAsync(Task pending)
    {
        try
        {
            await pending;
        }
        catch
        {
            // Observe late faults. The original disposal remains failed regardless of late completion.
        }
        while (_cleanup.TryPop(out var dispose))
        {
            try
            {
                await dispose();
            }
            catch
            {
                // Cleanup continues; no late success can erase the already reported failure.
            }
        }
    }
}

internal sealed class CdcPendingCleanupException(Task completion)
    : TimeoutException("CDC_API_PROJECTION_DRAIN_TIMEOUT")
{
    internal Task Completion { get; } = completion;
}
