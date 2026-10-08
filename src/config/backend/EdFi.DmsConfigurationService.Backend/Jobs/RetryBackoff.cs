// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The delay before a transiently failed job becomes eligible again (spec D-6): <c>base × 2^(attempt − 1)</c>,
/// capped at <c>maximum</c>.
/// </summary>
public static class RetryBackoff
{
    /// <summary>The delay after attempt <paramref name="attemptCount"/> (1 for the first attempt) failed.</summary>
    public static TimeSpan For(int attemptCount, TimeSpan baseDelay, TimeSpan maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, baseDelay);

        double factor = Math.Pow(2, attemptCount - 1);
        double ticks = baseDelay.Ticks * factor;
        return ticks >= maximum.Ticks ? maximum : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>The same delay in whole seconds, rounded up, as the lease repository takes it.</summary>
    public static int SecondsFor(int attemptCount, TimeSpan baseDelay, TimeSpan maximum) =>
        (int)Math.Ceiling(For(attemptCount, baseDelay, maximum).TotalSeconds);
}
