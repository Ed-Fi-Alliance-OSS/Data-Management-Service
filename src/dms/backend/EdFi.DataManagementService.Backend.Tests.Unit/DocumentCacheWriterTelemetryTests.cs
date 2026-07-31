// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheWriterTelemetry
{
    private static readonly DocumentCacheProjectionTargetKey TargetKey = new("tenant-a", new DataStoreId(7));

    [Test]
    public void It_records_one_counter_measurement_for_each_bounded_writer_outcome()
    {
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();

        foreach (DocumentCacheWriterOutcome outcome in Enum.GetValues<DocumentCacheWriterOutcome>())
        {
            telemetry.RecordOutcome(
                DocumentCacheWriterMetricContext.ForCacheWriter(
                    RelationalProviderToken.Postgresql,
                    TargetKey,
                    DocumentCacheWriterPurpose.DurableWorkProjection,
                    DocumentCacheLifecycleState.Tracking,
                    outcome
                )
            );
        }

        MetricMeasurement[] records = collector.MeasurementsFor(
            DocumentCacheWriterTelemetry.OutcomeCounterName
        );

        records.Should().HaveCount(Enum.GetValues<DocumentCacheWriterOutcome>().Length);
        records.Select(record => record.LongValue).Should().OnlyContain(value => value == 1);
        records
            .Select(record => record.Tags["outcome"])
            .Should()
            .BeEquivalentTo(Enum.GetNames<DocumentCacheWriterOutcome>());
        records.Should().OnlyContain(record => (string)record.Tags["provider"]! == "postgresql");
        records.Should().OnlyContain(record => (string)record.Tags["target_key"]! == "tenant-a:7");
        records
            .Should()
            .OnlyContain(record =>
                (string)record.Tags["purpose"]! == nameof(DocumentCacheWriterPurpose.DurableWorkProjection)
            );
        records.Should().OnlyContain(record => (string)record.Tags["lifecycle"]! == "Tracking");
    }

    [Test]
    public void It_records_transaction_phase_retry_and_same_document_histograms()
    {
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheWriterMetricContext context = DocumentCacheWriterMetricContext.ForCacheWriter(
            RelationalProviderToken.Postgresql,
            TargetKey,
            DocumentCacheWriterPurpose.DirectFill,
            DocumentCacheLifecycleState.Rebuilding,
            DocumentCacheWriterOutcome.CandidateWrittenAcknowledged
        );

        telemetry.RecordTransactionDuration(context, TimeSpan.FromMilliseconds(10));
        telemetry.RecordCacheDmlDuration(context, TimeSpan.FromMilliseconds(11));
        telemetry.RecordAcknowledgementDuration(context, TimeSpan.FromMilliseconds(12));
        telemetry.RecordRetry(context, TimeSpan.FromMilliseconds(13), attemptCount: 3);
        telemetry.RecordSameDocumentWait(
            context,
            DocumentCacheWriterContentionParticipant.CacheWriter,
            DocumentCacheWriterContentionPhase.Acknowledgement,
            TimeSpan.FromMilliseconds(14)
        );

        collector.MeasurementsFor(DocumentCacheWriterTelemetry.TransactionDurationName).Should().HaveCount(1);
        collector.MeasurementsFor(DocumentCacheWriterTelemetry.CacheDmlDurationName).Should().HaveCount(1);
        collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.AcknowledgementDurationName)
            .Should()
            .HaveCount(1);
        collector.MeasurementsFor(DocumentCacheWriterTelemetry.RetryDurationName).Should().HaveCount(1);
        collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.RetryAttemptsName)
            .Should()
            .ContainSingle()
            .Which.IntValue.Should()
            .Be(3);

        MetricMeasurement sameDocumentWait = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.SameDocumentWaitName)
            .Should()
            .ContainSingle()
            .Which;
        sameDocumentWait.DoubleValue.Should().Be(14);
        sameDocumentWait.Tags["participant"].Should().Be("CacheWriter");
        sameDocumentWait.Tags["phase"].Should().Be("Acknowledgement");
        sameDocumentWait.Tags["purpose"].Should().Be(nameof(DocumentCacheWriterPurpose.DirectFill));
    }

    [Test]
    public void It_records_canonical_writer_waits_in_the_same_sanitized_metric_family()
    {
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordSameDocumentWait(
            DocumentCacheWriterMetricContext.ForCanonicalWriter(
                RelationalProviderToken.SqlServer,
                dataStoreId: 99,
                DocumentCacheWriterTelemetryLabel.CanonicalWrite,
                nameof(RelationalWriteExecutorAttemptOutcome.AppliedWrite)
            ),
            DocumentCacheWriterContentionParticipant.CanonicalWriter,
            DocumentCacheWriterContentionPhase.CanonicalPersist,
            TimeSpan.FromMilliseconds(15)
        );

        MetricMeasurement sameDocumentWait = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.SameDocumentWaitName)
            .Should()
            .ContainSingle()
            .Which;
        sameDocumentWait.Tags["provider"].Should().Be("sqlserver");
        sameDocumentWait.Tags["target_key"].Should().Be("selected:99");
        sameDocumentWait.Tags["purpose"].Should().Be(DocumentCacheWriterTelemetryLabel.CanonicalWrite);
        sameDocumentWait.Tags["lifecycle"].Should().Be(DocumentCacheWriterTelemetryLabel.Unknown);
        sameDocumentWait
            .Tags["outcome"]
            .Should()
            .Be(nameof(RelationalWriteExecutorAttemptOutcome.AppliedWrite));
        sameDocumentWait.Tags["participant"].Should().Be("CanonicalWriter");
        sameDocumentWait.Tags["phase"].Should().Be("CanonicalPersist");
    }

    [Test]
    public void It_sanitizes_and_bounds_labels_without_document_identifiers_payloads_or_resource_labels()
    {
        const string sensitiveDocumentUuid = "11111111-1111-1111-1111-111111111111";
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheProjectionTargetKey noisyTargetKey = new(
            "tenant-a\n{unsafe-template}" + new string('x', 160),
            new DataStoreId(7)
        );

        telemetry.RecordOutcome(
            DocumentCacheWriterMetricContext.ForCacheWriter(
                RelationalProviderToken.Postgresql,
                noisyTargetKey,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                lifecycleState: null,
                DocumentCacheWriterOutcome.RetryBudgetExhausted
            )
        );

        MetricMeasurement record = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.OutcomeCounterName)
            .Should()
            .ContainSingle()
            .Which;

        record
            .Tags.Values.OfType<string>()
            .Should()
            .OnlyContain(label => label.Length <= 128 && !label.Contains('\n'));
        string joinedLabels = string.Join("|", record.Tags.Values.OfType<string>());
        joinedLabels.Should().NotContain("{");
        joinedLabels.Should().NotContain("}");
        joinedLabels.Should().NotContain(sensitiveDocumentUuid);
        joinedLabels.Should().NotContain("DocumentId");
        joinedLabels.Should().NotContain("DocumentUuid");
        joinedLabels.Should().NotContain("DocumentJson");
        joinedLabels.Should().NotContain("authorization-token");
        joinedLabels.Should().NotContain("ResourceName");
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly Meter _meter = new($"DocumentCacheWriterTelemetryTests.{Guid.NewGuid()}");
        private readonly MeterListener _listener = new();
        private readonly List<MetricMeasurement> _measurements = [];

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == _meter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            LongValue: measurement,
                            IntValue: null,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.SetMeasurementEventCallback<int>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            LongValue: null,
                            IntValue: measurement,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            LongValue: null,
                            IntValue: null,
                            Tags: CopyTags(tags),
                            DoubleValue: measurement
                        )
                    )
            );
            _listener.Start();
        }

        public DocumentCacheWriterTelemetry CreateTelemetry() => new(_meter);

        public MetricMeasurement[] MeasurementsFor(string instrumentName) =>
            [.. _measurements.Where(measurement => measurement.InstrumentName == instrumentName)];

        public void Dispose()
        {
            _listener.Dispose();
            _meter.Dispose();
        }

        private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> result = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                result[tag.Key] = tag.Value;
            }

            return result;
        }
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        long? LongValue,
        int? IntValue,
        Dictionary<string, object?> Tags,
        double? DoubleValue = null
    );
}
