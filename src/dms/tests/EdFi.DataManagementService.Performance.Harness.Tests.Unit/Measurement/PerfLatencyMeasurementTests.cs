// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_A_Summarized_Sample_Set
{
    private PerfLatencySummary _summary = null!;

    [SetUp]
    public void Setup()
    {
        _summary = PerfLatencyMeasurement.Summarize([30.0, 10.0, 40.0, 20.0]);
    }

    [Test]
    public void It_takes_the_nearest_rank_p50()
    {
        // ceil(0.50 * 4) = rank 2 of the sorted samples.
        _summary.P50Ms.Should().Be(20.0);
    }

    [Test]
    public void It_takes_the_nearest_rank_p95()
    {
        // ceil(0.95 * 4) = rank 4 of the sorted samples.
        _summary.P95Ms.Should().Be(40.0);
    }

    [Test]
    public void It_computes_mean_min_and_max()
    {
        _summary.MeanMs.Should().Be(25.0);
        _summary.MinMs.Should().Be(10.0);
        _summary.MaxMs.Should().Be(40.0);
    }

    [Test]
    public void It_retains_the_samples_in_measurement_order()
    {
        _summary.SamplesMs.Should().Equal(30.0, 10.0, 40.0, 20.0);
    }
}

[TestFixture]
public class Given_The_Epic_Iteration_Count
{
    private PerfLatencySummary _summary = null!;

    [SetUp]
    public void Setup()
    {
        _summary = PerfLatencyMeasurement.Summarize([
            .. Enumerable.Range(1, 30).Select(value => (double)value),
        ]);
    }

    [Test]
    public void It_takes_rank_fifteen_as_p50()
    {
        _summary.P50Ms.Should().Be(15.0);
    }

    [Test]
    public void It_takes_rank_twenty_nine_as_p95()
    {
        // ceil(0.95 * 30) = 28.5 rounded up to rank 29.
        _summary.P95Ms.Should().Be(29.0);
    }
}

[TestFixture]
public class Given_A_Single_Sample
{
    [Test]
    public void It_reports_that_sample_for_every_statistic()
    {
        PerfLatencySummary summary = PerfLatencyMeasurement.Summarize([7.5]);
        summary.P50Ms.Should().Be(7.5);
        summary.P95Ms.Should().Be(7.5);
        summary.MeanMs.Should().Be(7.5);
        summary.MinMs.Should().Be(7.5);
        summary.MaxMs.Should().Be(7.5);
    }
}

[TestFixture]
public class Given_An_Empty_Sample_Set
{
    [Test]
    public void It_rejects_the_summary()
    {
        FluentActions
            .Invoking(() => PerfLatencyMeasurement.Summarize([]))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }
}

[TestFixture]
public class Given_A_Measured_Operation
{
    private List<int> _observedIterations = null!;
    private PerfLatencySummary _summary = null!;

    [SetUp]
    public async Task Setup()
    {
        _observedIterations = [];
        _summary = await PerfLatencyMeasurement.MeasureAsync(
            iteration =>
            {
                _observedIterations.Add(iteration);
                return Task.CompletedTask;
            },
            warmupIterations: 2,
            measuredIterations: 3
        );
    }

    [Test]
    public void It_runs_warmups_before_measured_iterations()
    {
        _observedIterations.Should().Equal(0, 1, 2, 3, 4);
    }

    [Test]
    public void It_retains_one_sample_per_measured_iteration()
    {
        _summary.SamplesMs.Should().HaveCount(3);
        _summary.SamplesMs.Should().OnlyContain(sample => sample >= 0);
    }
}

[TestFixture]
public class Given_A_Failing_Operation
{
    [Test]
    public async Task It_propagates_the_failure()
    {
        await FluentActions
            .Invoking(() =>
                PerfLatencyMeasurement.MeasureAsync(
                    _ => throw new InvalidOperationException("request failed"),
                    warmupIterations: 1,
                    measuredIterations: 1
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("request failed");
    }
}

[TestFixture]
public class Given_Invalid_Iteration_Counts
{
    [Test]
    public async Task It_rejects_negative_warmups()
    {
        await FluentActions
            .Invoking(() => PerfLatencyMeasurement.MeasureAsync(_ => Task.CompletedTask, -1, 1))
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task It_rejects_zero_measured_iterations()
    {
        await FluentActions
            .Invoking(() => PerfLatencyMeasurement.MeasureAsync(_ => Task.CompletedTask, 0, 0))
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
    }
}
