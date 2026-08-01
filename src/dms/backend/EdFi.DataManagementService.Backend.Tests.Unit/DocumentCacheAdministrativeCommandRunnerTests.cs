// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheAdministrativeCommandRunner")]
public class Given_DocumentCacheAdministrativeCommandRunner
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public async Task It_rejects_target_replacement_before_acquiring_the_mutex()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(generation: 2);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(firstGeneration);
        var mutex = new RecordingAdministrativeMutex();
        var registry = new MutableTargetRegistry(
            Snapshot([EligibleObservation(firstGeneration)]),
            RuntimeSnapshot([replacementGeneration])
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            new StubProjectionSupervisor([runtimeContext]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Which.CurrentPhase.Should()
            .Be(DocumentCacheAdministrativeCommandPhase.ResolveTarget);
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_starts_the_workflow_timeout_only_after_mutex_acquisition()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(250)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        var mutex = new RecordingAdministrativeMutex(acquireDelay: TimeSpan.FromMilliseconds(400));
        MutableTargetRegistry registry = RegistryFor(executionContext);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            new StubProjectionSupervisor([runtimeContext]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.ElapsedCommandTime.Should().NotBeNull();
        mutex.AcquireCount.Should().Be(1);
    }

    [Test]
    public async Task It_runs_live_preflight_on_the_mutex_session_before_command_work()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex();
        var workflow = new DelegatingWorkflow(
            preflight: (context, _) =>
            {
                DocumentCacheGuardedNewEmptyActivationRequest request = new(
                    AdministrativeTargetKey,
                    Fingerprint
                );
                DocumentCacheAdministrativeCommandResult result =
                    DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                        request,
                        context.LiveTargetObservation,
                        new DocumentCacheGuardedNewEmptyActivationPreflightFacts(
                            executionContext.Generation,
                            DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                            ),
                            new DocumentCacheGuardedNewEmptyActivationState(
                                canonicalDocumentsEmpty: true,
                                documentCacheEmpty: true,
                                documentProjectionWorkEmpty: true
                            )
                        )
                    );

                return Task.FromResult(result);
            },
            execute: static (_, _) => throw new AssertionException("Command work must not run.")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            new DocumentCacheAdministrativeCommandRunnerRequest(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                AdministrativeTargetKey,
                Fingerprint
            ),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.LifecycleMismatch);
        result.ElapsedCommandTime.Should().NotBeNull();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
            );
        mutex.AcquireCount.Should().Be(1);
    }

    [Test]
    public async Task It_classifies_workflow_timeout_after_mutation_as_incomplete_retryable()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: static async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                context.MarkMutated(
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false)
                );
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Rebuilding);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SeedBaseline
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.Retryable
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
            );
    }

    [Test]
    public async Task It_keeps_active_command_observation_for_a_noncurrent_pinned_generation()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(generation: 2);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(firstGeneration);
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        observationStore.ObserveTarget(TargetHealth(firstGeneration));
        MutableTargetRegistry registry = RegistryFor(firstGeneration);
        TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                context.MarkMutated(
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false)
                );
                commandStarted.SetResult();
                await releaseCommand.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex(),
            observationStore
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(Request(), workflow);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        observationStore.CurrentSnapshot.ActiveAdministrativeCommands.Values.Should().ContainSingle();

        registry.CurrentRuntimeSnapshot = RuntimeSnapshot([replacementGeneration]);
        observationStore.ObserveTarget(TargetHealth(replacementGeneration));

        DocumentCacheAdministrativeCommandObservationSnapshot activeCommand = observationStore
            .CurrentSnapshot.ActiveAdministrativeCommands.Values.Should()
            .ContainSingle()
            .Subject;
        activeCommand.TargetGeneration.Value.Should().Be(1);
        activeCommand.IsCurrentGeneration.Should().BeFalse();
        activeCommand.CurrentTargetGeneration!.Value.Should().Be(2);

        releaseCommand.SetResult();
        DocumentCacheAdministrativeCommandResult result = await resultTask.ConfigureAwait(false);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced
            );
        observationStore.CurrentSnapshot.ActiveAdministrativeCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_classifies_mutex_acquisition_failure_before_mutation()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex(
            acquireException: new InvalidOperationException("provider failed")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Which.CurrentPhase.Should()
            .Be(DocumentCacheAdministrativeCommandPhase.AcquireMutex);
    }

    private static DocumentCacheAdministrativeCommandRunner CreateRunner(
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheProjectionSupervisor supervisor,
        IDocumentCacheAdministrativeMutex mutex,
        IDocumentCacheProjectionObservationSink? observationSink = null
    )
    {
        DocumentCacheProjectionObservationStore defaultObservationStore = new(
            new FixedTimeProvider(ObservedAt)
        );
        IDocumentCacheProjectionObservationSink sink = observationSink ?? defaultObservationStore;

        return new(
            supervisor,
            registry,
            mutex,
            new StubAdministrativePrimitives(),
            sink,
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );
    }

    private static DocumentCacheAdministrativeCommandRunnerRequest Request() =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            AdministrativeTargetKey,
            expectedPhysicalSourceFingerprint: Fingerprint
        );

    private static MutableTargetRegistry RegistryFor(DocumentCacheTargetExecutionContext executionContext) =>
        new(Snapshot([EligibleObservation(executionContext)]), RuntimeSnapshot([executionContext]));

    private static DocumentCacheTargetRegistrySnapshot Snapshot(
        IEnumerable<DocumentCacheTargetObservation> observations
    ) => new(observations, ObservedAt);

    private static DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot(
        IEnumerable<DocumentCacheTargetExecutionContext> executionContexts
    ) => new(executionContexts, ObservedAt);

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        long generation,
        TimeSpan? workflowTimeout = null
    ) =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(generation),
            EffectiveSettings(workflowTimeout ?? TimeSpan.FromHours(24)),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
            Fingerprint,
            TrackingLifecycle,
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

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings(TimeSpan workflowTimeout) =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 2,
            projectorFailureBackoff: TimeSpan.FromSeconds(10),
            projectorBaselineHighWaterMark: 1000,
            workflowTimeout
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

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        new(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                MaterializationTargetContext(),
                new ThrowingDocumentCacheMaterializer(),
                new ThrowingDocumentCacheWriter()
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt))
        );

    private static DocumentCacheProjectionTargetHealthSnapshot TargetHealth(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        new(
            executionContext.TargetKey,
            executionContext.Generation,
            executionContext.EffectiveSettings.ProjectorPageSize,
            ObservedAt,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint
        );

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

    private sealed class DelegatingWorkflow(
        Func<
            DocumentCacheAdministrativeCommandExecutionContext,
            CancellationToken,
            Task<DocumentCacheAdministrativeCommandResult>
        > preflight,
        Func<
            DocumentCacheAdministrativeCommandExecutionContext,
            CancellationToken,
            Task<DocumentCacheAdministrativeCommandResult>
        > execute
    ) : IDocumentCacheAdministrativeCommandWorkflow
    {
        public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => preflight(context, cancellationToken);

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => execute(context, cancellationToken);
    }

    private sealed class SucceedingWorkflow : IDocumentCacheAdministrativeCommandWorkflow
    {
        public static SucceedingWorkflow Instance { get; } = new();

        public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(context.EligiblePreflightResult());

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(context.Completed());
    }

    private sealed class StubProjectionSupervisor(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> contexts
    ) : IDocumentCacheProjectionSupervisor
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            contexts.ToImmutableArray();

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class MutableTargetRegistry(
        DocumentCacheTargetRegistrySnapshot currentSnapshot,
        DocumentCacheTargetRuntimeSnapshot currentRuntimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; set; } = currentSnapshot;

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; set; } =
            currentRuntimeSnapshot;

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class RecordingAdministrativeMutex(
        TimeSpan? acquireDelay = null,
        Exception? acquireException = null
    ) : IDocumentCacheAdministrativeMutex
    {
        public int AcquireCount { get; private set; }

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public async Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
            DocumentCacheTargetConnectionInput connectionInput,
            CancellationToken cancellationToken = default
        )
        {
            AcquireCount++;

            if (acquireDelay is not null)
            {
                await Task.Delay(acquireDelay.Value, cancellationToken).ConfigureAwait(false);
            }

            if (acquireException is not null)
            {
                throw acquireException;
            }

            return new RecordingMutexLease();
        }
    }

    private sealed class RecordingMutexLease : IDocumentCacheAdministrativeMutexLease
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen => true;

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IRelationalWriteSession>(new RecordingWriteSession());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWriteSession : IRelationalWriteSession
    {
        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
        ) => Task.FromResult(DocumentCacheLifecycleReadResult.Success(TrackingLifecycle));

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
    }

    private sealed class ThrowingDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
