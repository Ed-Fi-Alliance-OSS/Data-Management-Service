// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Warm steady-state latency measurement with the same semantics as the repo's existing write
/// measurement — unmeasured warmups first, one Stopwatch reading per iteration, nearest-rank
/// percentiles — but retaining every raw sample so artifacts let a later comparison recompute
/// any statistic. The operation must throw on failure; nothing here judges elapsed time,
/// because a timing assertion would be flaky on shared hardware and would not be evidence.
/// </summary>
public static class PerfLatencyMeasurement
{
    public static async Task<PerfLatencySummary> MeasureAsync(
        Func<int, Task> operationAsync,
        int warmupIterations,
        int measuredIterations
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(measuredIterations, 1);

        for (int iteration = 0; iteration < warmupIterations; iteration++)
        {
            await operationAsync(iteration);
        }

        double[] samplesMs = new double[measuredIterations];
        Stopwatch stopwatch = new();
        for (int iteration = 0; iteration < measuredIterations; iteration++)
        {
            stopwatch.Restart();
            await operationAsync(warmupIterations + iteration);
            stopwatch.Stop();
            samplesMs[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        return Summarize(samplesMs);
    }

    /// <summary>
    /// Summarizes an existing sample list, preserving the samples in their original
    /// (measurement) order so temporal effects stay visible in the artifacts. Also used for
    /// driver-observed command timings, which are collected rather than timed here.
    /// </summary>
    public static PerfLatencySummary Summarize(IReadOnlyList<double> samplesMs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(samplesMs.Count);

        double[] sorted = [.. samplesMs];
        Array.Sort(sorted);

        return new PerfLatencySummary(
            NearestRank(sorted, 50),
            NearestRank(sorted, 95),
            samplesMs.Average(),
            sorted[0],
            sorted[^1],
            [.. samplesMs]
        );
    }

    private static double NearestRank(double[] sorted, int percentile)
    {
        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Max(rank, 1) - 1];
    }
}
