// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheStatusTelemetry")]
public class Given_DocumentCacheStatusTelemetry
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 16, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("", 1);
    private const string TargetLabel = "t1_22dea2068aad74fc28655d36";

    [Test]
    public void It_records_status_observation_metrics_and_structured_log_with_bounded_target()
    {
        using MetricCollector collector = new();
        var logger = new CapturingLogger<DocumentCacheStatusTelemetry>();
        DocumentCacheStatusTelemetry telemetry = collector.CreateTelemetry(logger);
        DocumentCacheTargetObservation targetObservation = ResolvedTarget();

        telemetry.RecordStatusObservation(targetObservation, StatusTarget());

        MetricMeasurement observation = collector
            .MeasurementsFor(DocumentCacheStatusTelemetry.StatusObservationCounterName)
            .Should()
            .ContainSingle()
            .Which;
        observation.LongValue.Should().Be(1);
        observation.Tags["provider"].Should().Be("postgresql");
        observation.Tags["target"].Should().Be(TargetLabel);
        observation.Tags["lifecycle"].Should().Be("tracking");
        observation.Tags["queue_presence"].Should().Be("notEmpty");
        observation.Tags["operational_health_status"].Should().Be("operational");
        observation.Tags["operational_health_reason"].Should().Be("none");
        observation.Tags["caught_up_status"].Should().Be("notCaughtUp");
        observation.Tags["caught_up_reason"].Should().Be("queueNotEmpty");

        CapturedLogEntry logEntry = logger.Entries.Should().ContainSingle().Which;
        logEntry.Message.Should().Contain("DocumentCacheStatusObserved");
        logEntry.Properties["Target"].Should().Be(TargetLabel);
        logEntry.Properties.Values.OfType<string>().Should().NotContain(TargetKey.ToString());
    }

    [Test]
    public void It_records_provider_duration_and_oldest_work_age_metrics()
    {
        using MetricCollector collector = new();
        DocumentCacheStatusTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordProviderObservation(
            TargetKey,
            RelationalProviderToken.Postgresql,
            DocumentCacheStatusProviderObservationTelemetryOutcome.Succeeded,
            DocumentCacheStatusReason.None,
            TimeSpan.FromMilliseconds(1250),
            DocumentCacheLifecycleState.Tracking,
            oldestWorkAgeSeconds: 42.5
        );

        MetricMeasurement duration = collector
            .MeasurementsFor(DocumentCacheStatusTelemetry.ProviderObservationDurationName)
            .Should()
            .ContainSingle()
            .Which;
        duration.DoubleValue.Should().Be(1.25);
        duration.Tags["provider"].Should().Be("postgresql");
        duration.Tags["target"].Should().Be(TargetLabel);
        duration.Tags["outcome"].Should().Be("succeeded");
        duration.Tags["reason"].Should().Be("none");

        MetricMeasurement oldestWorkAge = collector
            .MeasurementsFor(DocumentCacheStatusTelemetry.OldestWorkAgeName)
            .Should()
            .ContainSingle()
            .Which;
        oldestWorkAge.DoubleValue.Should().Be(42.5);
        oldestWorkAge.Tags["provider"].Should().Be("postgresql");
        oldestWorkAge.Tags["target"].Should().Be(TargetLabel);
        oldestWorkAge.Tags["lifecycle"].Should().Be("tracking");
    }

    [Test]
    public void It_records_state_missing_or_invalid_oldest_work_age_with_unknown_lifecycle_label()
    {
        using MetricCollector collector = new();
        DocumentCacheStatusTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordProviderObservation(
            TargetKey,
            RelationalProviderToken.Postgresql,
            DocumentCacheStatusProviderObservationTelemetryOutcome.Succeeded,
            DocumentCacheStatusReason.None,
            TimeSpan.FromMilliseconds(25),
            lifecycleState: null,
            oldestWorkAgeSeconds: 17.25
        );

        MetricMeasurement oldestWorkAge = collector
            .MeasurementsFor(DocumentCacheStatusTelemetry.OldestWorkAgeName)
            .Should()
            .ContainSingle()
            .Which;
        oldestWorkAge.DoubleValue.Should().Be(17.25);
        oldestWorkAge.Tags["provider"].Should().Be("postgresql");
        oldestWorkAge.Tags["target"].Should().Be(TargetLabel);
        oldestWorkAge.Tags["lifecycle"].Should().Be("unknown");
    }

    [Test]
    public void It_records_provider_failure_metric_and_warning_log_without_oldest_work_age()
    {
        using MetricCollector collector = new();
        var logger = new CapturingLogger<DocumentCacheStatusTelemetry>();
        DocumentCacheStatusTelemetry telemetry = collector.CreateTelemetry(logger);

        telemetry.RecordProviderObservation(
            TargetKey,
            RelationalProviderToken.Postgresql,
            DocumentCacheStatusProviderObservationTelemetryOutcome.Failed,
            DocumentCacheStatusReason.ProviderObservationFailed,
            TimeSpan.FromMilliseconds(10),
            lifecycleState: null,
            oldestWorkAgeSeconds: null
        );

        MetricMeasurement duration = collector
            .MeasurementsFor(DocumentCacheStatusTelemetry.ProviderObservationDurationName)
            .Should()
            .ContainSingle()
            .Which;
        duration.Tags["outcome"].Should().Be("failed");
        duration.Tags["reason"].Should().Be("providerObservationFailed");
        collector.MeasurementsFor(DocumentCacheStatusTelemetry.OldestWorkAgeName).Should().BeEmpty();

        CapturedLogEntry logEntry = logger.Entries.Should().ContainSingle().Which;
        logEntry.Level.Should().Be(LogLevel.Warning);
        logEntry.Message.Should().Contain("DocumentCacheStatusProviderObservationFailed");
        logEntry.Properties["Target"].Should().Be(TargetLabel);
        logEntry.Properties["Reason"].Should().Be("providerObservationFailed");
    }

    private static DocumentCacheTargetObservation ResolvedTarget() =>
        DocumentCacheTargetObservation.ResolvedEligible(
            TargetKey,
            EffectiveSettings(),
            new DocumentCacheTargetContextGeneration(1),
            RelationalProviderToken.Postgresql,
            new DocumentCachePhysicalSourceFingerprint(
                "sha256:1111111111111111111111111111111111111111111111111111111111111111"
            ),
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

    private static DocumentCacheStatusTarget StatusTarget() =>
        new(
            DocumentCacheStatusTargetKey.FromTargetKey(TargetKey),
            targetGeneration: 1,
            ObservedAt,
            durableObservedAt: ObservedAt,
            provider: "postgresql",
            physicalSourceFingerprint: "opaque",
            new DocumentCacheStatusResolutionComponent(
                DocumentCacheStatusResolutionStatus.Resolved,
                DocumentCacheStatusResolutionReason.None,
                ObservedAt,
                message: null
            ),
            new DocumentCacheStatusEligibilityComponent(
                DocumentCacheStatusEligibilityStatus.Eligible,
                DocumentCacheStatusReason.None,
                message: null
            ),
            new DocumentCacheStatusInventoryComponentGroup(
                ObservedAt,
                ValidInventoryComponent(),
                ValidInventoryComponent(),
                ValidInventoryComponent(),
                ValidInventoryComponent(),
                new DocumentCacheStatusEnqueueTriggerComponent(
                    DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                    DocumentCacheStatusInventoryReason.None,
                    message: null
                )
            ),
            new DocumentCacheStatusProviderPrerequisitesComponent(
                DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                DocumentCacheStatusProviderPrerequisiteReason.None,
                ObservedAt,
                NotApplicableProviderPrerequisiteComponent(),
                NotApplicableProviderPrerequisiteComponent()
            ),
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Tracking,
                DocumentCacheStatusAvailability.Available,
                message: null
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Clear,
                recoveryRequired: false,
                message: null
            ),
            new DocumentCacheOperationalHealthComponent(
                DocumentCacheOperationalHealthStatus.Operational,
                DocumentCacheStatusReason.None,
                message: null
            ),
            new DocumentCacheCaughtUpComponent(
                DocumentCacheCaughtUpStatus.NotCaughtUp,
                DocumentCacheStatusReason.QueueNotEmpty,
                message: null
            ),
            new DocumentCacheStatusQueueSummary(
                DocumentCacheStatusQueuePresence.NotEmpty,
                ObservedAt.AddSeconds(-42),
                oldestWorkAgeSeconds: 42,
                DocumentCacheStatusBacklogEstimate.Unavailable
            ),
            new DocumentCacheStatusExecutionStateComponent(
                DocumentCacheStatusExecutionState.Idle,
                ObservedAt,
                activeWorkers: 0,
                concurrencySlotsUsed: 0,
                targetBackoffUntil: null,
                lastSuccessfulWorkAt: ObservedAt.AddSeconds(-5),
                lastFailureAt: null,
                message: null
            ),
            activeCommand: null,
            lastEndedDiagnostic: null,
            new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(),
            new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
            new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
            DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(EffectiveSettings()),
            new DocumentCacheStatusEnqueueFailures()
        );

    private static DocumentCacheStatusInventoryComponent ValidInventoryComponent() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, message: null);

    private static DocumentCacheStatusProviderPrerequisiteComponent NotApplicableProviderPrerequisiteComponent() =>
        new(
            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            message: null
        );

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings() =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromSeconds(2),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 100,
            projectorMaxConcurrentTargets: 1,
            projectorFailureBackoff: TimeSpan.FromSeconds(30),
            projectorBaselineHighWaterMark: 10000,
            administrationWorkflowTimeout: TimeSpan.FromMinutes(10)
        );

    private sealed class MetricCollector : IDisposable
    {
        private readonly Meter _meter = new($"DocumentCacheStatusTelemetryTests.{Guid.NewGuid()}");
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

        public DocumentCacheStatusTelemetry CreateTelemetry(
            ILogger<DocumentCacheStatusTelemetry>? logger = null
        ) => new(_meter, logger);

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

    private sealed record CapturedLogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties
    );

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Dictionary<string, object?> properties = [];
            if (state is IEnumerable<KeyValuePair<string, object?>> stateProperties)
            {
                foreach (KeyValuePair<string, object?> property in stateProperties)
                {
                    properties[property.Key] = property.Value;
                }
            }

            Entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), properties));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose() { }
    }
}
