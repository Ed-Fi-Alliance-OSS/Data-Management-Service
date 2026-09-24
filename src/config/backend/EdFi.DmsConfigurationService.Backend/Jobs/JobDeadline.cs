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
    /// A command timeout derived from the time left, at least 1 s because 0 would mean no timeout. The token from
    /// <see cref="CreateTokenSource"/> still cancels the command at the exact deadline.
    /// </summary>
    public int CommandTimeoutSeconds => Math.Max(1, (int)Math.Ceiling(Remaining.TotalSeconds));

    /// <summary>A token source cancelled when the time left runs out, linked to <paramref name="cancellationToken"/>.</summary>
    public CancellationTokenSource CreateTokenSource(CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Remaining);
        return source;
    }
}
