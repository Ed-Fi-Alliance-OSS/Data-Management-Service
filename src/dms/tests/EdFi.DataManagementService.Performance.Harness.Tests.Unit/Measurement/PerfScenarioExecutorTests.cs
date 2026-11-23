// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Offset_Policy
{
    [Test]
    public void It_resolves_the_zero_offset()
    {
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetZero, 500, 450_000).Should().Be(0);
    }

    [Test]
    public void It_resolves_the_shallow_offset_to_the_page_size()
    {
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetShallow, 25, 450_000).Should().Be(25);
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetShallow, 500, 450_000).Should().Be(500);
    }

    [Test]
    public void It_resolves_the_deep_offset_from_configuration()
    {
        PerfScenarioExecutor.OffsetFor(PerfScenarios.TraditionalOffsetDeep, 25, 450_000).Should().Be(450_000);
    }

    [Test]
    public void It_rejects_unknown_scenarios()
    {
        FluentActions
            .Invoking(() => PerfScenarioExecutor.OffsetFor("cursor-first-range", 25, 450_000))
            .Should()
            .Throw<ArgumentException>();
    }
}

[TestFixture]
public class Given_A_GetMany_Response_Body
{
    [Test]
    public void It_counts_the_returned_rows_from_the_observed_body()
    {
        PerfScenarioExecutor.CountReturnedRows("""[{"id":"a"},{"id":"b"},{"id":"c"}]""").Should().Be(3);
    }

    [Test]
    public void It_counts_an_empty_page_as_zero()
    {
        PerfScenarioExecutor.CountReturnedRows("[]").Should().Be(0);
    }

    [Test]
    public void It_rejects_a_non_array_body()
    {
        FluentActions
            .Invoking(() => PerfScenarioExecutor.CountReturnedRows("""{"error":"not a page"}"""))
            .Should()
            .Throw<PerfObservationException>();
    }
}

[TestFixture]
public class Given_The_Cell_Execution_Order
{
    private IReadOnlyList<PerfExecutedCell> _cells = null!;

    [SetUp]
    public void Setup()
    {
        _cells = PerfScenarioExecutor.CellsInExecutionOrder(450_000);
    }

    [Test]
    public void It_enumerates_the_full_matrix_in_canonical_order()
    {
        _cells
            .Select(cell => (cell.ScenarioId, cell.PageSize, cell.Offset))
            .Should()
            .Equal(
                (PerfScenarios.TraditionalOffsetZero, 25, 0),
                (PerfScenarios.TraditionalOffsetZero, 500, 0),
                (PerfScenarios.TraditionalOffsetShallow, 25, 25),
                (PerfScenarios.TraditionalOffsetShallow, 500, 500),
                (PerfScenarios.TraditionalOffsetDeep, 25, 450_000),
                (PerfScenarios.TraditionalOffsetDeep, 500, 450_000)
            );
    }
}

/// <summary>
/// Records every observer call in order, so tests can assert the hook sequence.
/// </summary>
internal sealed class RecordingReadObserver : IPerfReadObserver
{
    public List<string> Calls { get; } = [];

    public void OnAttempt(PerfExecutedCell cell, int iteration, bool isWarmup) =>
        Calls.Add($"attempt {iteration}{(isWarmup ? " warmup" : string.Empty)}");

    public Task VerifyAsync(PerfExecutedCell cell, int iteration, HttpStatusCode status, string body)
    {
        Calls.Add($"verify {iteration}");
        return Task.CompletedTask;
    }

    public void OnCompleted(PerfExecutedCell cell, int iteration, bool isWarmup) =>
        Calls.Add($"completed {iteration}{(isWarmup ? " warmup" : string.Empty)}");
}

[TestFixture]
public class Given_No_Observer
{
    private FakeReadPath _path = null!;
    private PerfCellMeasurement _measurement = null!;

    [SetUp]
    public async Task Setup()
    {
        _path = new FakeReadPath(ReadPageSamples.ZeroSmall);
        _measurement = await _path.MeasureAsync(2, 5, observer: null);
    }

    [Test]
    public void It_sends_one_request_per_iteration_to_the_cell_url()
    {
        _path.Urls.Should().HaveCount(7).And.AllBe("/data/ed-fi/students?limit=25&offset=0");
    }

    [Test]
    public void It_keeps_one_latency_and_one_driver_sample_per_measured_iteration()
    {
        _measurement.LatencyMs.SamplesMs.Should().HaveCount(5);
        _measurement
            .DriverExecuteMs.SamplesMs.Should()
            .HaveCount(5)
            .And.AllSatisfy(sample => sample.Should().Be(0.5));
    }

    [Test]
    public void It_reports_the_returned_rows_and_hydration_sql()
    {
        _measurement.ReturnedRows.Should().Be(25);
        _measurement.HydrationBatchSql.Should().Be("SELECT hydration batch");
    }

    [Test]
    public async Task It_still_rejects_a_request_with_two_database_commands()
    {
        FakeReadPath path = new(ReadPageSamples.ZeroSmall) { CommandsAt = request => request == 4 ? 2 : 1 };

        Func<Task> measure = () => path.MeasureAsync(2, 5, observer: null);

        await measure
            .Should()
            .ThrowAsync<PerfObservationException>()
            .WithMessage("*iteration 4: expected exactly one database command per request; observed 2*");
    }

    [Test]
    public async Task It_still_rejects_a_short_page()
    {
        FakeReadPath path = new(ReadPageSamples.ZeroSmall)
        {
            BodyAt = _ => ReadPageSamples.Page(0, 24).ToJsonString(),
        };

        Func<Task> measure = () => path.MeasureAsync(2, 5, observer: null);

        await measure.Should().ThrowAsync<PerfObservationException>().WithMessage("*returned rows 24*");
    }
}

[TestFixture]
public class Given_An_Observer_On_A_Successful_Cell
{
    [Test]
    public async Task It_calls_attempt_verify_and_completed_in_order_for_every_iteration()
    {
        RecordingReadObserver observer = new();

        await new FakeReadPath(ReadPageSamples.ZeroSmall).MeasureAsync(1, 2, observer);

        observer
            .Calls.Should()
            .Equal(
                "attempt 0 warmup",
                "verify 0",
                "completed 0 warmup",
                "attempt 1",
                "verify 1",
                "completed 1",
                "attempt 2",
                "verify 2",
                "completed 2"
            );
    }
}

[TestFixture]
public class Given_A_Request_That_Throws_At_Measured_Iteration_7
{
    private const int Warmup = 2;
    private PerfBenchmarkReadObserver _observer = null!;

    [SetUp]
    public async Task Setup()
    {
        _observer = new PerfBenchmarkReadObserver(
            ReadPageSamples.DeepOffset,
            Warmup,
            10,
            () => ReadPageSamples.Stamp(0)
        );
        FakeReadPath path = new(ReadPageSamples.ZeroSmall) { ThrowAt = Warmup + 7 };

        Func<Task> measure = () => path.MeasureAsync(Warmup, 10, _observer);

        await measure.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public void It_counts_the_attempt_recorded_before_dispatch_but_not_a_completion()
    {
        PerfReadCellCounts counts = _observer.Counts[0];

        counts.MeasuredAttempted.Should().Be(8);
        counts.MeasuredCompleted.Should().Be(7);
        counts.MeasuredFailed.Should().Be(1);
        counts.WarmupCompleted.Should().Be(Warmup);
        _observer.AllReadsCompleted.Should().BeFalse();
    }
}

[TestFixture]
public class Given_The_Executor_Check_Fails_After_Observer_Verification
{
    [Test]
    public async Task It_does_not_complete_that_request()
    {
        RecordingReadObserver observer = new();
        FakeReadPath path = new(ReadPageSamples.ZeroSmall) { CommandsAt = request => request == 1 ? 2 : 1 };

        Func<Task> measure = () => path.MeasureAsync(1, 3, observer);

        await measure.Should().ThrowAsync<PerfObservationException>();
        observer
            .Calls.Should()
            .Equal("attempt 0 warmup", "verify 0", "completed 0 warmup", "attempt 1", "verify 1");
    }
}
