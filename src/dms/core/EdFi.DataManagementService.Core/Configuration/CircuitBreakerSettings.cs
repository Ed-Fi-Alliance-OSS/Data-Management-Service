// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

internal class CircuitBreakerSettings
{
    /// <summary>
    /// Fraction of sampled calls that must fail before the circuit opens.
    /// </summary>
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>
    /// Rolling window over which the failure ratio is assessed. Long enough that
    /// <see cref="MinimumThroughput"/> is reachable at a low sustained request rate.
    /// </summary>
    public double SamplingDurationSeconds { get; set; } = 120;

    /// <summary>
    /// Calls required inside the window before the circuit may open at all.
    /// </summary>
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>
    /// How long the circuit stays open before admitting a trial call.
    /// </summary>
    public double BreakDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Rejects values the resilience pipeline cannot accept, so the failure names the setting here
    /// instead of surfacing later as a pipeline-construction error. The bounds mirror the ranges
    /// Polly declares on its circuit-breaker options - both durations
    /// <c>[0.5s, 24h]</c> inclusive, throughput <c>[2, int.MaxValue]</c>, ratio <c>[0, 1]</c> - with
    /// one deliberate exception: a ratio of exactly 0 is rejected here although Polly permits it,
    /// because it asks the breaker to open on a window with no failures in it at all.
    /// Values that are merely badly tuned are reported by <see cref="GetTuningWarnings"/> rather
    /// than thrown: startup is the wrong place to refuse a configuration that works, and a
    /// deployment carrying thresholds from an earlier release must be able to upgrade and retune.
    /// </summary>
    public void Validate()
    {
        // Ordered comparisons are all false against NaN, so a non-finite value would slip past every
        // bound below and fail later inside TimeSpan or the strategy builder, which is exactly the
        // deferred, unnamed failure this method exists to prevent.
        if (!double.IsFinite(FailureRatio) || FailureRatio is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                "CircuitBreaker:FailureRatio must be a finite number greater than 0 and at most 1"
            );
        }

        if (!IsWithinPollyDurationRange(SamplingDurationSeconds))
        {
            throw new InvalidOperationException(
                "CircuitBreaker:SamplingDurationSeconds must be a finite number from 0.5 to "
                    + $"{MaximumDurationSeconds} inclusive"
            );
        }

        if (MinimumThroughput < 2)
        {
            throw new InvalidOperationException("CircuitBreaker:MinimumThroughput must be >= 2");
        }

        if (!IsWithinPollyDurationRange(BreakDurationSeconds))
        {
            throw new InvalidOperationException(
                "CircuitBreaker:BreakDurationSeconds must be a finite number from 0.5 to "
                    + $"{MaximumDurationSeconds} inclusive"
            );
        }
    }

    /// <summary>
    /// Polly declares both circuit-breaker durations as an inclusive range from half a second to one
    /// day. An out-of-range value is rejected by the strategy builder, so bounding it here is what
    /// turns that into a failure naming the setting. Note the bounds are inclusive at both ends -
    /// Polly's prose says "greater than 0.5 seconds" while the range attribute it actually enforces
    /// accepts exactly 0.5.
    /// </summary>
    private const double MaximumDurationSeconds = 86400;

    private static bool IsWithinPollyDurationRange(double seconds) =>
        double.IsFinite(seconds) && seconds is >= 0.5 and <= MaximumDurationSeconds;

    /// <summary>
    /// Configurations the pipeline accepts but that leave the breaker unable to do its job. Both
    /// are silent at runtime, which is why they are worth saying out loud at startup: the first
    /// sheds all traffic over a single anomaly, the second sheds nothing at all while the backend
    /// is down. Reported rather than thrown so an upgrade never fails on a working deployment.
    /// </summary>
    public IEnumerable<string> GetTuningWarnings()
    {
        if (FailureRatio * MinimumThroughput <= 1)
        {
            yield return $"FailureRatio ({FailureRatio}) multiplied by MinimumThroughput "
                + $"({MinimumThroughput}) is {FailureRatio * MinimumThroughput}, so a single failed "
                + "request can open the circuit and refuse all traffic for "
                + $"{BreakDurationSeconds}s. Raise either value so the product exceeds 1.";
        }

        double throughputFloorPerSecond = MinimumThroughput / SamplingDurationSeconds;
        if (throughputFloorPerSecond > 1)
        {
            yield return $"MinimumThroughput ({MinimumThroughput}) over SamplingDurationSeconds "
                + $"({SamplingDurationSeconds}) requires a sustained {throughputFloorPerSecond:0.##} "
                + "requests per second before the circuit can open at all; below that rate it stays "
                + "closed even while the backend is failing every request. Lengthen the sampling "
                + "window or lower the throughput floor.";
        }
    }
}
