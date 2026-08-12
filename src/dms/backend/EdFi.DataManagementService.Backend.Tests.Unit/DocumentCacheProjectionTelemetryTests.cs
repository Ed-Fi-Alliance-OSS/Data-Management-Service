// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheProjectionTelemetry")]
public class Given_DocumentCacheProjectionTelemetry
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("Tenant-A", 7);
    private static readonly DocumentCacheTargetContextGeneration Generation = new(3);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [Test]
    public void It_records_target_state_poison_and_failure_metrics_without_document_id_labels()
    {
        using MetricCollector collector = new();
        DocumentCacheProjectionTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheProjectionTargetHealthSnapshot snapshot = new(
            TargetKey,
            Generation,
            effectiveProjectorPageSize: 2,
            ObservedAt,
            RelationalProviderToken.Postgresql,
            Fingerprint,
            new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: true,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: false,
                cancellationObservedAt: null
            ),
            lifecycleFence: DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycle(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
                ObservedAt
            ),
            poisonTraversal: new DocumentCacheProjectionPoisonTraversalSnapshot(
                effectiveProjectorPageSize: 2,
                suppressedDocumentCount: 3,
                earliestRetryAt: ObservedAt.AddSeconds(30),
                suppressedDocumentIds: [101, 102, 103]
            ),
            failureDiagnostics: new DocumentCacheProjectionFailureDiagnostics(
                effectiveProjectorPageSize: 2,
                failureCount: 2,
                earliestRetryAt: ObservedAt.AddSeconds(30),
                evictionCount: 1,
                documentDiagnostics:
                [
                    new DocumentCacheProjectionDocumentDiagnostic(
                        201,
                        DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
                        "work anomaly",
                        ObservedAt
                    ),
                    new DocumentCacheProjectionDocumentDiagnostic(
                        202,
                        DocumentCacheProjectionDocumentDiagnosticCategory.ProviderFailure,
                        "provider failure",
                        ObservedAt
                    ),
                ]
            )
        );

        telemetry.RecordTargetObservation(snapshot);

        MetricMeasurement targetState = collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.TargetStateCounterName)
            .Should()
            .ContainSingle()
            .Which;
        targetState.LongValue.Should().Be(1);
        targetState.Tags["provider"].Should().Be("postgresql");
        targetState.Tags["target_key"].Should().Be("Tenant-A:7");
        targetState.Tags["outcome"].Should().Be(DocumentCacheProjectionTelemetryLabel.Active);
        targetState.Tags["lifecycle"].Should().Be(nameof(DocumentCacheLifecycleState.Tracking));

        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.PoisonSuppressedCountName)
            .Should()
            .ContainSingle()
            .Which.IntValue.Should()
            .Be(3);
        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.FailureBackoffCountName)
            .Should()
            .ContainSingle()
            .Which.IntValue.Should()
            .Be(2);

        AssertAllowedTelemetryTags(targetState);
        string joinedLabels = string.Join("|", targetState.Tags.Values.OfType<string>());
        joinedLabels.Should().NotContain("101");
        joinedLabels.Should().NotContain("201");
        joinedLabels.Should().NotContain("DocumentId");
        joinedLabels.Should().NotContain(Fingerprint.Value);
    }

    [Test]
    public void It_records_active_administrative_command_labels_for_target_observations()
    {
        using MetricCollector collector = new();
        DocumentCacheProjectionTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheAdministrativeCommandExecutionId executionId =
            DocumentCacheAdministrativeCommandExecutionId.New();
        DocumentCacheProjectionTargetHealthSnapshot snapshot = new(
            TargetKey,
            Generation,
            effectiveProjectorPageSize: 2,
            ObservedAt,
            RelationalProviderToken.Postgresql,
            Fingerprint,
            new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: true,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: false,
                cancellationObservedAt: null
            ),
            lifecycleFence: DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycle(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false),
                ObservedAt
            ),
            activeCommandExecutionId: executionId,
            activeAdministrativeCommand: DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            activeAdministrativePhase: DocumentCacheAdministrativeCommandPhase.SeedBaseline
        );

        telemetry.RecordTargetObservation(snapshot);

        MetricMeasurement targetState = collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.TargetStateCounterName)
            .Should()
            .ContainSingle()
            .Which;
        targetState
            .Tags["command"]
            .Should()
            .Be(nameof(DocumentCacheAdministrativeCommand.OnlineCacheRebuild));
        targetState.Tags["phase"].Should().Be(nameof(DocumentCacheAdministrativeCommandPhase.SeedBaseline));
        targetState.Tags["command"].Should().NotBe(DocumentCacheProjectionTelemetryLabel.None);
        targetState.Tags["phase"].Should().NotBe(DocumentCacheProjectionTelemetryLabel.None);
    }

    [Test]
    public void It_records_target_diagnostic_category_for_target_observations()
    {
        using MetricCollector collector = new();
        DocumentCacheProjectionTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheProjectionTargetHealthSnapshot snapshot = new(
            TargetKey,
            Generation,
            effectiveProjectorPageSize: 2,
            ObservedAt,
            RelationalProviderToken.Postgresql,
            Fingerprint,
            lifecycleFence: DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycle(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
                ObservedAt
            ),
            targetDiagnostics:
            [
                new DocumentCacheTargetDiagnostic(
                    TargetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    RelationalProviderToken.Postgresql,
                    Generation,
                    physicalSourceFingerprint: null,
                    lifecycle: null,
                    inventory: null,
                    enqueueTrigger: null,
                    sqlServerPrerequisites: null,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.DeterministicInvariantFailure,
                    "DocumentCache read invariant failure."
                ),
            ]
        );

        telemetry.RecordTargetObservation(snapshot);

        MetricMeasurement targetState = collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.TargetStateCounterName)
            .Should()
            .ContainSingle()
            .Which;
        targetState
            .Tags["category"]
            .Should()
            .Be(nameof(DocumentCacheTargetDiagnosticCategory.DeterministicInvariantFailure));
    }

    [Test]
    public void It_records_administrative_phase_mutation_mutex_and_result_metrics()
    {
        using MetricCollector collector = new();
        DocumentCacheProjectionTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheAdministrativeCommandObservationSnapshot observation = new(
            DocumentCacheAdministrativeCommandExecutionId.New(),
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            TargetKey,
            Generation,
            effectiveProjectorPageSize: 2,
            effectiveWorkflowTimeout: TimeSpan.FromMinutes(5),
            startedAt: ObservedAt,
            observedAt: ObservedAt.AddMilliseconds(25),
            currentPhase: DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            lastCompletedPhase: DocumentCacheAdministrativeCommandPhase.Preflight,
            mutated: true,
            physicalSourceFingerprint: Fingerprint,
            lifecycle: DocumentCacheLifecycleState.Rebuilding,
            cacheAheadRecoveryRequired: false,
            elapsedCommandTime: TimeSpan.FromMilliseconds(25),
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                    DocumentCacheAdministrativeCommandPhase.Preflight,
                    retryable: true,
                    diagnosticCategory: DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
                    affectedDocumentIds: [301, 302],
                    "bounded affected ids"
                ),
            ]
        );
        DocumentCacheAdministrativeCommandResult result = new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey),
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
            mutated: true,
            targetGeneration: Generation.Value,
            physicalSourceFingerprint: Fingerprint,
            lifecycle: DocumentCacheLifecycleState.Rebuilding,
            cacheAheadRecoveryRequired: false,
            phaseDiagnostics: observation.PhaseDiagnostics,
            elapsedCommandTime: TimeSpan.FromMilliseconds(31)
        );

        telemetry.RecordAdministrativeCommandObservation(observation, RelationalProviderToken.SqlServer);
        telemetry.RecordAdministrativeCommandMutation(observation, RelationalProviderToken.SqlServer);
        telemetry.RecordAdministrativeMutexOutcome(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            TargetKey,
            RelationalProviderToken.SqlServer,
            DocumentCacheAdministrativeCommandClassification.Succeeded.ToString(),
            category: null,
            TimeSpan.FromMilliseconds(7)
        );
        telemetry.RecordAdministrativeCommandResult(
            result,
            RelationalProviderToken.SqlServer,
            TimeSpan.FromMinutes(5),
            DocumentCacheAdministrativeCommandPhase.SeedBaseline
        );

        MetricMeasurement phase = collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.AdministrativePhaseCounterName)
            .Should()
            .ContainSingle()
            .Which;
        phase.Tags["provider"].Should().Be("sqlserver");
        phase.Tags["command"].Should().Be(nameof(DocumentCacheAdministrativeCommand.OnlineCacheRebuild));
        phase.Tags["phase"].Should().Be(nameof(DocumentCacheAdministrativeCommandPhase.SeedBaseline));
        phase
            .Tags["category"]
            .Should()
            .Be(nameof(DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout));

        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.AdministrativeMutationCounterName)
            .Should()
            .ContainSingle();
        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.AdministrativeMutexDurationName)
            .Should()
            .ContainSingle()
            .Which.DoubleValue.Should()
            .Be(7);
        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.AdministrativeCommandDurationName)
            .Should()
            .ContainSingle()
            .Which.DoubleValue.Should()
            .Be(31);

        MetricMeasurement commandResult = collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.AdministrativeCommandResultCounterName)
            .Should()
            .ContainSingle()
            .Which;
        commandResult
            .Tags["outcome"]
            .Should()
            .Be(nameof(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable));
        commandResult
            .Tags["category"]
            .Should()
            .Be(nameof(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout));
        AssertAllowedTelemetryTags(commandResult);

        string joinedLabels = string.Join("|", collector.AllTagValues.OfType<string>());
        joinedLabels.Should().NotContain("301");
        joinedLabels.Should().NotContain("302");
        joinedLabels.Should().NotContain(Fingerprint.Value);
    }

    [Test]
    public void It_clamps_negative_elapsed_durations_to_zero()
    {
        using MetricCollector collector = new();
        DocumentCacheProjectionTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheProjectionTargetRuntimeContext context = RuntimeContext();
        DocumentCacheProjectionSchedulerDispatchResult dispatchResult =
            DocumentCacheProjectionSchedulerDispatchResult.Faulted(
                context,
                DocumentCacheProjectionDrainPageResult.PageProcessed(2),
                ObservedAt,
                ObservedAt.AddMilliseconds(-15)
            );

        Action recordDispatch = () =>
            telemetry.RecordSchedulerDispatch(
                context,
                dispatchResult,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            );
        Action recordMutex = () =>
            telemetry.RecordAdministrativeMutexOutcome(
                DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
                TargetKey,
                RelationalProviderToken.Postgresql,
                DocumentCacheAdministrativeCommandClassification.ProviderConcurrencyRetryExhausted.ToString(),
                DocumentCacheAdministrativeDiagnosticCategory.ProviderConcurrencyRetryExhausted,
                TimeSpan.FromMilliseconds(-9)
            );

        recordDispatch.Should().NotThrow();
        recordMutex.Should().NotThrow();

        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.DispatchDurationName)
            .Should()
            .ContainSingle()
            .Which.DoubleValue.Should()
            .Be(0);
        collector
            .MeasurementsFor(DocumentCacheProjectionTelemetry.AdministrativeMutexDurationName)
            .Should()
            .ContainSingle()
            .Which.DoubleValue.Should()
            .Be(0);
    }

    [Test]
    public void It_sanitizes_and_bounds_metric_context_labels()
    {
        DocumentCacheProjectionTelemetryContext context = new(
            "postgresql\n{provider}",
            "tenant\n{unsafe}" + new string('x', 160),
            "outcome\n{unsafe}",
            "category\n{unsafe}",
            "Tracking",
            "OnlineCacheRebuild",
            "SeedBaseline"
        );

        Dictionary<string, object?> tags = MetricCollector.CopyTags(context.ToTags());

        tags.Values.OfType<string>().Should().OnlyContain(value => value.Length <= 128);
        string joinedLabels = string.Join("|", tags.Values.OfType<string>());
        joinedLabels.Should().NotContain("\n");
        joinedLabels.Should().NotContain("{");
        joinedLabels.Should().NotContain("}");
    }

    private static void AssertAllowedTelemetryTags(MetricMeasurement measurement)
    {
        measurement
            .Tags.Keys.Should()
            .BeSubsetOf(["provider", "target_key", "outcome", "category", "lifecycle", "command", "phase"]);
    }

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext()
    {
        DocumentCacheTargetExecutionContext executionContext = new(
            TargetKey,
            Generation,
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
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
            Fingerprint,
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

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                MaterializationTargetContext(),
                new ThrowingDocumentCacheMaterializer(),
                new ThrowingDocumentCacheWriter()
            ),
            new NoOpObservationSink()
        );
    }

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext() =>
        new(
            new DocumentCacheProjectionTargetKey(TargetKey.TenantKey, new DataStoreId(TargetKey.DataStoreId)),
            MappingSet(),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            "connection"
        );

    private static MappingSet MappingSet()
    {
        EffectiveSchemaInfo effectiveSchema = new(
            ApiSchemaFormatVersion: "5.2.0",
            RelationalMappingVersion: "v2",
            EffectiveSchemaHash: "schema-hash",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        return new MappingSet(
            new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                SqlDialect.Pgsql,
                effectiveSchema.RelationalMappingVersion
            ),
            new DerivedRelationalModelSet(effectiveSchema, SqlDialect.Pgsql, [], [], [], [], [], []),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly Meter _meter = new($"DocumentCacheProjectionTelemetryTests.{Guid.NewGuid()}");
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

        public IEnumerable<object?> AllTagValues =>
            _measurements.SelectMany(measurement => measurement.Tags.Values);

        public DocumentCacheProjectionTelemetry CreateTelemetry() => new(_meter);

        public MetricMeasurement[] MeasurementsFor(string instrumentName) =>
            [.. _measurements.Where(measurement => measurement.InstrumentName == instrumentName)];

        public void Dispose()
        {
            _listener.Dispose();
            _meter.Dispose();
        }

        public static Dictionary<string, object?> CopyTags(TagList tags)
        {
            Dictionary<string, object?> result = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                result[tag.Key] = tag.Value;
            }

            return result;
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

    private sealed class ThrowingDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotSupportedException("Telemetry tests do not materialize documents.");
    }

    private sealed class ThrowingDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotSupportedException("Telemetry tests do not write documents.");
    }

    private sealed class NoOpObservationSink : IDocumentCacheProjectionObservationSink
    {
        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => _ = (contextKey, endReason, endedAt);

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }
}
