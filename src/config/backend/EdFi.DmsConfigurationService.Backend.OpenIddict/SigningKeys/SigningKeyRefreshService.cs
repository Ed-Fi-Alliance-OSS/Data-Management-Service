// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The deadline-driven refresh loop (spec §4.4 "Scheduling"). Each pass captures the provider's state-change signal,
/// then reads its status, and computes when the next attempt is due from the outcome of the last completed attempt:
/// <list type="bullet">
/// <item><b>Never loaded</b>: due now; the first attempt is the startup attempt.</item>
/// <item>
/// <b>Last attempt failed</b>: due when the retry delay has elapsed
/// (<see cref="SigningKeyProviderStatus.RetryDelayRemaining"/>), also once that is past.
/// </item>
/// <item>
/// <b>Last attempt succeeded</b>: due when the snapshot's age (see <see cref="SigningKeySnapshot.GetAge"/>) reaches
/// <see cref="SigningKeySettings.RefreshInterval"/> ± 10 %, drawn once per snapshot version.
/// </item>
/// </list>
/// Every decision uses elapsed time: the monotonic clock for the retry delay, and the snapshot's combined wall-clock and
/// monotonic age for the refresh deadline. A refresh is therefore due no later than its drawn interval of monotonic time
/// after publication. A wall clock ahead of that baseline makes it due earlier, and reversing that lead returns the
/// deadline to the baseline. A wall-clock step wakes nothing; it takes effect at the next wake. The logged due instant is
/// a wall-clock diagnostic.
/// An attempt starts only when it is due, the gate is eligible, no attempt is in flight, and no store operation is
/// outstanding, and only while the provider is still in the observed state: admission goes through
/// <see cref="ISigningKeySnapshotProvider.RefreshIfUnchangedAsync"/> with the status's
/// <see cref="SigningKeyProviderStatus.StateVersion"/>, so a request that changes the state after the read (for example
/// by publishing fresh keys) makes the provider refuse, and the loop recomputes. While an attempt is in flight or an
/// operation that outlived its deadline is still running, the loop
/// waits for the state-change signal alone: no load can start before that operation ends, so an expired deadline is
/// not a reason to wake. Otherwise it waits until the start time or the next signal. A signal only makes the loop
/// recompute; it never starts a load by itself. Every wait is rounded up to a whole millisecond, and only a recomputation
/// that finds nothing left to wait starts an attempt. A failed attempt, the startup attempt included, never stops the loop,
/// whatever the exception (a source's <see cref="ObjectDisposedException"/> is an ordinary retrieval failure); only
/// <see cref="SigningKeyProviderStatus.ProviderDisposed"/> does.
/// </summary>
public sealed class SigningKeyRefreshService : BackgroundService
{
    private const double RefreshJitterFraction = 0.1;

    private readonly ISigningKeySnapshotProvider _provider;
    private readonly SigningKeySettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SigningKeyRefreshService> _logger;
    private readonly Random _random;

    private RefreshDeadline? _refreshDeadline;

    /// <param name="random">
    /// Source of the refresh-deadline jitter; <see cref="Random.Shared"/> when omitted. Only read by the loop.
    /// </param>
    /// <exception cref="OptionsValidationException">A signing-key setting is invalid.</exception>
    public SigningKeyRefreshService(
        ISigningKeySnapshotProvider provider,
        IOptions<IdentityOptions> identityOptions,
        TimeProvider timeProvider,
        ILogger<SigningKeyRefreshService> logger,
        Random? random = null
    )
    {
        ArgumentNullException.ThrowIfNull(identityOptions);

        _provider = provider;
        _settings = SigningKeySettings.FromIdentityOptions(identityOptions.Value);
        _timeProvider = timeProvider;
        _logger = logger;
        _random = random ?? Random.Shared;
    }

    /// <summary>Why the loop woke; logged with the recomputed due time (spec §4.9).</summary>
    private enum WakeReason
    {
        RetryDeadline,
        Tick,
        Signal,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: stop quietly.
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        WakeReason? wake = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            // The signal is captured before the status is read, so a transition between the two completes the
            // signal this pass waits on instead of being missed.
            Task signal = _provider.AttemptStateChanged;
            SigningKeyProviderStatus status = _provider.Status;
            if (status.ProviderDisposed)
            {
                // Disposal is read from the provider itself, never inferred from an exception a source may throw.
                return;
            }

            TimeSpan dueIn = DueIn(status);

            if (wake is { } reason)
            {
                _logger.LogDebug(
                    "Signing-key refresh service woke ({WakeReason}); next attempt due at {DueAt:O}, attempt in flight {LoadInFlight}, store operation outstanding {StoreOperationOutstanding}",
                    reason,
                    _timeProvider.GetUtcNow() + dueIn,
                    status.LoadInFlight,
                    status.StoreOperationOutstanding
                );
            }

            if (status.LoadInFlight || status.StoreOperationOutstanding)
            {
                await signal.WaitAsync(stoppingToken);
                wake = WakeReason.Signal;
                continue;
            }

            TimeSpan startIn = dueIn > status.RetryDelayRemaining ? dueIn : status.RetryDelayRemaining;
            if (startIn <= TimeSpan.Zero)
            {
                // Refused (StateChanged) when another caller changed the state since the read; the next pass recomputes.
                await _provider.RefreshIfUnchangedAsync(
                    status.LastOutcome is null
                        ? SigningKeyRefreshTrigger.Startup
                        : SigningKeyRefreshTrigger.Timer,
                    status.StateVersion,
                    stoppingToken
                );
                wake = null;
                continue;
            }

            wake = await WaitAsync(
                signal,
                RoundUpToWholeMilliseconds(startIn),
                status.LastOutcome is SigningKeyRefreshOutcome.Failed
                    ? WakeReason.RetryDeadline
                    : WakeReason.Tick,
                stoppingToken
            );
        }
    }

    /// <summary>
    /// The elapsed time until the next attempt is due, from the outcome of the last completed attempt, never from gate
    /// eligibility. Zero or negative means due now.
    /// </summary>
    private TimeSpan DueIn(SigningKeyProviderStatus status) =>
        status.LastOutcome switch
        {
            null => TimeSpan.Zero,
            SigningKeyRefreshOutcome.Succeeded succeeded => RefreshAfterFor(succeeded.Snapshot)
                - succeeded.Snapshot.GetAge(_timeProvider),
            _ => status.RetryDelayRemaining,
        };

    /// <summary>
    /// The age at which <paramref name="snapshot"/> is due for its normal refresh. The jitter is drawn once per snapshot
    /// version, so recomputing after a signal never moves the deadline of the same snapshot.
    /// </summary>
    private TimeSpan RefreshAfterFor(SigningKeySnapshot snapshot)
    {
        if (_refreshDeadline is not { } deadline || deadline.Version != snapshot.Version)
        {
            double factor = 1 - RefreshJitterFraction + (2 * RefreshJitterFraction * _random.NextDouble());
            deadline = new RefreshDeadline(snapshot.Version, _settings.RefreshInterval * factor);
            _refreshDeadline = deadline;
        }

        return deadline.RefreshAfter;
    }

    /// <summary>
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> counts whole milliseconds and drops a fraction,
    /// so a positive wait shorter than one millisecond would complete at once, and the loop would recompute without
    /// time passing. Rounding up keeps every wait at least one whole millisecond. It never makes a start early: a start
    /// is admitted only by a recomputation that finds nothing left to wait.
    /// </summary>
    private static TimeSpan RoundUpToWholeMilliseconds(TimeSpan delay)
    {
        long milliseconds = (delay.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
        return TimeSpan.FromTicks(Math.Max(1, milliseconds) * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Waits until <paramref name="delay"/> elapses or <paramref name="signal"/> completes.</summary>
    private async Task<WakeReason> WaitAsync(
        Task signal,
        TimeSpan delay,
        WakeReason deadlineReason,
        CancellationToken stoppingToken
    )
    {
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task elapsed = Task.Delay(delay, _timeProvider, timer.Token);
        Task first = await Task.WhenAny(signal, elapsed);
        await timer.CancelAsync();
        stoppingToken.ThrowIfCancellationRequested();
        return first == signal ? WakeReason.Signal : deadlineReason;
    }

    private readonly record struct RefreshDeadline(long Version, TimeSpan RefreshAfter);
}
