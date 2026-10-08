// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The deadline of one job database operation (spec A3): a budget measured from a start time. Command timeouts
/// and cancellation are derived from the time left on it, and <see cref="JobDatabaseSession"/> never lets the
/// caller wait past it. It is never extended.
/// </summary>
public readonly record struct JobDeadline(long Started, TimeSpan Budget)
{
    public static JobDeadline Start(TimeSpan budget) => new(Stopwatch.GetTimestamp(), budget);

    /// <summary>The time left, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            TimeSpan remaining = Budget - Stopwatch.GetElapsedTime(Started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool Expired => Remaining == TimeSpan.Zero;

    /// <summary>
    /// A command timeout derived from the time left, at least 1 s because 0 would mean no timeout. The caller's
    /// wait is bounded separately, by <see cref="JobDatabaseSession"/>.
    /// </summary>
    public int CommandTimeoutSeconds => Math.Max(1, (int)Math.Ceiling(Remaining.TotalSeconds));

    /// <summary>
    /// A token cancelled when the time left runs out or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public JobDeadlineCancellation CreateCancellation(CancellationToken cancellationToken) =>
        new(Remaining, cancellationToken);
}

/// <summary>
/// A token cancelled when a deadline's time runs out, or when a linked token is cancelled. Its timer catches what
/// the token's callbacks throw: a timer-driven cancellation (such as <c>CancellationTokenSource.CancelAfter</c>)
/// rethrows a callback's exception on the timer thread, where it is unhandled and ends the process.
/// </summary>
public sealed class JobDeadlineCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly Timer _timer;

    internal JobDeadlineCancellation(TimeSpan remaining, CancellationToken cancellationToken)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(
            static state => CancelQuietly((CancellationTokenSource)state!),
            _source,
            remaining,
            Timeout.InfiniteTimeSpan
        );
    }

    public CancellationToken Token => _source.Token;

    public bool IsCancellationRequested => _source.IsCancellationRequested;

    public void Dispose()
    {
        _timer.Dispose();
        _source.Dispose();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A callback failed. The token is cancelled regardless, and its owner observes the cancellation.
        }
        catch (ObjectDisposedException)
        {
            // Disposed before the timer fired: nothing waits on the token any more.
        }
    }
}
