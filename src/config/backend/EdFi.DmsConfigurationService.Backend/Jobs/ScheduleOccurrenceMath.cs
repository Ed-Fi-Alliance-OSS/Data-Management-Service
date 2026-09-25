// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The C# reference for the D-9 coalescing rule, used to compute expected values in tests. Production code
/// advances schedules in SQL, from one database-time sample.
/// </summary>
public static class ScheduleOccurrenceMath
{
    /// <summary>
    /// The smallest boundary <c>nextRunAt + j·interval</c> strictly after <paramref name="now"/>, or
    /// <paramref name="nextRunAt"/> unchanged when it is already after <paramref name="now"/>.
    /// </summary>
    public static DateTime Advance(DateTime nextRunAt, TimeSpan interval, DateTime now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        long elapsedTicks = now.Ticks - nextRunAt.Ticks;
        if (elapsedTicks < 0)
        {
            return nextRunAt;
        }

        long missedIntervals = elapsedTicks / interval.Ticks;
        return new DateTime(nextRunAt.Ticks + ((missedIntervals + 1) * interval.Ticks), nextRunAt.Kind);
    }
}
