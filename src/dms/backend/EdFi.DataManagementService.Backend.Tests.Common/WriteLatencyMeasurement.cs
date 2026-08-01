// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>One scenario's warm steady-state latency, in milliseconds.</summary>
public sealed record WriteLatencySample(
    string Scenario,
    int Iterations,
    double MedianMilliseconds,
    double P95Milliseconds,
    double MeanMilliseconds
)
{
    public string ToReportLine() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Scenario} | n={Iterations} | median {MedianMilliseconds:F2} ms | p95 {P95Milliseconds:F2} ms | mean {MeanMilliseconds:F2} ms"
        );
}

/// <summary>
/// Measures warm, steady-state, end-to-end latency of a single write operation against a live provider.
/// </summary>
/// <remarks>
/// <para>
/// This is the acceptance measurement for the write-path batching story, which cannot be a command count:
/// merged command text varies with per-table row counts, so co-batching could trade saved wire time for
/// repeated planning (PostgreSQL auto-prepares only after repeated identical text; SQL Server's analogue is
/// plan-cache pressure). Warmup iterations exist precisely so the measured window observes prepared
/// statements and a populated plan cache rather than first-execution planning.
/// </para>
/// <para>
/// Nothing here asserts on elapsed time. A timing assertion in the test suite would be flaky on shared
/// hardware and would not be evidence; the harness reports percentiles for a human comparison against the
/// recorded baseline, and fails only if an iteration did not produce the expected result.
/// </para>
/// </remarks>
public static class WriteLatencyMeasurement
{
    /// <summary>
    /// Runs <paramref name="warmupIterations"/> unmeasured iterations, then times
    /// <paramref name="measuredIterations"/> of them. <paramref name="operationAsync"/> receives the
    /// iteration ordinal and must throw if the operation did not succeed, so a scenario cannot report a
    /// fast time for work it never did.
    /// </summary>
    public static async Task<WriteLatencySample> MeasureAsync(
        string scenario,
        Func<int, Task> operationAsync,
        int warmupIterations = 20,
        int measuredIterations = 100
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentNullException.ThrowIfNull(operationAsync);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(measuredIterations, 1);

        for (var iteration = 0; iteration < warmupIterations; iteration++)
        {
            await operationAsync(iteration).ConfigureAwait(false);
        }

        var elapsedMilliseconds = new double[measuredIterations];

        for (var iteration = 0; iteration < measuredIterations; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            await operationAsync(warmupIterations + iteration).ConfigureAwait(false);
            stopwatch.Stop();
            elapsedMilliseconds[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(elapsedMilliseconds);

        return new WriteLatencySample(
            scenario,
            measuredIterations,
            Percentile(elapsedMilliseconds, 0.50),
            Percentile(elapsedMilliseconds, 0.95),
            elapsedMilliseconds.Average()
        );
    }

    /// <summary>
    /// The nearest-rank percentile of an ascending sample. Nearest-rank rather than interpolated because
    /// the sample is small enough that interpolation would invent a value between two observations.
    /// </summary>
    private static double Percentile(double[] ascendingSample, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * ascendingSample.Length) - 1;

        return ascendingSample[Math.Clamp(rank, 0, ascendingSample.Length - 1)];
    }
}
