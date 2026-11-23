// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

internal static class ReadPageSamples
{
    public const long DeepOffset = 9_000;

    public static readonly PerfExecutedCell ZeroSmall = new(PerfScenarios.TraditionalOffsetZero, 25, 0);

    /// <summary>
    /// A page as DMS returns it: each fixture document with its id.
    /// </summary>
    public static JsonArray Page(long offset, int pageSize, Func<int, long>? ordinalAt = null) =>
        new([
            .. Enumerable
                .Range(0, pageSize)
                .Select(index =>
                {
                    long ordinal = ordinalAt?.Invoke(index) ?? offset + index + 1;
                    JsonObject item = PerfWriteScenarioExecutor.FixtureDocument(ordinal);
                    item["id"] = PerfFixtureDefinition.DocumentUuidFor(ordinal).ToString();
                    return (JsonNode)item;
                }),
        ]);

    public static PerfWindowStamp Stamp(int second) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(second), TimeSpan.FromMilliseconds(second * 10));
}

/// <summary>
/// A fake request path for <see cref="PerfScenarioExecutor.MeasureCellAsync" />: by default
/// every GET returns the cell's correct page and observes one database command.
/// </summary>
internal sealed class FakeReadPath(PerfExecutedCell cell)
{
    private readonly List<ObservedDbCommand> _commands = [];

    public List<string> Urls { get; } = [];

    public Func<int, string>? BodyAt { get; init; }

    public int? ThrowAt { get; init; }

    public Func<int, int> CommandsAt { get; init; } = _ => 1;

    public Task<(HttpStatusCode Status, string Body)> GetAsync(string url)
    {
        int request = Urls.Count;
        Urls.Add(url);
        if (request == ThrowAt)
        {
            throw new HttpRequestException("connection reset");
        }

        for (int command = 0; command < CommandsAt(request); command++)
        {
            _commands.Add(new ObservedDbCommand("SELECT hydration batch", 0.5));
        }

        string body =
            BodyAt?.Invoke(request) ?? ReadPageSamples.Page(cell.Offset, cell.PageSize).ToJsonString();
        return Task.FromResult((HttpStatusCode.OK, body));
    }

    public IReadOnlyList<ObservedDbCommand> Commands() => [.. _commands];

    public Task<PerfCellMeasurement> MeasureAsync(int warmup, int measured, IPerfReadObserver? observer) =>
        PerfScenarioExecutor.MeasureCellAsync(cell, warmup, measured, GetAsync, Commands, observer);
}

[TestFixture]
public class Given_A_Correct_Page
{
    [Test]
    public void It_passes_verification()
    {
        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(
                ReadPageSamples.ZeroSmall,
                0,
                HttpStatusCode.OK,
                ReadPageSamples.Page(0, 25).ToJsonString()
            );

        verify.Should().NotThrow();
    }

    [Test]
    public void It_passes_a_deep_page_of_the_largest_size()
    {
        PerfExecutedCell deep = new(PerfScenarios.TraditionalOffsetDeep, 500, ReadPageSamples.DeepOffset);

        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(
                deep,
                0,
                HttpStatusCode.OK,
                ReadPageSamples.Page(ReadPageSamples.DeepOffset, 500).ToJsonString()
            );

        verify.Should().NotThrow();
    }
}

[TestFixture]
public class Given_A_Page_With_A_Wrong_Student
{
    [Test]
    public void It_rejects_a_wrong_student_at_item_i()
    {
        string body = ReadPageSamples.Page(0, 25, index => index == 3 ? 999 : index + 1).ToJsonString();

        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(ReadPageSamples.ZeroSmall, 4, HttpStatusCode.OK, body);

        verify
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*iteration 4: item 3 must be student perf-000000004*got perf-000000999*");
    }

    [Test]
    public void It_rejects_twenty_five_copies_of_one_student()
    {
        string body = ReadPageSamples.Page(0, 25, _ => 1).ToJsonString();

        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(ReadPageSamples.ZeroSmall, 0, HttpStatusCode.OK, body);

        verify
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*item 1 must be student perf-000000002*");
    }

    [Test]
    public void It_rejects_a_matching_student_id_under_another_document_id()
    {
        JsonArray page = ReadPageSamples.Page(0, 25);
        page[7]!["id"] = PerfFixtureDefinition.DocumentUuidFor(42).ToString();

        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(
                ReadPageSamples.ZeroSmall,
                0,
                HttpStatusCode.OK,
                page.ToJsonString()
            );

        verify.Should().Throw<PerfObservationException>().WithMessage("*item 7*");
    }

    [Test]
    public void It_rejects_a_short_page()
    {
        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(
                ReadPageSamples.ZeroSmall,
                0,
                HttpStatusCode.OK,
                ReadPageSamples.Page(0, 24).ToJsonString()
            );

        verify.Should().Throw<PerfObservationException>().WithMessage("*24 items; expected 25*");
    }

    [Test]
    public void It_rejects_a_status_other_than_200()
    {
        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(
                ReadPageSamples.ZeroSmall,
                0,
                HttpStatusCode.InternalServerError,
                "{}"
            );

        verify.Should().Throw<PerfObservationException>().WithMessage("*HTTP 500*");
    }

    [Test]
    public void It_rejects_a_body_that_is_not_a_page()
    {
        Action verify = () =>
            PerfBenchmarkReadObserver.VerifyPage(
                ReadPageSamples.ZeroSmall,
                0,
                HttpStatusCode.OK,
                "{\"error\":1}"
            );

        verify.Should().Throw<PerfObservationException>().WithMessage("*not a JSON array*");
    }
}

[TestFixture]
public class Given_A_Page_Missing_Hydrated_Collections
{
    private static Action VerifyWith(Action<JsonArray> corrupt)
    {
        JsonArray page = ReadPageSamples.Page(0, 25);
        corrupt(page);
        return () =>
            PerfBenchmarkReadObserver.VerifyPage(
                ReadPageSamples.ZeroSmall,
                0,
                HttpStatusCode.OK,
                page.ToJsonString()
            );
    }

    [Test]
    public void It_rejects_a_last_item_without_visas()
    {
        VerifyWith(page => page[^1]!.AsObject().Remove("visas"))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*item 24 visas*");
    }

    [Test]
    public void It_rejects_a_first_item_with_an_empty_collection()
    {
        VerifyWith(page => page[0]!["identificationDocuments"] = new JsonArray())
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*item 0 identificationDocuments*");
    }

    [Test]
    public void It_rejects_a_collection_item_with_a_value_other_than_the_fixture()
    {
        VerifyWith(page => page[0]!["otherNames"]![0]!["firstName"] = "PerfUpdated")
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*item 0 otherNames*");
    }

    [Test]
    public void It_rejects_an_unresolved_descriptor()
    {
        VerifyWith(page =>
                page[^1]!["personalIdentificationDocuments"]![0]!
                    .AsObject()
                    .Remove("identificationDocumentUseDescriptor")
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*item 24 personalIdentificationDocuments*");
    }

    [Test]
    public void It_rejects_a_missing_birth_sex_descriptor()
    {
        VerifyWith(page => page[0]!.AsObject().Remove("birthSexDescriptor"))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*item 0 birthSexDescriptor*");
    }
}

[TestFixture]
public class Given_A_Verification_Failure_At_Measured_Iteration_7
{
    private const int Warmup = 10;
    private PerfBenchmarkReadObserver _observer = null!;

    [SetUp]
    public async Task Setup()
    {
        _observer = new PerfBenchmarkReadObserver(
            ReadPageSamples.DeepOffset,
            Warmup,
            60,
            () => ReadPageSamples.Stamp(0)
        );
        FakeReadPath path = new(ReadPageSamples.ZeroSmall)
        {
            BodyAt = request =>
                ReadPageSamples
                    .Page(0, 25, index => request == Warmup + 7 && index == 0 ? 2 : index + 1)
                    .ToJsonString(),
        };

        Func<Task> measure = () => path.MeasureAsync(Warmup, 60, _observer);

        await measure.Should().ThrowAsync<PerfObservationException>().WithMessage("*iteration 17: item 0*");
    }

    [Test]
    public void It_counts_eight_measured_attempts_and_seven_completions()
    {
        PerfReadCellCounts counts = _observer.Counts[0];

        counts.MeasuredAttempted.Should().Be(8);
        counts.MeasuredCompleted.Should().Be(7);
        counts.MeasuredFailed.Should().Be(1);
    }

    [Test]
    public void It_keeps_the_warmup_counts_separate()
    {
        PerfReadCellCounts counts = _observer.Counts[0];

        counts.WarmupAttempted.Should().Be(Warmup);
        counts.WarmupCompleted.Should().Be(Warmup);
        counts.WarmupFailed.Should().Be(0);
    }

    [Test]
    public void It_reports_every_planned_cell_with_untouched_cells_at_zero()
    {
        _observer
            .Counts.Select(cell => cell.CellId)
            .Should()
            .Equal(PerfBenchmarkScenarios.AllIds.SkipLast(1));
        _observer.Counts.Skip(1).Should().AllSatisfy(cell => cell.MeasuredAttempted.Should().Be(0));
        _observer.Counts[0].MeasuredWindow.Should().BeNull();
    }

    [Test]
    public void It_reports_that_not_every_read_completed()
    {
        _observer.AllReadsCompleted.Should().BeFalse();
    }
}

[TestFixture]
public class Given_Every_Planned_Read_Completes
{
    private PerfBenchmarkReadObserver _observer = null!;
    private int _second;

    [SetUp]
    public async Task Setup()
    {
        _second = 0;
        _observer = new PerfBenchmarkReadObserver(
            ReadPageSamples.DeepOffset,
            2,
            5,
            () => ReadPageSamples.Stamp(++_second)
        );

        foreach (
            PerfExecutedCell cell in PerfScenarioExecutor.CellsInExecutionOrder(ReadPageSamples.DeepOffset)
        )
        {
            await new FakeReadPath(cell).MeasureAsync(2, 5, _observer);
        }
    }

    [Test]
    public void It_reports_that_every_read_completed()
    {
        _observer.AllReadsCompleted.Should().BeTrue();
        _observer
            .Counts.Should()
            .AllSatisfy(cell =>
            {
                cell.WarmupCompleted.Should().Be(2);
                cell.MeasuredCompleted.Should().Be(5);
                cell.WarmupFailed.Should().Be(0);
                cell.MeasuredFailed.Should().Be(0);
            });
    }

    [Test]
    public void It_records_each_cell_window_from_first_measured_attempt_to_last_measured_completion()
    {
        // Two clock reads per cell, in execution order.
        _observer
            .Counts.Select(cell => cell.MeasuredWindow)
            .Should()
            .Equal(
                Enumerable
                    .Range(0, 6)
                    .Select(index =>
                        PerfMeasuredWindow.Between(
                            ReadPageSamples.Stamp((2 * index) + 1),
                            ReadPageSamples.Stamp((2 * index) + 2)
                        )
                    )
            );
    }

    [Test]
    public void It_rejects_a_cell_outside_the_plan()
    {
        Action attempt = () =>
            _observer.OnAttempt(new PerfExecutedCell(PerfScenarios.TraditionalOffsetDeep, 25, 1), 0, true);

        attempt.Should().Throw<PerfObservationException>().WithMessage("*not a planned cell*");
    }
}
