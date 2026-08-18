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
        DocumentCacheTargetObservation zTarget = ResolvedTarget(DocumentCacheTargetKey.Create("z", 3));
        DocumentCacheTargetObservation defaultTenantTarget = ResolvedTarget(
            DocumentCacheTargetKey.Create("", 1)
        );
        DocumentCacheTargetObservation aTarget = ResolvedTarget(DocumentCacheTargetKey.Create("a", 2));
        StaticTargetRegistry registry = new(
            [zTarget, defaultTenantTarget, aTarget],
            [ExecutionContext(zTarget), ExecutionContext(defaultTenantTarget), ExecutionContext(aTarget)]
        );
        DocumentCacheProjectionObservationStore observationStore = ObservationStore(
            zTarget,
            defaultTenantTarget,
            aTarget
        );
        ScriptedStatusObserver observer = new(Success);
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        response.Targets.Select(target => target.TargetKey.TenantKey).Should().Equal("", "a", "z");
        response.Targets.Select(target => target.TargetKey.DataStoreId).Should().Equal(1, 2, 3);
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
            .BeEquivalentTo([zTarget.TargetKey, defaultTenantTarget.TargetKey, aTarget.TargetKey]);
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
        ScriptedStatusObserver observer = new(
            async (_, cancellationToken) =>
            {
                await WaitForCancellationAsync(cancellationToken);
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        CapturingStatusTelemetry telemetry = new();
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer, telemetry);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

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
        ScriptedStatusObserver observer = new(
            async (_, cancellationToken) =>
            {
                await WaitForCancellationAsync(cancellationToken);
                return DocumentCacheStatusCurrentSourceObservationResult.Cancelled("cancelled");
            }
        );
        DocumentCacheStatusService service = CreateService(
            registry,
            ObservationStore(startedTarget),
            observer
        );

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

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
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(startedTarget.TargetKey));
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
        IDocumentCacheEnqueueFailureObservationProvider? enqueueFailureObservationProvider = null
    ) =>
        new(
            registry,
            observationStore
                ?? new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ProcessObservedAt)),
            observer is null ? [] : [observer],
            new FixedTimeProvider(ProcessObservedAt),
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
            DocumentCacheStatusTarget statusTarget
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
}
