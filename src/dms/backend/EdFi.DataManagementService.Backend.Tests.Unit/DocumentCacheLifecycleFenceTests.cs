// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheLifecycleFence")]
public class Given_DocumentCacheLifecycleFence
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);

    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheInventoryValidationResult SatisfiedInventory = new(
        DocumentCacheInventoryStatus.Satisfied,
        "Inventory satisfied."
    );

    private static readonly DocumentCacheEnqueueTriggerValidationResult SatisfiedEnqueueTrigger = new(
        DocumentCacheEnqueueTriggerStatus.Satisfied,
        "Enqueue trigger satisfied."
    );

    [Test]
    public async Task It_observes_DocumentCacheLifecycleFence_on_startup_and_pauses_when_disabled()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        RecordingLifecycleReader lifecycleReader = new();
        lifecycleReader.QueueSuccess(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = RegistryFor(executionContext);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            lifecycleReader
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheProjectionTargetRuntimeContext context = targetContextFactory
            .CreatedContexts.Should()
            .ContainSingle()
            .Subject;
        context.SchedulingState.IsTargetPaused.Should().BeTrue();
        lifecycleReader.ConnectionStrings.Should().ContainSingle().Which.Should().Be("connection-a");

        DocumentCacheProjectionTargetHealthSnapshot snapshot = observationSink
            .TargetSnapshots.Should()
            .ContainSingle()
            .Subject;
        snapshot.LifecycleFence.State.Should().Be(DocumentCacheProjectionLifecycleFenceState.Fenced);
        snapshot.LifecycleFence.Lifecycle!.State.Should().Be(DocumentCacheLifecycleState.Disabled);
        snapshot
            .LifecycleFence.DiagnosticCategory.Should()
            .Be(DocumentCacheTargetDiagnosticCategory.LifecycleMismatch);
    }

    [Test]
    public async Task It_refreshes_DocumentCacheLifecycleFence_pause_without_a_later_writer_attempt()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        RecordingLifecycleReader lifecycleReader = new();
        lifecycleReader.QueueSuccess(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        lifecycleReader.QueueSuccess(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(Snapshot(executionContext), RuntimeSnapshot(executionContext));
        registry.QueueRefresh(Snapshot(executionContext), RuntimeSnapshot(executionContext));
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            lifecycleReader
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext context = targetContextFactory.CreatedContexts.Single();
        context.SchedulingState.RecordOrdinaryDrainCompleted(
            DocumentCacheProjectionDrainPageResult.LifecycleFenced,
            ObservedAt,
            ObservedAt,
            executionContext.EffectiveSettings.ProjectorPollInterval
        );
        context.SchedulingState.IsTargetPaused.Should().BeTrue();

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        context.SchedulingState.IsTargetPaused.Should().BeFalse();
        observationSink
            .TargetSnapshots[^1]
            .LifecycleFence.State.Should()
            .Be(DocumentCacheProjectionLifecycleFenceState.Eligible);
        lifecycleReader.ConnectionStrings.Should().HaveCount(2);
    }

    [Test]
    public async Task It_treats_rebuilding_clear_latch_as_DocumentCacheLifecycleFence_eligible_without_success_evidence()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        RecordingLifecycleReader lifecycleReader = new();
        lifecycleReader.QueueSuccess(
            DocumentCacheLifecycleState.Rebuilding,
            cacheAheadRecoveryRequired: false
        );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = RegistryFor(executionContext);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            lifecycleReader
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheProjectionTargetRuntimeContext context = targetContextFactory.CreatedContexts.Single();
        context.SchedulingState.IsTargetPaused.Should().BeFalse();
        DocumentCacheProjectionTargetHealthSnapshot snapshot = observationSink.TargetSnapshots.Single();
        snapshot.LifecycleFence.State.Should().Be(DocumentCacheProjectionLifecycleFenceState.Eligible);
        snapshot.LifecycleFence.Lifecycle!.State.Should().Be(DocumentCacheLifecycleState.Rebuilding);
        snapshot.LastSuccess.Should().BeNull();
    }

    [Test]
    public async Task It_keeps_DocumentCacheLifecycleFence_paused_when_latch_is_set()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        RecordingLifecycleReader lifecycleReader = new();
        lifecycleReader.QueueSuccess(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = RegistryFor(executionContext);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            lifecycleReader
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheProjectionTargetRuntimeContext context = targetContextFactory.CreatedContexts.Single();
        context.SchedulingState.IsTargetPaused.Should().BeTrue();
        DocumentCacheProjectionTargetHealthSnapshot snapshot = observationSink.TargetSnapshots.Single();
        snapshot.LifecycleFence.State.Should().Be(DocumentCacheProjectionLifecycleFenceState.Fenced);
        snapshot
            .LifecycleFence.DiagnosticCategory.Should()
            .Be(DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet);
    }

    [Test]
    public async Task It_keeps_DocumentCacheLifecycleFence_paused_with_sanitized_unreadable_diagnostics()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext();
        RecordingLifecycleReader lifecycleReader = new();
        lifecycleReader.QueueException(new InvalidOperationException("secret connection text"));
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = RegistryFor(executionContext);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            lifecycleReader
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheProjectionTargetRuntimeContext context = targetContextFactory.CreatedContexts.Single();
        context.SchedulingState.IsTargetPaused.Should().BeTrue();
        DocumentCacheProjectionLifecycleFenceSnapshot fence = observationSink
            .TargetSnapshots.Single()
            .LifecycleFence;
        fence.State.Should().Be(DocumentCacheProjectionLifecycleFenceState.Fenced);
        fence
            .DiagnosticCategory.Should()
            .Be(DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure);
        fence.Message.Should().Be("DocumentCache lifecycle is unreadable.");
        fence.Message.Should().NotContain("secret");
    }

    private static DocumentCacheProjectionSupervisor CreateSupervisor(
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory,
        IDocumentCacheProjectionObservationSink observationSink,
        IDocumentCacheLifecycleReader lifecycleReader
    ) =>
        new(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([TargetKey]),
            new NoOpDocumentCacheProjectionScheduler(),
            lifecycleReader,
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionSupervisor>.Instance
        );

    private static DocumentCacheTargetExecutionContext ExecutionContext() =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            EffectiveSettings(),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection-a"),
            Fingerprint,
            TrackingLifecycle,
            SatisfiedInventory,
            SatisfiedEnqueueTrigger,
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings() =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 2,
            projectorFailureBackoff: TimeSpan.FromSeconds(10),
            projectorBaselineHighWaterMark: 1000,
            administrationWorkflowTimeout: TimeSpan.FromHours(24)
        );

    private static IOptions<DocumentCacheOptions> OptionsFor(IEnumerable<DocumentCacheTargetKey> targetKeys)
    {
        DocumentCacheOptions options = new()
        {
            Targets = targetKeys
                .Select(targetKey => new DocumentCacheTargetOptions
                {
                    TenantKey = targetKey.TenantKey,
                    DataStoreId = targetKey.DataStoreId,
                })
                .ToList(),
        };

        return Options.Create(options);
    }

    private static RecordingTargetRegistry RegistryFor(DocumentCacheTargetExecutionContext executionContext)
    {
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(Snapshot(executionContext), RuntimeSnapshot(executionContext));
        return registry;
    }

    private static DocumentCacheTargetRegistrySnapshot Snapshot(
        DocumentCacheTargetExecutionContext executionContext
    ) => new([EligibleObservation(executionContext)], ObservedAt);

    private static DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot(
        DocumentCacheTargetExecutionContext executionContext
    ) => new([executionContext], ObservedAt);

    private static DocumentCacheTargetObservation EligibleObservation(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            executionContext.TargetKey,
            executionContext.EffectiveSettings,
            executionContext.Generation,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint,
            executionContext.Lifecycle,
            executionContext.Inventory,
            executionContext.EnqueueTrigger,
            executionContext.SqlServerPrerequisites
        );

    private sealed class RecordingLifecycleReader : IDocumentCacheLifecycleReader
    {
        private readonly Queue<Func<CancellationToken, Task<DocumentCacheLifecycleReadResult>>> _reads =
            new();

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public List<string> ConnectionStrings { get; } = [];

        public void QueueSuccess(
            DocumentCacheLifecycleState lifecycleState,
            bool cacheAheadRecoveryRequired
        ) =>
            _reads.Enqueue(_ =>
                Task.FromResult(
                    DocumentCacheLifecycleReadResult.Success(
                        new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired)
                    )
                )
            );

        public void QueueException(Exception exception) =>
            _reads.Enqueue(_ => Task.FromException<DocumentCacheLifecycleReadResult>(exception));

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionStrings.Add(connectionString);

            return _reads.Count == 0
                ? Task.FromResult(DocumentCacheLifecycleReadResult.Success(TrackingLifecycle))
                : _reads.Dequeue()(cancellationToken);
        }
    }

    private sealed class RecordingTargetRegistry : IDocumentCacheTargetRegistry
    {
        private readonly Queue<(
            DocumentCacheTargetRegistrySnapshot Snapshot,
            DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot
        )> _refreshes = new();

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; private set; } =
            new([], ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; private set; } =
            new([], ObservedAt);

        public void QueueRefresh(
            DocumentCacheTargetRegistrySnapshot snapshot,
            DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
        ) => _refreshes.Enqueue((snapshot, runtimeSnapshot));

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            _ = reason;
            cancellationToken.ThrowIfCancellationRequested();

            (
                DocumentCacheTargetRegistrySnapshot Snapshot,
                DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot
            ) = _refreshes.Dequeue();
            CurrentSnapshot = Snapshot;
            CurrentRuntimeSnapshot = RuntimeSnapshot;
            return Task.FromResult(CurrentSnapshot);
        }
    }

    private sealed class RecordingTargetContextFactory(RecordingObservationSink observationSink)
        : IDocumentCacheProjectionTargetRuntimeContextFactory
    {
        public List<DocumentCacheProjectionTargetRuntimeContext> CreatedContexts { get; } = [];

        public Task<DocumentCacheProjectionTargetRuntimeContext> CreateAsync(
            DocumentCacheTargetExecutionContext executionContext,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            DocumentCacheProjectionTargetRuntimeContext context = new(
                executionContext,
                new DocumentCacheProjectionTargetProviderAdapters(
                    executionContext.ProviderToken,
                    MaterializationTargetContext(executionContext.TargetKey),
                    new StubDocumentCacheMaterializer(),
                    new StubDocumentCacheWriter()
                ),
                observationSink
            );
            CreatedContexts.Add(context);

            return Task.FromResult(context);
        }

        private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
            DocumentCacheTargetKey targetKey
        ) =>
            new(
                new DocumentCacheProjectionTargetKey(
                    targetKey.TenantKey,
                    new DataStoreId(targetKey.DataStoreId)
                ),
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
    }

    private sealed class RecordingObservationSink : IDocumentCacheProjectionObservationSink
    {
        public List<DocumentCacheProjectionTargetHealthSnapshot> TargetSnapshots { get; } = [];

        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) =>
            TargetSnapshots.Add(snapshot);

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        )
        {
            _ = contextKey;
            _ = endReason;
            _ = endedAt;
        }

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }

    private sealed class StubDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotImplementedException();
    }

    private sealed class StubDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotImplementedException();
    }

    private sealed class NoOpDocumentCacheProjectionScheduler : IDocumentCacheProjectionScheduler
    {
        public Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
            IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>.Empty);

        public Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
