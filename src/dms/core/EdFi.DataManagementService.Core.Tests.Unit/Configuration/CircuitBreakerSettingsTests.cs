// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
[Parallelizable]
public class Given_CircuitBreakerSettings_Are_Validated
{
    private static CircuitBreakerSettings Valid() =>
        new()
        {
            FailureRatio = 0.1,
            SamplingDurationSeconds = 120,
            MinimumThroughput = 20,
            BreakDurationSeconds = 30,
        };

    [Test]
    public void It_accepts_the_shipped_defaults()
    {
        var act = () => new CircuitBreakerSettings().Validate();

        act.Should().NotThrow();
    }

    [TestCase(0)]
    [TestCase(-0.1)]
    [TestCase(1.5)]
    public void It_rejects_a_failure_ratio_outside_the_unit_interval(double failureRatio)
    {
        var settings = Valid();
        settings.FailureRatio = failureRatio;

        var act = settings.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*FailureRatio*");
    }

    [Test]
    public void It_rejects_a_minimum_throughput_below_the_pipeline_floor()
    {
        var settings = Valid();
        settings.MinimumThroughput = 1;

        var act = settings.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*MinimumThroughput*");
    }

    /// <summary>
    /// The resilience pipeline rejects a duration of half a second or less, so validating it here
    /// is what makes the failure name the setting instead of surfacing later as a pipeline error.
    /// </summary>
    [TestCase(0.5)]
    [TestCase(0.1)]
    public void It_rejects_durations_the_resilience_pipeline_will_not_accept(double seconds)
    {
        var samplingSettings = Valid();
        samplingSettings.SamplingDurationSeconds = seconds;
        var breakSettings = Valid();
        breakSettings.BreakDurationSeconds = seconds;

        samplingSettings
            .Invoking(settings => settings.Validate())
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*SamplingDurationSeconds*");
        breakSettings
            .Invoking(settings => settings.Validate())
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*BreakDurationSeconds*");
    }

    /// <summary>
    /// The thresholds every environment template carried before the breaker was retuned. They are
    /// badly tuned rather than unusable, and refusing them would turn an upgrade into an outage for
    /// any deployment still carrying its own copy of the old values.
    /// </summary>
    [Test]
    public void It_accepts_the_thresholds_shipped_by_earlier_releases()
    {
        var settings = Valid();
        settings.FailureRatio = 0.01;
        settings.MinimumThroughput = 2;
        settings.SamplingDurationSeconds = 10;

        var act = settings.Validate;

        act.Should().NotThrow();
    }
}

[TestFixture]
[Parallelizable]
public class Given_CircuitBreakerSettings_Are_Checked_For_Tuning
{
    [Test]
    public void It_reports_nothing_for_the_shipped_defaults()
    {
        new CircuitBreakerSettings().GetTuningWarnings().Should().BeEmpty();
    }

    [Test]
    public void It_warns_when_a_single_failure_can_open_the_circuit()
    {
        CircuitBreakerSettings settings = new()
        {
            FailureRatio = 0.01,
            SamplingDurationSeconds = 10,
            MinimumThroughput = 2,
            BreakDurationSeconds = 30,
        };

        settings.GetTuningWarnings().Should().ContainSingle().Which.Should().Contain("single failed request");
    }

    /// <summary>
    /// The failure mode that is invisible in production: the circuit cannot open until the window
    /// holds MinimumThroughput calls, so a deployment quieter than that floor sheds nothing no
    /// matter how completely its backend is failing.
    /// </summary>
    [Test]
    public void It_warns_when_the_throughput_floor_outruns_plausible_traffic()
    {
        CircuitBreakerSettings settings = new()
        {
            FailureRatio = 0.5,
            SamplingDurationSeconds = 10,
            MinimumThroughput = 100,
            BreakDurationSeconds = 30,
        };

        settings
            .GetTuningWarnings()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("requests per second before the circuit can open");
    }
}
