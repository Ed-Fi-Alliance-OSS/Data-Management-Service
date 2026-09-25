// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

/// <summary>A retention repository whose batches are scripted; once the script runs out, nothing is left to delete.</summary>
public sealed class ScriptedRetentionRepository : IJobRetentionRepository
{
    public ConcurrentQueue<Func<Task<JobRetentionResult>>> Script { get; } = new();

    public ConcurrentQueue<(int RetentionSeconds, int BatchSize)> Calls { get; } = new();

    public Task<JobRetentionResult> DeleteFinishedOlderThan(
        int retentionSeconds,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue((retentionSeconds, batchSize));
        return Script.TryDequeue(out Func<Task<JobRetentionResult>>? next)
            ? next()
            : Task.FromResult<JobRetentionResult>(new JobRetentionResult.Success(0));
    }

    public void Deletes(params int[] batches)
    {
        foreach (int batch in batches)
        {
            Script.Enqueue(() => Task.FromResult<JobRetentionResult>(new JobRetentionResult.Success(batch)));
        }
    }
}

/// <summary>A retention service over a <see cref="ScriptedRetentionRepository"/>, with a fake clock.</summary>
public sealed class RetentionHarness : IDisposable
{
    public RetentionHarness()
    {
        Service = new JobRetentionService(Repository, Metrics, Options.Create(Settings), Time, Logger);
    }

    public ScriptedRetentionRepository Repository { get; } = new();
    public JobMetrics Metrics { get; } = new();
    public JobOptions Settings { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    public CapturingLogger<JobRetentionService> Logger { get; } = new();
    public JobRetentionService Service { get; }

    public int CallCount => Repository.Calls.Count;

    public Task AdvanceUntil(Func<bool> condition, string because) =>
        HostedServiceProbe.AdvanceUntil(Time, Settings.RetentionInterval, condition, because);

    public void Dispose()
    {
        Service.Dispose();
        Metrics.Dispose();
    }
}

public class JobRetentionServiceTests
{
    [TestFixture]
    public class Given_more_expired_jobs_than_one_batch
    {
        private RetentionHarness _harness = null!;
        private JobMetricsTests.Recorder _recorder = null!;

        [SetUp]
        public async Task Setup()
        {
            _recorder = new JobMetricsTests.Recorder();
            _harness = new RetentionHarness();
            _harness.Repository.Deletes(500, 500, 120);

            await _harness.Service.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(
                () => _harness.Logger.Entries.Any(entry => entry.EventId.Name == "RetentionDeleted"),
                "the first sweep finished"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await HostedServiceProbe.StopAsync(_harness.Service);
            _harness.Dispose();
            _recorder.Dispose();
        }

        [Test]
        public void It_sweeps_when_it_starts_until_a_batch_comes_back_short() =>
            _harness.CallCount.Should().Be(3);

        [Test]
        public void It_deletes_with_the_configured_retention_and_batch_size() =>
            _harness
                .Repository.Calls.Should()
                .OnlyContain(call => call.RetentionSeconds == 604_800 && call.BatchSize == 500);

        [Test]
        public void It_counts_every_deleted_job() =>
            _recorder
                .Measurements.Where(measurement => measurement.Instrument == "dmscs.jobs.retention_deleted")
                .Select(measurement => measurement.Value)
                .Should()
                .Equal(500, 500, 120);

        [Test]
        public void It_logs_the_sweep_total() =>
            _harness
                .Logger.Entries.Single(entry => entry.EventId.Name == "RetentionDeleted")
                .Field("Count")
                .Should()
                .Be(1_120);
    }

    [TestFixture]
    public class Given_a_retention_interval_passing
    {
        private RetentionHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RetentionHarness();
            await _harness.Service.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(() => _harness.CallCount == 1, "the first sweep ran");
            await _harness.AdvanceUntil(() => _harness.CallCount == 2, "the next sweep ran");
            await HostedServiceProbe.StopAsync(_harness.Service);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_sweeps_again_each_interval() => _harness.CallCount.Should().Be(2);

        [Test]
        public void It_logs_nothing_when_nothing_was_deleted() => _harness.Logger.Entries.Should().BeEmpty();

        [Test]
        public void It_stops_with_the_host() =>
            _harness.Service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_batch_that_fails
    {
        private RetentionHarness _harness = null!;
        private int _callsInTheFirstSweep;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RetentionHarness();
            _harness.Repository.Deletes(500);
            _harness.Repository.Script.Enqueue(() =>
                Task.FromResult<JobRetentionResult>(
                    new JobRetentionResult.FailureUnknown(
                        new JobFailureDiagnostic(
                            "Microsoft.Data.SqlClient.SqlException",
                            "1205",
                            "DeleteFinishedOlderThan"
                        )
                    )
                )
            );
            _harness.Repository.Deletes(40);

            await _harness.Service.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(
                () => _harness.Logger.Entries.Any(entry => entry.EventId.Name == "RetentionFailed"),
                "the failure was logged"
            );
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            _callsInTheFirstSweep = _harness.CallCount;
            await _harness.AdvanceUntil(() => _harness.CallCount == 3, "the next sweep ran");
        }

        [TearDown]
        public async Task TearDown()
        {
            await HostedServiceProbe.StopAsync(_harness.Service);
            _harness.Dispose();
        }

        [Test]
        public void It_ends_the_sweep_at_the_failure() => _callsInTheFirstSweep.Should().Be(2);

        [Test]
        public void It_logs_the_failure_with_its_safe_fields() =>
            _harness
                .Logger.Entries.Single(entry => entry.EventId.Name == "RetentionFailed")
                .Should()
                .Match<LogEntry>(entry =>
                    (int?)entry.Field("DeletedBeforeFailure") == 500
                    && (string?)entry.Field("ProviderErrorCode") == "1205"
                    && (string?)entry.Field("Operation") == "DeleteFinishedOlderThan"
                );

        [Test]
        public void It_sweeps_again_at_the_next_interval() =>
            _harness
                .Logger.Entries.Where(entry => entry.EventId.Name == "RetentionDeleted")
                .Select(entry => entry.Field("Count"))
                .Should()
                .Equal(500, 40);
    }

    [TestFixture]
    public class Given_a_repository_that_throws
    {
        private RetentionHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new RetentionHarness();
            _harness.Repository.Script.Enqueue(() =>
                throw new InvalidOperationException("Server=db;Password=hunter2")
            );
            _harness.Repository.Deletes(3);

            await _harness.Service.StartAsync(CancellationToken.None);
            await HostedServiceProbe.Until(
                () => _harness.Logger.Entries.Any(entry => entry.EventId.Name == "RetentionFailed"),
                "the failure was logged"
            );
            await _harness.AdvanceUntil(
                () => _harness.Logger.Entries.Any(entry => entry.EventId.Name == "RetentionDeleted"),
                "the next sweep deleted jobs"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await HostedServiceProbe.StopAsync(_harness.Service);
            _harness.Dispose();
        }

        [Test]
        public void It_does_not_fault_the_service() =>
            _harness.Service.ExecuteTask!.IsFaulted.Should().BeFalse();

        [Test]
        public void It_logs_the_type_chain_but_not_the_message() =>
            _harness
                .Logger.Entries.Single(entry => entry.EventId.Name == "RetentionFailed")
                .Should()
                .Match<LogEntry>(entry =>
                    (string?)entry.Field("ExceptionTypeChain") == "System.InvalidOperationException"
                    && !entry.Message.Contains("hunter2")
                );
    }
}
