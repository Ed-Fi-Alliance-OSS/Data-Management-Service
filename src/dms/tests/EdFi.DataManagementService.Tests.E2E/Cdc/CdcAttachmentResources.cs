// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Partial attachment must release every local resource; never mask the scenario failure.</summary>
internal sealed class CdcAttachmentResources : IAsyncDisposable
{
    private readonly Stack<Func<ValueTask>> _cleanup = new();

    public void Add(IDisposable resource) =>
        _cleanup.Push(() =>
        {
            resource.Dispose();
            return ValueTask.CompletedTask;
        });

    public void Add(IAsyncDisposable resource) => _cleanup.Push(resource.DisposeAsync);

    public async ValueTask DisposeAsync()
    {
        bool failed = false;
        while (_cleanup.TryPop(out var dispose))
        {
            try
            {
                await dispose();
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

    public async Task DisposeAfterFailureAsync()
    {
        try
        {
            await DisposeAsync();
        }
        catch
        { /* Preserve the original failure; no raw disposal diagnostics. */
        }
    }
}
