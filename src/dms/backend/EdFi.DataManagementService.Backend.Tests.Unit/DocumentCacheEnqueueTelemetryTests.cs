// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheEnqueueTelemetry")]
public class Given_DocumentCacheEnqueueTelemetry
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 15, 10, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("", 1);
    private const string TargetLabel = "t1_22dea2068aad74fc28655d36";

    [Test]
    public void It_retains_bounded_failures_by_current_target_oldest_to_newest()
    {
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 2);

        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Insert, "first"),
            DocumentCacheEnqueueFailureCategory.ProviderUnavailable
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "second"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "third"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );
        telemetry.RecordFailure(
            Context(
                DocumentCacheTargetKey.Create("other", 2),
                DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
                "other"
            ),
            DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
        );
        telemetry.RecordFailure(
            Context(targetKey: null, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "unknown"),
            DocumentCacheEnqueueFailureCategory.UnclassifiedProviderFailure
        );

        DocumentCacheEnqueueFailureSnapshot snapshot = telemetry.GetFailureSnapshot(TargetKey);

        snapshot.EvictedCount.Should().Be(1);
        snapshot
            .RecentEvents.Select(enqueueFailure => enqueueFailure.Message)
            .Should()
            .Equal("second", "third");
        snapshot
            .RecentEvents.Select(enqueueFailure => enqueueFailure.Category)
            .Should()
            .Equal(
                DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed,
                DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
            );
        telemetry
            .GetFailureSnapshot(DocumentCacheTargetKey.Create("missing", 3))
            .RecentEvents.Should()
            .BeEmpty();
    }

    [Test]
    public void It_drops_removed_configuration_buckets_and_starts_readded_targets_empty()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(TargetKey);
        DocumentCacheTargetObservation replacement = ResolvedTarget(
            DocumentCacheTargetKey.Create("other", 2)
        );
        var registry = new MutableTargetRegistry([target]);
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 10, registry);

        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "retained"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().ContainSingle();

        registry.ReplaceTargets([replacement]);

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();

        registry.ReplaceTargets([target]);

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();
    }

    [Test]
    public void It_retains_same_data_store_failures_only_for_the_exact_current_target()
    {
        DocumentCacheTargetKey tenantATarget = DocumentCacheTargetKey.Create("Tenant-A", 1);
        DocumentCacheTargetKey tenantBTarget = DocumentCacheTargetKey.Create("Tenant-B", 1);
        DocumentCacheTargetKey unconfiguredTenantTarget = DocumentCacheTargetKey.Create("Tenant-C", 1);
        var registry = new MutableTargetRegistry([
            ResolvedTarget(tenantATarget),
            ResolvedTarget(tenantBTarget),
        ]);
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 10, registry);

        telemetry.RecordFailure(
            Context(
                tenantBTarget,
                DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
                "tenant b failure"
            ),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );
        telemetry.RecordFailure(
            Context(
                unconfiguredTenantTarget,
                DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
                "unconfigured tenant failure"
            ),
            DocumentCacheEnqueueFailureCategory.ProviderUnavailable
        );

        telemetry.GetFailureSnapshot(tenantATarget).RecentEvents.Should().BeEmpty();
        telemetry.GetFailureSnapshot(unconfiguredTenantTarget).RecentEvents.Should().BeEmpty();

        DocumentCacheEnqueueFailureSnapshot tenantBSnapshot = telemetry.GetFailureSnapshot(tenantBTarget);
        tenantBSnapshot.RecentEvents.Should().ContainSingle();
        tenantBSnapshot.RecentEvents[0].TargetKey.Should().Be(tenantBTarget);
        tenantBSnapshot.RecentEvents[0].Message.Should().Be("tenant b failure");
    }

    [Test]
    public async Task It_maps_retained_failures_into_status_json_shape()
    {
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(pageSize: 10);
        DocumentCacheTargetObservation target = ResolvedTarget();
        DocumentCacheStatusService service = new(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt)),
            [new ScriptedStatusObserver()],
            new FixedTimeProvider(ObservedAt),
            telemetry
        );

        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Insert, "state missing"),
            DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "work failed"),
            DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.EnqueueFailures.EvictedCount.Should().Be(0);
        statusTarget
            .EnqueueFailures.RecentEvents.Select(enqueueFailure => enqueueFailure.Category)
            .Should()
            .Equal(
                DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid,
                DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed
            );
        statusTarget
            .EnqueueFailures.RecentEvents.Select(enqueueFailure => enqueueFailure.CanonicalOperation)
            .Should()
            .Equal(
                DocumentCacheStatusCanonicalOperation.Insert,
                DocumentCacheStatusCanonicalOperation.Update
            );
        statusTarget
            .EnqueueFailures.ByCategory.Select(categoryCount => (categoryCount.Category, categoryCount.Count))
            .Should()
            .Equal(
                (DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid, 1),
                (DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed, 1)
            );
    }

    [Test]
    public void It_records_enqueue_metrics_and_structured_logs_with_bounded_target_labels()
    {
        using MetricCollector collector = new();
        var logger = new CapturingLogger<DocumentCacheEnqueueTelemetry>();
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(
            pageSize: 10,
            logger: logger,
            meter: collector.Meter
        );

        telemetry.RecordSuccess(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Insert, "committed")
        );
        telemetry.RecordFailure(
            Context(TargetKey, DocumentCacheEnqueueTelemetryCanonicalOperation.Update, "failed\n{unsafe}"),
            DocumentCacheEnqueueFailureCategory.ProviderUnavailable
        );

        MetricMeasurement success = collector
            .MeasurementsFor(DocumentCacheEnqueueTelemetry.SuccessCounterName)
            .Should()
            .ContainSingle()
            .Which;
        success.Tags["provider"].Should().Be("postgresql");
        success.Tags["target"].Should().Be(TargetLabel);
        success.Tags["canonical_operation"].Should().Be("insert");
        success.Tags["resource_kind"].Should().Be("resource");
        success.Tags["outcome"].Should().Be("committed");
        success.Tags.Should().NotContainKey("target_key");

        MetricMeasurement failure = collector
            .MeasurementsFor(DocumentCacheEnqueueTelemetry.FailureCounterName)
            .Should()
            .ContainSingle()
            .Which;
        failure.Tags["target"].Should().Be(TargetLabel);
        failure.Tags["canonical_operation"].Should().Be("update");
        failure.Tags["resource_kind"].Should().Be("resource");
        failure.Tags["category"].Should().Be("providerUnavailable");
        failure.Tags.Should().NotContainKey("target_key");

        logger
            .Entries.Select(entry => entry.Message)
            .Should()
            .Contain(message => message.Contains("DocumentCacheEnqueueSucceeded", StringComparison.Ordinal))
            .And.Contain(message => message.Contains("DocumentCacheEnqueueFailed", StringComparison.Ordinal));
        logger.Entries.Should().OnlyContain(entry => entry.Properties["Target"]!.Equals(TargetLabel));
        logger
            .Entries.SelectMany(entry => entry.Properties.Values.OfType<string>())
            .Should()
            .NotContain(TargetKey.ToString());
    }

    [Test]
    public void It_records_classified_write_boundary_failures_with_unknown_target_without_retention()
    {
        using MetricCollector collector = new();
        var logger = new CapturingLogger<DocumentCacheEnqueueTelemetry>();
        DocumentCacheTargetObservation target = ResolvedTarget(TargetKey);
        var registry = new StaticTargetRegistry([target], [ExecutionContext(target)]);
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(
            pageSize: 10,
            targetRegistry: registry,
            logger: logger,
            meter: collector.Meter
        );

        DocumentCacheEnqueueTelemetryWriteBoundary.RecordFailureIfClassified(
            telemetry,
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            dataStoreSelection: null,
            registry,
            TargetKey.TenantKey,
            SqlDialect.Pgsql,
            DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
            DocumentCacheEnqueueTelemetryResourceKind.Resource,
            new StubDbException("insert or update on table DocumentProjectionWork violates foreign key")
        );

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();

        MetricMeasurement failure = collector
            .MeasurementsFor(DocumentCacheEnqueueTelemetry.FailureCounterName)
            .Should()
            .ContainSingle()
            .Which;
        failure.Tags["provider"].Should().Be("postgresql");
        failure.Tags["target"].Should().Be("unknown");
        failure.Tags["canonical_operation"].Should().Be("update");
        failure.Tags["resource_kind"].Should().Be("resource");
        failure.Tags["category"].Should().Be("workPersistenceFailed");
        failure.Tags.Should().NotContainKey("target_key");

        CapturedLogEntry logEntry = logger.Entries.Should().ContainSingle().Which;
        logEntry.Message.Should().Contain("DocumentCacheEnqueueFailed");
        logEntry.Properties["Target"].Should().Be("unknown");
        logEntry
            .Properties.Values.OfType<string>()
            .Should()
            .NotContain(value => value.Contains(TargetKey.ToString(), StringComparison.Ordinal));
    }

    [Test]
    public void It_does_not_record_unclassified_non_enqueue_failures_from_the_write_boundary()
    {
        using MetricCollector collector = new();
        DocumentCacheTargetObservation target = ResolvedTarget(TargetKey);
        var registry = new StaticTargetRegistry([target], [ExecutionContext(target)]);
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(
            pageSize: 10,
            targetRegistry: registry,
            meter: collector.Meter
        );

        DocumentCacheEnqueueTelemetryWriteBoundary.RecordFailureIfClassified(
            telemetry,
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            CreateSelectedDataStoreSelection(),
            registry,
            TargetKey.TenantKey,
            SqlDialect.Pgsql,
            DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
            DocumentCacheEnqueueTelemetryResourceKind.Resource,
            new StubDbException("ordinary provider write failure")
        );

        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();
        collector.MeasurementsFor(DocumentCacheEnqueueTelemetry.FailureCounterName).Should().BeEmpty();
    }

    [TestCase(
        "dms.DocumentCacheState singleton row is missing or unreadable for projection enqueue.",
        (int)DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
    )]
    [TestCase(
        "dms.DocumentCacheState.ProjectionLifecycleState has unsupported value Broken for projection enqueue.",
        (int)DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid
    )]
    [TestCase(
        "permission denied for function TF_Document_EnqueueProjectionInsert",
        (int)DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable
    )]
    [TestCase(
        "insert or update on table DocumentProjectionWork violates foreign key",
        (int)DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed
    )]
    public void It_classifies_canonical_enqueue_provider_failures(
        string providerMessage,
        int expectedCategory
    )
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException(providerMessage),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out string message
        );

        classified.Should().BeTrue();
        category.Should().Be((DocumentCacheEnqueueFailureCategory)expectedCategory);
        message.Should().NotContain("\r").And.NotContain("\n").And.NotContain("{").And.NotContain("}");
    }

    [Test]
    public void It_does_not_classify_ordinary_write_provider_failures_as_enqueue_failures()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("duplicate key value violates unique constraint"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeFalse();
        category.Should().Be(default(DocumentCacheEnqueueFailureCategory));
    }

    [Test]
    public void It_does_not_classify_unrecognized_non_enqueue_provider_failures()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("ordinary provider write failure"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeFalse();
        category.Should().Be(default(DocumentCacheEnqueueFailureCategory));
    }

    [Test]
    public void It_does_not_classify_transient_provider_failures_without_enqueue_artifacts()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("deadlock detected while writing the canonical resource row"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeFalse();
        category.Should().Be(default(DocumentCacheEnqueueFailureCategory));
    }

    [Test]
    public void It_classifies_provider_command_timeouts_with_enqueue_artifacts_as_provider_timeouts()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("command timeout while inserting into dms.DocumentProjectionWork"),
            new StubProviderCommandTimeoutClassifier(isProviderCommandTimeout: true),
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.ProviderTimeout);
    }

    [Test]
    public void It_classifies_transient_projection_work_failures_as_work_persistence_failed()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("deadlock detected while inserting into dms.DocumentProjectionWork"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
    }

    [Test]
    public void It_classifies_other_enqueue_artifact_transient_failures_as_unclassified_provider_failures()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("deadlock detected while reading dms.DocumentCacheState"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.UnclassifiedProviderFailure);
    }

    [Test]
    public void It_does_not_classify_provider_unavailable_failures_without_enqueue_artifacts()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("connection refused while opening the provider connection"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeFalse();
        category.Should().Be(default(DocumentCacheEnqueueFailureCategory));
    }

    [Test]
    public void It_classifies_provider_unavailable_failures_with_enqueue_artifacts()
    {
        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            new StubDbException("connection refused while reading dms.DocumentCacheState"),
            NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.ProviderUnavailable);
    }

    private static DocumentCacheEnqueueTelemetry CreateTelemetry(
        int pageSize,
        IDocumentCacheTargetRegistry? targetRegistry = null,
        ILogger<DocumentCacheEnqueueTelemetry>? logger = null,
        Meter? meter = null
    )
    {
        DocumentCacheOptions options = new();
        options.Projector.PageSize = pageSize;

        return new(
            Options.Create(options),
            new FixedTimeProvider(ObservedAt),
            logger ?? NullLogger<DocumentCacheEnqueueTelemetry>.Instance,
            targetRegistry,
            meter
        );
    }

    private static DocumentCacheEnqueueTelemetryContext Context(
        DocumentCacheTargetKey? targetKey,
        DocumentCacheEnqueueTelemetryCanonicalOperation operation,
        string message
    ) =>
        new(
            targetKey,
            RelationalProviderToken.Postgresql,
            operation,
            DocumentCacheEnqueueTelemetryResourceKind.Resource,
            message
        );

    private static IDataStoreSelection CreateSelectedDataStoreSelection()
    {
        var selection = new DataStoreSelection();
        selection.SetSelectedDataStore(
            new DataStore(
                TargetKey.DataStoreId,
                "postgresql",
                "document-cache-enqueue-telemetry",
                "Host=localhost;Database=document-cache-enqueue-telemetry",
                [],
                RelationalProviderToken.Postgresql,
                RelationalProviderMetadataStatus.Supported
            )
        );

        return selection;
    }

    private static DocumentCacheTargetObservation ResolvedTarget() => ResolvedTarget(TargetKey);

    private static DocumentCacheTargetObservation ResolvedTarget(DocumentCacheTargetKey targetKey) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            targetKey,
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 10,
                projectorMaxConcurrentTargets: 1,
                projectorFailureBackoff: TimeSpan.FromSeconds(30),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetContextGeneration(1),
            RelationalProviderToken.Postgresql,
            new DocumentCachePhysicalSourceFingerprint(
                "sha256:1111111111111111111111111111111111111111111111111111111111111111"
            ),
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Tracking,
                CacheAheadRecoveryRequired: false
            ),
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

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheTargetObservation target
    ) =>
        new(
            target.TargetKey,
            target.Generation!,
            target.EffectiveSettings,
            new DocumentCacheTargetDataStoreMetadata(target.TargetKey.DataStoreId, "PostgreSQL"),
            new DocumentCacheTargetConnectionInput(
                target.ProviderToken!,
                "Host=localhost;Database=document-cache-enqueue-telemetry"
            ),
            target.PhysicalSourceFingerprint!,
            target.Lifecycle!,
            target.Inventory!,
            target.EnqueueTrigger!,
            target.SqlServerPrerequisites
        );

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StaticTargetRegistry(
        ImmutableArray<DocumentCacheTargetObservation> targets,
        ImmutableArray<DocumentCacheTargetExecutionContext> executionContexts
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = new(targets, ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
            new(executionContexts, ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class MutableTargetRegistry(ImmutableArray<DocumentCacheTargetObservation> targets)
        : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; private set; } =
            new(targets, ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = new([], ObservedAt);

        public void ReplaceTargets(ImmutableArray<DocumentCacheTargetObservation> targets)
        {
            CurrentSnapshot = new DocumentCacheTargetRegistrySnapshot(targets, ObservedAt);
        }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class ScriptedStatusObserver : IDocumentCacheStatusCurrentSourceObserver
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheStatusCurrentSourceObservationResult> ObserveAsync(
            DocumentCacheStatusCurrentSourceObservationRequest request,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                DocumentCacheStatusCurrentSourceObservationResult.Success(
                    DocumentCacheLifecycleState.Tracking,
                    cacheAheadRecoveryRequired: false,
                    DocumentCacheStatusDurableQueuePresence.Empty,
                    oldestWorkFirstEnqueuedAt: null,
                    oldestWorkAgeSeconds: null,
                    ObservedAt
                )
            );
    }

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class StubProviderCommandTimeoutClassifier(bool isProviderCommandTimeout)
        : IDocumentCacheProviderCommandTimeoutClassifier
    {
        public bool IsProviderCommandTimeout(Exception exception) => isProviderCommandTimeout;
    }

    private sealed class MetricCollector : IDisposable
    {
        public Meter Meter { get; } = new($"DocumentCacheEnqueueTelemetryTests.{Guid.NewGuid()}");

        private readonly MeterListener _listener = new();
        private readonly List<MetricMeasurement> _measurements = [];

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Meter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(instrument.Name, LongValue: measurement, Tags: CopyTags(tags))
                    )
            );
            _listener.Start();
        }

        public MetricMeasurement[] MeasurementsFor(string instrumentName) =>
            [.. _measurements.Where(measurement => measurement.InstrumentName == instrumentName)];

        public void Dispose()
        {
            _listener.Dispose();
            Meter.Dispose();
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
