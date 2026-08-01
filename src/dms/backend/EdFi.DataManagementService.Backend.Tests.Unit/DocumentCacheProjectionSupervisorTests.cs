// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
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
[Category("DocumentCacheProjectionSupervisor")]
public class Given_DocumentCacheProjectionSupervisor
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

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
    public async Task It_starts_no_projection_workers_when_no_targets_are_configured()
    {
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            new DocumentCacheTargetRegistrySnapshot([], ObservedAt),
            new DocumentCacheTargetRuntimeSnapshot([], ObservedAt)
        );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        targetContextFactory.CreateCalls.Should().BeEmpty();
        observationSink.TargetSnapshots.Should().BeEmpty();
        supervisor.CurrentTargetContexts.Should().BeEmpty();
    }

    [Test]
    public async Task It_creates_an_isolated_runtime_context_for_each_resolved_eligible_target()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(executionContext)]),
            RuntimeSnapshot([executionContext])
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheProjectionTargetRuntimeContext createdContext = targetContextFactory
            .CreatedContexts.Should()
            .ContainSingle()
            .Subject;
        createdContext.TargetExecutionContext.Should().BeSameAs(executionContext);
        createdContext.ProviderAdapters.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
        createdContext.Materializer.Should().BeSameAs(targetContextFactory.Materializer);
        createdContext.Writer.Should().BeSameAs(targetContextFactory.Writer);
        createdContext.ObservationSink.Should().BeSameAs(observationSink);
        createdContext.Cursor.HasValue.Should().BeFalse();
        createdContext
            .FailureBackoffState.Capacity.Should()
            .Be(executionContext.EffectiveSettings.ProjectorPageSize);

        DocumentCacheProjectionTargetHealthSnapshot health = observationSink
            .TargetSnapshots.Should()
            .ContainSingle()
            .Subject;
        health.TargetKey.Should().Be(executionContext.TargetKey);
        health.Generation.Value.Should().Be(1);
        health.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
        health.PhysicalSourceFingerprint.Should().Be(Fingerprint);
        health.ExecutionState.IsRunning.Should().BeTrue();
        health.ExecutionState.IsActivelyProcessing.Should().BeFalse();
        health.LifecycleFence.State.Should().Be(DocumentCacheProjectionLifecycleFenceState.Eligible);
    }

    [Test]
    public async Task It_keeps_the_current_generation_when_transient_refresh_failure_retains_runtime_context()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        DocumentCacheTargetObservation retainedObservation = EligibleObservation(executionContext)
            .WithRetryDiagnostic(
                new DocumentCacheResolutionRetryState(
                    attemptCount: 1,
                    lastAttemptedAt: ObservedAt,
                    nextRetryAt: ObservedAt.AddSeconds(10),
                    DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                    "CMS refresh failed; retained current generation."
                ),
                Diagnostic(
                    executionContext,
                    DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
                    "CMS refresh failed; retaining current generation."
                )
            );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(executionContext)]),
            RuntimeSnapshot([executionContext])
        );
        registry.QueueRefresh(Snapshot([retainedObservation]), RuntimeSnapshot([executionContext]));
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheTargetRegistrySnapshot refreshFailureSnapshot = await supervisor.RefreshAsync(
            DocumentCacheTargetRefreshReason.SupervisorTriggered
        );

        targetContextFactory.CreateCalls.Should().ContainSingle();
        observationSink.EndedTargets.Should().BeEmpty();
        supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(1);
        refreshFailureSnapshot
            .GetTarget(executionContext.TargetKey)!
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure
            );
    }

    [Test]
    public async Task It_cancels_and_replaces_a_generation_when_execution_metadata_changes()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(targetKey, generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(
            targetKey,
            generation: 2,
            connectionInput: "connection-b"
        );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(firstGeneration)]),
            RuntimeSnapshot([firstGeneration])
        );
        registry.QueueRefresh(
            Snapshot([EligibleObservation(replacementGeneration)]),
            RuntimeSnapshot([replacementGeneration])
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([targetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext oldContext =
            targetContextFactory.CreatedContexts.Single();
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        oldContext.CancellationRequested.Should().BeTrue();
        observationSink
            .EndedTargets.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RecordingObservationSink.EndedTarget(
                    oldContext.ContextKey,
                    DocumentCacheProjectionTargetEndReason.Replaced
                )
            );
        targetContextFactory.CreateCalls.Should().HaveCount(2);
        supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(2);
    }

    [Test]
    public async Task It_preserves_an_active_administrative_command_generation_when_execution_metadata_changes()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(targetKey, generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(
            targetKey,
            generation: 2,
            connectionInput: "connection-b"
        );
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        RecordingTargetContextFactory targetContextFactory = new(observationStore);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(firstGeneration)]),
            RuntimeSnapshot([firstGeneration])
        );
        registry.QueueRefresh(
            Snapshot([EligibleObservation(replacementGeneration)]),
            RuntimeSnapshot([replacementGeneration])
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationStore,
            OptionsFor([targetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext oldContext =
            targetContextFactory.CreatedContexts.Single();
        DocumentCacheAdministrativeCommandExecutionContext commandContext = CommandContext(
            oldContext,
            observationStore
        );
        IDisposable activeCommandTracking = oldContext.TrackActiveAdministrativeCommand(commandContext);
        commandContext.Observe();

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        oldContext.CancellationRequested.Should().BeFalse();
        supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(2);
        DocumentCacheProjectionObservationSnapshot snapshot = observationStore.CurrentSnapshot;
        snapshot.GetCurrentTarget(oldContext.ContextKey).Should().BeNull();
        snapshot.GetCurrentTarget(targetKey)!.Generation.Value.Should().Be(2);
        DocumentCacheAdministrativeCommandObservationSnapshot activeCommand = snapshot.GetActiveCommand(
            commandContext.ExecutionId
        )!;
        activeCommand.Should().NotBeNull();
        activeCommand.IsCurrentGeneration.Should().BeFalse();
        activeCommand.CurrentTargetGeneration!.Value.Should().Be(2);
        snapshot.LastEndedTargetDiagnostics.Should().BeEmpty();

        activeCommandTracking.Dispose();
        observationStore.EndAdministrativeCommand(commandContext.ExecutionId);
        await (
            (IDocumentCacheProjectionRetainedTargetContextReleaser)supervisor
        ).ReleaseRetainedCommandOwnedTargetContextAsync(oldContext);

        oldContext.CancellationRequested.Should().BeTrue();
        observationStore
            .CurrentSnapshot.LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Which.EndReason.Should()
            .Be(DocumentCacheProjectionTargetEndReason.Replaced);
        observationStore.CurrentSnapshot.GetCurrentTarget(targetKey)!.Generation.Value.Should().Be(2);
    }

    [Test]
    public async Task It_preserves_a_command_owned_drain_generation_when_the_target_is_removed()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(targetKey, generation: 1);
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        RecordingTargetContextFactory targetContextFactory = new(observationStore);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(firstGeneration)]),
            RuntimeSnapshot([firstGeneration])
        );
        registry.QueueRefresh(Snapshot([]), RuntimeSnapshot([]));
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationStore,
            OptionsFor([targetKey])
        );
        TaskCompletionSource drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext oldContext =
            targetContextFactory.CreatedContexts.Single();
        Task<DocumentCacheProjectionDrainPageResult> drainTask =
            oldContext.DrainExecutor.RunAdministrativeCommandAsync(async cancellationToken =>
            {
                drainStarted.SetResult();
                await releaseDrain.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return DocumentCacheProjectionDrainPageResult.NoEligibleWork;
            });
        await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        oldContext.CancellationRequested.Should().BeFalse();
        supervisor.CurrentTargetContexts.Should().BeEmpty();
        observationStore.CurrentSnapshot.GetCurrentTarget(targetKey).Should().BeNull();
        observationStore.CurrentSnapshot.LastEndedTargetDiagnostics.Should().BeEmpty();

        releaseDrain.SetResult();
        await drainTask.ConfigureAwait(false);
        await (
            (IDocumentCacheProjectionRetainedTargetContextReleaser)supervisor
        ).ReleaseRetainedCommandOwnedTargetContextAsync(oldContext);

        oldContext.CancellationRequested.Should().BeTrue();
        observationStore
            .CurrentSnapshot.LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Which.EndReason.Should()
            .Be(DocumentCacheProjectionTargetEndReason.Removed);
    }

    [Test]
    public async Task It_marks_only_the_ineligible_target_generation_ended()
    {
        DocumentCacheTargetExecutionContext firstTarget = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        DocumentCacheTargetExecutionContext peerTarget = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantB", 8),
            generation: 1,
            connectionInput: "connection-peer"
        );
        DocumentCacheTargetObservation ineligibleFirstTarget =
            DocumentCacheTargetObservation.ResolvedIneligible(
                firstTarget.TargetKey,
                firstTarget.EffectiveSettings,
                firstTarget.Generation,
                firstTarget.ProviderToken,
                firstTarget.PhysicalSourceFingerprint,
                firstTarget.Lifecycle,
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Missing,
                    "Inventory missing."
                ),
                firstTarget.EnqueueTrigger,
                firstTarget.SqlServerPrerequisites,
                retryState: null,
                [
                    Diagnostic(
                        firstTarget,
                        DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                        "Inventory missing."
                    ),
                ]
            );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(firstTarget), EligibleObservation(peerTarget)]),
            RuntimeSnapshot([firstTarget, peerTarget])
        );
        registry.QueueRefresh(
            Snapshot([ineligibleFirstTarget, EligibleObservation(peerTarget)]),
            RuntimeSnapshot([peerTarget])
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([firstTarget.TargetKey, peerTarget.TargetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext endedContext =
            targetContextFactory.CreatedContexts.Single(context =>
                context.TargetKey.Equals(firstTarget.TargetKey)
            );
        DocumentCacheProjectionTargetRuntimeContext retainedContext =
            targetContextFactory.CreatedContexts.Single(context =>
                context.TargetKey.Equals(peerTarget.TargetKey)
            );
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        endedContext.CancellationRequested.Should().BeTrue();
        retainedContext.CancellationRequested.Should().BeFalse();
        observationSink
            .EndedTargets.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RecordingObservationSink.EndedTarget(
                    endedContext.ContextKey,
                    DocumentCacheProjectionTargetEndReason.Ineligible
                )
            );
        supervisor
            .CurrentTargetContexts.Should()
            .ContainSingle()
            .Which.TargetKey.Should()
            .Be(peerTarget.TargetKey);
    }

    private static DocumentCacheProjectionSupervisor CreateSupervisor(
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory,
        IDocumentCacheProjectionObservationSink observationSink,
        IOptions<DocumentCacheOptions> options
    ) =>
        new(
            registry,
            targetContextFactory,
            observationSink,
            options,
            new NoOpDocumentCacheProjectionScheduler(),
            new StubDocumentCacheLifecycleReader(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionSupervisor>.Instance
        );

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheTargetKey targetKey,
        long generation,
        string connectionInput = "connection-a"
    ) =>
        new(
            targetKey,
            new DocumentCacheTargetContextGeneration(generation),
            EffectiveSettings(),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, connectionInput),
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

    private static DocumentCacheTargetDiagnostic Diagnostic(
        DocumentCacheTargetExecutionContext executionContext,
        DocumentCacheTargetDiagnosticCategory category,
        string message
    ) =>
        new(
            executionContext.TargetKey,
            DocumentCacheTargetResolutionState.Resolved,
            executionContext.ProviderToken,
            executionContext.Generation,
            executionContext.PhysicalSourceFingerprint,
            executionContext.Lifecycle,
            executionContext.Inventory,
            executionContext.EnqueueTrigger,
            executionContext.SqlServerPrerequisites,
            retryState: null,
            category,
            message
        );

    private static DocumentCacheTargetRegistrySnapshot Snapshot(
        IEnumerable<DocumentCacheTargetObservation> observations
    ) => new(observations, ObservedAt);

    private static DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot(
        IEnumerable<DocumentCacheTargetExecutionContext> executionContexts
    ) => new(executionContexts, ObservedAt);

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

        public List<DocumentCacheTargetRefreshReason> RefreshReasons { get; } = [];

        public void QueueRefresh(
            DocumentCacheTargetRegistrySnapshot snapshot,
            DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
        ) => _refreshes.Enqueue((snapshot, runtimeSnapshot));

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshReasons.Add(reason);

            (
                DocumentCacheTargetRegistrySnapshot Snapshot,
                DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot
            ) = _refreshes.Dequeue();
            CurrentSnapshot = Snapshot;
            CurrentRuntimeSnapshot = RuntimeSnapshot;
            return Task.FromResult(CurrentSnapshot);
        }
    }

    private static DocumentCacheAdministrativeCommandExecutionContext CommandContext(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        IDocumentCacheProjectionObservationSink observationSink
    ) =>
        new(
            DocumentCacheAdministrativeCommandExecutionId.New(),
            new DocumentCacheAdministrativeCommandRunnerRequest(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                DocumentCacheAdministrativeTargetKey.FromTargetKey(targetContext.TargetKey),
                Fingerprint
            ),
            targetContext,
            new StubMutexLease(),
            new StubAdministrativePrimitives(),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            ObservedAt,
            CancellationToken.None
        );

    private sealed class RecordingTargetContextFactory(
        IDocumentCacheProjectionObservationSink observationSink
    ) : IDocumentCacheProjectionTargetRuntimeContextFactory
    {
        public StubDocumentCacheMaterializer Materializer { get; } = new();

        public StubDocumentCacheWriter Writer { get; } = new();

        public List<DocumentCacheTargetExecutionContext> CreateCalls { get; } = [];

        public List<DocumentCacheProjectionTargetRuntimeContext> CreatedContexts { get; } = [];

        public Task<DocumentCacheProjectionTargetRuntimeContext> CreateAsync(
            DocumentCacheTargetExecutionContext executionContext,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls.Add(executionContext);

            DocumentCacheProjectionTargetRuntimeContext context = new(
                executionContext,
                new DocumentCacheProjectionTargetProviderAdapters(
                    executionContext.ProviderToken,
                    MaterializationTargetContext(executionContext.TargetKey, executionContext.ProviderToken),
                    Materializer,
                    Writer
                ),
                observationSink
            );
            CreatedContexts.Add(context);

            return Task.FromResult(context);
        }

        private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
            DocumentCacheTargetKey targetKey,
            RelationalProviderToken providerToken
        ) =>
            new(
                new DocumentCacheProjectionTargetKey(
                    targetKey.TenantKey,
                    new DataStoreId(targetKey.DataStoreId)
                ),
                MappingSet(providerToken),
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                "connection"
            );

        private static MappingSet MappingSet(RelationalProviderToken providerToken)
        {
            SqlDialect dialect =
                providerToken == RelationalProviderToken.SqlServer ? SqlDialect.Mssql : SqlDialect.Pgsql;
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
                    dialect,
                    effectiveSchema.RelationalMappingVersion
                ),
                new DerivedRelationalModelSet(effectiveSchema, dialect, [], [], [], [], [], []),
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

        public List<EndedTarget> EndedTargets { get; } = [];

        public List<DocumentCacheAdministrativeCommandObservationSnapshot> AdministrativeCommandSnapshots { get; } =
        [];

        public List<DocumentCacheAdministrativeCommandExecutionId> EndedAdministrativeCommandIds { get; } =
        [];

        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) =>
            TargetSnapshots.Add(snapshot);

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => EndedTargets.Add(new EndedTarget(contextKey, endReason));

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => AdministrativeCommandSnapshots.Add(snapshot);

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            EndedAdministrativeCommandIds.Add(executionId);

        public sealed record EndedTarget(
            DocumentCacheProjectionTargetContextKey ContextKey,
            DocumentCacheProjectionTargetEndReason EndReason
        );
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

    private sealed class StubDocumentCacheLifecycleReader : IDocumentCacheLifecycleReader
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DocumentCacheLifecycleReadResult.Success(TrackingLifecycle));
        }
    }

    private sealed class StubMutexLease : IDocumentCacheAdministrativeMutexLease
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen => true;

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAdministrativePrimitives : IDocumentCacheAdministrativePrimitives
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
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
