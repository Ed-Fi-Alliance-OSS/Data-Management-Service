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
/// <b>Admission.</b> Whether a request needs a load at all is decided under the gate lock against the snapshot as it is
/// then, so a caller that saw no usable snapshot but was overtaken by a publication uses the new snapshot instead of
/// loading again. A load attempt, whatever its trigger, joins the attempt in flight. Otherwise it is refused while
/// <c>now &lt; NextAttemptAt</c>, or while an earlier store operation is still running (see <b>Ownership</b>);
/// otherwise it starts. A conditional refresh (<see cref="RefreshIfUnchangedAsync"/>) is first refused with
/// <see cref="SigningKeyRefusalReason.StateChanged"/> when the state version differs from the one its caller observed;
/// every transition (attempt start, completion, release of an outstanding operation, disposal) changes the version.
/// </item>
/// <item>
/// <b>Deadline.</b> The store call runs on the thread pool, so synchronous work in a source cannot delay a caller, under
/// a token that the load deadline (<see cref="SigningKeySettings.LoadTimeout"/>) and disposal cancel. The attempt ends
/// at the deadline even when the store ignores the token, and a result that arrives at or after the deadline is
/// rejected. Either way the timeout is recorded once, as <c>Failed(Retrieval)</c>, with backoff.
/// </item>
/// <item>
/// <b>Ownership.</b> The single-flight slot owns the store operation until the operation itself finishes, not just until
/// its attempt ends. While an operation that outlived its deadline is still running, no other store call starts: the
/// gate refuses with <see cref="SigningKeyRefusalReason.OperationOutstanding"/>. When it finishes, its result is
/// discarded and <see cref="AttemptStateChanged"/> fires. Every recovery bound therefore assumes the underlying store
/// operation terminates; the database drivers honor the token (step 1.4), and certificate reads are finite.
/// </item>
/// <item>
/// <b>Classification.</b> Any exception from the source is <c>Failed(Retrieval)</c>, whatever its type: drivers report
/// cancellation and timeouts differently. Records that were read but none of which can be used are
/// <c>Failed(Processing)</c>. A failure never replaces the current snapshot. Success publishes the snapshot, resets the
/// failure count, and opens the gate immediately. Failure increments the count and closes the gate for
/// <c>min(5·2^(n−1), 60) s ± 20 %</c>.
/// </item>
/// <item>
/// <b>Waiters.</b> A waiter's cancellation token cancels only its own wait: the shared load keeps running, and the
/// failure count is untouched.
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
    private Task<SigningKeySourceResult>? _storeOperation;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private SigningKeyRefreshOutcome? _lastOutcome;
    private DateTimeOffset? _lastCompletedAt;
    private long _version;
    private long _stateVersion;
    private TaskCompletionSource _stateChanged = NewSignal();
    private bool _disposed;

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

    /// <summary>Which snapshot states make a load unnecessary for a caller.</summary>
    private enum LoadNeed
    {
        /// <summary>Load regardless of the snapshot (scheduled refresh, unknown key).</summary>
        Always,

        /// <summary>A background reload for an overdue snapshot: unnecessary once a fresh one is published.</summary>
        UnlessFresh,

        /// <summary>A request that needs keys now: unnecessary once any usable snapshot is published.</summary>
        UnlessUsable,
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
                    _lastOutcome,
                    _storeOperation is not null,
                    _stateVersion,
                    _disposed
                );
            }
        }
    }

    public async Task<SigningKeySnapshot> GetUsableAsync(CancellationToken cancellationToken)
    {
        // Lock-free fast path. It only ever returns a snapshot; every decision to load is made again under the lock.
        SigningKeySnapshot? snapshot = Current;
        SigningKeySnapshotState state = StateOf(snapshot, _timeProvider.GetUtcNow());

        if (state == SigningKeySnapshotState.Fresh)
        {
            return snapshot!;
        }

        if (state == SigningKeySnapshotState.Overdue)
        {
            // The attempt task never faults, so it can be left unobserved; the gate may refuse it.
            _ = Admit(SigningKeyRefreshTrigger.Request, LoadNeed.UnlessFresh);
            return snapshot!;
        }

        Admission admission = Admit(SigningKeyRefreshTrigger.Request, LoadNeed.UnlessUsable);
        if (admission.Usable is not null)
        {
            return admission.Usable;
        }

        SigningKeyRefreshOutcome outcome = await admission.Attempt!.WaitAsync(cancellationToken);
        if (outcome is SigningKeyRefreshOutcome.Succeeded succeeded)
        {
            return succeeded.Snapshot;
        }

        throw new SigningKeysUnavailableException(
            admission.State == SigningKeySnapshotState.None
                ? SigningKeysUnavailableReason.NoSnapshot
                : SigningKeysUnavailableReason.SnapshotExpired,
            outcome
        );
    }

    public Task<SigningKeyRefreshOutcome> RefreshAsync(
        SigningKeyRefreshTrigger trigger,
        CancellationToken cancellationToken
    ) => Admit(trigger, LoadNeed.Always).Attempt!.WaitAsync(cancellationToken);

    public Task<SigningKeyRefreshOutcome> RefreshIfUnchangedAsync(
        SigningKeyRefreshTrigger trigger,
        long observedStateVersion,
        CancellationToken cancellationToken
    ) =>
        Admit(trigger, LoadNeed.Always, observedStateVersion: observedStateVersion)
            .Attempt!.WaitAsync(cancellationToken);

    public async Task<SigningKeyUnknownKeyOutcome> TryRefreshForUnknownKeyAsync(
        string keyId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        Admission admission = Admit(
            SigningKeyRefreshTrigger.UnknownKey,
            LoadNeed.Always,
            enforceCooldown: true,
            keyId: keyId
        );
        if (admission.KeyPresent)
        {
            return SigningKeyUnknownKeyOutcome.AlreadyPresent;
        }

        if (admission.Attempt is null)
        {
            return SigningKeyUnknownKeyOutcome.SuppressedCooldown;
        }

        return await admission.Attempt.WaitAsync(cancellationToken) switch
        {
            SigningKeyRefreshOutcome.Refused => SigningKeyUnknownKeyOutcome.RefusedGate,
            SigningKeyRefreshOutcome.Failed => SigningKeyUnknownKeyOutcome.RefreshFailed,
            SigningKeyRefreshOutcome.Succeeded succeeded when succeeded.Snapshot.ContainsKeyId(keyId) =>
                SigningKeyUnknownKeyOutcome.RefreshedFound,
            _ => SigningKeyUnknownKeyOutcome.RefreshedAbsent,
        };
    }

    /// <summary>
    /// Cancels the store operation in flight. Later admissions fail without calling the store or changing any state.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stateVersion++;
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    /// <summary>
    /// Decides, atomically with the gate, the cooldown, and the current snapshot, what a caller gets: a usable snapshot
    /// that makes a load unnecessary, the key already present, a suppressed refresh (no attempt), or an attempt to await
    /// (joined, refused, or newly started). With <paramref name="observedStateVersion"/>, a state other than the
    /// observed one refuses before anything else is considered.
    /// </summary>
    private Admission Admit(
        SigningKeyRefreshTrigger trigger,
        LoadNeed need,
        bool enforceCooldown = false,
        string? keyId = null,
        long? observedStateVersion = null
    )
    {
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            SigningKeySnapshotState state = StateOf(_current, now);

            if (_disposed)
            {
                return new Admission(
                    state,
                    Attempt: Task.FromResult<SigningKeyRefreshOutcome>(
                        new SigningKeyRefreshOutcome.Failed(
                            SigningKeyFailureKind.Retrieval,
                            new ObjectDisposedException(nameof(SigningKeySnapshotProvider))
                        )
                    )
                );
            }

            if (observedStateVersion is { } observed && observed != _stateVersion)
            {
                return new Admission(state, Attempt: Refused(SigningKeyRefusalReason.StateChanged));
            }

            if (
                (
                    need == LoadNeed.UnlessUsable
                    && state is SigningKeySnapshotState.Fresh or SigningKeySnapshotState.Overdue
                ) || (need == LoadNeed.UnlessFresh && state == SigningKeySnapshotState.Fresh)
            )
            {
                return new Admission(state, Usable: _current);
            }

            if (keyId is not null && _current?.ContainsKeyId(keyId) == true)
            {
                return new Admission(state, KeyPresent: true);
            }

            if (_inFlight is not null)
            {
                return new Admission(state, Attempt: _inFlight);
            }

            // The cooldown protects a snapshot that can validate tokens from refreshes driven by unknown key ids. An
            // empty snapshot rejects every token, so there is nothing to protect: on a fresh store, a key inserted
            // after the startup load is accepted on first sight rather than after the cooldown. Single-flight, the
            // retry gate and the backoff still bound the loads.
            if (
                enforceCooldown
                && _current is { Keys.Count: > 0 }
                && _lastCompletedAt is { } lastCompleted
                && now - lastCompleted < _settings.UnknownKeyRefreshCooldown
            )
            {
                return new Admission(state);
            }

            if (now < _nextAttemptAt)
            {
                return new Admission(state, Attempt: Refused(SigningKeyRefusalReason.RetryDelay));
            }

            if (_storeOperation is not null)
            {
                return new Admission(state, Attempt: Refused(SigningKeyRefusalReason.OperationOutstanding));
            }

            return new Admission(state, Attempt: StartAttemptUnderLock(trigger));
        }
    }

    private Task<SigningKeyRefreshOutcome> Refused(SigningKeyRefusalReason reason) =>
        Task.FromResult<SigningKeyRefreshOutcome>(
            new SigningKeyRefreshOutcome.Refused(_nextAttemptAt, reason)
        );

    private Task<SigningKeyRefreshOutcome> StartAttemptUnderLock(SigningKeyRefreshTrigger trigger)
    {
        long started = _timeProvider.GetTimestamp();
        CancellationTokenSource deadline = new(_settings.LoadTimeout, _timeProvider);
        CancellationTokenSource attemptToken = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token,
            _shutdown.Token
        );
        TaskCompletionSource<SigningKeyRefreshOutcome> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        // On the thread pool, so no synchronous part of the source runs on (and delays) the caller.
        Task<SigningKeySourceResult> operation = Task.Run(
            () => _source.LoadAsync(attemptToken.Token),
            CancellationToken.None
        );

        _inFlight = completion.Task;
        _storeOperation = operation;
        _stateVersion++;
        _ = SuperviseAsync(new Attempt(trigger, started, operation, deadline, attemptToken, completion));
        return completion.Task;
    }

    /// <summary>
    /// Ends the attempt at the store's answer or at the deadline, whichever comes first. The store operation stays owned
    /// by the gate until it finishes.
    /// </summary>
    private async Task SuperviseAsync(Attempt attempt)
    {
        SigningKeySourceResult? result = null;
        Exception? failure = null;
        try
        {
            result = await attempt.Operation.WaitAsync(attempt.Token.Token);
            if (
                attempt.Token.IsCancellationRequested
                || _timeProvider.GetElapsedTime(attempt.Started) >= _settings.LoadTimeout
            )
            {
                result = null;
                failure = new TimeoutException(
                    $"The signing-key load finished at or after its {_settings.LoadTimeout.TotalSeconds:F0} s deadline; its result was discarded."
                );
            }
        }
        catch (OperationCanceledException canceled) when (attempt.Deadline.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"The signing-key load did not finish within its {_settings.LoadTimeout.TotalSeconds:F0} s deadline.",
                canceled
            );
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        bool operationFinished = attempt.Operation.IsCompleted;
        attempt.Completion.SetResult(
            Complete(
                attempt,
                result,
                failure,
                operationFinished,
                _timeProvider.GetElapsedTime(attempt.Started)
            )
        );

        if (operationFinished)
        {
            attempt.DisposeTokens();
        }
        else
        {
            _ = ReleaseWhenFinishedAsync(attempt);
        }
    }

    /// <summary>
    /// Waits for a store operation that outlived its attempt, discards its result, and frees the gate for the next
    /// attempt.
    /// </summary>
    private async Task ReleaseWhenFinishedAsync(Attempt attempt)
    {
        await Task.WhenAny(attempt.Operation);
        _ = attempt.Operation.Exception;
        attempt.DisposeTokens();

        TaskCompletionSource signal;
        lock (_sync)
        {
            if (ReferenceEquals(_storeOperation, attempt.Operation))
            {
                _storeOperation = null;
            }

            _stateVersion++;
            signal = _stateChanged;
            _stateChanged = NewSignal();
        }

        _logger.LogWarning(
            "A signing-key load that outlived its deadline has finished ({OperationStatus}); its result was discarded",
            attempt.Operation.Status
        );
        signal.TrySetResult();
    }

    private SigningKeyRefreshOutcome Complete(
        Attempt attempt,
        SigningKeySourceResult? result,
        Exception? failure,
        bool operationFinished,
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

            if (operationFinished && ReferenceEquals(_storeOperation, attempt.Operation))
            {
                _storeOperation = null;
            }

            consecutiveFailures = _consecutiveFailures;
            _lastOutcome = outcome;
            _lastCompletedAt = now;
            _inFlight = null;
            _stateVersion++;
            signal = _stateChanged;
            _stateChanged = NewSignal();
        }

        Log(attempt.Trigger, outcome, duration, consecutiveFailures, retryDelay);
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

    /// <summary>
    /// What a caller gets from admission. <see cref="State"/> is the snapshot state admission saw; a null
    /// <see cref="Attempt"/> with no <see cref="Usable"/> snapshot and no <see cref="KeyPresent"/> means the cooldown
    /// suppressed the refresh.
    /// </summary>
    private readonly record struct Admission(
        SigningKeySnapshotState State,
        SigningKeySnapshot? Usable = null,
        Task<SigningKeyRefreshOutcome>? Attempt = null,
        bool KeyPresent = false
    );

    /// <summary>One attempt and the store operation it started, with the tokens that operation holds.</summary>
    private sealed record Attempt(
        SigningKeyRefreshTrigger Trigger,
        long Started,
        Task<SigningKeySourceResult> Operation,
        CancellationTokenSource Deadline,
        CancellationTokenSource Token,
        TaskCompletionSource<SigningKeyRefreshOutcome> Completion
    )
    {
        public void DisposeTokens()
        {
            Token.Dispose();
            Deadline.Dispose();
        }
    }
}
