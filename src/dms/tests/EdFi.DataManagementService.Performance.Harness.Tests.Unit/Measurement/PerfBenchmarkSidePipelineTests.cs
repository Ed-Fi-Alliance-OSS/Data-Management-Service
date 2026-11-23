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

/// <summary>
/// Fake side steps over the committed profile's smoke workload (reads 5/30, writes 5/20,
/// deep offset 9,000). Each step can be made to fail; the reads drive the observer through
/// every planned request the way the executor would.
/// </summary>
public sealed class FakeSideSteps
{
    public static readonly PerfBenchmarkConnectionSettings Connection = new(true, 0, 100, true, 3, 256);

    public Exception? PrepareFailure { get; init; }

    public Exception? ReadFailureAfterAllReads { get; init; }

    /// <summary>
    /// The absolute iteration of the first cell at which the read request throws.
    /// </summary>
    public int? ReadThrowsAt { get; init; }

    public int? WriteThrowsAt { get; init; }

    public IReadOnlyList<string> MismatchesBefore { get; init; } =
    ["updated-other-names-on-targets: expected 25, got 0."];

    public IReadOnlyList<string> MismatchesAfter { get; init; } = [];

    public bool WritesRan { get; private set; }

    public static PerfBenchmarkSidePlan Plan(string sideDirectory) =>
        new(
            PerfProvider.Postgresql,
            PerfBenchmarkProfileLoader.LoadFile(PerfBenchmarkProfileLoader.DefaultPath),
            Smoke: true,
            new PerfBenchmarkSideIdentity("run-0001", PerfBenchmarkSideResult.BaselineRole, 1, 1),
            PerfSlowdownSpec.None,
            sideDirectory,
            new string('b', 40)
        );

    public PerfBenchmarkSideSteps Steps(PerfBenchmarkSidePlan plan)
    {
        int second = 0;
        return new PerfBenchmarkSideSteps(
            () =>
                PrepareFailure is null
                    ? Task.FromResult(
                        new PerfBenchmarkSideFacts(
                            Connection,
                            "schema-hash",
                            "Warning",
                            true,
                            new PerfBenchmarkInstrumentation(true, true)
                        )
                    )
                    : throw PrepareFailure,
            async observer =>
            {
                PerfBenchmarkSideWorkload workload = plan.Workload;
                int total = workload.Reads.WarmupIterations + workload.Reads.MeasuredIterations;
                foreach (
                    PerfExecutedCell cell in PerfScenarioExecutor.CellsInExecutionOrder(workload.DeepOffset)
                )
                {
                    for (int iteration = 0; iteration < total; iteration++)
                    {
                        bool isWarmup = iteration < workload.Reads.WarmupIterations;
                        observer.OnAttempt(cell, iteration, isWarmup);
                        if (iteration == ReadThrowsAt)
                        {
                            throw new HttpRequestException("connection reset");
                        }

                        await observer.VerifyAsync(
                            cell,
                            iteration,
                            HttpStatusCode.OK,
                            ReadPageSamples.Page(cell.Offset, cell.PageSize).ToJsonString()
                        );
                        observer.OnCompleted(cell, iteration, isWarmup);
                    }
                }

                return ReadFailureAfterAllReads is null ? "read-run" : throw ReadFailureAfterAllReads;
            },
            outcomes =>
            {
                WritesRan = true;
                PerfBenchmarkSideWorkload workload = plan.Workload;
                int total = workload.Writes.WarmupIterations + workload.Writes.MeasuredIterations;
                for (int iteration = 0; iteration < total; iteration++)
                {
                    outcomes.OnAttempt(iteration);
                    if (iteration == WriteThrowsAt)
                    {
                        throw new HttpRequestException("connection reset");
                    }

                    outcomes.OnResponse(iteration, HttpStatusCode.NoContent, true, 9, string.Empty);
                }

                return Task.FromResult(
                    new PerfBenchmarkWriteOutcome(
                        outcomes.ToResult(
                            PerfLatencyMeasurement.Summarize([
                                .. Enumerable.Repeat(2.0, workload.Writes.MeasuredIterations),
                            ])
                        ),
                        MismatchesBefore,
                        MismatchesAfter
                    )
                );
            },
            () => PerfBenchmarkScenarios.AllIds.ToDictionary(id => id, _ => 0),
            () => ReadPageSamples.Stamp(++second)
        );
    }
}

public abstract class SidePipelineFixture
{
    private string _sideDirectory = null!;

    protected PerfBenchmarkSideResult Written { get; private set; } = null!;

    protected Exception? Thrown { get; private set; }

    protected FakeSideSteps Fake { get; private set; } = null!;

    protected abstract FakeSideSteps CreateFake();

    [SetUp]
    public async Task Setup()
    {
        _sideDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-side-pipeline-tests",
            Guid.NewGuid().ToString("N")
        );
        Fake = CreateFake();
        PerfBenchmarkSidePlan plan = FakeSideSteps.Plan(_sideDirectory);
        try
        {
            await PerfBenchmarkSidePipeline.RunAsync(plan, Fake.Steps(plan));
        }
        catch (Exception exception)
        {
            Thrown = exception;
        }

        Written = PerfBenchmarkSideResults.Read(
            await File.ReadAllTextAsync(Path.Combine(_sideDirectory, PerfBenchmarkSideResult.FileName))
        );
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_sideDirectory, recursive: true);
    }
}

[TestFixture]
public class Given_A_Side_That_Completes : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() => new();

    [Test]
    public void It_writes_a_completed_side_result()
    {
        Thrown.Should().BeNull();
        Written.Status.Should().Be(new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Completed));
        Written.RunId.Should().Be("run-0001");
        Written.Workload.Fixture.Should().Be("smoke-10k");
        Written.EffectiveConnection.Should().Be(FakeSideSteps.Connection);
        Written.Write.MeasuredCompleted.Should().Be(20);
        Written.Write.MeasuredWindow.Should().NotBeNull();
        Written.ReadCells.Should().AllSatisfy(cell => cell.MeasuredWindow.Should().NotBeNull());
    }
}

[TestFixture]
public class Given_A_Plan_Replay_Failure_After_Successful_Reads : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() =>
        new() { ReadFailureAfterAllReads = new PerfObservationException("plan replay failed for deep/500") };

    [Test]
    public void It_rethrows_the_failure()
    {
        Thrown
            .Should()
            .BeOfType<PerfObservationException>()
            .Which.Message.Should()
            .Be("plan replay failed for deep/500");
    }

    [Test]
    public void It_fails_the_side_in_the_post_read_phase_keeping_the_reason()
    {
        Written
            .Status.Should()
            .Be(
                new PerfBenchmarkSideStatus(
                    PerfBenchmarkSideStatus.Failed,
                    PerfBenchmarkSideStatus.PostReadPhase,
                    "plan replay failed for deep/500"
                )
            );
    }

    [Test]
    public void It_records_every_planned_read_as_completed_with_none_failed()
    {
        Written
            .ReadCells.Should()
            .AllSatisfy(cell =>
            {
                cell.WarmupCompleted.Should().Be(5);
                cell.MeasuredCompleted.Should().Be(30);
                cell.WarmupFailed.Should().Be(0);
                cell.MeasuredFailed.Should().Be(0);
            });
    }

    [Test]
    public void It_never_runs_the_writes()
    {
        Fake.WritesRan.Should().BeFalse();
        Written.Write.WarmupAttempted.Should().Be(0);
        Written.Write.MeasuredAttempted.Should().Be(0);
    }
}

[TestFixture]
public class Given_A_Read_That_Throws : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() => new() { ReadThrowsAt = 12 };

    [Test]
    public void It_fails_the_side_in_the_reads_phase_with_exact_counts()
    {
        Written.Status.Phase.Should().Be(PerfBenchmarkSideStatus.ReadsPhase);
        Written.Status.Reason.Should().Be("connection reset");
        Written.ReadCells[0].MeasuredAttempted.Should().Be(8);
        Written.ReadCells[0].MeasuredCompleted.Should().Be(7);
        Written.ReadCells[0].MeasuredFailed.Should().Be(1);
        Written.ReadCells.Skip(1).Should().AllSatisfy(cell => cell.WarmupAttempted.Should().Be(0));
    }
}

[TestFixture]
public class Given_A_Failure_Before_Any_Read : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() =>
        new() { PrepareFailure = new InvalidOperationException("no EffectiveSchemaHash") };

    [Test]
    public void It_fails_the_side_in_the_reads_phase_without_facts()
    {
        Written.Status.Phase.Should().Be(PerfBenchmarkSideStatus.ReadsPhase);
        Written.OwnershipPublished.Should().BeFalse();
        Written.EffectiveLogLevel.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_A_Write_That_Throws : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() => new() { WriteThrowsAt = 12 };

    [Test]
    public void It_fails_the_side_in_the_writes_phase_with_exact_counts()
    {
        Written.Status.Phase.Should().Be(PerfBenchmarkSideStatus.WritesPhase);
        Written.Write.WarmupCompleted.Should().Be(5);
        Written.Write.MeasuredAttempted.Should().Be(8);
        Written.Write.MeasuredCompleted.Should().Be(7);
        Written.Write.Failed.Should().Be(1);
        Written.Write.LatencyMs.Should().BeNull();
    }
}

[TestFixture]
public class Given_A_Write_Verification_That_Passes_Before_Any_Write : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() => new() { MismatchesBefore = [] };

    [Test]
    public void It_fails_the_side_because_the_verification_is_vacuous()
    {
        Written.Status.Phase.Should().Be(PerfBenchmarkSideStatus.WritesPhase);
        Written.Status.Reason.Should().Contain("passed before any write");
    }
}

[TestFixture]
public class Given_A_Write_Verification_That_Fails_After_The_Writes : SidePipelineFixture
{
    protected override FakeSideSteps CreateFake() =>
        new() { MismatchesAfter = ["updated-other-names-total: expected 25, got 24."] };

    [Test]
    public void It_fails_the_side_naming_the_mismatch()
    {
        Written.Status.Phase.Should().Be(PerfBenchmarkSideStatus.WritesPhase);
        Written.Status.Reason.Should().Contain("updated-other-names-total: expected 25, got 24.");
    }
}
