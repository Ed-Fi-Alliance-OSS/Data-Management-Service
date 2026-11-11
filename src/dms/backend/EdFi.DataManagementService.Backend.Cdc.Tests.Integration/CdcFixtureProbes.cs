// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed record CdcProbeFailureEvidence(
    string PrimaryFailure,
    string DrainFailure,
    string ProbeCompletion
);

internal sealed class CdcProbeDrainException(Exception primary, Task completion)
    : AggregateException(
        "CDC fixture probe drain failed.",
        primary,
        new TimeoutException("Probe drain deadline expired.")
    )
{
    internal Task Completion { get; } = completion;
}

/// <summary>
/// A fixture owns every polling operation until it exits. A failed drain transfers the operation
/// and its cancellation sources here, and defers all dependent cleanup without turning failure into success.
/// </summary>
internal sealed class CdcFixtureProbes(
    TimeProvider clock = null!,
    TimeSpan drainTimeout = default,
    Func<CdcProbeFailureEvidence, Task> publish = null!
)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _drainTimeout =
        drainTimeout == default ? TimeSpan.FromSeconds(30) : drainTimeout;
    private readonly Func<CdcProbeFailureEvidence, Task> _publish = publish ?? PublishAsync;
    private readonly ConcurrentQueue<Task> _pending = new();
    private readonly ConcurrentQueue<string> _lateOutcomes = new();
    private bool _closed;

    internal bool DrainFailed => !_pending.IsEmpty;
    internal IReadOnlyList<string> LateOutcomes => _lateOutcomes.ToArray();

    internal void RequireAvailable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (DrainFailed)
        {
            throw new InvalidOperationException("CDC fixture has an unresolved probe drain failure.");
        }
    }

    internal async Task WaitAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken token
    )
    {
        RequireAvailable();
        if (
            timeout <= TimeSpan.Zero
            || timeout > TimeSpan.FromMinutes(10)
            || pollInterval <= TimeSpan.Zero
            || pollInterval > timeout
            || _drainTimeout <= TimeSpan.Zero
        )
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        var timer = new CancellationTokenSource(timeout, _clock);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        Task<bool> active = Task.FromResult(false);
        bool transferred = false;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                active = probe(deadline.Token);
                bool satisfied = await active.WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (satisfied)
                {
                    return;
                }
                await Task.Delay(pollInterval, _clock, deadline.Token);
            }
        }
        catch (Exception original)
        {
            string primary =
                original is OperationCanceledException
                    ? CancellationCategory(token, timer.Token)
                    : "ProbeFailure";
            Exception failure =
                primary == "ObservationTimeout"
                    ? new TimeoutException("Controller fixture observation deadline expired.")
                    : original;
            Task cancellation = deadline.CancelAsync();
            Task<string> drain = ObserveExitAsync(active, cancellation);
            try
            {
                string outcome = await drain.WaitAsync(_drainTimeout, _clock);
                await PublishFailureAsync(new(primary, "None", outcome));
            }
            catch (TimeoutException)
            {
                // Retain the original task and sources; the timed-out await is not its owner.
                transferred = true;
                Task completion = FinishPendingAsync(drain, deadline, timer);
                _pending.Enqueue(completion);
                await PublishFailureAsync(new(primary, "Timeout", "Pending"));
                throw new CdcProbeDrainException(failure, completion);
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            if (!transferred)
            {
                deadline.Dispose();
                timer.Dispose();
            }
        }
    }

    private static string CancellationCategory(CancellationToken caller, CancellationToken timer) =>
        (caller.IsCancellationRequested, timer.IsCancellationRequested) switch
        {
            (true, _) => "CallerCancellation",
            (_, true) => "ObservationTimeout",
            _ => "ProbeCancellation",
        };

    private static async Task<string> ObserveExitAsync(Task active, Task cancellation)
    {
        try
        {
            await Task.WhenAll(active, cancellation);
            return "Completed";
        }
        catch (OperationCanceledException)
        {
            return "Cancelled";
        }
        catch
        {
            return "Faulted";
        }
    }

    private async Task FinishPendingAsync(
        Task<string> drain,
        CancellationTokenSource deadline,
        CancellationTokenSource timer
    )
    {
        _lateOutcomes.Enqueue(await drain);
        deadline.Dispose();
        timer.Dispose();
    }

    internal async Task DisposeResourcesAsync(Func<Task> cleanup)
    {
        _closed = true;
        Task completion = Task.WhenAll(_pending.ToArray());
        if (DrainFailed)
        {
            // Even an already-finished late probe cannot erase the failure. The returned completion
            // owns cleanup as well, so outer owners can keep their own dependencies alive until then.
            Task deferred = CleanupAfterExitAsync(completion, cleanup);
            throw new CdcProbeDrainException(new TimeoutException("Prior probe drain failed."), deferred);
        }
        await cleanup();
    }

    internal async Task DisposeInOrderAsync(params Func<Task>[] steps)
    {
        bool failed = false;
        for (int index = 0; index < steps.Length; index++)
        {
            try
            {
                await steps[index]();
            }
            catch (CdcProbeDrainException pending)
            {
                // The caller must receive the known drain failure now. The continuation owns
                // the remaining dependencies until the child has actually finished using them.
                var remaining = steps[(index + 1)..];
                Task deferred = CleanupAfterExitAsync(
                    pending.Completion,
                    () => DisposeInOrderAsync(remaining)
                );
                throw new CdcProbeDrainException(pending, deferred);
            }
            catch
            {
                failed = true;
            }
        }
        if (failed)
        {
            throw new InvalidOperationException("Fixture evidence or cleanup failed. Details redacted.");
        }
    }

    private async Task CleanupAfterExitAsync(Task completion, Func<Task> cleanup)
    {
        try
        {
            await completion;
        }
        catch
        {
            _lateOutcomes.Enqueue("CleanupFaulted");
        }
        try
        {
            await cleanup();
        }
        catch (CdcProbeDrainException nested)
        {
            await CleanupAfterExitAsync(nested.Completion, () => Task.CompletedTask);
        }
        catch
        {
            // Observe late cleanup faults explicitly, without exposing exception text.
            _lateOutcomes.Enqueue("CleanupFaulted");
        }
    }

    private async Task PublishFailureAsync(CdcProbeFailureEvidence evidence)
    {
        try
        {
            await _publish(evidence);
        }
        catch
        {
            // Evidence storage cannot replace the primary observation/drain failure.
        }
    }

    private static async Task PublishAsync(CdcProbeFailureEvidence evidence)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "cdc-controller-probe-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence));
        TestContext.AddTestAttachment(path, "Sanitized probe cancellation and drain evidence");
    }
}
