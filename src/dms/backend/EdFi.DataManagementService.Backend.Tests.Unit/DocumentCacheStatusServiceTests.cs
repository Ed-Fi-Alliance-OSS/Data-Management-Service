// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheStatusService")]
public class Given_DocumentCacheStatusService
{
    private static readonly DateTimeOffset RegistryObservedAt = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProcessObservedAt = new(2026, 8, 17, 12, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset RuntimeObservedAt = new(2026, 8, 17, 12, 0, 2, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = new(2026, 8, 17, 12, 0, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset OldestWorkFirstEnqueuedAt = DurableObservedAt.AddMinutes(-5);
    private static readonly DocumentCacheTargetContextGeneration Generation = new(3);

    [Test]
    public async Task It_returns_an_empty_contract_for_empty_configured_targets()
    {
        StaticTargetRegistry registry = new([], []);
        DocumentCacheStatusService service = CreateService(registry);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        response.ObservedAt.Should().Be(ProcessObservedAt);
        response.Targets.Should().BeEmpty();
        registry.RefreshCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_evaluates_explicit_targets_deterministically_without_refreshing_registry()
    {
        DocumentCacheTargetObservation tenantBTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("Tenant-B", 3)
        );
        DocumentCacheTargetObservation defaultTenantTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1)
        );
        DocumentCacheTargetObservation tenantAFirstTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("TENANT-A", 1)
        );
        DocumentCacheTargetObservation tenantATarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("tenant-a", 2)
        );
        StaticTargetRegistry registry = new(
            [tenantBTarget, defaultTenantTarget, tenantATarget, tenantAFirstTarget],
            [
                ExecutionContext(tenantBTarget),
                ExecutionContext(defaultTenantTarget),
                ExecutionContext(tenantATarget),
                ExecutionContext(tenantAFirstTarget),
            ]
        );
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(
            tenantBTarget,
            defaultTenantTarget,
            tenantATarget,
            tenantAFirstTarget
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        response
            .Targets.Select(target => (target.TargetKey.TenantKey, target.TargetKey.DataStoreId))
            .Should()
            .Equal(("", 1L), ("TENANT-A", 1L), ("tenant-a", 2L), ("Tenant-B", 3L));
        response
            .Targets.Should()
            .AllSatisfy(target =>
            {
                target.ProcessObservedAt.Should().Be(ProcessObservedAt);
                target.DurableObservedAt.Should().Be(DurableObservedAt);
                target.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
                target.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);
                target.EffectiveSettings.Status.EndpointTimeoutSeconds.Should().Be(30);
            });
        observer
            .StartedKeys.Should()
            .BeEquivalentTo([
                tenantBTarget.TargetKey,
                defaultTenantTarget.TargetKey,
                tenantATarget.TargetKey,
                tenantAFirstTarget.TargetKey,
            ]);
        registry.RefreshCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_uses_the_coherent_registry_status_snapshot_for_runtime_context_lookup()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("", 1);
        DocumentCacheTargetObservation statusSnapshotTarget = ResolvedTarget(
            targetKey,
            generation: new DocumentCacheTargetContextGeneration(3)
        );
        DocumentCacheTargetObservation replacementGeneration = ResolvedTarget(
            targetKey,
            generation: new DocumentCacheTargetContextGeneration(4)
        );
        StatusSnapshotTargetRegistry registry = new(
            statusSnapshotTarget,
            ExecutionContext(statusSnapshotTarget),
            ExecutionContext(replacementGeneration)
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(statusSnapshotTarget),
            observer
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.TargetGeneration.Should().Be(3);
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        statusTarget.DurableObservedAt.Should().Be(DurableObservedAt);
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(targetKey));
        registry.StatusSnapshotAccessCount.Should().Be(1);
        registry.CurrentSnapshotAccessCount.Should().Be(0);
        registry.CurrentRuntimeSnapshotAccessCount.Should().Be(0);
    }

    [Test]
    public async Task It_reports_configured_boot_state_resolution_unknown_without_target_not_found()
    {
        DocumentCacheTargetObservation target = DocumentCacheTargetObservation.Configured(
            DocumentCacheTargetKey.Create("", 1),
            EffectiveSettings()
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], []),
            observer: observer
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.Resolution.Status.Should().Be(DocumentCacheStatusResolutionStatus.Unknown);
        statusTarget.Resolution.Reason.Should().Be(DocumentCacheStatusResolutionReason.None);
        statusTarget.TargetGeneration.Should().BeNull();
        statusTarget.DurableObservedAt.Should().BeNull();
        observer.StartedKeys.Should().BeEmpty();
    }

    [Test]
    public void It_reports_a_missing_target_generation_separately_from_observer_availability()
    {
        DocumentCacheTargetObservation target = DocumentCacheTargetObservation.ResolvedIneligible(
            DocumentCacheTargetKey.Create("", 1),
            EffectiveSettings(),
            generation: null,
            RelationalProviderToken.Postgresql,
            Fingerprint(1),
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
            retryState: null,
            diagnostics: []
        );
        ScriptedStatusObserver configuredObserver = new(Success);
        Dictionary<RelationalProviderToken, IDocumentCacheStatusCurrentSourceObserver> observers = new()
        {
            [RelationalProviderToken.Postgresql] = configuredObserver,
        };

        bool selected = DocumentCacheStatusService.TrySelectCurrentSourceObserver(
            target,
            observers,
            out IDocumentCacheStatusCurrentSourceObserver? observer,
            out string? failureMessage
        );

        selected.Should().BeFalse();
        observer.Should().BeNull();
        failureMessage
            .Should()
            .Be("DocumentCache current target generation is not available for status observation.");
    }

    [TestCase(DocumentCacheTargetDiagnosticCategory.TargetNotConfigured)]
    [TestCase(DocumentCacheTargetDiagnosticCategory.TargetUnresolved)]
    public async Task It_maps_target_not_found_resolution_diagnostics_to_target_not_found(
        DocumentCacheTargetDiagnosticCategory category
    )
    {
        DocumentCacheTargetObservation target = UnresolvedTargetWithDiagnostic(
            DocumentCacheTargetKey.Create("", 1),
            category
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], []),
            observer: observer
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.Resolution.Status.Should().Be(DocumentCacheStatusResolutionStatus.Unresolved);
        statusTarget.Resolution.Reason.Should().Be(DocumentCacheStatusResolutionReason.TargetNotFound);
        observer.StartedKeys.Should().BeEmpty();
    }

    [Test]
    public async Task It_filters_command_diagnostics_by_the_serialized_target_generation()
    {
        DocumentCacheTargetContextGeneration commandGeneration = new(3);
        DocumentCacheTargetObservation staleCommandTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            generation: commandGeneration
        );
        DocumentCacheTargetObservation replacementTarget = ResolvedTarget(
            staleCommandTarget.TargetKey,
            generation: new DocumentCacheTargetContextGeneration(4)
        );
        DocumentCacheTargetObservation matchingTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 2),
            generation: commandGeneration
        );
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(
            staleCommandTarget,
            matchingTarget
        );

        DocumentCacheAdministrativeCommandExecutionId staleActiveExecutionId = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555")
        );
        DocumentCacheAdministrativeCommandExecutionId staleEndedExecutionId = new(
            Guid.Parse("22222222-3333-4444-5555-666666666666")
        );
        DocumentCacheAdministrativeCommandExecutionId matchingActiveExecutionId = new(
            Guid.Parse("33333333-4444-5555-6666-777777777777")
        );
        DocumentCacheAdministrativeCommandExecutionId matchingEndedExecutionId = new(
            Guid.Parse("44444444-5555-6666-7777-888888888888")
        );

        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                staleActiveExecutionId,
                staleCommandTarget,
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation
            )
        );
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                staleEndedExecutionId,
                staleCommandTarget,
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild
            )
        );
        observationStore.EndAdministrativeCommand(
            staleEndedExecutionId,
            CommandResult(staleCommandTarget, DocumentCacheAdministrativeCommand.OnlineCacheRebuild),
            RuntimeObservedAt.AddSeconds(10)
        );
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                matchingActiveExecutionId,
                matchingTarget,
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation
            )
        );
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                matchingEndedExecutionId,
                matchingTarget,
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild
            )
        );
        observationStore.EndAdministrativeCommand(
            matchingEndedExecutionId,
            CommandResult(matchingTarget, DocumentCacheAdministrativeCommand.OnlineCacheRebuild),
            RuntimeObservedAt.AddSeconds(10)
        );

        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry(
                [replacementTarget, matchingTarget],
                [ExecutionContext(replacementTarget), ExecutionContext(matchingTarget)]
            ),
            observationStore,
            new ScriptedStatusObserver(Success)
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget staleStatusTarget = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 1
        );
        staleStatusTarget.TargetGeneration.Should().Be(4);
        staleStatusTarget.ActiveCommand.Should().BeNull();
        staleStatusTarget.LastEndedDiagnostic.Should().BeNull();

        DocumentCacheStatusTarget matchingStatusTarget = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 2
        );
        matchingStatusTarget.TargetGeneration.Should().Be(3);
        matchingStatusTarget.ActiveCommand.Should().NotBeNull();
        matchingStatusTarget
            .ActiveCommand!.Command.Should()
            .Be(DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation);
        matchingStatusTarget.LastEndedDiagnostic.Should().NotBeNull();
        matchingStatusTarget
            .LastEndedDiagnostic!.Command.Should()
            .Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
    }

    [Test]
    public async Task It_skips_provider_observation_for_process_ineligible_targets()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(
            registry,
            observationStore: new DocumentCacheProjectionObservationStore(
                new FixedTimeProvider(ProcessObservedAt)
            ),
            observer
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget statusTarget = response.Targets.Single();
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.RuntimeNotObserved);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        statusTarget.ExecutionState.Status.Should().Be(DocumentCacheStatusExecutionState.NotObserved);
        observer.StartedKeys.Should().BeEmpty();
    }

    [Test]
    public async Task It_observes_durable_status_in_standalone_mode_when_runtime_is_not_observed()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        ScriptedStatusObserver observer = new(
            (request, cancellationToken) =>
            {
                _ = request;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    DocumentCacheStatusCurrentSourceObservationResult.Success(
                        DocumentCacheLifecycleState.Tracking,
                        cacheAheadRecoveryRequired: false,
                        DocumentCacheStatusDurableQueuePresence.NotEmpty,
                        OldestWorkFirstEnqueuedAt,
                        oldestWorkAgeSeconds: 300,
                        DurableObservedAt
                    )
                );
            }
        );
        DocumentCacheStatusService service = CreateService(
            registry,
            observationStore: new DocumentCacheProjectionObservationStore(
                new FixedTimeProvider(ProcessObservedAt)
            ),
            observer
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync(
            evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
        );

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.DurableObservedAt.Should().Be(DurableObservedAt);
        statusTarget.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Tracking);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        statusTarget.CacheAhead.State.Should().Be(DocumentCacheStatusCacheAheadState.Clear);
        statusTarget.CacheAhead.RecoveryRequired.Should().BeFalse();
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.NotEmpty);
        statusTarget.QueueSummary.OldestWorkFirstEnqueuedAt.Should().Be(OldestWorkFirstEnqueuedAt);
        statusTarget.QueueSummary.OldestWorkAgeSeconds.Should().Be(300);
        statusTarget.ExecutionState.Status.Should().Be(DocumentCacheStatusExecutionState.NotObserved);
        statusTarget.ActiveCommand.Should().BeNull();
        statusTarget.LastEndedDiagnostic.Should().BeNull();
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.RuntimeNotObserved);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.RuntimeNotObserved);
        observer.StartedKeys.Should().ContainSingle().Which.Should().Be(target.TargetKey);
    }

    [Test]
    public async Task It_reports_process_local_command_observations_for_observed_current_generation_in_standalone_mode()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(target);
        DocumentCacheAdministrativeCommandExecutionId activeExecutionId = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555")
        );
        DocumentCacheAdministrativeCommandExecutionId endedExecutionId = new(
            Guid.Parse("22222222-3333-4444-5555-666666666666")
        );

        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                activeExecutionId,
                target,
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation
            )
        );
        observationStore.ObserveAdministrativeCommand(
            CommandObservation(
                endedExecutionId,
                target,
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild
            )
        );
        observationStore.EndAdministrativeCommand(
            endedExecutionId,
            CommandResult(target, DocumentCacheAdministrativeCommand.OnlineCacheRebuild),
            RuntimeObservedAt.AddSeconds(10)
        );
        DocumentCacheStatusService service = CreateService(registry, observationStore, new(Success));

        DocumentCacheStatusTarget statusTarget = (
            await service.GetStatusAsync(
                evaluationMode: DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            )
        )
            .Targets.Should()
            .ContainSingle()
            .Which;

        statusTarget.ExecutionState.Status.Should().Be(DocumentCacheStatusExecutionState.WaitingForPoll);
        statusTarget.ActiveCommand.Should().NotBeNull();
        statusTarget
            .ActiveCommand!.Command.Should()
            .Be(DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation);
        statusTarget.LastEndedDiagnostic.Should().NotBeNull();
        statusTarget
            .LastEndedDiagnostic!.Command.Should()
            .Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);
    }

    [Test]
    public async Task It_maps_inventory_components_independently()
    {
        DocumentCacheInventoryValidationResult validInventory = new(
            DocumentCacheInventoryStatus.Satisfied,
            "Inventory satisfied."
        );
        DocumentCacheInventoryValidationResult invalidState = new(
            DocumentCacheInventoryStatus.Invalid,
            "DocumentCache state inventory is invalid."
        );
        DocumentCacheTargetObservation target = ResolvedInventoryInvalidTarget(
            DocumentCacheTargetKey.Create("", 1),
            invalidState,
            new DocumentCacheInventoryValidationComponents(
                invalidState,
                validInventory,
                validInventory,
                validInventory
            )
        );
        DocumentCacheStatusService service = CreateService(new StaticTargetRegistry([target], []));

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.Inventory.ObservedAt.Should().Be(RegistryObservedAt);
        statusTarget.Inventory.State.Status.Should().Be(DocumentCacheStatusInventoryStatus.Invalid);
        statusTarget.Inventory.State.Reason.Should().Be(DocumentCacheStatusInventoryReason.Invalid);
        statusTarget.Inventory.State.Message.Should().Be("DocumentCache state inventory is invalid.");
        statusTarget.Inventory.Work.Status.Should().Be(DocumentCacheStatusInventoryStatus.Valid);
        statusTarget.Inventory.Work.Message.Should().BeNull();
        statusTarget.Inventory.Cache.Status.Should().Be(DocumentCacheStatusInventoryStatus.Valid);
        statusTarget.Inventory.Cache.Message.Should().BeNull();
        statusTarget.Inventory.DataStoreIdentity.Status.Should().Be(DocumentCacheStatusInventoryStatus.Valid);
        statusTarget.Inventory.DataStoreIdentity.Message.Should().BeNull();
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
    }

    [TestCase(
        DocumentCacheLifecycleReadStatus.Missing,
        DocumentCacheStatusReason.StateMissingOrInvalid,
        DocumentCacheStatusEligibilityStatus.Ineligible,
        DocumentCacheOperationalHealthStatus.NonOperational,
        DocumentCacheCaughtUpStatus.NotCaughtUp
    )]
    [TestCase(
        DocumentCacheLifecycleReadStatus.Invalid,
        DocumentCacheStatusReason.StateMissingOrInvalid,
        DocumentCacheStatusEligibilityStatus.Ineligible,
        DocumentCacheOperationalHealthStatus.NonOperational,
        DocumentCacheCaughtUpStatus.NotCaughtUp
    )]
    [TestCase(
        DocumentCacheLifecycleReadStatus.Unreadable,
        DocumentCacheStatusReason.ProviderObservationFailed,
        DocumentCacheStatusEligibilityStatus.Unknown,
        DocumentCacheOperationalHealthStatus.Unknown,
        DocumentCacheCaughtUpStatus.Unknown
    )]
    public async Task It_maps_lifecycle_read_failures_when_no_higher_priority_process_failure_exists(
        DocumentCacheLifecycleReadStatus lifecycleReadStatus,
        DocumentCacheStatusReason expectedReason,
        DocumentCacheStatusEligibilityStatus expectedEligibilityStatus,
        DocumentCacheOperationalHealthStatus expectedOperationalHealthStatus,
        DocumentCacheCaughtUpStatus expectedCaughtUpStatus
    )
    {
        DocumentCacheTargetObservation target = ResolvedLifecycleReadFailureTarget(
            DocumentCacheTargetKey.Create("", 1),
            lifecycleReadStatus
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], []),
            observer: observer
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.Eligibility.Status.Should().Be(expectedEligibilityStatus);
        statusTarget.Eligibility.Reason.Should().Be(expectedReason);
        statusTarget.OperationalHealth.Status.Should().Be(expectedOperationalHealthStatus);
        statusTarget.OperationalHealth.Reason.Should().Be(expectedReason);
        statusTarget.CaughtUp.Status.Should().Be(expectedCaughtUpStatus);
        statusTarget.CaughtUp.Reason.Should().Be(expectedReason);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        statusTarget.DurableObservedAt.Should().BeNull();
        observer.StartedKeys.Should().BeEmpty();
    }

    [TestCase(DocumentCacheStatusReason.InventoryInvalid)]
    [TestCase(DocumentCacheStatusReason.EnqueueTriggerUnavailable)]
    [TestCase(DocumentCacheStatusReason.SqlServerPrerequisiteFailed)]
    [TestCase(DocumentCacheStatusReason.UnsupportedPrerequisiteIncident)]
    public async Task It_maps_higher_priority_process_failures_before_lifecycle_read_failures(
        DocumentCacheStatusReason expectedReason
    )
    {
        DocumentCacheTargetObservation target = ResolvedLifecycleReadFailureTargetWithHigherPriorityFailure(
            DocumentCacheTargetKey.Create("", 1),
            expectedReason
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], []),
            observer: observer
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.Eligibility.Status.Should().Be(DocumentCacheStatusEligibilityStatus.Ineligible);
        statusTarget.Eligibility.Reason.Should().Be(expectedReason);
        statusTarget
            .OperationalHealth.Status.Should()
            .Be(DocumentCacheOperationalHealthStatus.NonOperational);
        statusTarget.OperationalHealth.Reason.Should().Be(expectedReason);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        statusTarget.CaughtUp.Reason.Should().Be(expectedReason);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        statusTarget.DurableObservedAt.Should().BeNull();
        observer.StartedKeys.Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_target_diagnostic_event_observed_at_values()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(target);
        DocumentCacheProjectionTargetContextKey contextKey = observationStore
            .CurrentSnapshot.GetCurrentTarget(target.TargetKey)!
            .ContextKey;
        DateTimeOffset firstDiagnosticObservedAt = RuntimeObservedAt.AddSeconds(10);
        DateTimeOffset secondDiagnosticObservedAt = RuntimeObservedAt.AddSeconds(20);
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            observationStore,
            new ScriptedStatusObserver(Success)
        );

        observationStore.AppendTargetDiagnostic(
            contextKey,
            TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing),
            firstDiagnosticObservedAt
        );
        observationStore.AppendTargetDiagnostic(
            contextKey,
            TargetDiagnostic(target, DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet),
            secondDiagnosticObservedAt
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget
            .TargetDiagnostics.RecentEvents.Select(diagnostic => diagnostic.ObservedAt)
            .Should()
            .Equal(firstDiagnosticObservedAt, secondDiagnosticObservedAt);
    }

    [Test]
    public async Task It_orders_enqueue_failure_category_counts_by_fixed_public_order()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        DocumentCacheEnqueueFailureSnapshot snapshot = new(
            [
                EnqueueFailureEvent(
                    target.TargetKey,
                    DocumentCacheEnqueueFailureCategory.ProviderUnavailable,
                    1
                ),
                EnqueueFailureEvent(
                    target.TargetKey,
                    DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed,
                    2
                ),
                EnqueueFailureEvent(
                    target.TargetKey,
                    DocumentCacheEnqueueFailureCategory.StateMissingOrInvalid,
                    3
                ),
                EnqueueFailureEvent(
                    target.TargetKey,
                    DocumentCacheEnqueueFailureCategory.ProviderUnavailable,
                    4
                ),
                EnqueueFailureEvent(
                    target.TargetKey,
                    DocumentCacheEnqueueFailureCategory.UnclassifiedProviderFailure,
                    5
                ),
                EnqueueFailureEvent(
                    target.TargetKey,
                    DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable,
                    6
                ),
            ],
            evictedCount: 7
        );
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            ObservationStore(target),
            new ScriptedStatusObserver(Success),
            enqueueFailureObservationProvider: new StaticEnqueueFailureObservationProvider(
                target.TargetKey,
                snapshot
            )
        );

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget
            .EnqueueFailures.RecentEvents.Select(enqueueFailure => enqueueFailure.Category)
            .Should()
            .Equal(
                DocumentCacheStatusEnqueueFailureCategory.ProviderUnavailable,
                DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
                DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid,
                DocumentCacheStatusEnqueueFailureCategory.ProviderUnavailable,
                DocumentCacheStatusEnqueueFailureCategory.UnclassifiedProviderFailure,
                DocumentCacheStatusEnqueueFailureCategory.EnqueueTriggerUnavailable
            );
        statusTarget
            .EnqueueFailures.ByCategory.Select(categoryCount => (categoryCount.Category, categoryCount.Count))
            .Should()
            .Equal(
                (DocumentCacheStatusEnqueueFailureCategory.StateMissingOrInvalid, 1),
                (DocumentCacheStatusEnqueueFailureCategory.EnqueueTriggerUnavailable, 1),
                (DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed, 1),
                (DocumentCacheStatusEnqueueFailureCategory.ProviderUnavailable, 2),
                (DocumentCacheStatusEnqueueFailureCategory.UnclassifiedProviderFailure, 1)
            );
        statusTarget
            .EnqueueFailures.ByCategory.Select(categoryCount => categoryCount.Category)
            .Should()
            .NotContain(DocumentCacheStatusEnqueueFailureCategory.ProviderTimeout);
        statusTarget.EnqueueFailures.EvictedCount.Should().Be(7);
    }

    [Test]
    public async Task It_serializes_per_target_observation_timeout_without_blocking_peer_targets()
    {
        DocumentCacheTargetObservation slowTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            EffectiveSettings(statusObservationTimeout: TimeSpan.FromMilliseconds(30))
        );
        DocumentCacheTargetObservation fastTarget = ResolvedTarget(DocumentCacheTargetKey.Create("", 2));
        StaticTargetRegistry registry = new(
            [slowTarget, fastTarget],
            [ExecutionContext(slowTarget), ExecutionContext(fastTarget)]
        );
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(slowTarget, fastTarget);
        ScriptedStatusObserver observer = new(
            async (request, cancellationToken) =>
            {
                if (request.TargetExecutionContext.TargetKey.Equals(slowTarget.TargetKey))
                {
                    await WaitForCancellationAsync(cancellationToken);
                    return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
                }

                return await Success(request, cancellationToken);
            }
        );
        CapturingStatusTelemetry telemetry = new();
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer, telemetry);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget slowStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 1
        );
        slowStatus.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        slowStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusObservationTimeout);
        slowStatus.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);

        DocumentCacheStatusTarget fastStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 2
        );
        fastStatus.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        fastStatus.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);

        CapturedProviderObservation slowProviderObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle(observation => observation.TargetKey.Equals(slowTarget.TargetKey))
            .Which;
        slowProviderObservation
            .Outcome.Should()
            .Be(DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut);
        slowProviderObservation.Reason.Should().Be(DocumentCacheStatusReason.StatusObservationTimeout);
    }

    [Test]
    public async Task It_preserves_observation_timeout_when_provider_returns_cancelled_after_endpoint_timeout()
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            statusObservationTimeout: TimeSpan.FromMilliseconds(20),
            endpointTimeout: TimeSpan.FromMilliseconds(80)
        );
        DocumentCacheTargetObservation target = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        ScriptedStatusObserver observer = new(
            async (_, cancellationToken) =>
            {
                await WaitForCancellationAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(120));
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        CapturingStatusTelemetry telemetry = new();
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(target),
            observer,
            telemetry
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusObservationTimeout);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StatusObservationTimeout);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);

        CapturedProviderObservation providerObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle()
            .Which;
        providerObservation
            .Outcome.Should()
            .Be(DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut);
        providerObservation.Reason.Should().Be(DocumentCacheStatusReason.StatusObservationTimeout);
    }

    [Test]
    public async Task It_reports_completed_provider_success_when_timeout_token_is_cancelled_before_mapping()
    {
        await AssertCompletedProviderSuccessAfterTimeoutAsync(
            statusObservationTimeout: TimeSpan.FromMilliseconds(20),
            endpointTimeout: TimeSpan.FromMilliseconds(250)
        );
        await AssertCompletedProviderSuccessAfterTimeoutAsync(
            statusObservationTimeout: TimeSpan.FromMilliseconds(250),
            endpointTimeout: TimeSpan.FromMilliseconds(20)
        );
    }

    [TestCase(DocumentCacheStatusReason.StatusObservationTimeout, 20, 200)]
    [TestCase(DocumentCacheStatusReason.StatusEndpointTimeout, 200, 20)]
    public async Task It_preserves_status_timeout_reasons_in_standalone_mode(
        DocumentCacheStatusReason expectedReason,
        int statusObservationTimeoutMilliseconds,
        int endpointTimeoutMilliseconds
    )
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            statusObservationTimeout: TimeSpan.FromMilliseconds(statusObservationTimeoutMilliseconds),
            endpointTimeout: TimeSpan.FromMilliseconds(endpointTimeoutMilliseconds)
        );
        DocumentCacheTargetObservation target = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        TaskCompletionSource providerObservationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedStatusObserver observer = new(
            async (_, cancellationToken) =>
            {
                providerObservationStarted.TrySetResult();
                await WaitForCancellationAsync(cancellationToken);
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        ControlledTimeProvider timeProvider = new(ProcessObservedAt);
        DocumentCacheStatusService service = CreateService(
            registry,
            observer: observer,
            timeProvider: timeProvider
        );

        DocumentCacheStatusResponse response;
        using (CancellationTokenSource callerCancellationTokenSource = new())
        {
            Task<DocumentCacheStatusResponse> responseTask = service.GetStatusAsync(
                callerCancellationTokenSource.Token,
                DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
            );

            try
            {
                await providerObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                timeProvider.Advance(
                    TimeSpan.FromMilliseconds(
                        Math.Min(statusObservationTimeoutMilliseconds, endpointTimeoutMilliseconds)
                    )
                );
                response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                await callerCancellationTokenSource.CancelAsync();
                throw;
            }
        }

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget.OperationalHealth.Reason.Should().Be(expectedReason);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        statusTarget.CaughtUp.Reason.Should().Be(expectedReason);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unknown);
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(target.TargetKey));
    }

    [Test]
    public async Task It_serializes_provider_observation_failure_without_blocking_peer_targets()
    {
        DocumentCacheTargetObservation failedTarget = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        DocumentCacheTargetObservation healthyTarget = ResolvedTarget(DocumentCacheTargetKey.Create("", 2));
        StaticTargetRegistry registry = new(
            [failedTarget, healthyTarget],
            [ExecutionContext(failedTarget), ExecutionContext(healthyTarget)]
        );
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(
            failedTarget,
            healthyTarget
        );
        ScriptedStatusObserver observer = new(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    request.TargetExecutionContext.TargetKey.Equals(failedTarget.TargetKey)
                        ? DocumentCacheStatusCurrentSourceObservationResult.Failed(
                            "Provider statement failed."
                        )
                        : DocumentCacheStatusCurrentSourceObservationResult.Success(
                            DocumentCacheLifecycleState.Tracking,
                            cacheAheadRecoveryRequired: false,
                            DocumentCacheStatusDurableQueuePresence.Empty,
                            oldestWorkFirstEnqueuedAt: null,
                            oldestWorkAgeSeconds: null,
                            DurableObservedAt
                        )
                );
            }
        );
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget failedStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 1
        );
        failedStatus.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        failedStatus
            .OperationalHealth.Reason.Should()
            .Be(DocumentCacheStatusReason.ProviderObservationFailed);
        failedStatus.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        failedStatus.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);
        failedStatus.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unknown);
        failedStatus.DurableObservedAt.Should().BeNull();

        DocumentCacheStatusTarget healthyStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 2
        );
        healthyStatus.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        healthyStatus.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);
        healthyStatus.DurableObservedAt.Should().Be(DurableObservedAt);
        observer.StartedKeys.Should().BeEquivalentTo([failedTarget.TargetKey, healthyTarget.TargetKey]);
    }

    [Test]
    public async Task It_preserves_same_statement_queue_facts_when_provider_reports_state_missing_or_invalid()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(target);
        ScriptedStatusObserver observer = new(
            (request, cancellationToken) =>
            {
                _ = request;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    DocumentCacheStatusCurrentSourceObservationResult.StateMissingOrInvalid(
                        DurableObservedAt,
                        DocumentCacheStatusDurableQueuePresence.NotEmpty,
                        OldestWorkFirstEnqueuedAt,
                        oldestWorkAgeSeconds: 300,
                        "dms.DocumentCacheState singleton row is missing or invalid."
                    )
                );
            }
        );
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer);

        DocumentCacheStatusTarget statusTarget = (await service.GetStatusAsync()).Targets.Single();

        statusTarget.DurableObservedAt.Should().Be(DurableObservedAt);
        statusTarget.Lifecycle.State.Should().Be(DocumentCacheStatusLifecycleState.Invalid);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        statusTarget.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.NotEmpty);
        statusTarget.QueueSummary.OldestWorkFirstEnqueuedAt.Should().Be(OldestWorkFirstEnqueuedAt);
        statusTarget.QueueSummary.OldestWorkAgeSeconds.Should().Be(300);
        statusTarget
            .OperationalHealth.Status.Should()
            .Be(DocumentCacheOperationalHealthStatus.NonOperational);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StateMissingOrInvalid);
    }

    [Test]
    public async Task It_distinguishes_endpoint_budget_started_and_not_started_targets()
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            maxConcurrentTargets: 1,
            statusObservationTimeout: TimeSpan.FromSeconds(5),
            endpointTimeout: TimeSpan.FromMilliseconds(50)
        );
        DocumentCacheTargetObservation firstTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        DocumentCacheTargetObservation secondTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 2),
            effectiveSettings
        );
        StaticTargetRegistry registry = new(
            [firstTarget, secondTarget],
            [ExecutionContext(firstTarget), ExecutionContext(secondTarget)]
        );
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(
            firstTarget,
            secondTarget
        );
        TaskCompletionSource firstObservationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedStatusObserver observer = new(
            async (request, cancellationToken) =>
            {
                if (request.TargetExecutionContext.TargetKey.Equals(firstTarget.TargetKey))
                {
                    firstObservationStarted.TrySetResult();
                }

                await WaitForCancellationAsync(cancellationToken);
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        CapturingStatusTelemetry telemetry = new();
        ControlledTimeProvider timeProvider = new(ProcessObservedAt);
        DocumentCacheStatusService service = CreateService(
            registry,
            observationStore,
            observer,
            telemetry,
            timeProvider: timeProvider
        );

        DocumentCacheStatusResponse response;
        using (CancellationTokenSource callerCancellationTokenSource = new())
        {
            Task<DocumentCacheStatusResponse> responseTask = service.GetStatusAsync(
                callerCancellationTokenSource.Token
            );

            try
            {
                await firstObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                timeProvider.Advance(effectiveSettings.StatusEndpointTimeout);
                response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                await callerCancellationTokenSource.CancelAsync();
                throw;
            }
        }

        DocumentCacheStatusTarget firstStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 1
        );
        firstStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        firstStatus.OperationalHealth.Message.Should().Contain("started");

        DocumentCacheStatusTarget secondStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 2
        );
        secondStatus.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        secondStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        secondStatus.OperationalHealth.Message.Should().Contain("did not start");
        secondStatus.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        secondStatus.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        secondStatus.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);
        secondStatus.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unknown);
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(firstTarget.TargetKey));

        CapturedProviderObservation providerObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle()
            .Which;
        providerObservation.TargetKey.Should().Be(firstTarget.TargetKey);
        providerObservation
            .Outcome.Should()
            .Be(DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut);
        providerObservation.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
    }

    [Test]
    public async Task It_uses_the_shortest_effective_endpoint_timeout_when_target_settings_diverge()
    {
        DocumentCacheTargetEffectiveSettings firstSettings = EffectiveSettings(
            maxConcurrentTargets: 1,
            statusObservationTimeout: TimeSpan.FromSeconds(1),
            endpointTimeout: TimeSpan.FromSeconds(2)
        );
        DocumentCacheTargetEffectiveSettings secondSettings = EffectiveSettings(
            maxConcurrentTargets: 1,
            statusObservationTimeout: TimeSpan.FromSeconds(1),
            endpointTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheTargetObservation firstTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            firstSettings
        );
        DocumentCacheTargetObservation secondTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 2),
            secondSettings
        );
        StaticTargetRegistry registry = new(
            [firstTarget, secondTarget],
            [ExecutionContext(firstTarget), ExecutionContext(secondTarget)]
        );
        ScriptedStatusObserver observer = new(
            async (request, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                return await Success(request, cancellationToken);
            }
        );
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(firstTarget, secondTarget),
            observer
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        response
            .Targets.Should()
            .AllSatisfy(target =>
                target.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout)
            );
        observer.StartedKeys.Should().NotContain(key => key.Equals(secondTarget.TargetKey));
    }

    [Test]
    public async Task It_uses_the_endpoint_timeout_override_when_it_is_shorter_than_effective_settings()
    {
        TimeSpan endpointTimeoutOverride = TimeSpan.FromMilliseconds(40);
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            statusObservationTimeout: TimeSpan.FromSeconds(5),
            endpointTimeout: TimeSpan.FromSeconds(5)
        );
        DocumentCacheTargetObservation target = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        TaskCompletionSource providerObservationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedStatusObserver observer = new(
            async (_, cancellationToken) =>
            {
                providerObservationStarted.TrySetResult();
                await WaitForCancellationAsync(cancellationToken);
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        CapturingStatusTelemetry telemetry = new();
        ControlledTimeProvider timeProvider = new(ProcessObservedAt);
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(target),
            observer,
            telemetry,
            timeProvider: timeProvider
        );

        DocumentCacheStatusResponse response;
        using (CancellationTokenSource callerCancellationTokenSource = new())
        {
            Task<DocumentCacheStatusResponse> responseTask = service.GetStatusAsync(
                callerCancellationTokenSource.Token,
                endpointTimeoutOverride: endpointTimeoutOverride
            );

            try
            {
                await providerObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                timeProvider.Advance(endpointTimeoutOverride);
                response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                await callerCancellationTokenSource.CancelAsync();
                throw;
            }
        }

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);

        CapturedProviderObservation providerObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle()
            .Which;
        providerObservation.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
    }

    [Test]
    public async Task It_returns_endpoint_timeout_when_provider_ignores_cancellation()
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            statusObservationTimeout: TimeSpan.FromSeconds(5),
            endpointTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheTargetObservation target = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        TaskCompletionSource providerObservationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedStatusObserver observer = new(
            (_, _) =>
            {
                providerObservationStarted.TrySetResult();
                return new TaskCompletionSource<DocumentCacheStatusCurrentSourceObservationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                ).Task;
            }
        );
        CapturingStatusTelemetry telemetry = new();
        ControlledTimeProvider timeProvider = new(ProcessObservedAt);
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(target),
            observer,
            telemetry,
            timeProvider: timeProvider
        );

        DocumentCacheStatusResponse response;
        using (CancellationTokenSource callerCancellationTokenSource = new())
        {
            Task<DocumentCacheStatusResponse> responseTask = service.GetStatusAsync(
                callerCancellationTokenSource.Token
            );

            try
            {
                await providerObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                timeProvider.Advance(effectiveSettings.StatusEndpointTimeout);
                response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                await callerCancellationTokenSource.CancelAsync();
                throw;
            }
        }

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(target.TargetKey));

        CapturedProviderObservation providerObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle()
            .Which;
        providerObservation
            .Outcome.Should()
            .Be(DocumentCacheStatusProviderObservationTelemetryOutcome.TimedOut);
        providerObservation.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
    }

    [Test]
    public async Task It_preserves_process_failure_for_unstarted_targets_when_endpoint_budget_expires()
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            maxConcurrentTargets: 1,
            statusObservationTimeout: TimeSpan.FromSeconds(5),
            endpointTimeout: TimeSpan.FromMilliseconds(50)
        );
        DocumentCacheTargetObservation startedTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        DocumentCacheInventoryValidationResult invalidInventory = new(
            DocumentCacheInventoryStatus.Invalid,
            "Inventory invalid."
        );
        DocumentCacheTargetObservation unstartedInvalidTarget = ResolvedInventoryInvalidTarget(
            DocumentCacheTargetKey.Create("", 2),
            invalidInventory,
            new DocumentCacheInventoryValidationComponents(
                invalidInventory,
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Satisfied,
                    "Work inventory satisfied."
                ),
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Satisfied,
                    "Cache inventory satisfied."
                ),
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Satisfied,
                    "Data store identity inventory satisfied."
                )
            )
        );
        StaticTargetRegistry registry = new(
            [startedTarget, unstartedInvalidTarget],
            [ExecutionContext(startedTarget)]
        );
        TaskCompletionSource startedObservationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedStatusObserver observer = new(
            async (request, cancellationToken) =>
            {
                if (request.TargetExecutionContext.TargetKey.Equals(startedTarget.TargetKey))
                {
                    startedObservationStarted.TrySetResult();
                }

                await WaitForCancellationAsync(cancellationToken);
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        ControlledTimeProvider timeProvider = new(ProcessObservedAt);
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(startedTarget),
            observer,
            timeProvider: timeProvider
        );

        DocumentCacheStatusResponse response;
        using (CancellationTokenSource callerCancellationTokenSource = new())
        {
            Task<DocumentCacheStatusResponse> responseTask = service.GetStatusAsync(
                callerCancellationTokenSource.Token
            );

            try
            {
                await startedObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                timeProvider.Advance(effectiveSettings.StatusEndpointTimeout);
                response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                await callerCancellationTokenSource.CancelAsync();
                throw;
            }
        }

        DocumentCacheStatusTarget startedStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 1
        );
        startedStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);

        DocumentCacheStatusTarget unstartedStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 2
        );
        unstartedStatus.Eligibility.Status.Should().Be(DocumentCacheStatusEligibilityStatus.Ineligible);
        unstartedStatus.Eligibility.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
        unstartedStatus
            .OperationalHealth.Status.Should()
            .Be(DocumentCacheOperationalHealthStatus.NonOperational);
        unstartedStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
        unstartedStatus.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        unstartedStatus.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
        unstartedStatus.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unavailable);
        unstartedStatus.QueueSummary.Presence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        unstartedStatus.DurableObservedAt.Should().BeNull();
        observer.StartedKeys.Should().Contain(key => key.Equals(startedTarget.TargetKey));
        observer.StartedKeys.Should().NotContain(key => key.Equals(unstartedInvalidTarget.TargetKey));
    }

    [Test]
    public async Task It_throws_for_caller_cancellation_instead_of_serializing_target_status()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        using CancellationTokenSource cancellationTokenSource = new();
        await cancellationTokenSource.CancelAsync();
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            ObservationStore(target),
            new ScriptedStatusObserver(Success)
        );

        Func<Task> act = () => service.GetStatusAsync(cancellationTokenSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_propagates_caller_cancellation_returned_as_cancelled_without_provider_telemetry()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        using CancellationTokenSource cancellationTokenSource = new();
        ScriptedStatusObserver observer = new(
            async (_, _) =>
            {
                await cancellationTokenSource.CancelAsync();
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        CapturingStatusTelemetry telemetry = new();
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            ObservationStore(target),
            observer,
            telemetry
        );

        Func<Task> act = () => service.GetStatusAsync(cancellationTokenSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        telemetry.ProviderObservations.Should().BeEmpty();
        telemetry.StatusObservations.Should().BeEmpty();
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(target.TargetKey));
    }

    [Test]
    public async Task It_maps_unattributed_provider_cancelled_outcome_to_provider_observation_failed()
    {
        DocumentCacheTargetObservation target = ResolvedTarget(DocumentCacheTargetKey.Create("", 1));
        CapturingStatusTelemetry telemetry = new();
        DocumentCacheStatusService service = CreateService(
            new StaticTargetRegistry([target], [ExecutionContext(target)]),
            ObservationStore(target),
            new ScriptedStatusObserver(
                (_, _) =>
                    Task.FromResult(
                        DocumentCacheStatusCurrentSourceObservationResult.Cancelled(
                            "Provider cancelled without request timeout."
                        )
                    )
            ),
            telemetry
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        statusTarget
            .OperationalHealth.Reason.Should()
            .Be(DocumentCacheStatusReason.ProviderObservationFailed);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Unknown);

        CapturedProviderObservation providerObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle()
            .Which;
        providerObservation.TargetKey.Should().Be(target.TargetKey);
        providerObservation
            .Outcome.Should()
            .Be(DocumentCacheStatusProviderObservationTelemetryOutcome.Failed);
        providerObservation.Reason.Should().Be(DocumentCacheStatusReason.ProviderObservationFailed);
    }

    [Test]
    public async Task It_caps_bounded_parallelism_by_effective_max_concurrent_targets()
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(maxConcurrentTargets: 2);
        DocumentCacheTargetObservation firstTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        DocumentCacheTargetObservation secondTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 2),
            effectiveSettings
        );
        DocumentCacheTargetObservation thirdTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 3),
            effectiveSettings
        );
        StaticTargetRegistry registry = new(
            [firstTarget, secondTarget, thirdTarget],
            [ExecutionContext(firstTarget), ExecutionContext(secondTarget), ExecutionContext(thirdTarget)]
        );
        ScriptedStatusObserver observer = new(
            async (request, cancellationToken) =>
            {
                await Task.Delay(50, cancellationToken);
                return await Success(request, cancellationToken);
            }
        );
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(firstTarget, secondTarget, thirdTarget),
            observer
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        response.Targets.Should().HaveCount(3);
        observer.MaxActiveCount.Should().BeLessThanOrEqualTo(2);
        observer.StartedKeys.Should().HaveCount(3);
    }

    private static DocumentCacheStatusService CreateService(
        IDocumentCacheTargetRegistry registry,
        DocumentCacheProjectionObservationStore? observationStore = null,
        ScriptedStatusObserver? observer = null,
        IDocumentCacheStatusTelemetry? statusTelemetry = null,
        IDocumentCacheEnqueueFailureObservationProvider? enqueueFailureObservationProvider = null,
        TimeProvider? timeProvider = null
    ) =>
        new(
            registry,
            observationStore
                ?? new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ProcessObservedAt)),
            observer is null ? [] : [observer],
            timeProvider ?? new FixedTimeProvider(ProcessObservedAt),
            enqueueFailureObservationProvider,
            statusTelemetry: statusTelemetry
        );

    private static DocumentCacheProjectionObservationStore ObservationStore(
        params DocumentCacheTargetObservation[] targets
    )
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ProcessObservedAt));

        foreach (DocumentCacheTargetObservation target in targets)
        {
            store.ObserveTarget(
                new DocumentCacheProjectionTargetHealthSnapshot(
                    target.TargetKey,
                    target.Generation!,
                    target.EffectiveSettings.ProjectorPageSize,
                    RuntimeObservedAt,
                    target.ProviderToken,
                    target.PhysicalSourceFingerprint,
                    executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                        isRunning: true,
                        isActivelyProcessing: false,
                        isWaitingForWorkerGate: false,
                        isInBackoff: false,
                        backoffUntil: null,
                        cancellationRequested: false,
                        cancellationObservedAt: null
                    ),
                    lastSuccess: new DocumentCacheProjectionSuccessSnapshot(
                        1,
                        1,
                        RuntimeObservedAt.AddSeconds(-1)
                    )
                )
            );
        }

        return store;
    }

    private static DocumentCacheTargetObservation ResolvedTarget(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetEffectiveSettings? effectiveSettings = null,
        DocumentCacheTargetContextGeneration? generation = null
    )
    {
        DocumentCacheTargetEffectiveSettings settings = effectiveSettings ?? EffectiveSettings();
        return DocumentCacheTargetObservation.ResolvedEligible(
            targetKey,
            settings,
            generation ?? Generation,
            RelationalProviderToken.Postgresql,
            Fingerprint(targetKey.DataStoreId),
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
    }

    private static DocumentCacheTargetObservation UnresolvedTargetWithDiagnostic(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetDiagnosticCategory category
    ) =>
        DocumentCacheTargetObservation.Unresolved(
            targetKey,
            EffectiveSettings(),
            retryState: null,
            diagnostics:
            [
                new DocumentCacheTargetDiagnostic(
                    targetKey,
                    DocumentCacheTargetResolutionState.Unresolved,
                    providerToken: null,
                    generation: null,
                    physicalSourceFingerprint: null,
                    lifecycle: null,
                    inventory: null,
                    enqueueTrigger: null,
                    sqlServerPrerequisites: null,
                    retryState: null,
                    category,
                    $"Diagnostic {category}"
                ),
            ]
        );

    private static DocumentCacheTargetObservation ResolvedInventoryInvalidTarget(
        DocumentCacheTargetKey targetKey,
        DocumentCacheInventoryValidationResult aggregateInventory,
        DocumentCacheInventoryValidationComponents inventoryComponents
    )
    {
        DocumentCacheTargetEffectiveSettings settings = EffectiveSettings();
        return DocumentCacheTargetObservation.ResolvedIneligible(
            targetKey,
            settings,
            Generation,
            RelationalProviderToken.Postgresql,
            Fingerprint(targetKey.DataStoreId),
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            aggregateInventory,
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
            retryState: null,
            diagnostics: [],
            inventoryComponents: inventoryComponents
        );
    }

    private static DocumentCacheTargetObservation ResolvedLifecycleReadFailureTarget(
        DocumentCacheTargetKey targetKey,
        DocumentCacheLifecycleReadStatus lifecycleReadStatus
    )
    {
        DocumentCacheInventoryValidationResult inventory = new(
            DocumentCacheInventoryStatus.Satisfied,
            "Inventory satisfied."
        );
        DocumentCacheEnqueueTriggerValidationResult enqueueTrigger = new(
            DocumentCacheEnqueueTriggerStatus.Satisfied,
            "Enqueue trigger satisfied."
        );
        DocumentCacheSqlServerPrerequisiteDetails prerequisites =
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable();
        List<DocumentCacheTargetDiagnostic> diagnostics =
        [
            new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                RelationalProviderToken.Postgresql,
                Generation,
                Fingerprint(targetKey.DataStoreId),
                lifecycle: null,
                inventory,
                enqueueTrigger,
                sqlServerPrerequisites: prerequisites,
                retryState: null,
                category: DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                message: "Lifecycle read failed."
            ),
        ];

        return DocumentCacheTargetObservation.ResolvedIneligible(
            targetKey,
            EffectiveSettings(),
            Generation,
            RelationalProviderToken.Postgresql,
            Fingerprint(targetKey.DataStoreId),
            lifecycle: null,
            inventory,
            enqueueTrigger,
            sqlServerPrerequisites: prerequisites,
            retryState: null,
            diagnostics,
            lifecycleReadStatus: lifecycleReadStatus
        );
    }

    private static DocumentCacheTargetObservation ResolvedLifecycleReadFailureTargetWithHigherPriorityFailure(
        DocumentCacheTargetKey targetKey,
        DocumentCacheStatusReason higherPriorityReason
    )
    {
        DocumentCacheInventoryValidationResult? inventory = new(
            DocumentCacheInventoryStatus.Satisfied,
            "Inventory satisfied."
        );
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger = new(
            DocumentCacheEnqueueTriggerStatus.Satisfied,
            "Enqueue trigger satisfied."
        );
        DocumentCacheSqlServerPrerequisiteDetails? prerequisites =
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable();
        List<DocumentCacheTargetDiagnostic> diagnostics =
        [
            new(
                targetKey,
                DocumentCacheTargetResolutionState.Resolved,
                RelationalProviderToken.Postgresql,
                Generation,
                Fingerprint(targetKey.DataStoreId),
                lifecycle: null,
                inventory,
                enqueueTrigger,
                sqlServerPrerequisites: prerequisites,
                retryState: null,
                category: DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                message: "Lifecycle read failed."
            ),
        ];

        void AddHigherPriorityDiagnostic(DocumentCacheTargetDiagnosticCategory category, string message)
        {
            diagnostics.Add(
                new(
                    targetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    RelationalProviderToken.Postgresql,
                    Generation,
                    Fingerprint(targetKey.DataStoreId),
                    lifecycle: null,
                    inventory,
                    enqueueTrigger,
                    sqlServerPrerequisites: prerequisites,
                    retryState: null,
                    category,
                    message
                )
            );
        }

        switch (higherPriorityReason)
        {
            case DocumentCacheStatusReason.InventoryInvalid:
                inventory = new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Invalid,
                    "Inventory invalid."
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                    "Inventory invalid."
                );
                break;
            case DocumentCacheStatusReason.EnqueueTriggerUnavailable:
                enqueueTrigger = new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Disabled,
                    "Enqueue trigger disabled."
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
                    "Enqueue trigger disabled."
                );
                break;
            case DocumentCacheStatusReason.SqlServerPrerequisiteFailed:
                prerequisites = SqlServerPrerequisites(
                    DocumentCacheProviderPrerequisiteStatus.Disabled,
                    DocumentCacheProviderPrerequisiteStatus.Satisfied
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                    "SQL Server prerequisite failed."
                );
                break;
            case DocumentCacheStatusReason.UnsupportedPrerequisiteIncident:
                prerequisites = SqlServerPrerequisites(
                    DocumentCacheProviderPrerequisiteStatus.Unreadable,
                    DocumentCacheProviderPrerequisiteStatus.Unreadable
                );
                AddHigherPriorityDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                    "SQL Server prerequisite incident."
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(higherPriorityReason),
                    higherPriorityReason,
                    "Unsupported higher-priority lifecycle failure case."
                );
        }

        return DocumentCacheTargetObservation.ResolvedIneligible(
            targetKey,
            EffectiveSettings(),
            Generation,
            RelationalProviderToken.Postgresql,
            Fingerprint(targetKey.DataStoreId),
            lifecycle: null,
            inventory,
            enqueueTrigger,
            sqlServerPrerequisites: prerequisites,
            retryState: null,
            diagnostics,
            lifecycleReadStatus: DocumentCacheLifecycleReadStatus.Missing
        );
    }

    private static async Task AssertCompletedProviderSuccessAfterTimeoutAsync(
        TimeSpan statusObservationTimeout,
        TimeSpan endpointTimeout
    )
    {
        DocumentCacheTargetEffectiveSettings effectiveSettings = EffectiveSettings(
            statusObservationTimeout: statusObservationTimeout,
            endpointTimeout: endpointTimeout
        );
        DocumentCacheTargetObservation target = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1),
            effectiveSettings
        );
        StaticTargetRegistry registry = new([target], [ExecutionContext(target)]);
        CapturingStatusTelemetry telemetry = new();
        ControlledTimeProvider timeProvider = new(ProcessObservedAt);
        TaskCompletionSource providerObservationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedStatusObserver observer = new(
            (_, cancellationToken) =>
            {
                providerObservationStarted.TrySetResult();
                cancellationToken
                    .WaitHandle.WaitOne(TimeSpan.FromSeconds(2))
                    .Should()
                    .BeTrue("the configured status timeout should cancel the provider token");
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
        );
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(target),
            observer,
            telemetry,
            timeProvider: timeProvider
        );

        Task<DocumentCacheStatusResponse> responseTask = service.GetStatusAsync();
        await providerObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(
            TimeSpan.FromTicks(Math.Min(statusObservationTimeout.Ticks, endpointTimeout.Ticks))
        );

        DocumentCacheStatusResponse response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));

        DocumentCacheStatusTarget statusTarget = response.Targets.Should().ContainSingle().Which;
        statusTarget.OperationalHealth.Status.Should().Be(DocumentCacheOperationalHealthStatus.Operational);
        statusTarget.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.None);
        statusTarget.CaughtUp.Status.Should().Be(DocumentCacheCaughtUpStatus.CaughtUp);
        statusTarget.CaughtUp.Reason.Should().Be(DocumentCacheStatusReason.None);
        statusTarget.Lifecycle.Availability.Should().Be(DocumentCacheStatusAvailability.Available);
        statusTarget.DurableObservedAt.Should().Be(DurableObservedAt);

        CapturedProviderObservation providerObservation = telemetry
            .ProviderObservations.Should()
            .ContainSingle()
            .Which;
        providerObservation
            .Outcome.Should()
            .Be(DocumentCacheStatusProviderObservationTelemetryOutcome.Succeeded);
        providerObservation.Reason.Should().Be(DocumentCacheStatusReason.None);
    }

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
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            targetObservation.Inventory!,
            targetObservation.EnqueueTrigger!,
            targetObservation.SqlServerPrerequisites
        );

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings(
        int maxConcurrentTargets = 4,
        TimeSpan? statusObservationTimeout = null,
        TimeSpan? endpointTimeout = null
    ) =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromSeconds(2),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 100,
            projectorMaxConcurrentTargets: maxConcurrentTargets,
            projectorFailureBackoff: TimeSpan.FromSeconds(30),
            projectorBaselineHighWaterMark: 10000,
            administrationWorkflowTimeout: TimeSpan.FromMinutes(10),
            statusObservationTimeout,
            statusEndpointTimeout: endpointTimeout
        );

    private static DocumentCachePhysicalSourceFingerprint Fingerprint(long dataStoreId) =>
        new($"sha256:{dataStoreId:x64}");

    private static DocumentCacheSqlServerPrerequisiteDetails SqlServerPrerequisites(
        DocumentCacheProviderPrerequisiteStatus readCommittedSnapshotStatus,
        DocumentCacheProviderPrerequisiteStatus nestedTriggersStatus
    ) =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                readCommittedSnapshotStatus,
                "Read committed snapshot."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                nestedTriggersStatus,
                "Nested triggers."
            )
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
            $"Diagnostic {category}"
        );

    private static DocumentCacheAdministrativeCommandObservationSnapshot CommandObservation(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        DocumentCacheTargetObservation target,
        DocumentCacheAdministrativeCommand command
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
            ]
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

    private static DocumentCacheEnqueueFailureTelemetryEvent EnqueueFailureEvent(
        DocumentCacheTargetKey targetKey,
        DocumentCacheEnqueueFailureCategory category,
        int secondsAfterRuntimeObservation
    ) =>
        new(
            RuntimeObservedAt.AddSeconds(secondsAfterRuntimeObservation),
            targetKey,
            category,
            DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
            DocumentCacheEnqueueTelemetryResourceKind.Resource,
            $"Failure {category}"
        );

    private static Task<DocumentCacheStatusCurrentSourceObservationResult> Success(
        DocumentCacheStatusCurrentSourceObservationRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = request;

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

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The fake provider surfaces cancellation as its typed cancelled result.
        }
    }

    private sealed class StaticTargetRegistry(
        IEnumerable<DocumentCacheTargetObservation> targets,
        IEnumerable<DocumentCacheTargetExecutionContext> executionContexts
    ) : IDocumentCacheTargetRegistry
    {
        public int RefreshCallCount { get; private set; }

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } =
            new(targets, RegistryObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
            new(executionContexts, RegistryObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            RefreshCallCount++;
            throw new InvalidOperationException("Status service must not refresh DocumentCache targets.");
        }
    }

    private sealed class StatusSnapshotTargetRegistry(
        DocumentCacheTargetObservation statusSnapshotTarget,
        DocumentCacheTargetExecutionContext statusSnapshotExecutionContext,
        DocumentCacheTargetExecutionContext currentRuntimeExecutionContext
    ) : IDocumentCacheTargetRegistry
    {
        public int CurrentSnapshotAccessCount { get; private set; }

        public int CurrentRuntimeSnapshotAccessCount { get; private set; }

        public int StatusSnapshotAccessCount { get; private set; }

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot
        {
            get
            {
                CurrentSnapshotAccessCount++;
                return new DocumentCacheTargetRegistrySnapshot([statusSnapshotTarget], RegistryObservedAt);
            }
        }

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot
        {
            get
            {
                CurrentRuntimeSnapshotAccessCount++;
                return new DocumentCacheTargetRuntimeSnapshot(
                    [currentRuntimeExecutionContext],
                    RegistryObservedAt
                );
            }
        }

        public DocumentCacheTargetStatusSnapshot CurrentStatusSnapshot
        {
            get
            {
                StatusSnapshotAccessCount++;
                return new DocumentCacheTargetStatusSnapshot(
                    new DocumentCacheTargetRegistrySnapshot([statusSnapshotTarget], RegistryObservedAt),
                    new DocumentCacheTargetRuntimeSnapshot(
                        [statusSnapshotExecutionContext],
                        RegistryObservedAt
                    )
                );
            }
        }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Status service must not refresh DocumentCache targets.");
    }

    private sealed class ScriptedStatusObserver(
        Func<
            DocumentCacheStatusCurrentSourceObservationRequest,
            CancellationToken,
            Task<DocumentCacheStatusCurrentSourceObservationResult>
        > observeAsync
    ) : IDocumentCacheStatusCurrentSourceObserver
    {
        private int _activeCount;
        private int _maxActiveCount;
        private readonly ConcurrentQueue<DocumentCacheTargetKey> _startedKeys = new();

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public ImmutableArray<DocumentCacheTargetKey> StartedKeys => _startedKeys.ToImmutableArray();

        public int MaxActiveCount => Volatile.Read(ref _maxActiveCount);

        public async Task<DocumentCacheStatusCurrentSourceObservationResult> ObserveAsync(
            DocumentCacheStatusCurrentSourceObservationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _startedKeys.Enqueue(request.TargetExecutionContext.TargetKey);
            int activeCount = Interlocked.Increment(ref _activeCount);
            TrackMaxActiveCount(activeCount);

            try
            {
                return await observeAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }

        private void TrackMaxActiveCount(int activeCount)
        {
            int observedMax;
            do
            {
                observedMax = Volatile.Read(ref _maxActiveCount);
                if (activeCount <= observedMax)
                {
                    return;
                }
            } while (
                Interlocked.CompareExchange(ref _maxActiveCount, activeCount, observedMax) != observedMax
            );
        }
    }

    private sealed class StaticEnqueueFailureObservationProvider(
        DocumentCacheTargetKey retainedTargetKey,
        DocumentCacheEnqueueFailureSnapshot snapshot
    ) : IDocumentCacheEnqueueFailureObservationProvider
    {
        public DocumentCacheEnqueueFailureSnapshot GetFailureSnapshot(DocumentCacheTargetKey targetKey)
        {
            ArgumentNullException.ThrowIfNull(targetKey);
            return targetKey.Equals(retainedTargetKey) ? snapshot : new DocumentCacheEnqueueFailureSnapshot();
        }
    }

    private sealed class CapturingStatusTelemetry : IDocumentCacheStatusTelemetry
    {
        private readonly ConcurrentQueue<DocumentCacheStatusTarget> _statusObservations = new();
        private readonly ConcurrentQueue<CapturedProviderObservation> _providerObservations = new();

        public ImmutableArray<DocumentCacheStatusTarget> StatusObservations =>
            _statusObservations.ToImmutableArray();

        public ImmutableArray<CapturedProviderObservation> ProviderObservations =>
            _providerObservations.ToImmutableArray();

        public void RecordStatusObservation(
            DocumentCacheTargetObservation targetObservation,
            DocumentCacheStatusTarget statusTarget,
            TimeSpan? providerObservationDuration
        )
        {
            ArgumentNullException.ThrowIfNull(targetObservation);
            _statusObservations.Enqueue(statusTarget);
        }

        public void RecordProviderObservation(
            DocumentCacheTargetKey targetKey,
            RelationalProviderToken providerToken,
            DocumentCacheStatusProviderObservationTelemetryOutcome outcome,
            DocumentCacheStatusReason reason,
            TimeSpan duration,
            DocumentCacheLifecycleState? lifecycleState,
            double? oldestWorkAgeSeconds
        )
        {
            _providerObservations.Enqueue(
                new CapturedProviderObservation(
                    targetKey,
                    providerToken,
                    outcome,
                    reason,
                    duration,
                    lifecycleState,
                    oldestWorkAgeSeconds
                )
            );
        }
    }

    private sealed record CapturedProviderObservation(
        DocumentCacheTargetKey TargetKey,
        RelationalProviderToken ProviderToken,
        DocumentCacheStatusProviderObservationTelemetryOutcome Outcome,
        DocumentCacheStatusReason Reason,
        TimeSpan Duration,
        DocumentCacheLifecycleState? LifecycleState,
        double? OldestWorkAgeSeconds
    );

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ControlledTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

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
    }
}
