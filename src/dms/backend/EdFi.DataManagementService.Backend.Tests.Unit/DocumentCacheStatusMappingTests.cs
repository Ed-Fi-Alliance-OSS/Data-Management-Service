// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheStatusMapping")]
public class Given_DocumentCacheStatusMapping
{
    private static readonly DateTimeOffset RegistryObservedAt = new(2026, 8, 17, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProcessObservedAt = new(2026, 8, 17, 14, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset RuntimeObservedAt = new(2026, 8, 17, 14, 0, 2, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = new(2026, 8, 17, 14, 0, 3, TimeSpan.Zero);

    [Test]
    public async Task It_maps_projection_diagnostics_to_public_categories()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(generation: 3, pageSize: 10);
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ProcessObservedAt)
        );
        observationStore.ObserveTarget(
            TargetHealth(
                target,
                targetDiagnostics:
                [
                    TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing),
                    TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure),
                    TargetDiagnostic(
                        target,
                        DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
                    ),
                    TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure),
                    TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet),
                ],
                documentDiagnostics:
                [
                    DocumentDiagnostic(
                        101,
                        DocumentCacheProjectionDocumentDiagnosticCategory.PossibleUnseededBaseline
                    ),
                    DocumentDiagnostic(102, DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly),
                    DocumentDiagnostic(103, DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome),
                    DocumentDiagnostic(
                        104,
                        DocumentCacheProjectionDocumentDiagnosticCategory.ProviderFailure
                    ),
                    DocumentDiagnostic(
                        105,
                        DocumentCacheProjectionDocumentDiagnosticCategory.PoisonSuppressed
                    ),
                ],
                suppressedDocumentCount: 3,
                suppressedDocumentIds: [201, 202],
                poisonTraversalDiagnostics:
                [
                    PoisonTraversalDiagnostic(
                        301,
                        DocumentCacheProjectionPoisonTraversalDiagnosticCategory.RetryScheduled,
                        RuntimeObservedAt.AddSeconds(10)
                    ),
                    PoisonTraversalDiagnostic(
                        302,
                        DocumentCacheProjectionPoisonTraversalDiagnosticCategory.PageCapacityExhausted,
                        RuntimeObservedAt.AddSeconds(20)
                    ),
                    PoisonTraversalDiagnostic(
                        303,
                        DocumentCacheProjectionPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
                        RuntimeObservedAt.AddSeconds(30)
                    ),
                ],
                targetDiagnosticEvictionCount: 2,
                documentDiagnosticEvictionCount: 3,
                poisonTraversalDiagnosticEvictionCount: 4
            )
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            observationStore
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget
            .TargetDiagnostics.RecentEvents.Select(diagnostic => diagnostic.Category)
            .Should()
            .Equal(
                DocumentCacheStatusTargetDiagnosticCategory.TargetResolution,
                DocumentCacheStatusTargetDiagnosticCategory.Inventory,
                DocumentCacheStatusTargetDiagnosticCategory.ProviderPrerequisite,
                DocumentCacheStatusTargetDiagnosticCategory.ProviderObservationFailed,
                DocumentCacheStatusTargetDiagnosticCategory.TargetInvariant
            );
        statusTarget.TargetDiagnostics.EvictedCount.Should().Be(2);
        statusTarget
            .DocumentDiagnostics.RecentEvents.Select(diagnostic => diagnostic.Category)
            .Should()
            .Equal(
                DocumentCacheStatusDocumentDiagnosticCategory.CacheAheadSuspected,
                DocumentCacheStatusDocumentDiagnosticCategory.SourceChanged,
                DocumentCacheStatusDocumentDiagnosticCategory.WriterFailed,
                DocumentCacheStatusDocumentDiagnosticCategory.MaterializationFailed,
                DocumentCacheStatusDocumentDiagnosticCategory.PoisonRetryScheduled
            );
        statusTarget.DocumentDiagnostics.EvictedCount.Should().Be(3);
        statusTarget
            .PoisonTraversalDiagnostics.RecentEvents.Select(diagnostic => diagnostic.DocumentId)
            .Should()
            .Equal(301, 302, 303);
        statusTarget
            .PoisonTraversalDiagnostics.RecentEvents.Select(diagnostic => diagnostic.Category)
            .Should()
            .Equal(
                DocumentCacheStatusPoisonTraversalDiagnosticCategory.RetryScheduled,
                DocumentCacheStatusPoisonTraversalDiagnosticCategory.PageCapacityExhausted,
                DocumentCacheStatusPoisonTraversalDiagnosticCategory.SkippedUntilRetry
            );
        statusTarget
            .PoisonTraversalDiagnostics.RecentEvents.Select(diagnostic => diagnostic.ObservedAt)
            .Should()
            .Equal(
                RuntimeObservedAt.AddSeconds(10),
                RuntimeObservedAt.AddSeconds(20),
                RuntimeObservedAt.AddSeconds(30)
            );
        statusTarget.PoisonTraversalDiagnostics.EvictedCount.Should().Be(4);
        statusTarget.EnqueueFailures.RecentEvents.Should().BeEmpty();
    }

    [Test]
    public async Task It_maps_target_diagnostic_event_observed_at_values()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(generation: 3, pageSize: 10);
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ProcessObservedAt)
        );
        DocumentCacheProjectionTargetHealthSnapshot targetHealth = TargetHealth(target);
        DateTimeOffset firstDiagnosticObservedAt = RuntimeObservedAt.AddSeconds(10);
        DateTimeOffset secondDiagnosticObservedAt = RuntimeObservedAt.AddSeconds(20);

        observationStore.ObserveTarget(targetHealth);
        observationStore.AppendTargetDiagnostic(
            targetHealth.ContextKey,
            TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing),
            firstDiagnosticObservedAt
        );
        observationStore.AppendTargetDiagnostic(
            targetHealth.ContextKey,
            TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet),
            secondDiagnosticObservedAt
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            observationStore
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(statusTarget))!.AsObject();
        JsonArray recentEvents = root["targetDiagnostics"]!["recentEvents"]!.AsArray();

        statusTarget
            .TargetDiagnostics.RecentEvents.Select(diagnostic => diagnostic.ObservedAt)
            .Should()
            .Equal(firstDiagnosticObservedAt, secondDiagnosticObservedAt);
        recentEvents
            .Select(diagnostic => diagnostic!["observedAt"]!.GetValue<string>())
            .Should()
            .Equal("2026-08-17T14:00:12Z", "2026-08-17T14:00:22Z");
    }

    [TestCase(
        true,
        false,
        TestName = "It_maps_active_processing_cancellation_to_cancelling_with_durable_facts"
    )]
    [TestCase(false, true, TestName = "It_maps_worker_gate_cancellation_to_cancelling_with_durable_facts")]
    public async Task It_maps_active_runtime_cancellation_to_cancelling_with_durable_facts(
        bool isActivelyProcessing,
        bool isWaitingForWorkerGate
    )
    {
        DocumentCacheTargetObservation target = ResolvedTarget(generation: 3);
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ProcessObservedAt)
        );
        observationStore.ObserveTarget(
            TargetHealth(
                target,
                executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                    isRunning: true,
                    isActivelyProcessing,
                    isWaitingForWorkerGate,
                    isInBackoff: false,
                    backoffUntil: null,
                    cancellationRequested: true,
                    cancellationObservedAt: RuntimeObservedAt
                )
            )
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            observationStore
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(statusTarget))!.AsObject();

        statusTarget.ExecutionState.Status.Should().Be(DocumentCacheStatusExecutionState.Cancelling);
        root["executionState"]!["status"]!.GetValue<string>().Should().Be("cancelling");
        statusTarget.DurableObservedAt.Should().Be(DurableObservedAt);
        statusTarget.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Tracking);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        statusTarget.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Clear);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Empty);
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.None);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.None);
    }

    [Test]
    public async Task It_maps_terminal_runtime_cancellation_to_cancelled()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(generation: 3);
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ProcessObservedAt)
        );
        observationStore.ObserveTarget(
            TargetHealth(
                target,
                executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                    isRunning: false,
                    isActivelyProcessing: false,
                    isWaitingForWorkerGate: false,
                    isInBackoff: false,
                    backoffUntil: null,
                    cancellationRequested: true,
                    cancellationObservedAt: RuntimeObservedAt
                )
            )
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            observationStore
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(statusTarget))!.AsObject();

        statusTarget.ExecutionState.Status.Should().Be(DocumentCacheStatusExecutionState.Cancelled);
        root["executionState"]!["status"]!.GetValue<string>().Should().Be("cancelled");
        statusTarget.DurableObservedAt.Should().BeNull();
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.RuntimeCancelled);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.RuntimeCancelled);
    }

    [Test]
    public async Task It_maps_every_public_execution_state_from_reachable_runtime_observations()
    {
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ProcessObservedAt)
        );
        List<DocumentCacheTargetObservation> targets = [];
        List<DocumentCacheTargetExecutionContext> executionContexts = [];
        Dictionary<int, DocumentCacheStatusExecutionState> expectedByDataStoreId = [];

        AddTarget(
            dataStoreId: 1,
            expectedState: DocumentCacheStatusExecutionState.NotObserved,
            executionState: null,
            observeRuntime: false
        );
        AddTarget(
            dataStoreId: 2,
            expectedState: DocumentCacheStatusExecutionState.Idle,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: false,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: false,
                cancellationObservedAt: null
            )
        );
        AddTarget(
            dataStoreId: 3,
            expectedState: DocumentCacheStatusExecutionState.WaitingForPoll,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: false,
                cancellationObservedAt: null
            )
        );
        AddTarget(
            dataStoreId: 4,
            expectedState: DocumentCacheStatusExecutionState.WaitingForConcurrency,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: true,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: false,
                cancellationObservedAt: null
            )
        );
        AddTarget(
            dataStoreId: 5,
            expectedState: DocumentCacheStatusExecutionState.Active,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: true,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: false,
                cancellationObservedAt: null
            )
        );
        AddTarget(
            dataStoreId: 6,
            expectedState: DocumentCacheStatusExecutionState.TargetBackoff,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: false,
                isInBackoff: true,
                backoffUntil: RuntimeObservedAt.AddSeconds(30),
                cancellationRequested: false,
                cancellationObservedAt: null
            )
        );
        AddTarget(
            dataStoreId: 7,
            expectedState: DocumentCacheStatusExecutionState.Cancelling,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: true,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: true,
                cancellationObservedAt: RuntimeObservedAt
            )
        );
        AddTarget(
            dataStoreId: 8,
            expectedState: DocumentCacheStatusExecutionState.Cancelled,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: false,
                isActivelyProcessing: false,
                isWaitingForWorkerGate: false,
                isInBackoff: false,
                backoffUntil: null,
                cancellationRequested: true,
                cancellationObservedAt: RuntimeObservedAt
            )
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry(targets, executionContexts),
            observationStore
        );

        DocumentCacheStatusTarget[] statusTargets = (await service.GetStatusAsync()).Targets.ToArray();

        statusTargets
            .Select(target => target.ExecutionState.Status)
            .Should()
            .Equal(expectedByDataStoreId.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        statusTargets
            .Select(target => JsonSerializer.Serialize(target.ExecutionState.Status))
            .Should()
            .Equal(
                "\"notObserved\"",
                "\"idle\"",
                "\"waitingForPoll\"",
                "\"waitingForConcurrency\"",
                "\"active\"",
                "\"targetBackoff\"",
                "\"cancelling\"",
                "\"cancelled\""
            );

        void AddTarget(
            int dataStoreId,
            DocumentCacheStatusExecutionState expectedState,
            DocumentCacheProjectionExecutionStateSnapshot? executionState,
            bool observeRuntime = true
        )
        {
            DocumentCacheTargetObservation target = ResolvedTarget(
                generation: dataStoreId,
                dataStoreId: dataStoreId
            );

            targets.Add(target);
            executionContexts.Add(ExecutionContext(target));
            expectedByDataStoreId.Add(dataStoreId, expectedState);

            if (observeRuntime)
            {
                observationStore.ObserveTarget(TargetHealth(target, executionState: executionState));
            }
        }
    }

    [Test]
    public async Task It_maps_only_current_generation_commands_without_generation_fields()
    {
        DocumentCacheTargetObservation oldTarget = ResolvedTarget(generation: 1);
        DocumentCacheTargetObservation currentTarget = ResolvedTarget(generation: 2);
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ProcessObservedAt)
        );
        DocumentCacheAdministrativeCommandExecutionId oldExecutionId = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555")
        );
        DocumentCacheAdministrativeCommandExecutionId endedExecutionId = new(
            Guid.Parse("22222222-3333-4444-5555-666666666666")
        );
        DocumentCacheAdministrativeCommandExecutionId activeExecutionId = new(
            Guid.Parse("33333333-4444-5555-6666-777777777777")
        );

        observationStore.ObserveTarget(TargetHealth(oldTarget));
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                oldExecutionId,
                oldTarget,
                DocumentCacheAdministrativeCommand.OfflineActivation
            )
        );
        observationStore.ObserveTarget(TargetHealth(currentTarget));
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                endedExecutionId,
                currentTarget,
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild
            )
        );
        observationStore.EndAdministrativeCommand(
            endedExecutionId,
            CommandResult(currentTarget, DocumentCacheAdministrativeCommand.OnlineCacheRebuild),
            RuntimeObservedAt.AddSeconds(10)
        );
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                activeExecutionId,
                currentTarget,
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                cancellationRequested: true
            )
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([currentTarget], [ExecutionContext(currentTarget)]),
            observationStore
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(statusTarget))!.AsObject();
        string json = root.ToJsonString();

        root["activeCommand"]!["command"]!.GetValue<string>().Should().Be("guardedNewEmptyActivation");
        root["activeCommand"]!["status"]!.GetValue<string>().Should().Be("cancelling");
        root["activeCommand"]!["phase"]!.GetValue<string>().Should().Be("drainWork");
        root["activeCommand"]!["phaseDiagnostics"]![0]!["diagnosticCategory"]!
            .GetValue<string>()
            .Should()
            .Be("providerCommandTimeout");
        root["activeCommand"]!["phaseDiagnostics"]![0]!.AsObject().Should().NotContainKey("evictedCount");
        root["lastEndedDiagnostic"]!["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        root["lastEndedDiagnostic"]!["outcome"]!.GetValue<string>().Should().Be("succeeded");

        json.Should().NotContain("offlineActivation");
        json.Should().NotContain("currentTargetGeneration");
        json.Should().NotContain("isCurrentGeneration");
        json.Should().NotContain("activateNewEmpty");
        json.Should().NotContain("offlineActivate");
        json.Should().NotContain("offlineDeactivate");
        json.Should().NotContain("onlineRebuild");
        json.Should().NotContain("cacheAheadRecovery");
        json.Should().NotContain("integrityScrub");
    }

    private static DocumentCacheStatusService CreateService(
        StaticTargetRegistry registry,
        DocumentCacheProjectionObservationStore observationStore
    ) =>
        new(
            registry,
            observationStore,
            [new ScriptedStatusObserver()],
            new FixedTimeProvider(ProcessObservedAt)
        );

    private static DocumentCacheProjectionTargetHealthSnapshot TargetHealth(
        DocumentCacheTargetObservation target,
        ImmutableArray<DocumentCacheTargetDiagnostic> targetDiagnostics = default,
        ImmutableArray<DocumentCacheProjectionDocumentDiagnostic> documentDiagnostics = default,
        int suppressedDocumentCount = 0,
        ImmutableArray<long> suppressedDocumentIds = default,
        ImmutableArray<DocumentCacheProjectionPoisonTraversalDiagnostic> poisonTraversalDiagnostics = default,
        long targetDiagnosticEvictionCount = 0,
        long documentDiagnosticEvictionCount = 0,
        long poisonTraversalDiagnosticEvictionCount = 0,
        DocumentCacheProjectionExecutionStateSnapshot? executionState = null
    ) =>
        new(
            target.TargetKey,
            target.Generation!,
            target.EffectiveSettings.ProjectorPageSize,
            RuntimeObservedAt,
            target.ProviderToken,
            target.PhysicalSourceFingerprint,
            executionState
                ?? new DocumentCacheProjectionExecutionStateSnapshot(
                    isRunning: true,
                    isActivelyProcessing: false,
                    isWaitingForWorkerGate: false,
                    isInBackoff: false,
                    backoffUntil: null,
                    cancellationRequested: false,
                    cancellationObservedAt: null
                ),
            lastSuccess: new DocumentCacheProjectionSuccessSnapshot(
                documentId: 11,
                contentVersion: 12,
                completedAt: RuntimeObservedAt.AddSeconds(-1)
            ),
            poisonTraversal: new DocumentCacheProjectionPoisonTraversalSnapshot(
                target.EffectiveSettings.ProjectorPageSize,
                suppressedDocumentCount,
                RuntimeObservedAt.AddSeconds(30),
                suppressedDocumentIds.IsDefault ? [] : suppressedDocumentIds,
                poisonTraversalDiagnostics.IsDefault ? [] : poisonTraversalDiagnostics,
                poisonTraversalDiagnosticEvictionCount
            ),
            failureDiagnostics: new DocumentCacheProjectionFailureDiagnostics(
                target.EffectiveSettings.ProjectorPageSize,
                documentDiagnostics.IsDefault ? 0 : documentDiagnostics.Length,
                RuntimeObservedAt.AddSeconds(30),
                documentDiagnosticEvictionCount,
                documentDiagnostics.IsDefault ? [] : documentDiagnostics
            ),
            targetDiagnostics: targetDiagnostics.IsDefault ? [] : targetDiagnostics,
            targetDiagnosticEvictionCount: targetDiagnosticEvictionCount
        );

    private static DocumentCacheTargetDiagnostic TargetDiagnostic(
        DocumentCacheTargetObservation target,
        DocumentCacheTargetDiagnosticCategory category
    ) =>
        new(
            target.TargetKey,
            DocumentCacheTargetResolutionState.Resolved,
            target.ProviderToken,
            target.Generation,
            target.PhysicalSourceFingerprint,
            target.Lifecycle,
            target.Inventory,
            target.EnqueueTrigger,
            target.SqlServerPrerequisites,
            retryState: null,
            category,
            $"Diagnostic {category}\r\n{{unsafe}}"
        );

    private static DocumentCacheProjectionDocumentDiagnostic DocumentDiagnostic(
        long documentId,
        DocumentCacheProjectionDocumentDiagnosticCategory category
    ) =>
        new(
            documentId,
            category,
            $"Document diagnostic {category}\r\n{{unsafe}}",
            RuntimeObservedAt,
            RuntimeObservedAt.AddSeconds(30)
        );

    private static DocumentCacheProjectionPoisonTraversalDiagnostic PoisonTraversalDiagnostic(
        long documentId,
        DocumentCacheProjectionPoisonTraversalDiagnosticCategory category,
        DateTimeOffset observedAt
    ) =>
        new(
            documentId,
            category,
            $"Poison traversal diagnostic {category}\r\n{{unsafe}}",
            observedAt,
            observedAt.AddSeconds(30)
        );

    private static DocumentCacheAdministrativeCommandObservationSnapshot CommandObservation(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        DocumentCacheTargetObservation target,
        DocumentCacheAdministrativeCommand command,
        bool cancellationRequested = false
    ) =>
        new(
            executionId,
            command,
            target.TargetKey,
            target.Generation!,
            target.EffectiveSettings.ProjectorPageSize,
            effectiveWorkflowTimeout: TimeSpan.FromHours(1),
            startedAt: RuntimeObservedAt.AddMinutes(-5),
            observedAt: RuntimeObservedAt,
            currentPhase: DocumentCacheAdministrativeCommandPhase.DrainWork,
            lastCompletedPhase: DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            mutated: true,
            physicalSourceFingerprint: target.PhysicalSourceFingerprint,
            lifecycle: DocumentCacheLifecycleState.Rebuilding,
            cacheAheadRecoveryRequired: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.DrainWork,
                    DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                    retryable: true,
                    DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
                    affectedDocumentIds: [99],
                    "provider timeout"
                ),
            ],
            cancellationRequested: cancellationRequested
        );

    private static DocumentCacheAdministrativeCommandResult CommandResult(
        DocumentCacheTargetObservation target,
        DocumentCacheAdministrativeCommand command
    ) =>
        new(
            command,
            DocumentCacheAdministrativeTargetKey.FromTargetKey(target.TargetKey),
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            mutated: true,
            targetGeneration: target.Generation!.Value,
            physicalSourceFingerprint: target.PhysicalSourceFingerprint,
            lifecycle: DocumentCacheLifecycleState.Tracking,
            cacheAheadRecoveryRequired: false,
            phaseDiagnostics: []
        );

    private static DocumentCacheTargetObservation ResolvedTarget(
        long generation,
        int pageSize = 10,
        int dataStoreId = 1
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            DocumentCacheTargetKey.Create("", dataStoreId),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromSeconds(2),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: pageSize,
                projectorMaxConcurrentTargets: 4,
                projectorFailureBackoff: TimeSpan.FromSeconds(30),
                projectorBaselineHighWaterMark: 10000,
                administrationWorkflowTimeout: TimeSpan.FromMinutes(10),
                statusObservationTimeout: TimeSpan.FromSeconds(5),
                statusEndpointTimeout: TimeSpan.FromSeconds(30)
            ),
            new DocumentCacheTargetContextGeneration(generation),
            RelationalProviderToken.Postgresql,
            new DocumentCachePhysicalSourceFingerprint("sha256:" + new string('a', 64)),
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

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheTargetObservation targetObservation
    ) =>
        new(
            targetObservation.TargetKey,
            targetObservation.Generation!,
            targetObservation.EffectiveSettings,
            new DocumentCacheTargetDataStoreMetadata(targetObservation.TargetKey.DataStoreId, "PostgreSQL"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "Host=localhost"),
            targetObservation.PhysicalSourceFingerprint!,
            targetObservation.Lifecycle!,
            targetObservation.Inventory!,
            targetObservation.EnqueueTrigger!,
            targetObservation.SqlServerPrerequisites
        );

    private sealed class StaticTargetRegistry(
        IEnumerable<DocumentCacheTargetObservation> targets,
        IEnumerable<DocumentCacheTargetExecutionContext> executionContexts
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } =
            new(targets, RegistryObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
            new(executionContexts, RegistryObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Status mapping must not refresh DocumentCache targets.");
    }

    private sealed class ScriptedStatusObserver : IDocumentCacheStatusCurrentSourceObserver
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public ConcurrentQueue<DocumentCacheTargetKey> StartedKeys { get; } = new();

        public Task<DocumentCacheStatusCurrentSourceObservationResult> ObserveAsync(
            DocumentCacheStatusCurrentSourceObservationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedKeys.Enqueue(request.TargetExecutionContext.TargetKey);

            return Task.FromResult(
                DocumentCacheStatusCurrentSourceObservationResult.Success(
                    DocumentCacheLifecycleState.Tracking,
                    cacheAheadRecoveryRequired: false,
                    DocumentCacheStatusDurableQueuePresence.Empty,
                    oldestWorkFirstEnqueuedAt: null,
                    oldestWorkAgeSeconds: null,
                    DurableObservedAt
                )
            );
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
