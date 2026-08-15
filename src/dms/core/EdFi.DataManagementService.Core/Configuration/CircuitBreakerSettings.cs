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
    /// Rejects the two misconfigurations that leave the breaker unable to do its job in opposite
    /// directions: thresholds so low that one anomalous failure opens the circuit, and a throughput
    /// floor so high relative to the sampling window that the circuit can never open at all. Both
    /// are silent at runtime - the first sheds traffic nothing was wrong with, the second sheds
    /// nothing while the backend is down - so they are caught at startup instead.
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

        if (BreakDurationSeconds <= 0)
        {
            throw new InvalidOperationException("CircuitBreaker:BreakDurationSeconds must be > 0");
        }

        if (FailureRatio * MinimumThroughput <= 1)
        {
            throw new InvalidOperationException(
                "CircuitBreaker:FailureRatio multiplied by CircuitBreaker:MinimumThroughput must be "
                    + "greater than 1, otherwise a single failure opens the circuit"
            );
        }
    }
}
