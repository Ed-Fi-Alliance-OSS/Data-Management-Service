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
        using var operations = new CdcOperationDrain();
        Task<bool> active = Task.FromResult(false);
        Exception failure = null!;
        string primary = "ProbeFailure";
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                active = ProbeAsync(probe, deadline.Token, operations);
                bool satisfied = await active.WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (satisfied)
                {
                    break;
                }
                await Task.Delay(pollInterval, _clock, deadline.Token);
            }
        }
        catch (Exception original)
        {
            primary =
                original is OperationCanceledException
                    ? CancellationCategory(token, timer.Token)
                    : "ProbeFailure";
            failure =
                primary == "ObservationTimeout"
                    ? new TimeoutException("Controller fixture observation deadline expired.")
                    : original;
        }
        Task cancellation = failure is not null ? deadline.CancelAsync() : Task.CompletedTask;
        Task<string> drain = ObserveExitAsync(active, cancellation, deadline, timer);
        try
        {
            string outcome = await drain.WaitAsync(_drainTimeout, _clock);
            if (failure is not null)
            {
                await PublishFailureAsync(new(primary, "None", outcome));
            }
        }
        catch (TimeoutException)
        {
            // The retained drain owns underlying operations, cancellation callbacks and source disposal.
            Task completion = FinishPendingAsync(drain);
            _pending.Enqueue(completion);
            await PublishFailureAsync(new(primary, "Timeout", "Pending"));
            throw new CdcProbeDrainException(
                failure ?? new TimeoutException("Fixture probe cleanup deadline expired."),
                completion
            );
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task<bool> ProbeAsync(
        Func<CancellationToken, Task<bool>> probe,
        CancellationToken token,
        CdcOperationDrain operations
    )
    {
        try
        {
            return await probe(token);
        }
        finally
        {
            // A successful outer result also cannot authorize another poll or teardown while
            // a timed-out transport from this poll is still unwinding.
            await operations.DrainAsync();
        }
    }

    private static string CancellationCategory(CancellationToken caller, CancellationToken timer) =>
        (caller.IsCancellationRequested, timer.IsCancellationRequested) switch
        {
            (true, _) => "CallerCancellation",
            (_, true) => "ObservationTimeout",
            _ => "ProbeCancellation",
        };

    private static async Task<string> ObserveExitAsync(
        Task active,
        Task cancellation,
        CancellationTokenSource deadline,
        CancellationTokenSource timer
    )
    {
        string outcome;
        try
        {
            await Task.WhenAll(active, cancellation);
            outcome = "Completed";
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
        }
        catch
        {
            outcome = "Faulted";
        }
        // CancelAsync does not await callbacks when a parent already initiated cancellation.
        // Linked-source disposal joins that parent registration and may block. Keep that join
        // off the observing thread, inside the same bounded, retained drain as the probe itself.
        await Task.Run(() =>
        {
            deadline.Dispose();
            timer.Dispose();
        });
        return outcome;
    }

    private async Task FinishPendingAsync(Task<string> drain) => _lateOutcomes.Enqueue(await drain);

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
