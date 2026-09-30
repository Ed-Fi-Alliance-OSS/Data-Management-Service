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
/// <item><b>Last attempt failed</b>: due at <see cref="SigningKeyProviderStatus.NextAttemptAt"/>, also once that is past.</item>
/// <item>
/// <b>Last attempt succeeded</b>: due at the snapshot's retrieval time plus
/// <see cref="SigningKeySettings.RefreshInterval"/> ± 10 %, drawn once per snapshot version.
/// </item>
/// </list>
/// An attempt starts only when it is due, the gate is eligible, no attempt is in flight, and no store operation is
/// outstanding. While an attempt is in flight or an operation that outlived its deadline is still running, the loop
/// waits for the state-change signal alone: no load can start before that operation ends, so an expired deadline is
/// not a reason to wake. Otherwise it waits until the start time or the next signal. A signal only makes the loop
/// recompute; it never starts a load by itself. A failed attempt, the startup attempt included, never stops the loop.
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
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset dueAt = DueAt(status);

            if (wake is { } reason)
            {
                _logger.LogDebug(
                    "Signing-key refresh service woke ({WakeReason}); next attempt due at {DueAt:O}, attempt in flight {LoadInFlight}, store operation outstanding {StoreOperationOutstanding}",
                    reason,
                    dueAt,
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

            DateTimeOffset startAt = dueAt > status.NextAttemptAt ? dueAt : status.NextAttemptAt;
            if (now >= startAt)
            {
                SigningKeyRefreshOutcome outcome = await _provider.RefreshAsync(
                    status.LastOutcome is null
                        ? SigningKeyRefreshTrigger.Startup
                        : SigningKeyRefreshTrigger.Timer,
                    stoppingToken
                );
                if (outcome is SigningKeyRefreshOutcome.Failed { Exception: ObjectDisposedException })
                {
                    // The provider is gone; every further attempt would fail at once without a state change.
                    return;
                }

                wake = null;
                continue;
            }

            wake = await WaitAsync(
                signal,
                startAt - now,
                status.LastOutcome is SigningKeyRefreshOutcome.Failed
                    ? WakeReason.RetryDeadline
                    : WakeReason.Tick,
                stoppingToken
            );
        }
    }

    /// <summary>The due time from the outcome of the last completed attempt, never from gate eligibility.</summary>
    private DateTimeOffset DueAt(SigningKeyProviderStatus status) =>
        status.LastOutcome switch
        {
            null => DateTimeOffset.MinValue,
            SigningKeyRefreshOutcome.Succeeded succeeded => RefreshDeadlineFor(succeeded.Snapshot),
            _ => status.NextAttemptAt,
        };

    /// <summary>
    /// The normal refresh deadline of <paramref name="snapshot"/>. The jitter is drawn once per snapshot version, so
    /// recomputing after a signal never moves the deadline of the same snapshot.
    /// </summary>
    private DateTimeOffset RefreshDeadlineFor(SigningKeySnapshot snapshot)
    {
        if (_refreshDeadline is not { } deadline || deadline.Version != snapshot.Version)
        {
            double factor = 1 - RefreshJitterFraction + (2 * RefreshJitterFraction * _random.NextDouble());
            deadline = new RefreshDeadline(
                snapshot.Version,
                snapshot.RetrievedAt + (_settings.RefreshInterval * factor)
            );
            _refreshDeadline = deadline;
        }

        return deadline.DueAt;
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

    private readonly record struct RefreshDeadline(long Version, DateTimeOffset DueAt);
}
