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
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    public enum SchedulerWakeKind
    {
        PollSleep,
        TargetBackoff,
    }

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
    public async Task It_preserves_an_administratively_retained_generation_when_execution_metadata_changes()
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
        DocumentCacheProjectionAdministrativeTargetRetainResult retainResult = await (
            (IDocumentCacheProjectionAdministrativeTargetRetainer)supervisor
        ).TryRetainCurrentTargetForAdministrativeCommandAsync(targetKey);

        retainResult.TargetContext.Should().BeSameAs(oldContext);
        retainResult.Retention.Should().NotBeNull();

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        oldContext.CancellationRequested.Should().BeFalse();
        supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(2);
        observationStore.CurrentSnapshot.LastEndedTargetDiagnostics.Should().BeEmpty();

        retainResult.Retention!.Dispose();
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
    public async Task It_rejects_administrative_retention_after_target_context_cancellation_starts()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        DocumentCacheProjectionTargetRuntimeContext targetContext = await targetContextFactory.CreateAsync(
            executionContext
        );

        targetContext.Cancel();

        targetContext.TryRetainForAdministrativeCommand().Should().BeNull();
    }

    [Test]
    public async Task It_populates_target_health_success_and_active_command_fields_from_runtime_context()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        RecordingTargetContextFactory targetContextFactory = new(observationStore);
        DocumentCacheProjectionTargetRuntimeContext targetContext = await targetContextFactory.CreateAsync(
            executionContext
        );
        DocumentCacheAdministrativeCommandExecutionContext commandContext = CommandContext(
            targetContext,
            observationStore
        );

        targetContext.SchedulingState.RecordProjectionSuccess(
            documentId: 501,
            contentVersion: 6001,
            completedAt: ObservedAt.AddSeconds(1)
        );
        using IDisposable activeCommandTracking = targetContext.TrackActiveAdministrativeCommand(
            commandContext
        );
        commandContext.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);

        DocumentCacheProjectionTargetHealthSnapshot snapshot =
            DocumentCacheProjectionTargetHealthSnapshotFactory.Create(
                targetContext,
                ObservedAt.AddSeconds(2)
            );

        snapshot.LastSuccess.Should().NotBeNull();
        snapshot.LastSuccess!.DocumentId.Should().Be(501);
        snapshot.LastSuccess.ContentVersion.Should().Be(6001);
        snapshot.LastSuccess.CompletedAt.Should().Be(ObservedAt.AddSeconds(1));
        snapshot.ActiveCommandExecutionId.Should().Be(commandContext.ExecutionId);
        snapshot
            .ActiveAdministrativeCommand.Should()
            .Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
        snapshot.ActiveAdministrativePhase.Should().Be(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
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

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_retains_an_ordinary_drain_generation_until_the_current_slice_releases_when_the_target_changes(
        bool replaceTarget
    )
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(targetKey, generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(
            targetKey,
            generation: 2,
            connectionInput: "connection-b"
        );
        DocumentCacheProjectionTargetEndReason expectedEndReason = replaceTarget
            ? DocumentCacheProjectionTargetEndReason.Replaced
            : DocumentCacheProjectionTargetEndReason.Removed;
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        RecordingTargetContextFactory targetContextFactory = new(observationStore);
        RecordingTargetRegistry registry = new();
        registry.QueueRefresh(
            Snapshot([EligibleObservation(firstGeneration)]),
            RuntimeSnapshot([firstGeneration])
        );
        registry.QueueRefresh(
            replaceTarget ? Snapshot([EligibleObservation(replacementGeneration)]) : Snapshot([]),
            replaceTarget ? RuntimeSnapshot([replacementGeneration]) : RuntimeSnapshot([])
        );
        BlockingOrdinaryDrainScheduler scheduler = new();
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationStore,
            OptionsFor([targetKey]),
            scheduler
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForDrainStartedAsync();
            DocumentCacheProjectionTargetRuntimeContext oldContext =
                targetContextFactory.CreatedContexts.Single();

            await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

            oldContext.CancellationRequested.Should().BeTrue();
            targetContextFactory.DisposedContexts.Should().NotContain(oldContext.ContextKey);
            supervisor
                .CurrentTargetContexts.Select(context => context.ContextKey)
                .Should()
                .NotContain(oldContext.ContextKey);
            observationStore.CurrentSnapshot.GetCurrentTarget(oldContext.ContextKey).Should().BeNull();
            observationStore.CurrentSnapshot.LastEndedTargetDiagnostics.Should().BeEmpty();

            if (replaceTarget)
            {
                supervisor
                    .CurrentTargetContexts.Should()
                    .ContainSingle()
                    .Which.Generation.Value.Should()
                    .Be(2);
                observationStore.CurrentSnapshot.GetCurrentTarget(targetKey)!.Generation.Value.Should().Be(2);
            }
            else
            {
                supervisor.CurrentTargetContexts.Should().BeEmpty();
                observationStore.CurrentSnapshot.GetCurrentTarget(targetKey).Should().BeNull();
            }

            scheduler.ReleaseDrain();
            await scheduler.WaitForDrainReleasedAsync();
            await targetContextFactory.WaitForDisposedContextAsync(oldContext.ContextKey);

            observationStore
                .CurrentSnapshot.LastEndedTargetDiagnostics.Values.Should()
                .ContainSingle()
                .Which.Should()
                .Match<DocumentCacheProjectionTargetEndedDiagnosticSnapshot>(diagnostic =>
                    diagnostic.ContextKey == oldContext.ContextKey
                    && diagnostic.EndReason == expectedEndReason
                );
        }
        finally
        {
            scheduler.ReleaseDrain();
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task It_retains_a_generation_replaced_after_dispatch_acquires_the_target_before_drain_ownership()
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
        BlockingOrdinaryDispatchLeaseScheduler scheduler = new();
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationStore,
            OptionsFor([targetKey]),
            scheduler
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForDispatchAcquiredAsync();
            DocumentCacheProjectionTargetRuntimeContext oldContext =
                targetContextFactory.CreatedContexts.Single();
            oldContext.HasOrdinaryDispatchLease.Should().BeTrue();
            oldContext.DrainExecutor.CurrentOwner.Should().BeNull();

            await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

            oldContext.CancellationRequested.Should().BeTrue();
            targetContextFactory.DisposedContexts.Should().NotContain(oldContext.ContextKey);
            supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(2);
            observationStore.CurrentSnapshot.GetCurrentTarget(oldContext.ContextKey).Should().BeNull();
            observationStore.CurrentSnapshot.GetCurrentTarget(targetKey)!.Generation.Value.Should().Be(2);
            observationStore.CurrentSnapshot.LastEndedTargetDiagnostics.Should().BeEmpty();

            scheduler.ReleaseDispatch();
            await scheduler.WaitForDispatchReleasedAsync();
            await targetContextFactory.WaitForDisposedContextAsync(oldContext.ContextKey);

            observationStore
                .CurrentSnapshot.LastEndedTargetDiagnostics.Values.Should()
                .ContainSingle()
                .Which.Should()
                .Match<DocumentCacheProjectionTargetEndedDiagnosticSnapshot>(diagnostic =>
                    diagnostic.ContextKey == oldContext.ContextKey
                    && diagnostic.EndReason == DocumentCacheProjectionTargetEndReason.Replaced
                );
        }
        finally
        {
            scheduler.ReleaseDispatch();
            await supervisor.StopAsync(CancellationToken.None);
        }
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

    [Test]
    public async Task It_no_ops_refresh_after_shutdown_has_started()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(
            firstGeneration.TargetKey,
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
            OptionsFor([firstGeneration.TargetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        await supervisor.StopAsync(CancellationToken.None);
        DocumentCacheTargetRegistrySnapshot stoppedRefreshSnapshot = await supervisor.RefreshAsync(
            DocumentCacheTargetRefreshReason.SupervisorTriggered
        );

        stoppedRefreshSnapshot
            .GetTarget(firstGeneration.TargetKey)!
            .Generation.Should()
            .Be(firstGeneration.Generation);
        registry.RefreshReasons.Should().Equal(DocumentCacheTargetRefreshReason.Startup);
        targetContextFactory.CreateCalls.Should().ContainSingle().Which.Should().BeSameAs(firstGeneration);
        supervisor.CurrentTargetContexts.Should().BeEmpty();
    }

    [Test]
    public async Task It_serializes_shutdown_with_an_in_flight_refresh()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(
            DocumentCacheTargetKey.Create("TenantA", 7),
            generation: 1
        );
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(
            firstGeneration.TargetKey,
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
        RecordingTargetRegistry.BlockingRefreshControl blockingRefresh = registry.QueueBlockingRefresh(
            Snapshot([EligibleObservation(replacementGeneration)]),
            RuntimeSnapshot([replacementGeneration])
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([firstGeneration.TargetKey])
        );

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        Task<DocumentCacheTargetRegistrySnapshot> refreshTask = supervisor.RefreshAsync(
            DocumentCacheTargetRefreshReason.SupervisorTriggered
        );
        await blockingRefresh.WaitForStartedAsync();

        Task stopTask = supervisor.StopAsync(CancellationToken.None);
        Task completedBeforeRefreshReleased = await Task.WhenAny(
            stopTask,
            Task.Delay(TimeSpan.FromMilliseconds(100))
        );
        completedBeforeRefreshReleased.Should().NotBe(stopTask);

        blockingRefresh.Release();
        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        registry
            .RefreshReasons.Should()
            .Equal(
                DocumentCacheTargetRefreshReason.Startup,
                DocumentCacheTargetRefreshReason.SupervisorTriggered
            );
        targetContextFactory.CreateCalls.Should().ContainSingle().Which.Should().BeSameAs(firstGeneration);
        supervisor.CurrentTargetContexts.Should().BeEmpty();
        observationSink
            .EndedTargets.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new RecordingObservationSink.EndedTarget(
                    new DocumentCacheProjectionTargetContextKey(
                        firstGeneration.TargetKey,
                        firstGeneration.Generation
                    ),
                    DocumentCacheProjectionTargetEndReason.Shutdown
                )
            );
    }

    [Test]
    public async Task It_retains_an_active_ordinary_drain_until_shutdown_ownership_releases()
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
        TaskCompletionSource drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext activeContext = targetContextFactory
            .CreatedContexts.Should()
            .ContainSingle()
            .Subject;
        Task<DocumentCacheProjectionDrainPageResult?> drainTask =
            activeContext.DrainExecutor.TryRunOrdinaryDrainSliceAsync(async _ =>
            {
                drainStarted.SetResult();
                await releaseDrain.Task.ConfigureAwait(false);
                return DocumentCacheProjectionDrainPageResult.NoEligibleWork;
            });
        await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = supervisor.StopAsync(CancellationToken.None);
            Task completedBeforeDrainReleased = await Task.WhenAny(
                stopTask,
                Task.Delay(TimeSpan.FromMilliseconds(100))
            );

            completedBeforeDrainReleased.Should().NotBe(stopTask);
            activeContext.CancellationRequested.Should().BeTrue();
            targetContextFactory.DisposedContexts.Should().NotContain(activeContext.ContextKey);
            observationSink.EndedTargets.Should().BeEmpty();
            supervisor.CurrentTargetContexts.Should().BeEmpty();

            releaseDrain.SetResult();
            (await drainTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().NotBeNull();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

            targetContextFactory
                .DisposedContexts.Should()
                .ContainSingle()
                .Which.Should()
                .Be(activeContext.ContextKey);
            observationSink
                .EndedTargets.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new RecordingObservationSink.EndedTarget(
                        activeContext.ContextKey,
                        DocumentCacheProjectionTargetEndReason.Shutdown
                    )
                );
        }
        finally
        {
            releaseDrain.TrySetResult();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task It_retains_an_active_administrative_command_until_shutdown_ownership_releases()
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
        TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext activeContext = targetContextFactory
            .CreatedContexts.Should()
            .ContainSingle()
            .Subject;
        IDisposable? commandRetention = activeContext.RetainForAdministrativeCommand();
        Task<int> commandTask = activeContext.DrainExecutor.RunAdministrativeCommandAsync(async _ =>
        {
            commandStarted.SetResult();
            await releaseCommand.Task.ConfigureAwait(false);
            return 1;
        });
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = supervisor.StopAsync(CancellationToken.None);
            Task completedBeforeCommandReleased = await Task.WhenAny(
                stopTask,
                Task.Delay(TimeSpan.FromMilliseconds(100))
            );

            completedBeforeCommandReleased.Should().NotBe(stopTask);
            activeContext.CancellationRequested.Should().BeFalse();
            targetContextFactory.DisposedContexts.Should().NotContain(activeContext.ContextKey);
            observationSink.EndedTargets.Should().BeEmpty();
            supervisor.CurrentTargetContexts.Should().BeEmpty();

            releaseCommand.SetResult();
            (await commandTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
            Task completedBeforeRetentionReleased = await Task.WhenAny(
                stopTask,
                Task.Delay(TimeSpan.FromMilliseconds(100))
            );
            completedBeforeRetentionReleased.Should().NotBe(stopTask);

            commandRetention.Dispose();
            commandRetention = null;
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

            activeContext.CancellationRequested.Should().BeTrue();
            targetContextFactory
                .DisposedContexts.Should()
                .ContainSingle()
                .Which.Should()
                .Be(activeContext.ContextKey);
            observationSink
                .EndedTargets.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new RecordingObservationSink.EndedTarget(
                        activeContext.ContextKey,
                        DocumentCacheProjectionTargetEndReason.Shutdown
                    )
                );
        }
        finally
        {
            releaseCommand.TrySetResult();
            commandRetention?.Dispose();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task It_coalesces_concurrent_refresh_signals_into_one_pending_background_refresh()
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
        RecordingTargetRegistry.BlockingRefreshControl signaledRefresh = registry.QueueBlockingRefresh(
            Snapshot([EligibleObservation(executionContext)]),
            RuntimeSnapshot([executionContext])
        );
        RecordingProjectionScheduler scheduler = new(
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler
        );

        Parallel.For(0, 32, _ => supervisor.SignalRefresh());

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await signaledRefresh.WaitForStartedAsync();

            registry
                .RefreshReasons.Should()
                .Equal(
                    DocumentCacheTargetRefreshReason.Startup,
                    DocumentCacheTargetRefreshReason.CmsRefreshNotification
                );

            signaledRefresh.Release();
            await scheduler.WaitForCallCountAsync(2);
        }
        finally
        {
            signaledRefresh.Release();
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task It_does_not_block_a_refresh_signal_behind_an_in_flight_reconciliation()
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
        RecordingTargetRegistry.BlockingRefreshControl blockingRefresh = registry.QueueBlockingRefresh(
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
        Task<DocumentCacheTargetRegistrySnapshot> inFlightRefresh = supervisor.RefreshAsync(
            DocumentCacheTargetRefreshReason.SupervisorTriggered
        );
        await blockingRefresh.WaitForStartedAsync();

        try
        {
            await Task.Run(supervisor.SignalRefresh).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            blockingRefresh.Release();
            await inFlightRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task It_logs_a_failed_signaled_refresh_and_retries_after_a_later_signal()
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
        var refreshException = new InvalidOperationException("CMS refresh failed");
        registry.QueueRefreshException(refreshException);
        RecordingTargetRegistry.BlockingRefreshControl retryRefresh = registry.QueueBlockingRefresh(
            Snapshot([EligibleObservation(executionContext)]),
            RuntimeSnapshot([executionContext])
        );
        RecordingProjectionScheduler scheduler = new(
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        RecordingLogger<DocumentCacheProjectionSupervisor> logger = new();
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler,
            logger: logger
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(1);

            supervisor.SignalRefresh();
            await logger.WaitForWarningAsync();

            supervisor.SignalRefresh();
            await retryRefresh.WaitForStartedAsync();

            registry
                .RefreshReasons.Should()
                .Equal(
                    DocumentCacheTargetRefreshReason.Startup,
                    DocumentCacheTargetRefreshReason.CmsRefreshNotification,
                    DocumentCacheTargetRefreshReason.CmsRefreshNotification
                );
            logger
                .WarningEntries.Should()
                .ContainSingle()
                .Which.Should()
                .Match<RecordingLogger<DocumentCacheProjectionSupervisor>.Entry>(entry =>
                    ReferenceEquals(entry.Exception, refreshException)
                    && entry.Message.Contains("later signal", StringComparison.Ordinal)
                );

            retryRefresh.Release();
            await scheduler.WaitForCallCountAsync(3);
        }
        finally
        {
            retryRefresh.Release();
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task It_logs_a_failed_scheduled_refresh_and_retries_at_the_next_poll_interval()
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
        var refreshException = new InvalidOperationException("scheduled CMS refresh failed");
        registry.QueueRefreshException(refreshException);
        RecordingTargetRegistry.BlockingRefreshControl retryRefresh = registry.QueueBlockingRefresh(
            Snapshot([EligibleObservation(executionContext)]),
            RuntimeSnapshot([executionContext])
        );
        ControlledTimeProvider timeProvider = new(ObservedAt);
        RecordingProjectionScheduler scheduler = new(
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        RecordingLogger<DocumentCacheProjectionSupervisor> logger = new();
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler,
            timeProvider,
            logger: logger
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(1);
            await timeProvider.WaitForTimerCountAsync(1);

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            await logger.WaitForWarningAsync();
            await scheduler.WaitForCallCountAsync(2);

            Task retryStartedBeforeNextPoll = retryRefresh.WaitForStartedAsync();
            Task completedBeforeNextPoll = await Task.WhenAny(
                retryStartedBeforeNextPoll,
                Task.Delay(TimeSpan.FromMilliseconds(100))
            );
            completedBeforeNextPoll.Should().NotBe(retryStartedBeforeNextPoll);

            await timeProvider.WaitForTimerCountAsync(2);
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            await retryStartedBeforeNextPoll;

            registry
                .RefreshReasons.Should()
                .Equal(
                    DocumentCacheTargetRefreshReason.Startup,
                    DocumentCacheTargetRefreshReason.SupervisorTriggered,
                    DocumentCacheTargetRefreshReason.SupervisorTriggered
                );
            logger
                .WarningEntries.Should()
                .ContainSingle()
                .Which.Should()
                .Match<RecordingLogger<DocumentCacheProjectionSupervisor>.Entry>(entry =>
                    ReferenceEquals(entry.Exception, refreshException)
                    && entry.Message.Contains("scheduled supervisor refresh", StringComparison.Ordinal)
                );

            retryRefresh.Release();
            await scheduler.WaitForCallCountAsync(3);
        }
        finally
        {
            retryRefresh.Release();
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task It_reconsiders_ready_targets_immediately_after_a_page_is_processed()
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
        RecordingProjectionScheduler scheduler = new(
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.PageProcessed(3)),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(2);

            registry.RefreshReasons.Should().Equal(DocumentCacheTargetRefreshReason.Startup);
            DocumentCacheProjectionTargetContextKey expectedContextKey = targetContextFactory
                .CreatedContexts.Single()
                .ContextKey;
            scheduler.CallBatches.Should().HaveCount(2);
            scheduler.CallBatches[0].Should().Equal(expectedContextKey);
            scheduler.CallBatches[1].Should().Equal(expectedContextKey);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task It_refreshes_at_the_poll_interval_during_sustained_page_processing()
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
        registry.QueueRefresh(
            Snapshot([EligibleObservation(executionContext)]),
            RuntimeSnapshot([executionContext])
        );
        ControlledTimeProvider timeProvider = new(ObservedAt);
        RecordingDocumentCacheLifecycleReader lifecycleReader = new();
        RecordingProjectionScheduler scheduler = new(
            contexts =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(2));
                return DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.PageProcessed(3));
            },
            contexts =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(2));
                return DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.PageProcessed(3));
            },
            contexts =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                return DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.PageProcessed(3));
            },
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.PageProcessed(3)),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler,
            timeProvider,
            lifecycleReader
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(4);

            DocumentCacheProjectionTargetContextKey expectedContextKey = targetContextFactory
                .CreatedContexts.Should()
                .ContainSingle()
                .Subject.ContextKey;
            scheduler
                .CallBatches.Take(4)
                .Should()
                .AllSatisfy(batch => batch.Should().Equal(expectedContextKey));
            registry
                .RefreshReasons.Should()
                .Equal(
                    DocumentCacheTargetRefreshReason.Startup,
                    DocumentCacheTargetRefreshReason.SupervisorTriggered
                );
            lifecycleReader.ReadCount.Should().Be(2);
            timeProvider.GetUtcNow().Should().Be(ObservedAt.AddSeconds(5));
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task It_uses_expiration_aware_registry_refresh_at_the_poll_interval_for_current_targets()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        IOptions<DocumentCacheOptions> options = OptionsFor([targetKey]);
        ControlledTimeProvider timeProvider = new(ObservedAt);
        RecordingSupervisorDataStoreProvider dataStoreProvider = new();
        dataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
        RegistryTargetContextBuilder registryContextBuilder = new(options);
        DocumentCacheTargetRegistry registry = new(
            dataStoreProvider,
            registryContextBuilder,
            options,
            timeProvider,
            NullLogger<DocumentCacheTargetRegistry>.Instance
        );
        RecordingObservationSink observationSink = new();
        RecordingTargetContextFactory targetContextFactory = new(observationSink);
        RecordingProjectionScheduler scheduler = new(
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            contexts => DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            options,
            scheduler,
            timeProvider
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(1);

            await timeProvider.WaitForTimerCountAsync(1);
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            await scheduler.WaitForCallCountAsync(2);

            dataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            dataStoreProvider.RefreshIfExpiredCalls.Should().Equal("TenantA");
            registryContextBuilder.BuildCalls.Should().ContainSingle();
            targetContextFactory
                .CreatedContexts.Should()
                .ContainSingle()
                .Which.Generation.Value.Should()
                .Be(1);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task It_waits_for_the_poll_interval_after_all_ready_targets_report_no_work()
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
        RecordingProjectionScheduler scheduler = new(contexts =>
            DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(1);

            scheduler.CallBatches.Should().ContainSingle();
            registry.RefreshReasons.Should().Equal(DocumentCacheTargetRefreshReason.Startup);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [TestCase(SchedulerWakeKind.PollSleep)]
    [TestCase(SchedulerWakeKind.TargetBackoff)]
    [NonParallelizable]
    public async Task It_dispatches_again_when_a_scheduler_deadline_arrives_before_the_next_poll_tick(
        SchedulerWakeKind wakeKind
    )
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
        ControlledTimeProvider timeProvider = new(ObservedAt);
        RecordingProjectionScheduler scheduler = new(
            contexts =>
            {
                DateTimeOffset wakeAt = timeProvider.GetUtcNow().AddSeconds(2);
                foreach (DocumentCacheProjectionTargetRuntimeContext context in contexts)
                {
                    if (wakeKind == SchedulerWakeKind.PollSleep)
                    {
                        context.SchedulingState.SetPollSleepUntil(wakeAt);
                    }
                    else
                    {
                        context.SchedulingState.SetTargetBackoffUntil(wakeAt);
                    }
                }

                return [];
            },
            contexts =>
            {
                foreach (DocumentCacheProjectionTargetRuntimeContext context in contexts)
                {
                    context.SchedulingState.GetReadinessBlock(
                        timeProvider.GetUtcNow(),
                        context.CancellationRequested,
                        context.DrainExecutor
                    );
                }

                return [];
            }
        );
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([executionContext.TargetKey]),
            scheduler,
            timeProvider
        );

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await scheduler.WaitForCallCountAsync(1);

            DocumentCacheProjectionTargetRuntimeContext runtimeContext = targetContextFactory
                .CreatedContexts.Should()
                .ContainSingle()
                .Subject;
            scheduler.CallBatches.Should().ContainSingle().Which.Should().Equal(runtimeContext.ContextKey);
            runtimeContext
                .SchedulingState.GetNextSchedulingWakeAt(
                    timeProvider.GetUtcNow(),
                    runtimeContext.CancellationRequested,
                    runtimeContext.DrainExecutor
                )
                .Should()
                .Be(ObservedAt.AddSeconds(2));

            await timeProvider.WaitForTimerCountAsync(1);
            timeProvider.Advance(TimeSpan.FromSeconds(2));
            await scheduler.WaitForCallCountAsync(2);

            scheduler.CallBatches.Should().HaveCount(2);
            registry.RefreshReasons.Should().Equal(DocumentCacheTargetRefreshReason.Startup);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    private static DocumentCacheProjectionSupervisor CreateSupervisor(
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory,
        IDocumentCacheProjectionObservationSink observationSink,
        IOptions<DocumentCacheOptions> options,
        IDocumentCacheProjectionScheduler? scheduler = null,
        TimeProvider? timeProvider = null,
        IDocumentCacheLifecycleReader? lifecycleReader = null,
        ILogger<DocumentCacheProjectionSupervisor>? logger = null
    ) =>
        new(
            registry,
            targetContextFactory,
            observationSink,
            options,
            scheduler ?? new NoOpDocumentCacheProjectionScheduler(),
            lifecycleReader ?? new StubDocumentCacheLifecycleReader(),
            timeProvider ?? new FixedTimeProvider(ObservedAt),
            logger ?? NullLogger<DocumentCacheProjectionSupervisor>.Instance
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

    private static DataStore CreateDataStore(
        long id,
        string connectionString,
        RelationalProviderToken? relationalProviderToken = null
    ) =>
        new(
            id,
            "Operational",
            "Display name must not leak",
            connectionString,
            new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("schoolYear")] = new("2026"),
            },
            relationalProviderToken ?? RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported
        );

    private sealed class RecordingTargetRegistry : IDocumentCacheTargetRegistry
    {
        private readonly Queue<QueuedRefresh> _refreshes = new();

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; private set; } =
            new([], ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; private set; } =
            new([], ObservedAt);

        public List<DocumentCacheTargetRefreshReason> RefreshReasons { get; } = [];

        public void QueueRefresh(
            DocumentCacheTargetRegistrySnapshot snapshot,
            DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
        ) =>
            _refreshes.Enqueue(
                new QueuedRefresh(snapshot, runtimeSnapshot, BlockingControl: null, Exception: null)
            );

        public void QueueRefreshException(Exception exception) =>
            _refreshes.Enqueue(
                new QueuedRefresh(
                    CurrentSnapshot,
                    CurrentRuntimeSnapshot,
                    BlockingControl: null,
                    Exception: exception
                )
            );

        public BlockingRefreshControl QueueBlockingRefresh(
            DocumentCacheTargetRegistrySnapshot snapshot,
            DocumentCacheTargetRuntimeSnapshot runtimeSnapshot
        )
        {
            BlockingRefreshControl blockingControl = new();
            _refreshes.Enqueue(
                new QueuedRefresh(snapshot, runtimeSnapshot, blockingControl, Exception: null)
            );
            return blockingControl;
        }

        public async Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshReasons.Add(reason);

            QueuedRefresh refresh = _refreshes.Dequeue();
            if (refresh.BlockingControl is not null)
            {
                await refresh.BlockingControl.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
            }

            if (refresh.Exception is not null)
            {
                throw refresh.Exception;
            }

            CurrentSnapshot = refresh.RegistrySnapshot;
            CurrentRuntimeSnapshot = refresh.RegistryRuntimeSnapshot;
            return CurrentSnapshot;
        }

        public sealed class BlockingRefreshControl
        {
            private readonly TaskCompletionSource _started = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            private readonly TaskCompletionSource _released = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            public Task WaitForStartedAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            public void Release() => _released.TrySetResult();

            internal async Task WaitForReleaseAsync(CancellationToken cancellationToken)
            {
                _started.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed record QueuedRefresh(
            DocumentCacheTargetRegistrySnapshot RegistrySnapshot,
            DocumentCacheTargetRuntimeSnapshot RegistryRuntimeSnapshot,
            BlockingRefreshControl? BlockingControl,
            Exception? Exception
        );
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private readonly object _sync = new();
        private readonly List<Entry> _entries = [];
        private readonly TaskCompletionSource _warningLogged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public IReadOnlyList<Entry> WarningEntries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_sync)
            {
                _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }

            if (logLevel == LogLevel.Warning)
            {
                _warningLogged.TrySetResult();
            }
        }

        public Task WaitForWarningAsync() => _warningLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            ObservedAt,
            CancellationToken.None
        );

    private sealed class RecordingTargetContextFactory(
        IDocumentCacheProjectionObservationSink observationSink
    ) : IDocumentCacheProjectionTargetRuntimeContextFactory
    {
        private readonly object _disposeSync = new();
        private readonly List<DocumentCacheProjectionTargetContextKey> _disposedContexts = [];
        private readonly List<DisposeWaiter> _disposeWaiters = [];

        public StubDocumentCacheMaterializer Materializer { get; } = new();

        public StubDocumentCacheWriter Writer { get; } = new();

        public List<DocumentCacheTargetExecutionContext> CreateCalls { get; } = [];

        public List<DocumentCacheProjectionTargetRuntimeContext> CreatedContexts { get; } = [];

        public ImmutableArray<DocumentCacheProjectionTargetContextKey> DisposedContexts
        {
            get
            {
                lock (_disposeSync)
                {
                    return _disposedContexts.ToImmutableArray();
                }
            }
        }

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
                observationSink,
                () => RecordDisposedContextAsync(executionContext)
            );
            CreatedContexts.Add(context);

            return Task.FromResult(context);
        }

        public Task WaitForDisposedContextAsync(DocumentCacheProjectionTargetContextKey contextKey)
        {
            lock (_disposeSync)
            {
                if (_disposedContexts.Contains(contextKey))
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeWaiters.Add(new DisposeWaiter(contextKey, completion));
                return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private ValueTask RecordDisposedContextAsync(DocumentCacheTargetExecutionContext executionContext)
        {
            DocumentCacheProjectionTargetContextKey contextKey = new(
                executionContext.TargetKey,
                executionContext.Generation
            );

            lock (_disposeSync)
            {
                _disposedContexts.Add(contextKey);
                foreach (
                    DisposeWaiter waiter in _disposeWaiters
                        .Where(waiter => waiter.ContextKey == contextKey)
                        .ToArray()
                )
                {
                    waiter.Completion.SetResult();
                    _disposeWaiters.Remove(waiter);
                }
            }

            return ValueTask.CompletedTask;
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

        private sealed record DisposeWaiter(
            DocumentCacheProjectionTargetContextKey ContextKey,
            TaskCompletionSource Completion
        );
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

    private sealed class RegistryTargetContextBuilder(IOptions<DocumentCacheOptions> options)
        : IDocumentCacheTargetContextBuilder
    {
        private readonly DocumentCacheTargetEffectiveSettings _effectiveSettings =
            DocumentCacheTargetEffectiveSettings.FromOptions(options.Value);

        public List<RegistryBuildCall> BuildCalls { get; } = [];

        public Task<DocumentCacheTargetContextBuildResult> BuildAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCacheResolvedTargetDataStore resolvedDataStore,
            DocumentCacheTargetContextGeneration generation,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCalls.Add(new RegistryBuildCall(targetKey, resolvedDataStore, generation));

            RelationalProviderToken providerToken =
                resolvedDataStore.RelationalProviderToken ?? RelationalProviderToken.Postgresql;
            DocumentCacheTargetExecutionContext executionContext = new(
                targetKey,
                generation,
                _effectiveSettings,
                new DocumentCacheTargetDataStoreMetadata(
                    resolvedDataStore.Id,
                    resolvedDataStore.DataStoreType
                ),
                new DocumentCacheTargetConnectionInput(
                    providerToken,
                    resolvedDataStore.ConnectionFactoryInput ?? "connection"
                ),
                Fingerprint,
                TrackingLifecycle,
                SatisfiedInventory,
                SatisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );

            return Task.FromResult(
                new DocumentCacheTargetContextBuildResult(
                    EligibleObservation(executionContext),
                    executionContext
                )
            );
        }
    }

    private sealed record RegistryBuildCall(
        DocumentCacheTargetKey TargetKey,
        DocumentCacheResolvedTargetDataStore ResolvedDataStore,
        DocumentCacheTargetContextGeneration Generation
    );

    private sealed class RecordingSupervisorDataStoreProvider : IDataStoreProvider
    {
        private readonly Dictionary<string, Queue<IList<DataStore>>> _queuedLoadResults = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, IList<DataStore>> _loadedDataStores = new(
            StringComparer.OrdinalIgnoreCase
        );

        public List<string> LoadDataStoreCalls { get; } = [];

        public List<string> RefreshIfExpiredCalls { get; } = [];

        public void QueueLoadResult(string? tenant, params DataStore[] dataStores) =>
            GetQueue(tenant).Enqueue(dataStores);

        public Task<IList<DataStore>> LoadDataStores(string? tenant = null)
        {
            string tenantKey = GetTenantKey(tenant);
            LoadDataStoreCalls.Add(tenantKey);

            Queue<IList<DataStore>> queue = GetQueue(tenant);
            IList<DataStore> dataStores = queue.Count == 0 ? [] : queue.Dequeue();
            _loadedDataStores[tenantKey] = dataStores;
            return Task.FromResult(dataStores);
        }

        public Task RefreshInstancesIfExpiredAsync(string? tenant = null)
        {
            RefreshIfExpiredCalls.Add(GetTenantKey(tenant));
            return Task.CompletedTask;
        }

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
            _loadedDataStores.TryGetValue(GetTenantKey(tenant), out IList<DataStore>? dataStores)
                ? dataStores.ToList().AsReadOnly()
                : [];

        public DataStore? GetById(long id, string? tenant = null) =>
            _loadedDataStores.TryGetValue(GetTenantKey(tenant), out IList<DataStore>? dataStores)
                ? dataStores.FirstOrDefault(dataStore => dataStore.Id == id)
                : null;

        public bool IsLoaded(string? tenant = null) => _loadedDataStores.ContainsKey(GetTenantKey(tenant));

        public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([]);

        public bool TenantExists(string tenant) => false;

        public IReadOnlyList<string> GetLoadedTenantKeys() => [];

        private Queue<IList<DataStore>> GetQueue(string? tenant)
        {
            string tenantKey = GetTenantKey(tenant);
            if (!_queuedLoadResults.TryGetValue(tenantKey, out Queue<IList<DataStore>>? queue))
            {
                queue = new Queue<IList<DataStore>>();
                _queuedLoadResults.Add(tenantKey, queue);
            }

            return queue;
        }

        private static string GetTenantKey(string? tenant) => tenant ?? string.Empty;
    }

    private static ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> DispatchedResults(
        ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> contexts,
        DocumentCacheProjectionDrainPageResult drainResult
    ) =>
        contexts
            .Select(context =>
                DocumentCacheProjectionSchedulerDispatchResult.Dispatched(
                    context,
                    drainResult,
                    ObservedAt,
                    ObservedAt
                )
            )
            .ToImmutableArray();

    private sealed class RecordingProjectionScheduler(
        params Func<
            ImmutableArray<DocumentCacheProjectionTargetRuntimeContext>,
            ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>
        >[] resultFactories
    ) : IDocumentCacheProjectionScheduler
    {
        private readonly object _sync = new();
        private readonly Queue<
            Func<
                ImmutableArray<DocumentCacheProjectionTargetRuntimeContext>,
                ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>
            >
        > _resultFactories = new(resultFactories);
        private readonly List<Waiter> _waiters = [];

        public List<ImmutableArray<DocumentCacheProjectionTargetContextKey>> CallBatches { get; } = [];

        public Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
            IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> contexts =
                targetContexts.ToImmutableArray();
            Func<
                ImmutableArray<DocumentCacheProjectionTargetRuntimeContext>,
                ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>
            > resultFactory;

            lock (_sync)
            {
                CallBatches.Add(contexts.Select(context => context.ContextKey).ToImmutableArray());
                resultFactory = _resultFactories.Count == 0 ? _ => [] : _resultFactories.Dequeue();
            }

            ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results = resultFactory(contexts);

            lock (_sync)
            {
                CompleteSatisfiedWaiters();
            }

            return Task.FromResult(results);
        }

        public Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task WaitForCallCountAsync(int callCount)
        {
            lock (_sync)
            {
                if (CallBatches.Count >= callCount)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new Waiter(callCount, completion));
                return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private void CompleteSatisfiedWaiters()
        {
            foreach (
                Waiter waiter in _waiters.Where(waiter => CallBatches.Count >= waiter.CallCount).ToArray()
            )
            {
                waiter.Completion.SetResult();
                _waiters.Remove(waiter);
            }
        }

        private sealed record Waiter(int CallCount, TaskCompletionSource Completion);
    }

    private sealed class BlockingOrdinaryDispatchLeaseScheduler : IDocumentCacheProjectionScheduler
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _dispatchAcquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseDispatch = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _dispatchReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _callCount;

        public List<ImmutableArray<DocumentCacheProjectionTargetContextKey>> CallBatches { get; } = [];

        public Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
            IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> contexts =
                targetContexts.ToImmutableArray();

            lock (_sync)
            {
                CallBatches.Add(contexts.Select(context => context.ContextKey).ToImmutableArray());
            }

            if (Interlocked.Increment(ref _callCount) != 1 || contexts.IsEmpty)
            {
                return Task.FromResult(
                    DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
                );
            }

            return RunBlockingDispatchAcquisitionAsync(contexts[0]);
        }

        public Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task WaitForDispatchAcquiredAsync() =>
            _dispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForDispatchReleasedAsync() =>
            _dispatchReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseDispatch() => _releaseDispatch.TrySetResult();

        private async Task<
            ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>
        > RunBlockingDispatchAcquisitionAsync(DocumentCacheProjectionTargetRuntimeContext context)
        {
            IDisposable? lease = context.TryAcquireOrdinaryDispatchLease();
            if (lease is null)
            {
                _dispatchReleased.TrySetResult();
                return
                [
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(
                        context,
                        DocumentCacheProjectionTargetReadinessBlockReason.CancellationPending,
                        ObservedAt
                    ),
                ];
            }

            try
            {
                _dispatchAcquired.TrySetResult();
                await _releaseDispatch.Task.ConfigureAwait(false);
                return
                [
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(
                        context,
                        context.CancellationRequested
                            ? DocumentCacheProjectionTargetReadinessBlockReason.CancellationPending
                            : DocumentCacheProjectionTargetReadinessBlockReason.LocalDrainActive,
                        ObservedAt
                    ),
                ];
            }
            finally
            {
                lease.Dispose();
                _dispatchReleased.TrySetResult();
            }
        }
    }

    private sealed class BlockingOrdinaryDrainScheduler : IDocumentCacheProjectionScheduler
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _drainStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseDrain = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _drainReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _callCount;

        public List<ImmutableArray<DocumentCacheProjectionTargetContextKey>> CallBatches { get; } = [];

        public Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
            IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> contexts =
                targetContexts.ToImmutableArray();

            lock (_sync)
            {
                CallBatches.Add(contexts.Select(context => context.ContextKey).ToImmutableArray());
            }

            if (Interlocked.Increment(ref _callCount) != 1 || contexts.IsEmpty)
            {
                return Task.FromResult(
                    DispatchedResults(contexts, DocumentCacheProjectionDrainPageResult.NoEligibleWork)
                );
            }

            return RunBlockingOrdinaryDrainAsync(contexts[0], cancellationToken);
        }

        public Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task WaitForDrainStartedAsync() => _drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForDrainReleasedAsync() => _drainReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseDrain() => _releaseDrain.TrySetResult();

        private async Task<
            ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>
        > RunBlockingOrdinaryDrainAsync(
            DocumentCacheProjectionTargetRuntimeContext context,
            CancellationToken cancellationToken
        )
        {
            DocumentCacheProjectionDrainPageResult? drainResult = await context
                .DrainExecutor.TryRunOrdinaryDrainSliceAsync(
                    async _ =>
                    {
                        _drainStarted.TrySetResult();
                        await _releaseDrain.Task.ConfigureAwait(false);
                        return DocumentCacheProjectionDrainPageResult.NoEligibleWork;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            _drainReleased.TrySetResult();

            return drainResult is null
                ?
                [
                    DocumentCacheProjectionSchedulerDispatchResult.Skipped(
                        context,
                        DocumentCacheProjectionTargetReadinessBlockReason.LocalDrainActive,
                        ObservedAt
                    ),
                ]
                :
                [
                    DocumentCacheProjectionSchedulerDispatchResult.Dispatched(
                        context,
                        drainResult,
                        ObservedAt,
                        ObservedAt
                    ),
                ];
        }
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

    private sealed class RecordingDocumentCacheLifecycleReader : IDocumentCacheLifecycleReader
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public int ReadCount { get; private set; }

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
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

    private sealed class ControlledTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ControlledTimer> _timers = [];
        private readonly List<TimerWaiter> _timerWaiters = [];
        private DateTimeOffset _utcNow = utcNow;
        private int _createdTimerCount;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            ArgumentNullException.ThrowIfNull(callback);

            ControlledTimer timer = new(this, callback, state, dueTime, period);
            ImmutableArray<TimerCallbackRegistration> dueCallbacks;
            lock (_sync)
            {
                _timers.Add(timer);
                _createdTimerCount++;
                CompleteTimerWaitersNoLock();
                dueCallbacks = CollectDueCallbacksNoLock();
            }

            QueueCallbacks(dueCallbacks);
            return timer;
        }

        public void Advance(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be nonnegative.");
            }

            ImmutableArray<TimerCallbackRegistration> dueCallbacks;
            lock (_sync)
            {
                _utcNow += delay;
                dueCallbacks = CollectDueCallbacksNoLock();
            }

            QueueCallbacks(dueCallbacks);
        }

        public Task WaitForTimerCountAsync(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Timer count must be positive.");
            }

            lock (_sync)
            {
                if (_createdTimerCount >= count)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerWaiters.Add(new TimerWaiter(count, completion));
                return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private ImmutableArray<TimerCallbackRegistration> CollectDueCallbacksNoLock()
        {
            ImmutableArray<TimerCallbackRegistration>.Builder callbacks =
                ImmutableArray.CreateBuilder<TimerCallbackRegistration>();

            foreach (ControlledTimer timer in _timers.ToArray())
            {
                if (!timer.TryConsumeDueNoLock(_utcNow, out TimerCallbackRegistration? callback))
                {
                    continue;
                }

                callbacks.Add(callback);
                if (timer.IsDisposedNoLock)
                {
                    _timers.Remove(timer);
                }
            }

            return callbacks.ToImmutable();
        }

        private void CompleteTimerWaitersNoLock()
        {
            foreach (
                TimerWaiter waiter in _timerWaiters
                    .Where(waiter => _createdTimerCount >= waiter.Count)
                    .ToArray()
            )
            {
                waiter.Completion.SetResult();
                _timerWaiters.Remove(waiter);
            }
        }

        private static void QueueCallbacks(ImmutableArray<TimerCallbackRegistration> callbacks)
        {
            foreach (TimerCallbackRegistration callback in callbacks)
            {
                ThreadPool.QueueUserWorkItem(
                    static state =>
                    {
                        TimerCallbackRegistration registration = (TimerCallbackRegistration)state!;
                        registration.Callback(registration.State);
                    },
                    callback
                );
            }
        }

        private sealed class ControlledTimer(
            ControlledTimeProvider timeProvider,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        ) : ITimer
        {
            private DateTimeOffset? _dueAt = CalculateDueAtNoLock(timeProvider, dueTime);
            private TimeSpan _period = RequireValidPeriod(period);
            private bool _disposed;

            public bool IsDisposedNoLock => _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                ImmutableArray<TimerCallbackRegistration> dueCallbacks;
                lock (timeProvider._sync)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueAt = CalculateDueAtNoLock(timeProvider, dueTime);
                    _period = RequireValidPeriod(period);
                    dueCallbacks = timeProvider.CollectDueCallbacksNoLock();
                }

                QueueCallbacks(dueCallbacks);
                return true;
            }

            public bool TryConsumeDueNoLock(
                DateTimeOffset now,
                out TimerCallbackRegistration callbackRegistration
            )
            {
                callbackRegistration = null!;
                if (_disposed || _dueAt is null || _dueAt > now)
                {
                    return false;
                }

                callbackRegistration = new TimerCallbackRegistration(callback, state);
                if (_period > TimeSpan.Zero)
                {
                    _dueAt = now + _period;
                }
                else
                {
                    _disposed = true;
                }

                return true;
            }

            public void Dispose()
            {
                lock (timeProvider._sync)
                {
                    _disposed = true;
                    timeProvider._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            private static DateTimeOffset? CalculateDueAtNoLock(
                ControlledTimeProvider timeProvider,
                TimeSpan dueTime
            )
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return null;
                }

                if (dueTime < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(dueTime), "Due time must be nonnegative.");
                }

                return timeProvider._utcNow + dueTime;
            }

            private static TimeSpan RequireValidPeriod(TimeSpan period)
            {
                if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(period), "Period must be nonnegative.");
                }

                return period;
            }
        }

        private sealed record TimerCallbackRegistration(TimerCallback Callback, object? State);

        private sealed record TimerWaiter(int Count, TaskCompletionSource Completion);
    }
}
