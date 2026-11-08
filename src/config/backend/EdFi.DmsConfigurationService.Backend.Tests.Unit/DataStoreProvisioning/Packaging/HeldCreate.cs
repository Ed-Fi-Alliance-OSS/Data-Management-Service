// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// A create a fixture starts against a held restore and owns until its teardown (Step 5.1 review). The running create
/// and its cancellation source belong to the fixture, not to locals of its setup, so a setup that fails once the
/// restore is held still leaves teardown able to cancel the create, end the hold and join the create before the lane
/// is torn down.
/// </summary>
public sealed class HeldCreate
{
    /// <summary>
    /// How long a join waits for a create that was cancelled, timed out by the test or unblocked; a bound on a failure,
    /// never a race a healthy create can lose.
    /// </summary>
    public static readonly TimeSpan JoinBound = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _cancellation;
    private readonly Task<DmsDataStoreCreateResult> _running;
    private bool _stopped;

    private HeldCreate(
        CancellationTokenSource cancellation,
        Func<CancellationToken, Task<DmsDataStoreCreateResult>> create
    )
    {
        _cancellation = cancellation;
        _running = create(cancellation.Token);
    }

    /// <summary>Starts <paramref name="create"/> with a cancellation token this instance owns.</summary>
    public static HeldCreate Start(Func<CancellationToken, Task<DmsDataStoreCreateResult>> create) =>
        new(new CancellationTokenSource(), create);

    /// <summary>Cancels the create, as its caller would.</summary>
    public Task CancelAsync() => _cancellation.CancelAsync();

    /// <summary>
    /// Waits up to <see cref="JoinBound"/> for the create to end and returns it ended, to be awaited for its result or
    /// its exception; throws <see cref="TimeoutException"/> when it is still running.
    /// </summary>
    public async Task<Task<DmsDataStoreCreateResult>> JoinAsync()
    {
        try
        {
            await _running.WaitAsync(JoinBound);
        }
        catch (Exception) when (_running.IsCompleted)
        {
            // The create's own outcome; the caller awaits the returned task for it.
        }

        return _running;
    }

    /// <summary>
    /// The teardown, run whether or not setup got as far as completing the create: cancels the create, runs
    /// <paramref name="unblock"/>, which must end the hold whether or not anything still waits on it, then joins the
    /// create within <see cref="JoinBound"/>. Every step runs even when an earlier one fails.
    /// </summary>
    public async Task StopAsync(Func<Task> unblock)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await RunAllAsync(
            () => _cancellation.CancelAsync(),
            unblock,
            async () => await JoinAsync(),
            () =>
            {
                _cancellation.Dispose();
                return Task.CompletedTask;
            }
        );
    }

    /// <summary>Runs every step in order even when an earlier one fails, then rethrows the failures.</summary>
    public static async Task RunAllAsync(params Func<Task>[] steps)
    {
        List<Exception> failures = [];
        foreach (Func<Task> step in steps)
        {
            try
            {
                await step();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }
}
