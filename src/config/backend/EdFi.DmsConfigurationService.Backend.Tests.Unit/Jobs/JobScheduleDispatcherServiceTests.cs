// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

/// <summary>A schedule repository whose materializations are scripted; once the script runs out, nothing is due.</summary>
public sealed class ScriptedScheduleRepository : IJobScheduleRepository
{
    public ConcurrentQueue<Func<Task<JobScheduleMaterializeResult>>> Script { get; } = new();

    public ConcurrentQueue<(string Owner, int LeaseSeconds, string JobId)> Calls { get; } = new();

    public Task<JobScheduleMaterializeResult> MaterializeNextDue(
        string owner,
        int leaseSeconds,
        string newJobId,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue((owner, leaseSeconds, newJobId));
        return Script.TryDequeue(out Func<Task<JobScheduleMaterializeResult>>? next)
            ? next()
            : Task.FromResult<JobScheduleMaterializeResult>(new JobScheduleMaterializeResult.NoneDue());
    }

    public void Returns(params JobScheduleMaterializeResult[] results)
    {
        foreach (JobScheduleMaterializeResult result in results)
        {
            Script.Enqueue(() => Task.FromResult(result));
        }
    }

    public Task<JobScheduleUpsertResult> Upsert(
        JobScheduleUpsertCommand command,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    public Task<JobScheduleDisableResult> Disable(string scheduleType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JobScheduleDisableResult> DisableById(long id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JobScheduleListResult> ListByType(string scheduleType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>A dispatcher over a <see cref="ScriptedScheduleRepository"/>, with a fake clock.</summary>
public sealed class DispatcherHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    public DispatcherHarness()
    {
        ServiceCollection services = new();
        services.AddSingleton<IJobScheduleRepository>(Repository);
        _provider = services.BuildServiceProvider();
        Dispatcher = new JobScheduleDispatcherService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Metrics,
            Options.Create(Settings),
            Time,
            Logger
        );
    }

    public ScriptedScheduleRepository Repository { get; } = new();
    public JobMetrics Metrics { get; } = new();
    public JobOptions Settings { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    public CapturingLogger<JobScheduleDispatcherService> Logger { get; } = new();
    public JobScheduleDispatcherService Dispatcher { get; }

    public int CallCount => Repository.Calls.Count;

    public Task AdvanceUntil(Func<bool> condition, string because) =>
        HostedServiceProbe.AdvanceUntil(Time, Settings.PollInterval, condition, because);

    public static JobScheduleMaterializeResult.Materialized Materialized(long id, string scheduleType) =>
        new(
            id,
            scheduleType,
            $"job-{id}",
            new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 24, 12, 0, 1, DateTimeKind.Utc)
        );

    public void Dispose()
    {
        Dispatcher.Dispose();
        Metrics.Dispose();
        _provider.Dispose();
    }
}

public class JobScheduleDispatcherServiceTests
{
    [TestFixture]
    public class Given_several_due_schedules
    {
        private DispatcherHarness _harness = null!;
        private JobMetricsTests.Recorder _recorder = null!;

        [SetUp]
        public async Task Setup()
        {
            _recorder = new JobMetricsTests.Recorder();
            _harness = new DispatcherHarness();
            _harness.Repository.Returns(
                DispatcherHarness.Materialized(1, "Nightly.Refresh"),
                DispatcherHarness.Materialized(2, "Hourly.Check"),
                new JobScheduleMaterializeResult.AlreadyEnqueued(
                    3,
                    "Nightly.Refresh",
                    new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 24, 12, 0, 1, DateTimeKind.Utc)
                )
            );

            await _harness.Dispatcher.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(
                () => _harness.CallCount == 4,
                "the first poll found nothing more due"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await HostedServiceProbe.StopAsync(_harness.Dispatcher);
            _harness.Dispose();
            _recorder.Dispose();
        }

        [Test]
        public void It_materializes_until_none_is_due_in_one_poll() => _harness.CallCount.Should().Be(4);

        [Test]
        public void It_uses_one_owner_a_bounded_lease_and_a_new_job_id_each_time()
        {
            _harness
                .Repository.Calls.Select(call => call.Owner)
                .Distinct()
                .Should()
                .Equal(_harness.Dispatcher.Owner);
            _harness.Repository.Calls.Should().OnlyContain(call => call.LeaseSeconds == 20);
            _harness.Repository.Calls.Select(call => call.JobId).Should().OnlyHaveUniqueItems();
        }

        [Test]
        public void It_logs_each_occurrence() =>
            _harness
                .Logger.Entries.Select(entry => entry.EventId.Name)
                .Should()
                .Equal(
                    "ScheduleOccurrenceEnqueued",
                    "ScheduleOccurrenceEnqueued",
                    "ScheduleOccurrenceAlreadyEnqueued"
                );

        [Test]
        public void It_counts_enqueued_occurrences_by_schedule_type() =>
            _recorder
                .Measurements.Where(measurement =>
                    measurement.Instrument == "dmscs.schedules.occurrences_enqueued"
                )
                .Select(measurement => measurement.Tags["schedule_type"])
                .Should()
                .Equal("Nightly.Refresh", "Hourly.Check");
    }

    [TestFixture("ownership lost")]
    [TestFixture("failure unknown")]
    public class Given_a_materialization_that_does_not_succeed(string failure)
    {
        private DispatcherHarness _harness = null!;
        private int _callsInTheFirstPoll;

        [SetUp]
        public async Task Setup()
        {
            _harness = new DispatcherHarness();
            _harness.Repository.Returns(
                DispatcherHarness.Materialized(1, "Nightly.Refresh"),
                failure == "ownership lost"
                    ? new JobScheduleMaterializeResult.OwnershipLost()
                    : new JobScheduleMaterializeResult.FailureUnknown(
                        new JobFailureDiagnostic("Npgsql.PostgresException", "57014", "MaterializeNextDue")
                    ),
                DispatcherHarness.Materialized(2, "Nightly.Refresh")
            );

            await _harness.Dispatcher.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(
                () =>
                    _harness.Logger.Entries.Any(entry =>
                        entry.EventId.Name == "ScheduleMaterializationFailed"
                    ),
                "the failure was logged"
            );
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            _callsInTheFirstPoll = _harness.CallCount;
            await _harness.AdvanceUntil(() => _harness.CallCount == 4, "the next poll finished");
        }

        [TearDown]
        public async Task TearDown()
        {
            await HostedServiceProbe.StopAsync(_harness.Dispatcher);
            _harness.Dispose();
        }

        [Test]
        public void It_ends_the_poll_at_the_failure() => _callsInTheFirstPoll.Should().Be(2);

        [Test]
        public void It_logs_the_failure_with_its_safe_fields()
        {
            LogEntry entry = _harness.Logger.Entries.Single(entry =>
                entry.EventId.Name == "ScheduleMaterializationFailed"
            );
            entry.Field("Operation").Should().Be("MaterializeNextDue");
            if (failure == "failure unknown")
            {
                entry.Field("Outcome").Should().Be("FailureUnknown");
                entry.Field("ProviderErrorCode").Should().Be("57014");
                entry.Field("ExceptionTypeChain").Should().Be("Npgsql.PostgresException");
            }
            else
            {
                entry.Field("Outcome").Should().Be("OwnershipLost");
            }
        }

        [Test]
        public void It_continues_on_the_next_poll() =>
            _harness
                .Logger.Entries.Count(entry => entry.EventId.Name == "ScheduleOccurrenceEnqueued")
                .Should()
                .Be(2);
    }

    [TestFixture]
    public class Given_a_repository_that_throws
    {
        private DispatcherHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new DispatcherHarness();
            _harness.Repository.Script.Enqueue(() =>
                throw new InvalidOperationException("Server=db;Password=hunter2")
            );
            _harness.Repository.Returns(DispatcherHarness.Materialized(1, "Nightly.Refresh"));

            await _harness.Dispatcher.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(
                () =>
                    _harness.Logger.Entries.Any(entry =>
                        entry.EventId.Name == "ScheduleMaterializationFailed"
                    ),
                "the failure was logged"
            );
            await _harness.AdvanceUntil(
                () =>
                    _harness.Logger.Entries.Any(entry => entry.EventId.Name == "ScheduleOccurrenceEnqueued"),
                "the next poll enqueued the occurrence"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await HostedServiceProbe.StopAsync(_harness.Dispatcher);
            _harness.Dispose();
        }

        [Test]
        public void It_does_not_fault_the_service() =>
            _harness.Dispatcher.ExecuteTask!.IsFaulted.Should().BeFalse();

        [Test]
        public void It_logs_the_type_chain_but_not_the_message() =>
            _harness
                .Logger.Entries.Single(entry => entry.EventId.Name == "ScheduleMaterializationFailed")
                .Should()
                .Match<LogEntry>(entry =>
                    (string?)entry.Field("ExceptionTypeChain") == "System.InvalidOperationException"
                    && !entry.Message.Contains("hunter2")
                );
    }

    [TestFixture]
    public class Given_an_idle_scheduler
    {
        private DispatcherHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new DispatcherHarness();
            await _harness.Dispatcher.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(() => _harness.CallCount == 1, "the first poll ran");
            await _harness.AdvanceUntil(() => _harness.CallCount == 3, "two more polls ran");
            await HostedServiceProbe.StopAsync(_harness.Dispatcher);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_asks_once_per_poll_and_logs_nothing()
        {
            _harness.CallCount.Should().Be(3);
            _harness.Logger.Entries.Should().BeEmpty();
        }

        [Test]
        public void It_stops_with_the_host() =>
            _harness.Dispatcher.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }
}
