// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// A faked key repository whose active-public-key read is scripted per test and records the fake time and token of
/// every call.
/// </summary>
internal sealed class KeyRepositoryHarness
{
    private static readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    private readonly List<(DateTimeOffset At, CancellationToken Token)> _calls = [];

    public KeyRepositoryHarness(TimeProvider timeProvider)
    {
        A.CallTo(() => Repository.GetActivePublicKeysAsync(A<CancellationToken>._))
            .ReturnsLazily(
                (CancellationToken cancellationToken) =>
                {
                    lock (_calls)
                    {
                        _calls.Add((timeProvider.GetUtcNow(), cancellationToken));
                    }

                    return Behavior(cancellationToken);
                }
            );
    }

    public IOpenIddictTokenRepository Repository { get; } = A.Fake<IOpenIddictTokenRepository>();

    /// <summary>What the next read does; by default it returns no rows.</summary>
    public Func<CancellationToken, Task<IEnumerable<PublicKeyInfo>>> Behavior { get; set; } =
        _ => Task.FromResult(Enumerable.Empty<PublicKeyInfo>());

    public IReadOnlyList<(DateTimeOffset At, CancellationToken Token)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>
    /// Waits (in real time, bounded) until the store has been called <paramref name="count"/> times. Store calls run on
    /// the thread pool, so a test must wait for one before advancing fake time past its deadline.
    /// </summary>
    public async Task WaitForCallsAsync(int count)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (Calls.Count < count)
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"The store was called {Calls.Count} times, not {count}.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>A row for a key id; the same id always carries the same RSA public key.</summary>
    public static PublicKeyInfo Row(string keyId) =>
        new()
        {
            KeyId = keyId,
            PublicKey = _keys.GetOrAdd(
                keyId,
                static _ =>
                {
                    using RSA rsa = RSA.Create(2048);
                    return rsa.ExportSubjectPublicKeyInfo();
                }
            ),
        };

    /// <summary>A row whose key material is in no supported format.</summary>
    public static PublicKeyInfo Garbage(string keyId) => new() { KeyId = keyId, PublicKey = [1, 2, 3] };

    public void Returns(params PublicKeyInfo[] rows) =>
        Behavior = _ => Task.FromResult<IEnumerable<PublicKeyInfo>>(rows);

    public void ReturnsKeys(params string[] keyIds) => Returns([.. keyIds.Select(Row)]);

    public void Fails(Exception exception) =>
        Behavior = _ => Task.FromException<IEnumerable<PublicKeyInfo>>(exception);

    /// <summary>Holds every read until the returned gate is opened; the read honors cancellation.</summary>
    public TaskCompletionSource<IEnumerable<PublicKeyInfo>> Gate()
    {
        TaskCompletionSource<IEnumerable<PublicKeyInfo>> gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Behavior = cancellationToken =>
        {
            cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            return gate.Task;
        };
        return gate;
    }
}

/// <summary>Always returns the same sample, so a 0.5 sample makes the backoff jitter factor exactly 1.</summary>
internal sealed class FixedRandom(double sample) : Random
{
    public override double NextDouble() => sample;
}

/// <summary>
/// Fake time that runs a callback inside the next <see cref="GetUtcNow"/> call, to interleave another caller at an
/// exact point of the code under test.
/// </summary>
internal sealed class InterceptingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private Action? _next;

    public void OnNextUtcNow(Action action) => _next = action;

    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Exchange(ref _next, null)?.Invoke();
        return base.GetUtcNow();
    }
}

/// <summary>
/// Fake time whose timers never fire: the clock moves, but a deadline callback is never delivered, as when a timer
/// runs late on a busy host. Only an elapsed-time check can then notice a deadline has passed.
/// </summary>
internal sealed class LateTimerTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    ) => base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
}

/// <summary>
/// Fake time that records the due instant of every <c>Task.Delay</c> timer, the only timers the refresh service
/// creates, so a test can wait until the service is parked on its wait before advancing time. A load deadline's timer
/// belongs to a <see cref="CancellationTokenSource"/> and is not recorded.
/// </summary>
internal sealed class SchedulerTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly List<DateTimeOffset> _waits = [];
    private readonly List<SchedulerWait> _created = [];
    private readonly List<(
        Func<SchedulerWait, bool> Matches,
        TaskCompletionSource<SchedulerWait> Parked
    )> _parkedWaiters = [];

    public IReadOnlyList<DateTimeOffset> Waits
    {
        get
        {
            lock (_waits)
            {
                return [.. _waits];
            }
        }
    }

    /// <summary>Every scheduler wait created so far, with the instant it was created and its requested duration.</summary>
    public IReadOnlyList<SchedulerWait> CreatedWaits
    {
        get
        {
            lock (_waits)
            {
                return [.. _created];
            }
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        // Recorded after the timer exists, so advancing time once the record is seen cannot deliver it late.
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        if (state is not CancellationTokenSource && dueTime != Timeout.InfiniteTimeSpan)
        {
            SchedulerWait wait = new(GetUtcNow(), dueTime);
            List<TaskCompletionSource<SchedulerWait>> parked = [];
            lock (_waits)
            {
                _waits.Add(wait.DueAt);
                _created.Add(wait);
                foreach (var waiter in _parkedWaiters.Where(waiter => waiter.Matches(wait)).ToList())
                {
                    _parkedWaiters.Remove(waiter);
                    parked.Add(waiter.Parked);
                }
            }

            foreach (TaskCompletionSource<SchedulerWait> waiter in parked)
            {
                waiter.TrySetResult(wait);
            }
        }

        return timer;
    }

    /// <summary>
    /// Completes when a scheduler wait that satisfies <paramref name="matches"/> exists: at once if one was already
    /// created, otherwise when the service creates it. The signal is the wait's creation, not an elapsed delay.
    /// </summary>
    public Task<SchedulerWait> Parked(Func<SchedulerWait, bool> matches)
    {
        TaskCompletionSource<SchedulerWait> parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waits)
        {
            if (_created.Find(wait => matches(wait)) is { } existing)
            {
                parked.TrySetResult(existing);
            }
            else
            {
                _parkedWaiters.Add((matches, parked));
            }
        }

        return parked.Task;
    }

    /// <summary>Waits (in real time, bounded) until a scheduler wait due at <paramref name="dueAt"/> exists.</summary>
    public Task WaitForWaitAsync(DateTimeOffset dueAt) =>
        SigningKeyTestSupport.WaitUntilAsync(
            () => Waits.Contains(dueAt),
            () =>
                $"No wait is due at {dueAt:O}; waits: {string.Join(", ", Waits.Select(wait => wait.ToString("O")))}."
        );
}

/// <summary>One scheduler wait: when it was created, in fake time, and the duration the service asked for.</summary>
internal sealed record SchedulerWait(DateTimeOffset CreatedAt, TimeSpan DueTime)
{
    public DateTimeOffset DueAt => CreatedAt + DueTime;
}

/// <summary>
/// Fake time whose wall clock can be stepped independently of elapsed time (spec R3.1). It wraps, rather than derives
/// from, a <see cref="FakeTimeProvider"/>: <see cref="GetUtcNow"/> is the inner wall time plus
/// <see cref="WallOffset"/>, while timestamps, their frequency and timers come from the inner provider unchanged. A
/// wall step therefore moves neither <see cref="GetTimestamp"/> nor a pending timer, and <see cref="Advance"/> moves
/// both, as on a real host whose clock is set while time keeps elapsing.
/// </summary>
internal sealed class SkewableTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    public FakeTimeProvider Inner { get; } = inner;

    public TimeSpan WallOffset { get; private set; }

    /// <summary>Moves the wall clock only, as an NTP step or a manual clock change would.</summary>
    public void StepWallClock(TimeSpan step) => WallOffset += step;

    /// <summary>Lets time elapse: both the wall clock and the monotonic timestamp move, and due timers fire.</summary>
    public void Advance(TimeSpan delta) => Inner.Advance(delta);

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow() + WallOffset;

    public override long GetTimestamp() => Inner.GetTimestamp();

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    ) => Inner.CreateTimer(callback, state, dueTime, period);
}

/// <summary>
/// A provider decorator that counts status reads and refresh requests, and can run an action right after a status read,
/// before that status reaches the caller.
/// </summary>
internal sealed class ObservedSnapshotProvider(ISigningKeySnapshotProvider inner)
    : ISigningKeySnapshotProvider
{
    private readonly List<(DateTimeOffset At, SigningKeyRefreshTrigger Trigger)> _refreshes = [];
    private readonly TaskCompletionSource _spinDetected = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _statusReads;
    private int _spinBaseline = -1;
    private int _spinCap;
    private (Func<SigningKeyProviderStatus, bool> When, Action Action)? _afterStatusRead;

    public TimeProvider? Clock { get; init; }

    public int StatusReads => Volatile.Read(ref _statusReads);

    /// <summary>
    /// Completes when the status was read more than the armed cap of times (see <see cref="ArmSpinDetector"/>): the
    /// service is recomputing without waiting.
    /// </summary>
    public Task SpinDetected => _spinDetected.Task;

    /// <summary>Status reads since <see cref="ArmSpinDetector"/> was called.</summary>
    public int StatusReadsSinceArmed => StatusReads - Volatile.Read(ref _spinBaseline);

    /// <summary>
    /// From now on, the status read after the first <paramref name="cap"/> completes <see cref="SpinDetected"/> and throws,
    /// so a service that recomputes without waiting ends its loop instead of hanging the run. A correct service reads the
    /// status a few times per wake, far below the cap.
    /// </summary>
    public void ArmSpinDetector(int cap)
    {
        _spinCap = cap;
        Volatile.Write(ref _spinBaseline, StatusReads);
    }

    public IReadOnlyList<(DateTimeOffset At, SigningKeyRefreshTrigger Trigger)> Refreshes
    {
        get
        {
            lock (_refreshes)
            {
                return [.. _refreshes];
            }
        }
    }

    public SigningKeySnapshot? Current => inner.Current;

    public DateTimeOffset NextAttemptAt => inner.NextAttemptAt;

    public Task AttemptStateChanged => inner.AttemptStateChanged;

    public SigningKeyProviderStatus Status
    {
        get
        {
            int reads = Interlocked.Increment(ref _statusReads);
            int baseline = Volatile.Read(ref _spinBaseline);
            if (baseline >= 0 && reads - baseline > _spinCap)
            {
                _spinDetected.TrySetResult();
                throw new InvalidOperationException(
                    $"The status was read {reads - baseline} times since the spin detector was armed: the service is recomputing without waiting."
                );
            }

            SigningKeyProviderStatus status = inner.Status;
            if (_afterStatusRead is { } hook && hook.When(status))
            {
                _afterStatusRead = null;
                hook.Action();
            }

            return status;
        }
    }

    /// <summary>Runs <paramref name="action"/> once, after the first status read that satisfies <paramref name="when"/>.</summary>
    public void AfterStatusRead(Func<SigningKeyProviderStatus, bool> when, Action action) =>
        _afterStatusRead = (when, action);

    public Task<SigningKeySnapshot> GetUsableAsync(CancellationToken cancellationToken) =>
        inner.GetUsableAsync(cancellationToken);

    public Task<SigningKeyRefreshOutcome> RefreshAsync(
        SigningKeyRefreshTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        lock (_refreshes)
        {
            _refreshes.Add(((Clock ?? TimeProvider.System).GetUtcNow(), trigger));
        }

        return inner.RefreshAsync(trigger, cancellationToken);
    }

    public Task<SigningKeyRefreshOutcome> RefreshIfUnchangedAsync(
        SigningKeyRefreshTrigger trigger,
        long observedStateVersion,
        CancellationToken cancellationToken
    )
    {
        lock (_refreshes)
        {
            _refreshes.Add(((Clock ?? TimeProvider.System).GetUtcNow(), trigger));
        }

        return inner.RefreshIfUnchangedAsync(trigger, observedStateVersion, cancellationToken);
    }

    public Task<SigningKeyUnknownKeyOutcome> TryRefreshForUnknownKeyAsync(
        string keyId,
        CancellationToken cancellationToken
    ) => inner.TryRefreshForUnknownKeyAsync(keyId, cancellationToken);
}

internal static class SigningKeyTestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider NewTime() => new(Start);

    public static SigningKeySnapshotProvider Provider(
        KeyRepositoryHarness harness,
        TimeProvider timeProvider,
        IdentityOptions? options = null,
        ILogger<SigningKeySnapshotProvider>? logger = null,
        Random? random = null
    ) =>
        new(
            new DatabaseSigningKeySource(harness.Repository, NullLogger<DatabaseSigningKeySource>.Instance),
            Options.Create(options ?? new IdentityOptions()),
            timeProvider,
            logger ?? NullLogger<SigningKeySnapshotProvider>.Instance,
            random ?? new FixedRandom(0.5)
        );

    /// <summary>Awaits with a real-time guard so a broken implementation fails instead of hanging the run.</summary>
    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <inheritdoc cref="Bounded{T}(Task{T})"/>
    public static Task Bounded(this Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Polls (in real time, bounded) until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException(failure());
            }

            await Task.Delay(5);
        }
    }

    public static IReadOnlyList<string> MessagesAt<T>(ILogger<T> logger, LogLevel level) =>
        Fake.GetCalls(logger)
            .Where(call => call.Method.Name == nameof(ILogger.Log) && call.GetArgument<LogLevel>(0) == level)
            .Select(call => call.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();
}
