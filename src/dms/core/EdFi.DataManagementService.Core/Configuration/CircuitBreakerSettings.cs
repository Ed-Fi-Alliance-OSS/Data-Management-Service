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
    /// Rejects only values the resilience pipeline itself cannot accept, so the failure names the
    /// setting here instead of surfacing later as a pipeline-construction error. Values that are
    /// merely badly tuned are reported by <see cref="GetTuningWarnings"/> rather than thrown:
    /// startup is the wrong place to refuse a configuration that works, and a deployment carrying
    /// thresholds from an earlier release must be able to upgrade and then retune.
    /// </summary>
    public void Validate()
    {
        if (FailureRatio is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                "CircuitBreaker:FailureRatio must be greater than 0 and at most 1"
            );
        }

        if (SamplingDurationSeconds <= 0.5)
        {
            throw new InvalidOperationException(
                "CircuitBreaker:SamplingDurationSeconds must be greater than 0.5"
            );
        }

        // Polly's own lower bound; a smaller value is rejected by the strategy itself.
        if (MinimumThroughput < 2)
        {
            throw new InvalidOperationException("CircuitBreaker:MinimumThroughput must be >= 2");
        }

        // Polly's own lower bound, matched here so an invalid value is rejected with a named setting
        // rather than surfacing later as a pipeline-construction failure.
        if (BreakDurationSeconds <= 0.5)
        {
            throw new InvalidOperationException(
                "CircuitBreaker:BreakDurationSeconds must be greater than 0.5"
            );
        }
    }

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
