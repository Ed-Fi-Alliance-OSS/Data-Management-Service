// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The snapshot provider and its load gate (spec §4.4).
/// <list type="bullet">
/// <item>
/// A load attempt, whatever its trigger, joins the attempt in flight; otherwise it is refused while
/// <c>now &lt; NextAttemptAt</c>; otherwise it starts under a token that the load deadline
/// (<see cref="SigningKeySettings.LoadTimeout"/>) and disposal both cancel. The deadline bounds the attempt even when
/// the store ignores the token.
/// </item>
/// <item>
/// Any exception from the source is <c>Failed(Retrieval)</c>, whatever its type: drivers report cancellation and
/// timeouts differently. Records that were read but none of which can be used are <c>Failed(Processing)</c>. A failure
/// never replaces the current snapshot.
/// </item>
/// <item>
/// Success publishes the snapshot, resets the failure count, and opens the gate immediately. Failure increments the
/// count and closes the gate for <c>min(5·2^(n−1), 60) s ± 20 %</c>.
/// </item>
/// <item>
/// A waiter's cancellation token cancels only its own wait: the shared load keeps running, and the failure count is
/// untouched.
/// </item>
/// </list>
/// </summary>
public sealed class SigningKeySnapshotProvider : ISigningKeySnapshotProvider, IDisposable
{
    private const double JitterFraction = 0.2;
    private static readonly TimeSpan _backoffBase = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _backoffMaximum = TimeSpan.FromSeconds(60);

    private readonly ISigningKeySource _source;
    private readonly SigningKeySettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SigningKeySnapshotProvider> _logger;
    private readonly Random _random;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _sync = new();

    private SigningKeySnapshot? _current;
    private Task<SigningKeyRefreshOutcome>? _inFlight;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private SigningKeyRefreshOutcome? _lastOutcome;
    private DateTimeOffset? _lastCompletedAt;
    private long _version;
    private TaskCompletionSource _stateChanged = NewSignal();

    /// <param name="random">
    /// Source of the backoff jitter; <see cref="Random.Shared"/> when omitted. Only read under the provider's lock.
    /// </param>
    /// <exception cref="OptionsValidationException">A signing-key setting is invalid.</exception>
    public SigningKeySnapshotProvider(
        ISigningKeySource source,
        IOptions<IdentityOptions> identityOptions,
        TimeProvider timeProvider,
        ILogger<SigningKeySnapshotProvider> logger,
        Random? random = null
    )
    {
        ArgumentNullException.ThrowIfNull(identityOptions);

        _source = source;
        _settings = SigningKeySettings.FromIdentityOptions(identityOptions.Value);
        _timeProvider = timeProvider;
        _logger = logger;
        _random = random ?? Random.Shared;
    }

    public SigningKeySnapshot? Current => Volatile.Read(ref _current);

    public DateTimeOffset NextAttemptAt
    {
        get
        {
            lock (_sync)
            {
                return _nextAttemptAt;
            }
        }
    }

    public Task AttemptStateChanged
    {
        get
        {
            lock (_sync)
            {
                return _stateChanged.Task;
            }
        }
    }

    public SigningKeyProviderStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new SigningKeyProviderStatus(
                    StateOf(_current, _timeProvider.GetUtcNow()),
                    _current,
                    _consecutiveFailures,
                    _nextAttemptAt,
                    _inFlight is not null,
                    _lastOutcome
                );
            }
        }
    }

    public async Task<SigningKeySnapshot> GetUsableAsync(CancellationToken cancellationToken)
    {
        SigningKeySnapshot? snapshot = Current;
        SigningKeySnapshotState state = StateOf(snapshot, _timeProvider.GetUtcNow());

        switch (state)
        {
            case SigningKeySnapshotState.Fresh:
                return snapshot!;

            case SigningKeySnapshotState.Overdue:
                // The attempt task never faults, so it can be left unobserved; the gate may refuse it.
                _ = StartOrJoin(SigningKeyRefreshTrigger.Request);
                return snapshot!;
        }

        SigningKeyRefreshOutcome outcome = await StartOrJoin(SigningKeyRefreshTrigger.Request)
            .WaitAsync(cancellationToken);

        if (outcome is SigningKeyRefreshOutcome.Succeeded succeeded)
        {
            return succeeded.Snapshot;
        }

        throw new SigningKeysUnavailableException(
            state == SigningKeySnapshotState.None
                ? SigningKeysUnavailableReason.NoSnapshot
                : SigningKeysUnavailableReason.SnapshotExpired,
            outcome
        );
    }

    public Task<SigningKeyRefreshOutcome> RefreshAsync(
        SigningKeyRefreshTrigger trigger,
        CancellationToken cancellationToken
    ) => StartOrJoin(trigger).WaitAsync(cancellationToken);

    public async Task<SigningKeyUnknownKeyOutcome> TryRefreshForUnknownKeyAsync(
        string keyId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        if (Current?.ContainsKeyId(keyId) == true)
        {
            return SigningKeyUnknownKeyOutcome.AlreadyPresent;
        }

        if (
            !TryStartOrJoin(
                SigningKeyRefreshTrigger.UnknownKey,
                enforceCooldown: true,
                out Task<SigningKeyRefreshOutcome> attempt
            )
        )
        {
            return SigningKeyUnknownKeyOutcome.SuppressedCooldown;
        }

        return await attempt.WaitAsync(cancellationToken) switch
        {
            SigningKeyRefreshOutcome.Refused => SigningKeyUnknownKeyOutcome.RefusedGate,
            SigningKeyRefreshOutcome.Failed => SigningKeyUnknownKeyOutcome.RefreshFailed,
            SigningKeyRefreshOutcome.Succeeded succeeded when succeeded.Snapshot.ContainsKeyId(keyId) =>
                SigningKeyUnknownKeyOutcome.RefreshedFound,
            _ => SigningKeyUnknownKeyOutcome.RefreshedAbsent,
        };
    }

    /// <summary>Cancels an attempt in flight; later attempts fail.</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private Task<SigningKeyRefreshOutcome> StartOrJoin(SigningKeyRefreshTrigger trigger)
    {
        TryStartOrJoin(trigger, enforceCooldown: false, out Task<SigningKeyRefreshOutcome> attempt);
        return attempt;
    }

    /// <summary>
    /// Joins the attempt in flight or starts one, atomically with the gate and (for unknown-kid refreshes) cooldown
    /// checks. When the gate is closed, <paramref name="attempt"/> is a completed
    /// <see cref="SigningKeyRefreshOutcome.Refused"/>. Returns <see langword="false"/> only when the cooldown suppresses
    /// the refresh.
    /// </summary>
    private bool TryStartOrJoin(
        SigningKeyRefreshTrigger trigger,
        bool enforceCooldown,
        out Task<SigningKeyRefreshOutcome> attempt
    )
    {
        TaskCompletionSource<SigningKeyRefreshOutcome> completion;
        lock (_sync)
        {
            if (_inFlight is not null)
            {
                attempt = _inFlight;
                return true;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (
                enforceCooldown
                && _lastCompletedAt is { } lastCompleted
                && now - lastCompleted < _settings.UnknownKeyRefreshCooldown
            )
            {
                attempt = Task.FromResult<SigningKeyRefreshOutcome>(
                    new SigningKeyRefreshOutcome.Refused(_nextAttemptAt)
                );
                return false;
            }

            if (now < _nextAttemptAt)
            {
                attempt = Task.FromResult<SigningKeyRefreshOutcome>(
                    new SigningKeyRefreshOutcome.Refused(_nextAttemptAt)
                );
                return true;
            }

            completion = new TaskCompletionSource<SigningKeyRefreshOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _inFlight = completion.Task;
        }

        // Started outside the lock: a source that completes synchronously re-enters the lock to publish.
        _ = RunAttemptAsync(trigger, completion);
        attempt = completion.Task;
        return true;
    }

    private async Task RunAttemptAsync(
        SigningKeyRefreshTrigger trigger,
        TaskCompletionSource<SigningKeyRefreshOutcome> completion
    )
    {
        long started = _timeProvider.GetTimestamp();
        SigningKeySourceResult? result = null;
        Exception? failure = null;
        try
        {
            using CancellationTokenSource deadline = new(_settings.LoadTimeout, _timeProvider);
            using CancellationTokenSource attemptToken = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token,
                _shutdown.Token
            );

            Task<SigningKeySourceResult> load = LoadAsync(attemptToken.Token);
            // If the deadline ends the wait first, the orphaned load's fault is still observed.
            _ = load.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

            result = await load.WaitAsync(attemptToken.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        completion.SetResult(Complete(trigger, result, failure, _timeProvider.GetElapsedTime(started)));
    }

    private async Task<SigningKeySourceResult> LoadAsync(CancellationToken cancellationToken) =>
        await _source.LoadAsync(cancellationToken);

    private SigningKeyRefreshOutcome Complete(
        SigningKeyRefreshTrigger trigger,
        SigningKeySourceResult? result,
        Exception? failure,
        TimeSpan duration
    )
    {
        SigningKeyRefreshOutcome outcome;
        TaskCompletionSource signal;
        TimeSpan retryDelay = TimeSpan.Zero;
        int consecutiveFailures;
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            outcome = Classify(result, failure, now);

            if (outcome is SigningKeyRefreshOutcome.Succeeded succeeded)
            {
                Volatile.Write(ref _current, succeeded.Snapshot);
                _consecutiveFailures = 0;
                _nextAttemptAt = now;
            }
            else
            {
                _consecutiveFailures++;
                retryDelay = Backoff(_consecutiveFailures);
                _nextAttemptAt = now + retryDelay;
            }

            consecutiveFailures = _consecutiveFailures;
            _lastOutcome = outcome;
            _lastCompletedAt = now;
            _inFlight = null;
            signal = _stateChanged;
            _stateChanged = NewSignal();
        }

        Log(trigger, outcome, duration, consecutiveFailures, retryDelay);
        signal.TrySetResult();
        return outcome;
    }

    /// <summary>Called under the lock, so versions are assigned in publication order.</summary>
    private SigningKeyRefreshOutcome Classify(
        SigningKeySourceResult? result,
        Exception? failure,
        DateTimeOffset now
    )
    {
        if (failure is not null || result is null)
        {
            return new SigningKeyRefreshOutcome.Failed(
                SigningKeyFailureKind.Retrieval,
                failure ?? new InvalidOperationException("The key source returned no result.")
            );
        }

        if (result.Entries.Count == 0 && result.DiscardedCount > 0)
        {
            return new SigningKeyRefreshOutcome.Failed(
                SigningKeyFailureKind.Processing,
                new InvalidDataException(
                    $"None of the {result.DiscardedCount} active key records could be used."
                )
            );
        }

        return new SigningKeyRefreshOutcome.Succeeded(
            new SigningKeySnapshot(result.Entries, now, ++_version, _source.Kind),
            result.DiscardedCount
        );
    }

    /// <summary><c>min(5·2^(n−1), 60) s</c>, scaled by a jitter factor in [0.8, 1.2). Called under the lock.</summary>
    private TimeSpan Backoff(int consecutiveFailures)
    {
        TimeSpan nominal =
            consecutiveFailures >= 5
                ? _backoffMaximum
                : TimeSpan.FromTicks(
                    Math.Min(_backoffBase.Ticks << (consecutiveFailures - 1), _backoffMaximum.Ticks)
                );
        double factor = 1 - JitterFraction + (2 * JitterFraction * _random.NextDouble());
        return nominal * factor;
    }

    private SigningKeySnapshotState StateOf(SigningKeySnapshot? snapshot, DateTimeOffset now) =>
        snapshot is null ? SigningKeySnapshotState.None : snapshot.GetState(now, _settings);

    private void Log(
        SigningKeyRefreshTrigger trigger,
        SigningKeyRefreshOutcome outcome,
        TimeSpan duration,
        int consecutiveFailures,
        TimeSpan retryDelay
    )
    {
        switch (outcome)
        {
            case SigningKeyRefreshOutcome.Succeeded succeeded:
                _logger.LogInformation(
                    "Signing-key snapshot {Version} published from {Source} ({Trigger}): {KeyCount} keys, {DiscardedCount} records discarded, retrieved in {DurationMs} ms",
                    succeeded.Snapshot.Version,
                    succeeded.Snapshot.Source,
                    trigger,
                    succeeded.KeyCount,
                    succeeded.DiscardedEntryCount,
                    (long)duration.TotalMilliseconds
                );
                if (succeeded.KeyCount == 0)
                {
                    _logger.LogWarning(
                        "Signing-key snapshot {Version} is empty: the {Source} store holds no active signing key, so every token will be rejected",
                        succeeded.Snapshot.Version,
                        succeeded.Snapshot.Source
                    );
                }

                break;

            case SigningKeyRefreshOutcome.Failed failed:
                _logger.LogError(
                    "Signing-key load failed ({Trigger}): category {Category}, kind {FailureKind}, consecutive failures {ConsecutiveFailures}, next attempt in {RetryDelaySeconds:F1} s. {ExceptionType}: {ExceptionMessage}",
                    trigger,
                    AuthenticationDependencyCategory.SigningKeyStore,
                    failed.Kind,
                    consecutiveFailures,
                    retryDelay.TotalSeconds,
                    failed.Exception.GetType().Name,
                    LoggingUtility.SanitizeForLog(failed.Exception.Message)
                );
                break;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
