// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class SideResultSamples
{
    public static readonly PerfMeasuredWindow Window = new(
        new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 23, 12, 0, 3, TimeSpan.Zero),
        125.5
    );

    public static readonly IReadOnlyList<string> ReadCellIds = [.. PerfBenchmarkScenarios.AllIds.SkipLast(1)];

    /// <summary>
    /// A completed smoke-workload side: reads 5/30, writes 5/20, no injection.
    /// </summary>
    public static PerfBenchmarkSideResult Completed() =>
        new(
            PerfBenchmarkSideResult.CurrentSchemaVersion,
            "run-0001",
            new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Completed),
            PerfBenchmarkSideResult.BaselineRole,
            Pair: 1,
            Position: 1,
            "0.1.0",
            new string('a', 64),
            new PerfBenchmarkSideWorkload(
                "postgresql",
                "smoke-10k",
                9_000,
                new PerfBenchmarkIterations(5, 30),
                new PerfBenchmarkIterations(5, 20)
            ),
            [.. ReadCellIds.Select(id => new PerfReadCellCounts(id, 5, 5, 0, 30, 30, 0, Window))],
            new PerfWriteScenarioResult(
                PerfBenchmarkScenarios.StudentPutCollectionUpdate,
                5,
                5,
                20,
                20,
                0,
                [new PerfCommandCountFrequency(9, 20)],
                Window,
                PerfLatencyMeasurement.Summarize([
                    .. Enumerable.Range(1, 20).Select(sample => (double)sample),
                ])
            ),
            new PerfBenchmarkConnectionSettings(true, 0, 100, true, 3, 256),
            "effective-schema-hash",
            "Warning",
            OwnershipPublished: true,
            new PerfBenchmarkInstrumentation(QueryRecorder: true, DriverCommandObserver: true),
            new PerfBenchmarkInjection(
                new Dictionary<string, decimal>(),
                PerfBenchmarkScenarios.AllIds.ToDictionary(id => id, _ => 0)
            )
        );

    /// <summary>
    /// A side that failed at measured read 8 of the first cell; nothing after it ran.
    /// </summary>
    public static PerfBenchmarkSideResult FailedInReads() =>
        Completed() with
        {
            Status = new PerfBenchmarkSideStatus(
                PerfBenchmarkSideStatus.Failed,
                PerfBenchmarkSideStatus.ReadsPhase,
                "traditional-offset-zero/25 iteration 12: item 0 must be student perf-000000001"
            ),
            ReadCells =
            [
                new PerfReadCellCounts(ReadCellIds[0], 5, 5, 0, 8, 7, 1),
                .. ReadCellIds.Skip(1).Select(id => new PerfReadCellCounts(id, 0, 0, 0, 0, 0, 0)),
            ],
            Write = new PerfWriteScenarioResult(
                PerfBenchmarkScenarios.StudentPutCollectionUpdate,
                0,
                0,
                0,
                0,
                0,
                []
            ),
        };

    public static IReadOnlyList<string> Errors(PerfBenchmarkSideResult result) =>
        PerfBenchmarkSideResults.Validate(result);
}

[TestFixture]
public class Given_A_Side_Result
{
    [Test]
    public void It_round_trips_a_completed_side()
    {
        string json = PerfBenchmarkSideResults.Serialize(SideResultSamples.Completed());

        PerfBenchmarkSideResults.Serialize(PerfBenchmarkSideResults.Read(json)).Should().Be(json);
    }

    [Test]
    public void It_round_trips_a_failed_side_with_partial_counts()
    {
        string json = PerfBenchmarkSideResults.Serialize(SideResultSamples.FailedInReads());

        PerfBenchmarkSideResult read = PerfBenchmarkSideResults.Read(json);

        read.Status.Phase.Should().Be(PerfBenchmarkSideStatus.ReadsPhase);
        read.ReadCells[0].MeasuredFailed.Should().Be(1);
        read.Write.LatencyMs.Should().BeNull();
    }

    [Test]
    public void It_writes_lf_only_camel_case_json()
    {
        string json = PerfBenchmarkSideResults.Serialize(SideResultSamples.Completed());

        json.Should().NotContain("\r").And.Contain("\"schemaVersion\": \"benchmark-side 1.0.0\"");
    }

    [Test]
    public void It_requires_the_schema_version()
    {
        JsonObject json = JsonNode
            .Parse(PerfBenchmarkSideResults.Serialize(SideResultSamples.Completed()))!
            .AsObject();
        json.Remove("schemaVersion");

        Action read = () => PerfBenchmarkSideResults.Read(json.ToJsonString());

        read.Should().Throw<PerfArtifactValidationException>().WithMessage("*schemaVersion*");
    }

    [Test]
    public void It_rejects_another_schema_version()
    {
        SideResultSamples
            .Errors(SideResultSamples.Completed() with { SchemaVersion = "benchmark-side 2.0.0" })
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("schemaVersion");
    }

    [Test]
    public void It_rejects_an_unknown_member()
    {
        JsonObject json = JsonNode
            .Parse(PerfBenchmarkSideResults.Serialize(SideResultSamples.Completed()))!
            .AsObject();
        json["injectionSlowdown"] = 1;

        Action read = () => PerfBenchmarkSideResults.Read(json.ToJsonString());

        read.Should().Throw<PerfArtifactValidationException>().WithMessage("*injectionSlowdown*");
    }

    [Test]
    public void It_requires_a_phase_and_reason_for_a_failed_side()
    {
        SideResultSamples
            .Errors(
                SideResultSamples.FailedInReads() with
                {
                    Status = new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Failed),
                }
            )
            .Should()
            .Contain(error => error.Contains("status.phase"))
            .And.Contain(error => error.Contains("status.reason"));
    }

    [Test]
    public void It_rejects_an_unknown_failure_phase()
    {
        SideResultSamples
            .Errors(
                SideResultSamples.FailedInReads() with
                {
                    Status = new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Failed, "setup", "boom"),
                }
            )
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("status.phase");
    }

    [Test]
    public void It_rejects_a_completed_side_carrying_a_reason()
    {
        SideResultSamples
            .Errors(
                SideResultSamples.Completed() with
                {
                    Status = new PerfBenchmarkSideStatus(PerfBenchmarkSideStatus.Completed, Reason: "boom"),
                }
            )
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no phase or reason");
    }

    [Test]
    public void It_requires_completed_to_equal_measured_for_every_read_cell_on_success()
    {
        PerfBenchmarkSideResult result = SideResultSamples.Completed();
        PerfBenchmarkSideResult shortCell = result with
        {
            ReadCells =
            [
                result.ReadCells[0] with
                {
                    MeasuredCompleted = 29,
                    MeasuredAttempted = 29,
                },
                .. result.ReadCells.Skip(1),
            ],
        };

        SideResultSamples
            .Errors(shortCell)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("readCells.traditional-offset-zero/25");
    }

    [Test]
    public void It_requires_completed_to_equal_measured_for_the_write_on_success()
    {
        PerfBenchmarkSideResult result = SideResultSamples.Completed();

        SideResultSamples
            .Errors(
                result with
                {
                    Write = result.Write with { MeasuredAttempted = 19, MeasuredCompleted = 19 },
                }
            )
            .Should()
            .Contain(error => error.StartsWith("write:"));
    }

    [Test]
    public void It_requires_one_write_latency_sample_per_measured_write()
    {
        PerfBenchmarkSideResult result = SideResultSamples.Completed();
        PerfBenchmarkSideResult missingSample = result with
        {
            Write = result.Write with { LatencyMs = PerfLatencyMeasurement.Summarize([1.0, 2.0]) },
        };

        SideResultSamples
            .Errors(missingSample)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("write.latencyMs must hold exactly 20 samples; got 2");
    }

    [Test]
    public void It_requires_failed_to_equal_attempted_minus_completed()
    {
        PerfBenchmarkSideResult result = SideResultSamples.FailedInReads();
        PerfBenchmarkSideResult inconsistent = result with
        {
            ReadCells = [result.ReadCells[0] with { MeasuredFailed = 0 }, .. result.ReadCells.Skip(1)],
        };

        SideResultSamples
            .Errors(inconsistent)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("failed must equal attempted - completed");
    }

    [Test]
    public void It_requires_every_read_cell_in_execution_order()
    {
        PerfBenchmarkSideResult result = SideResultSamples.Completed();

        SideResultSamples
            .Errors(result with { ReadCells = [.. result.ReadCells.Reverse()] })
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("readCells must list exactly");
    }

    [Test]
    public void It_validates_the_side_identity()
    {
        SideResultSamples
            .Errors(
                SideResultSamples.Completed() with
                {
                    RunId = " ",
                    Role = "control",
                    Position = 3,
                    ProfileSha256 = "abc",
                }
            )
            .Should()
            .HaveCount(4);
    }

    [Test]
    public void It_keeps_the_injection_apart_from_the_instrumentation()
    {
        JsonObject json = JsonNode
            .Parse(PerfBenchmarkSideResults.Serialize(SideResultSamples.Completed()))!
            .AsObject();

        json["instrumentation"]!
            .AsObject()
            .Select(member => member.Key)
            .Should()
            .Equal("queryRecorder", "driverCommandObserver");
        json["injection"]!
            .AsObject()
            .Select(member => member.Key)
            .Should()
            .Equal("factors", "affectedRequests");
    }

    [Test]
    public void It_accepts_a_candidate_injection_with_its_affected_counts()
    {
        PerfBenchmarkSideResult candidate = SideResultSamples.Completed() with
        {
            Role = PerfBenchmarkSideResult.CandidateRole,
            Injection = new PerfBenchmarkInjection(
                new Dictionary<string, decimal>
                {
                    [PerfBenchmarkScenarios.StudentPutCollectionUpdate] = 1.0m,
                },
                PerfBenchmarkScenarios.AllIds.ToDictionary(
                    id => id,
                    id => id == PerfBenchmarkScenarios.StudentPutCollectionUpdate ? 25 : 0
                )
            ),
        };

        SideResultSamples.Errors(candidate).Should().BeEmpty();
    }

    [Test]
    public void It_rejects_affected_requests_on_a_scenario_that_was_not_injected()
    {
        PerfBenchmarkSideResult result = SideResultSamples.Completed() with
        {
            Injection = new PerfBenchmarkInjection(
                new Dictionary<string, decimal>(),
                PerfBenchmarkScenarios.AllIds.ToDictionary(id => id, _ => 1)
            ),
        };

        SideResultSamples
            .Errors(result)
            .Should()
            .HaveCount(7)
            .And.AllSatisfy(error => error.Should().Contain("only an injected scenario"));
    }

    [Test]
    public void It_rejects_an_injection_factor_out_of_bounds()
    {
        PerfBenchmarkSideResult result = SideResultSamples.Completed() with
        {
            Injection = new PerfBenchmarkInjection(
                new Dictionary<string, decimal> { [PerfBenchmarkScenarios.StudentPutCollectionUpdate] = 6m },
                PerfBenchmarkScenarios.AllIds.ToDictionary(id => id, _ => 0)
            ),
        };

        SideResultSamples.Errors(result).Should().ContainSingle().Which.Should().Contain("injection.factors");
    }
}
