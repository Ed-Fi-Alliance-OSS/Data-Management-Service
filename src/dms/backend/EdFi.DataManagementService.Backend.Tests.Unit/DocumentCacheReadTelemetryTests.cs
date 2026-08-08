// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheReadTelemetry")]
public class Given_DocumentCacheReadTelemetry
{
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("Tenant-A", 7);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [Test]
    public void It_records_bounded_read_lookup_fallback_direct_fill_and_derivative_metrics()
    {
        using MetricCollector collector = new();
        DocumentCacheReadTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheTargetExecutionContext targetContext = ExecutionContext();
        DocumentCacheReadTelemetryContext attemptContext = DocumentCacheReadTelemetryContext.ForTarget(
            targetContext,
            DocumentCacheReadAccelerationOperation.GetById,
            DocumentCacheReadAccelerationResourceKind.Resource,
            DocumentCacheReadTelemetryLabel.Attempted
        );
        DocumentCacheReadTelemetryContext hitContext = DocumentCacheReadTelemetryContext.ForTarget(
            targetContext,
            DocumentCacheReadAccelerationOperation.Query,
            DocumentCacheReadAccelerationResourceKind.Descriptor,
            DocumentCacheReadTelemetryLabel.PageHit
        );
        DocumentCacheReadTelemetryContext fallbackContext = DocumentCacheReadTelemetryContext.ForTarget(
            targetContext,
            DocumentCacheReadAccelerationOperation.GetById,
            DocumentCacheReadAccelerationResourceKind.Resource,
            DocumentCacheReadAccelerationFallbackReason.CacheLookupUnavailable.ToString()
        );

        telemetry.RecordAttempt(attemptContext);
        telemetry.RecordHit(hitContext);
        telemetry.RecordPageHit(hitContext);
        telemetry.RecordMiss(fallbackContext);
        telemetry.RecordFallback(fallbackContext);
        telemetry.RecordCacheUnavailable(fallbackContext);
        telemetry.RecordAdapterAcquisitionFailure(fallbackContext);
        telemetry.RecordUnexpectedException(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.Query,
                DocumentCacheReadAccelerationResourceKind.Resource,
                DocumentCacheReadTelemetryLabel.UnexpectedException
            )
        );
        telemetry.RecordDirectFill(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                DocumentCacheReadAccelerationResourceKind.Resource,
                DocumentCacheReadTelemetryLabel.Attempted
            )
        );
        telemetry.RecordDirectFill(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                DocumentCacheReadAccelerationResourceKind.Resource,
                DocumentCacheReadTelemetryLabel.Succeeded
            )
        );
        telemetry.RecordDirectFill(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                DocumentCacheReadAccelerationResourceKind.Resource,
                DocumentCacheReadTelemetryLabel.Failed
            )
        );
        telemetry.RecordDirectFill(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                DocumentCacheReadAccelerationResourceKind.Resource,
                DocumentCacheReadTelemetryLabel.TimedOut
            )
        );
        telemetry.RecordDirectFill(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.GetById,
                DocumentCacheReadAccelerationResourceKind.Resource,
                DocumentCacheReadTelemetryLabel.Skipped
            )
        );
        telemetry.RecordCacheLookupDuration(attemptContext, TimeSpan.FromMilliseconds(11));
        telemetry.RecordDirectFillDuration(attemptContext, TimeSpan.FromMilliseconds(12));
        telemetry.RecordDerivativeTargetBypass(
            DocumentCacheReadTelemetryContext.ForTarget(
                targetContext,
                DocumentCacheReadAccelerationOperation.Query,
                DocumentCacheReadAccelerationResourceKind.Resource,
                "Snapshot"
            )
        );

        MetricMeasurement attempt = collector
            .MeasurementsFor(DocumentCacheReadTelemetry.AttemptCounterName)
            .Should()
            .ContainSingle()
            .Which;
        attempt.LongValue.Should().Be(1);
        attempt.Tags["provider"].Should().Be("postgresql");
        attempt.Tags["target_key"].Should().Be("Tenant-A:7");
        attempt.Tags["effective_target_kind"].Should().Be(DocumentCacheReadTelemetryLabel.Primary);
        attempt.Tags["operation"].Should().Be(nameof(DocumentCacheReadAccelerationOperation.GetById));
        attempt.Tags["resource_kind"].Should().Be(nameof(DocumentCacheReadAccelerationResourceKind.Resource));
        attempt.Tags["outcome"].Should().Be(DocumentCacheReadTelemetryLabel.Attempted);

        collector.MeasurementsFor(DocumentCacheReadTelemetry.HitCounterName).Should().ContainSingle();
        collector.MeasurementsFor(DocumentCacheReadTelemetry.PageHitCounterName).Should().ContainSingle();
        collector.MeasurementsFor(DocumentCacheReadTelemetry.MissCounterName).Should().ContainSingle();
        collector.MeasurementsFor(DocumentCacheReadTelemetry.FallbackCounterName).Should().ContainSingle();
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.CacheUnavailableCounterName)
            .Should()
            .ContainSingle();
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.AdapterAcquisitionFailureCounterName)
            .Should()
            .ContainSingle();
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.UnexpectedExceptionCounterName)
            .Should()
            .ContainSingle();
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.DirectFillCounterName)
            .Select(measurement => measurement.Tags["outcome"])
            .Should()
            .BeEquivalentTo([
                DocumentCacheReadTelemetryLabel.Attempted,
                DocumentCacheReadTelemetryLabel.Succeeded,
                DocumentCacheReadTelemetryLabel.Failed,
                DocumentCacheReadTelemetryLabel.TimedOut,
                DocumentCacheReadTelemetryLabel.Skipped,
            ]);
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.CacheLookupDurationName)
            .Should()
            .ContainSingle()
            .Which.DoubleValue.Should()
            .Be(11);
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.DirectFillDurationName)
            .Should()
            .ContainSingle()
            .Which.DoubleValue.Should()
            .Be(12);
        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.DerivativeTargetBypassCounterName)
            .Should()
            .ContainSingle();

        AssertAllowedTelemetryTags(attempt);
    }

    [Test]
    public void It_sanitizes_and_bounds_labels_without_document_or_query_payloads()
    {
        using MetricCollector collector = new();
        DocumentCacheReadTelemetry telemetry = collector.CreateTelemetry();
        string sensitiveDocumentUuid = "11111111-1111-1111-1111-111111111111";
        DocumentCacheReadTelemetryContext context = DocumentCacheReadTelemetryContext.ForNoTarget(
            DocumentCacheReadAccelerationOperation.GetById,
            DocumentCacheReadAccelerationResourceKind.Resource,
            "Unsafe\n{template}" + new string('x', 160)
        );

        telemetry.RecordFallback(context);

        MetricMeasurement fallback = collector
            .MeasurementsFor(DocumentCacheReadTelemetry.FallbackCounterName)
            .Should()
            .ContainSingle()
            .Which;
        fallback
            .Tags.Values.OfType<string>()
            .Should()
            .OnlyContain(label => label.Length <= 128 && !label.Contains('\n'));
        string joinedLabels = string.Join("|", fallback.Tags.Values.OfType<string>());
        joinedLabels.Should().NotContain("{");
        joinedLabels.Should().NotContain("}");
        joinedLabels.Should().NotContain(sensitiveDocumentUuid);
        joinedLabels.Should().NotContain("DocumentId");
        joinedLabels.Should().NotContain("DocumentUuid");
        joinedLabels.Should().NotContain("DocumentJson");
        joinedLabels.Should().NotContain("authorization-token");
        joinedLabels.Should().NotContain("query=");
        joinedLabels.Should().NotContain("Namespace");
    }

    [Test]
    public void It_records_raw_bounded_lookup_outcome_labels_for_miss_metrics()
    {
        using MetricCollector collector = new();
        DocumentCacheReadTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheTargetExecutionContext targetContext = ExecutionContext();
        DocumentCacheReadLookupOutcome[] rawOutcomes =
        [
            DocumentCacheReadLookupOutcome.LifecycleDisabled,
            DocumentCacheReadLookupOutcome.LifecycleResetting,
            DocumentCacheReadLookupOutcome.LifecycleRebuilding,
            DocumentCacheReadLookupOutcome.CacheAheadRecoveryRequired,
            DocumentCacheReadLookupOutcome.MissingCacheRow,
            DocumentCacheReadLookupOutcome.MissingSourceRow,
            DocumentCacheReadLookupOutcome.SourceDrift,
            DocumentCacheReadLookupOutcome.StaleCacheRow,
            DocumentCacheReadLookupOutcome.MissingLifecycleState,
            DocumentCacheReadLookupOutcome.InvalidLifecycleState,
            DocumentCacheReadLookupOutcome.ProjectionTargetIneligible,
            DocumentCacheReadLookupOutcome.ProviderPrerequisiteIneligible,
            DocumentCacheReadLookupOutcome.CacheUnavailable,
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure,
        ];

        foreach (DocumentCacheReadLookupOutcome rawOutcome in rawOutcomes)
        {
            telemetry.RecordMiss(
                DocumentCacheReadTelemetryContext.ForTarget(
                    targetContext,
                    DocumentCacheReadAccelerationOperation.GetById,
                    DocumentCacheReadAccelerationResourceKind.Resource,
                    rawOutcome.ToString()
                )
            );
        }

        collector
            .MeasurementsFor(DocumentCacheReadTelemetry.MissCounterName)
            .Select(measurement => measurement.Tags["outcome"])
            .Should()
            .BeEquivalentTo(rawOutcomes.Select(static outcome => outcome.ToString()));
    }

    private static void AssertAllowedTelemetryTags(MetricMeasurement measurement)
    {
        measurement
            .Tags.Keys.Should()
            .BeEquivalentTo(
                "provider",
                "target_key",
                "effective_target_kind",
                "operation",
                "resource_kind",
                "outcome"
            );
    }

    private static DocumentCacheTargetExecutionContext ExecutionContext() =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 3,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "Host=localhost"),
            Fingerprint,
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private sealed class MetricCollector : IDisposable
    {
        private readonly Meter _meter = new($"DocumentCacheReadTelemetryTests.{Guid.NewGuid()}");
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
                            DoubleValue: null,
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
                            DoubleValue: measurement,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.Start();
        }

        public DocumentCacheReadTelemetry CreateTelemetry() => new(_meter);

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
        double? DoubleValue,
        Dictionary<string, object?> Tags
    );
}
