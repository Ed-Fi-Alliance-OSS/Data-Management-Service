// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Lets a fixture retain operations that a bounded adapter await may leave running. Tracking does
/// not change the adapter's timeout contract. Drain only after the caller has stopped producing work.
/// </summary>
internal sealed class CdcOperationDrain : IDisposable
{
    private static readonly AsyncLocal<CdcOperationDrain> _current = new();
    private readonly CdcOperationDrain _previous = _current.Value!;
    private readonly ConcurrentQueue<Task> _operations = new();

    internal CdcOperationDrain() => _current.Value = this;

    internal static Task<T> Track<T>(Task<T> operation)
    {
        _current.Value?._operations.Enqueue(operation);
        return operation;
    }

    internal async Task DrainAsync()
    {
        // A retained parent can start more tracked work before it exits. Keep draining until all
        // such descendants have exited too; a snapshot of the initial tasks is insufficient.
        while (_operations.TryDequeue(out var operation))
        {
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _current.Value = _previous;
}
