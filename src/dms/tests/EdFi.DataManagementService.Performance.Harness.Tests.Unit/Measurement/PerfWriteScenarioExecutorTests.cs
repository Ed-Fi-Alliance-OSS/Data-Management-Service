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

[TestFixture]
public class Given_Write_Target_Selection
{
    private PerfWriteTargets _targets = null!;

    [SetUp]
    public void Setup()
    {
        _targets = PerfWriteTargets.Select(PerfFixtureKind.Primary500k.RowCount, 20, 200);
    }

    [Test]
    public void It_selects_one_target_per_iteration()
    {
        _targets.WarmupOrdinals.Should().HaveCount(20);
        _targets.MeasuredOrdinals.Should().HaveCount(200);
        _targets.Count.Should().Be(220);
    }

    [Test]
    public void It_selects_distinct_students_with_warmup_and_measured_disjoint()
    {
        _targets.All.Should().OnlyHaveUniqueItems();
        _targets.WarmupOrdinals.Should().NotIntersectWith(_targets.MeasuredOrdinals);
    }

    [Test]
    public void It_is_deterministic()
    {
        PerfWriteTargets again = PerfWriteTargets.Select(PerfFixtureKind.Primary500k.RowCount, 20, 200);

        again.All.Should().Equal(_targets.All);
    }

    [Test]
    public void It_strides_over_the_whole_fixture()
    {
        long stride = 500_000 / 220;
        _targets.All.Should().Equal(Enumerable.Range(0, 220).Select(index => 1 + (index * stride)));
        _targets.All.Max().Should().BeGreaterThan(PerfFixtureKind.Primary500k.RowCount * 99 / 100);
        _targets.All.Max().Should().BeLessThanOrEqualTo(PerfFixtureKind.Primary500k.RowCount);
    }

    [Test]
    public void It_maps_absolute_iterations_to_warmup_then_measured_targets()
    {
        _targets.OrdinalFor(0).Should().Be(_targets.WarmupOrdinals[0]);
        _targets.OrdinalFor(19).Should().Be(_targets.WarmupOrdinals[19]);
        _targets.OrdinalFor(20).Should().Be(_targets.MeasuredOrdinals[0]);
        _targets.OrdinalFor(219).Should().Be(_targets.MeasuredOrdinals[199]);
    }

    [Test]
    public void It_can_target_every_student_of_the_fixture()
    {
        PerfWriteTargets.Select(10, 4, 6).All.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
    }

    [Test]
    public void It_rejects_more_targets_than_students()
    {
        Action select = () => PerfWriteTargets.Select(10, 5, 6);

        select.Should().Throw<ArgumentException>().WithMessage("*11 write targets*10*");
    }
}

[TestFixture]
public class Given_A_Write_Payload
{
    private const long Ordinal = 12_345;

    private static string Descriptor(string resource) => PerfFixtureDefinition.DescriptorUriFor(resource);

    private static JsonObject IdentificationDocument() =>
        new()
        {
            ["identificationDocumentUseDescriptor"] = Descriptor("IdentificationDocumentUseDescriptor"),
            ["personalInformationVerificationDescriptor"] = Descriptor(
                "PersonalInformationVerificationDescriptor"
            ),
        };

    // Written out independently of the executor: the fixture document of student 12,345.
    private static readonly JsonObject _fixtureDocument = new()
    {
        ["studentUniqueId"] = "perf-000012345",
        ["firstName"] = "Perf",
        ["lastSurname"] = "Student",
        ["birthDate"] = "2010-01-01",
        ["birthSexDescriptor"] = Descriptor("SexDescriptor"),
        ["otherNames"] = new JsonArray(
            new JsonObject
            {
                ["otherNameTypeDescriptor"] = Descriptor("OtherNameTypeDescriptor"),
                ["firstName"] = "Perf",
                ["lastSurname"] = "Student",
            }
        ),
        ["identificationDocuments"] = new JsonArray(IdentificationDocument()),
        ["personalIdentificationDocuments"] = new JsonArray(IdentificationDocument()),
        ["visas"] = new JsonArray(new JsonObject { ["visaDescriptor"] = Descriptor("VisaDescriptor") }),
    };

    [Test]
    public void It_changes_only_the_first_other_name()
    {
        JsonObject payload = PerfWriteScenarioExecutor.PayloadFor(Ordinal);

        payload["otherNames"]![0]!["firstName"]!.GetValue<string>().Should().Be("PerfUpdated");

        payload["otherNames"]![0]!["firstName"] = "Perf";
        JsonNode.DeepEquals(payload, _fixtureDocument).Should().BeTrue(payload.ToJsonString());
    }

    [Test]
    public void It_keeps_every_collection_at_one_item()
    {
        JsonObject payload = PerfWriteScenarioExecutor.PayloadFor(Ordinal);

        foreach (
            string collection in new[]
            {
                "otherNames",
                "identificationDocuments",
                "personalIdentificationDocuments",
                "visas",
            }
        )
        {
            payload[collection]!
                .AsArray()
                .Should()
                .HaveCount(PerfFixtureDefinition.ChildCollectionRowsPerStudent);
        }
    }

    [Test]
    public void It_identifies_the_target_student()
    {
        PerfWriteScenarioExecutor.PayloadFor(Ordinal)["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.StudentUniqueIdFor(Ordinal));
    }
}

[TestFixture]
public class Given_Write_Outcomes_That_All_Succeed
{
    private static readonly DateTimeOffset _start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private PerfWriteOutcomes _outcomes = null!;
    private int _clockReads;

    [SetUp]
    public async Task Setup()
    {
        // The clock is read only at the window edges: before the first measured dispatch and
        // after the last measured completion.
        Queue<PerfWindowStamp> stamps = new([
            new PerfWindowStamp(_start.AddSeconds(2), TimeSpan.FromMilliseconds(100)),
            new PerfWindowStamp(_start.AddSeconds(4.5), TimeSpan.FromMilliseconds(350)),
        ]);
        _clockReads = 0;
        _outcomes = new PerfWriteOutcomes(
            warmupIterations: 2,
            measuredIterations: 3,
            () =>
            {
                _clockReads++;
                return stamps.Dequeue();
            }
        );
        int[] commandCounts = [9, 9, 7, 9, 9];

        await PerfLatencyMeasurement.MeasureAsync(
            _ => Task.CompletedTask,
            2,
            3,
            iteration =>
            {
                _outcomes.OnAttempt(iteration);
                return Task.CompletedTask;
            },
            iteration =>
            {
                _outcomes.OnResponse(
                    iteration,
                    HttpStatusCode.NoContent,
                    hasEtag: true,
                    commandCounts[iteration],
                    string.Empty
                );
                return Task.CompletedTask;
            }
        );
    }

    [Test]
    public void It_counts_every_request_as_attempted_and_completed()
    {
        _outcomes.WarmupAttempted.Should().Be(2);
        _outcomes.WarmupCompleted.Should().Be(2);
        _outcomes.MeasuredAttempted.Should().Be(3);
        _outcomes.MeasuredCompleted.Should().Be(3);
        _outcomes.Failed.Should().Be(0);
        _outcomes.FailureReason.Should().BeNull();
    }

    [Test]
    public void It_records_the_measured_command_count_distribution()
    {
        _outcomes
            .MeasuredCommandCounts.Should()
            .Equal(new PerfCommandCountFrequency(7, 1), new PerfCommandCountFrequency(9, 2));
    }

    [Test]
    public void It_opens_the_window_at_the_first_measured_attempt_and_closes_it_at_the_last_completion()
    {
        _clockReads.Should().Be(2);
        _outcomes
            .MeasuredWindow.Should()
            .Be(new PerfMeasuredWindow(_start.AddSeconds(2), _start.AddSeconds(4.5), 250));
    }

    [Test]
    public void It_produces_the_scenario_result()
    {
        PerfWriteScenarioResult result = _outcomes.ToResult(
            PerfLatencyMeasurement.Summarize([1.0, 2.0, 3.0])
        );

        result.ScenarioId.Should().Be(PerfBenchmarkScenarios.StudentPutCollectionUpdate);
        result.MeasuredCompleted.Should().Be(3);
        result.MeasuredWindow!.StartUtc.Should().Be(_start.AddSeconds(2));
        result.LatencyMs!.SamplesMs.Should().HaveCount(3);
    }
}

public abstract class WriteOutcomeFailureFixture
{
    protected const int FailingIteration = 9;

    private int _operations;
    private Exception? _thrown;

    protected PerfWriteOutcomes Outcomes { get; private set; } = null!;

    protected abstract (HttpStatusCode Status, bool HasEtag) FailingResponse { get; }

    [SetUp]
    public async Task Setup()
    {
        // 2 warmup + 10 measured; the failure lands on measured iteration 7.
        Outcomes = new PerfWriteOutcomes(
            warmupIterations: 2,
            measuredIterations: 10,
            () => new PerfWindowStamp(DateTimeOffset.UnixEpoch, TimeSpan.Zero)
        );
        _operations = 0;

        Func<Task> run = () =>
            PerfLatencyMeasurement.MeasureAsync(
                _ =>
                {
                    _operations++;
                    return Task.CompletedTask;
                },
                2,
                10,
                iteration =>
                {
                    Outcomes.OnAttempt(iteration);
                    return Task.CompletedTask;
                },
                iteration =>
                {
                    (HttpStatusCode status, bool hasEtag) =
                        iteration == FailingIteration ? FailingResponse : (HttpStatusCode.NoContent, true);
                    Outcomes.OnResponse(iteration, status, hasEtag, 9, "{\"detail\":\"boom\"}");
                    return Task.CompletedTask;
                }
            );

        _thrown = (await run.Should().ThrowAsync<PerfObservationException>()).Which;
    }

    [Test]
    public void It_stops_at_the_failing_request()
    {
        _operations.Should().Be(FailingIteration + 1);
    }

    [Test]
    public void It_keeps_exact_counts()
    {
        Outcomes.WarmupAttempted.Should().Be(2);
        Outcomes.WarmupCompleted.Should().Be(2);
        Outcomes.MeasuredAttempted.Should().Be(8);
        Outcomes.MeasuredCompleted.Should().Be(7);
        Outcomes.Failed.Should().Be(1);
    }

    [Test]
    public void It_reports_exact_counts_without_a_window_or_latency()
    {
        PerfWriteScenarioResult partial = Outcomes.ToResult();

        partial.MeasuredAttempted.Should().Be(8);
        partial.MeasuredCompleted.Should().Be(7);
        partial.Failed.Should().Be(1);
        partial.MeasuredWindow.Should().BeNull();
        partial.LatencyMs.Should().BeNull();
    }

    [Test]
    public void It_records_the_reason_it_threw()
    {
        Outcomes.FailureReason.Should().Be(_thrown!.Message);
        Outcomes.FailureReason.Should().Contain($"iteration {FailingIteration}");
    }
}

[TestFixture]
public class Given_A_Write_Answered_With_A_Status_Other_Than_204 : WriteOutcomeFailureFixture
{
    protected override (HttpStatusCode Status, bool HasEtag) FailingResponse =>
        (HttpStatusCode.Conflict, true);

    [Test]
    public void It_names_the_status_and_the_body()
    {
        Outcomes.FailureReason.Should().Contain("HTTP 409").And.Contain("boom");
    }
}

[TestFixture]
public class Given_A_Write_Answered_Without_An_Etag : WriteOutcomeFailureFixture
{
    protected override (HttpStatusCode Status, bool HasEtag) FailingResponse =>
        (HttpStatusCode.NoContent, false);

    [Test]
    public void It_names_the_missing_etag()
    {
        Outcomes.FailureReason.Should().Contain("no etag");
    }
}

[TestFixture]
public class Given_A_Write_Request_That_Throws
{
    [Test]
    public async Task It_stays_attempted_but_not_completed()
    {
        PerfWriteOutcomes outcomes = new(
            warmupIterations: 0,
            measuredIterations: 3,
            () => new PerfWindowStamp(DateTimeOffset.UnixEpoch, TimeSpan.Zero)
        );

        Func<Task> run = () =>
            PerfLatencyMeasurement.MeasureAsync(
                iteration =>
                    iteration == 1 ? throw new HttpRequestException("connection reset") : Task.CompletedTask,
                0,
                3,
                iteration =>
                {
                    outcomes.OnAttempt(iteration);
                    return Task.CompletedTask;
                },
                iteration =>
                {
                    outcomes.OnResponse(iteration, HttpStatusCode.NoContent, true, 9, string.Empty);
                    return Task.CompletedTask;
                }
            );

        await run.Should().ThrowAsync<HttpRequestException>();
        outcomes.MeasuredAttempted.Should().Be(2);
        outcomes.MeasuredCompleted.Should().Be(1);
        outcomes.Failed.Should().Be(1);
    }
}

[TestFixture]
public class Given_Write_Verification_Queries
{
    private static readonly PerfFixtureDefinition _definition = new(PerfFixtureKind.Smoke10k);
    private static readonly PerfWriteTargets _targets = PerfWriteTargets.Select(10_000, 5, 20);

    [Test]
    public void It_quotes_postgresql_identifiers()
    {
        PerfWriteScenarioExecutor
            .VerificationQueries(PerfProvider.Postgresql, _definition, _targets)[0]
            .Sql.Should()
            .Contain("\"edfi\".\"StudentOtherName\"")
            .And.Contain("\"FirstName\" = 'PerfUpdated'");
    }

    [Test]
    public void It_quotes_mssql_identifiers()
    {
        PerfWriteScenarioExecutor
            .VerificationQueries(PerfProvider.Mssql, _definition, _targets)[0]
            .Sql.Should()
            .Contain("[edfi].[StudentOtherName]")
            .And.Contain("[FirstName] = 'PerfUpdated'");
    }

    [Test]
    public void It_restricts_the_target_count_to_every_target_document()
    {
        PerfVerificationQuery onTargets = PerfWriteScenarioExecutor.VerificationQueries(
            PerfProvider.Postgresql,
            _definition,
            _targets
        )[0];

        onTargets.ExpectedValue.Should().Be(25);
        string inList = onTargets.Sql[(onTargets.Sql.IndexOf("IN (", StringComparison.Ordinal) + 4)..^2];
        inList
            .Split(", ")
            .Select(long.Parse)
            .Should()
            .Equal(_targets.All.Select(PerfFixtureDefinition.DocumentIdFor));
    }

    [Test]
    public void It_expects_every_child_table_to_keep_one_row_per_student()
    {
        PerfWriteScenarioExecutor
            .VerificationQueries(PerfProvider.Mssql, _definition, _targets)
            .Skip(2)
            .Should()
            .HaveCount(4)
            .And.AllSatisfy(query => query.ExpectedValue.Should().Be(10_000));
    }
}

[TestFixture]
public class Given_Write_Verification_Counts
{
    private static readonly IReadOnlyList<PerfVerificationQuery> _queries =
        PerfWriteScenarioExecutor.VerificationQueries(
            PerfProvider.Postgresql,
            new PerfFixtureDefinition(PerfFixtureKind.Smoke10k),
            PerfWriteTargets.Select(10_000, 5, 20)
        );

    private static IReadOnlyList<string> Evaluate(Func<PerfVerificationQuery, long> actual) =>
        PerfWriteScenarioExecutor.Evaluate(_queries.Select(query => (query, actual(query))));

    [Test]
    public void It_passes_when_every_count_matches()
    {
        Evaluate(query => query.ExpectedValue).Should().BeEmpty();
    }

    [Test]
    public void It_fails_before_any_write_happened()
    {
        Evaluate(query =>
                query.Name.StartsWith("updated-", StringComparison.Ordinal) ? 0 : query.ExpectedValue
            )
            .Should()
            .Equal(
                "updated-other-names-on-targets: expected 25, got 0.",
                "updated-other-names-total: expected 25, got 0."
            );
    }

    [Test]
    public void It_fails_a_no_op_that_changed_fewer_rows_than_targets()
    {
        Evaluate(query => query.Name == "updated-other-names-on-targets" ? 24 : query.ExpectedValue)
            .Should()
            .Equal("updated-other-names-on-targets: expected 25, got 24.");
    }

    [Test]
    public void It_fails_an_update_that_touched_a_student_outside_the_targets()
    {
        Evaluate(query => query.Name == "updated-other-names-total" ? 26 : query.ExpectedValue)
            .Should()
            .Equal("updated-other-names-total: expected 25, got 26.");
    }

    [Test]
    public void It_fails_a_changed_child_total()
    {
        Evaluate(query => query.Name == "StudentVisa-row-count" ? 9_999 : query.ExpectedValue)
            .Should()
            .Equal("StudentVisa-row-count: expected 10000, got 9999.");
    }
}
