// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobMetricsTests
{
    public sealed record Measurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags
    );

    /// <summary>Collects every measurement published on the jobs meter while it is alive.</summary>
    public sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == JobMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Record(instrument, value, tags)
            );
            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Record(instrument, value, tags)
            );
            _listener.Start();
        }

        public ConcurrentQueue<Measurement> Measurements { get; } = new();

        public Measurement Single(string instrument) =>
            Measurements.Should().ContainSingle(measurement => measurement.Instrument == instrument).Subject;

        public void Dispose() => _listener.Dispose();

        private void Record(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags
        )
        {
            Dictionary<string, object?> copied = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }
            Measurements.Enqueue(new Measurement(instrument.Name, value, copied));
        }
    }

    [TestFixture]
    public class Given_an_execution_that_completes
    {
        private Recorder _recorder = null!;
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _recorder = new Recorder();
            _harness = new ExecutorHarness();
            await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Dispose();
            _recorder.Dispose();
        }

        [Test]
        public void It_counts_the_claim_by_job_type_and_reclaim() =>
            _recorder
                .Single("dmscs.jobs.claimed")
                .Tags.Should()
                .BeEquivalentTo(
                    new Dictionary<string, object?>
                    {
                        ["job_type"] = ExecutorHarness.JobType,
                        ["reclaimed"] = false,
                    }
                );

        [Test]
        public void It_records_the_queue_delay_from_database_times() =>
            _recorder.Single("dmscs.jobs.queue_delay").Value.Should().Be(2_000);

        [Test]
        public void It_counts_the_finish_by_job_type_and_outcome() =>
            _recorder
                .Single("dmscs.jobs.finished")
                .Tags.Should()
                .BeEquivalentTo(
                    new Dictionary<string, object?>
                    {
                        ["job_type"] = ExecutorHarness.JobType,
                        ["outcome"] = "Completed",
                    }
                );

        [Test]
        public void It_records_the_duration() =>
            _recorder.Single("dmscs.jobs.duration").Tags["job_type"].Should().Be(ExecutorHarness.JobType);

        [Test]
        public void It_counts_no_uncertainty() =>
            _recorder
                .Measurements.Should()
                .NotContain(measurement => measurement.Instrument == "dmscs.jobs.ownership_uncertain");
    }

    [TestFixture]
    public class Given_an_execution_that_ends_uncertain
    {
        private Recorder _recorder = null!;
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _recorder = new Recorder();
            _harness = new ExecutorHarness();
            _harness.Leases.OutcomeResult = _ =>
                Task.FromResult<JobWriteResult>(
                    new JobWriteResult.ResultUnknown(
                        new JobFailureDiagnostic("System.TimeoutException", null, "Complete")
                    )
                );
            await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(reclaimed: true),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Dispose();
            _recorder.Dispose();
        }

        [Test]
        public void It_counts_the_uncertainty_by_reason() =>
            _recorder
                .Single("dmscs.jobs.ownership_uncertain")
                .Tags.Should()
                .BeEquivalentTo(
                    new Dictionary<string, object?>
                    {
                        ["job_type"] = ExecutorHarness.JobType,
                        ["reason"] = "WriteOutcomeUnknown",
                    }
                );

        [Test]
        public void It_tags_the_reclaim() =>
            _recorder.Single("dmscs.jobs.claimed").Tags["reclaimed"].Should().Be(true);

        [Test]
        public void It_counts_the_finish_as_uncertain() =>
            _recorder.Single("dmscs.jobs.finished").Tags["outcome"].Should().Be("OwnershipUncertain");
    }

    [TestFixture]
    public class Given_schedule_and_retention_measurements
    {
        private Recorder _recorder = null!;

        [SetUp]
        public void Setup()
        {
            _recorder = new Recorder();
            using JobMetrics metrics = new();
            metrics.OccurrenceEnqueued("Nightly\nRefresh");
            metrics.RetentionDeleted(500);
        }

        [TearDown]
        public void TearDown() => _recorder.Dispose();

        [Test]
        public void It_counts_enqueued_occurrences_with_a_safe_schedule_type() =>
            _recorder
                .Single("dmscs.schedules.occurrences_enqueued")
                .Tags["schedule_type"]
                .Should()
                .Be("NightlyRefresh");

        [Test]
        public void It_counts_deleted_jobs() =>
            _recorder.Single("dmscs.jobs.retention_deleted").Value.Should().Be(500);
    }
}
