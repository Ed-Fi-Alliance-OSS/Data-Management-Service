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
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer);

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
        DocumentCacheStatusService service = CreateService(registry, observationStore, observer);

        DocumentCacheStatusResponse response = await service.GetStatusAsync();

        DocumentCacheStatusTarget firstStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 1
        );
        firstStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        firstStatus.OperationalHealth.Message.Should().Contain("started");

        DocumentCacheStatusTarget secondStatus = response.Targets.Single(target =>
            target.TargetKey.DataStoreId == 2
        );
        secondStatus.OperationalHealth.Reason.Should().Be(DocumentCacheStatusReason.StatusEndpointTimeout);
        secondStatus.OperationalHealth.Message.Should().Contain("did not start");
        observer.StartedKeys.Should().ContainSingle(key => key.Equals(firstTarget.TargetKey));
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
        StaticTargetRegistry registry,
        DocumentCacheProjectionObservationStore? observationStore = null,
        ScriptedStatusObserver? observer = null
    ) =>
        new(
            registry,
            observationStore
                ?? new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ProcessObservedAt)),
            observer is null ? [] : [observer],
            new FixedTimeProvider(ProcessObservedAt)
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
        DocumentCacheTargetEffectiveSettings? effectiveSettings = null
    )
    {
        DocumentCacheTargetEffectiveSettings settings = effectiveSettings ?? EffectiveSettings();
        return DocumentCacheTargetObservation.ResolvedEligible(
            targetKey,
            settings,
            Generation,
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
