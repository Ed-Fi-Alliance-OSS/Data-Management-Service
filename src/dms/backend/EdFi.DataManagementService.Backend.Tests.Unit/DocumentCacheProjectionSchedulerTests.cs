// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheProjectionScheduler")]
public class Given_DocumentCacheProjectionScheduler
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
    public async Task It_selects_ready_targets_in_normalized_target_key_and_generation_order()
    {
        RecordingDrainPageProcessor drainPageProcessor = new(_ =>
            DocumentCacheProjectionDrainPageResult.PageProcessed(1)
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext laterGeneration = RuntimeContext(
            DocumentCacheTargetKey.Create("tenant-a", 2),
            generation: 2,
            observationSink
        );
        DocumentCacheProjectionTargetRuntimeContext firstTarget = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1,
            observationSink
        );
        DocumentCacheProjectionTargetRuntimeContext secondTarget = RuntimeContext(
            DocumentCacheTargetKey.Create("tenant-a", 2),
            generation: 1,
            observationSink
        );
        DocumentCacheProjectionTargetRuntimeContext lastTarget = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-B", 1),
            generation: 1,
            observationSink
        );

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results =
            await scheduler.RunReadyTargetsOnceAsync([
                lastTarget,
                laterGeneration,
                firstTarget,
                secondTarget,
            ]);

        results.Should().HaveCount(4);
        results
            .Should()
            .OnlyContain(result =>
                result.Status == DocumentCacheProjectionSchedulerDispatchStatus.Dispatched
            );
        drainPageProcessor
            .Calls.Select(call => call.Context.ContextKey)
            .Should()
            .Equal(
                firstTarget.ContextKey,
                secondTarget.ContextKey,
                laterGeneration.ContextKey,
                lastTarget.ContextKey
            );
        drainPageProcessor
            .Calls.GroupBy(call => call.Context.ContextKey)
            .Should()
            .OnlyContain(group => group.Count() == 1);
    }

    [Test]
    public async Task It_uses_max_concurrent_targets_as_the_only_ordinary_worker_gate()
    {
        BlockingDrainPageProcessor drainPageProcessor = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            new RecordingObservationSink(),
            maxConcurrentTargets: 2
        );
        DocumentCacheProjectionTargetRuntimeContext[] contexts =
        [
            RuntimeContext(DocumentCacheTargetKey.Create("Tenant-A", 1), generation: 1),
            RuntimeContext(DocumentCacheTargetKey.Create("Tenant-B", 1), generation: 1),
            RuntimeContext(DocumentCacheTargetKey.Create("Tenant-C", 1), generation: 1),
        ];

        Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> schedulerTask =
            scheduler.RunReadyTargetsOnceAsync(contexts);

        await drainPageProcessor.WaitForCallCountAsync(2);
        drainPageProcessor.ActiveCount.Should().Be(2);
        drainPageProcessor.MaxActiveCount.Should().Be(2);
        drainPageProcessor.CompletedCount.Should().Be(0);

        drainPageProcessor.ReleaseOne(DocumentCacheProjectionDrainPageResult.PageProcessed(1));
        await drainPageProcessor.WaitForCallCountAsync(3);

        drainPageProcessor.MaxActiveCount.Should().Be(2);
        drainPageProcessor.ReleaseAll(DocumentCacheProjectionDrainPageResult.PageProcessed(1));
        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results = await schedulerTask;

        results.Should().HaveCount(3);
        results
            .Should()
            .OnlyContain(result =>
                result.Status == DocumentCacheProjectionSchedulerDispatchStatus.Dispatched
            );
        drainPageProcessor.CompletedCount.Should().Be(3);
    }

    [Test]
    public async Task It_returns_dispatched_targets_behind_skipped_targets_in_the_ready_rotation()
    {
        RecordingDrainPageProcessor drainPageProcessor = new(_ =>
            DocumentCacheProjectionDrainPageResult.PageProcessed(1)
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext first = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1,
            observationSink
        );
        DocumentCacheProjectionTargetRuntimeContext initiallySleeping = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-B", 1),
            generation: 1,
            observationSink
        );
        initiallySleeping.SchedulingState.SetPollSleepUntil(ObservedAt.AddSeconds(30));
        DocumentCacheProjectionTargetRuntimeContext third = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-C", 1),
            generation: 1,
            observationSink
        );

        await scheduler.RunReadyTargetsOnceAsync([first, initiallySleeping, third]);
        drainPageProcessor
            .Calls.Select(call => call.Context.ContextKey)
            .Should()
            .Equal(first.ContextKey, third.ContextKey);

        drainPageProcessor.Calls.Clear();
        initiallySleeping.SchedulingState.SetPollSleepUntil(ObservedAt.AddTicks(-1));
        await scheduler.RunReadyTargetsOnceAsync([first, initiallySleeping, third]);

        drainPageProcessor
            .Calls.Select(call => call.Context.ContextKey)
            .Should()
            .Equal(initiallySleeping.ContextKey, first.ContextKey, third.ContextKey);
    }

    [Test]
    public async Task It_skips_sleeping_command_owned_and_cancellation_pending_targets_without_draining()
    {
        RecordingDrainPageProcessor drainPageProcessor = new(_ =>
            DocumentCacheProjectionDrainPageResult.PageProcessed(1)
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext pollSleeping = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1,
            observationSink
        );
        pollSleeping.SchedulingState.SetPollSleepUntil(ObservedAt.AddSeconds(30));
        DocumentCacheProjectionTargetRuntimeContext backoffSleeping = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-B", 1),
            generation: 1,
            observationSink
        );
        backoffSleeping.SchedulingState.SetTargetBackoffUntil(ObservedAt.AddSeconds(30));
        DocumentCacheProjectionTargetRuntimeContext commandOwned = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-C", 1),
            generation: 1,
            observationSink
        );
        TaskCompletionSource commandRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DocumentCacheProjectionDrainPageResult> commandTask =
            commandOwned.DrainExecutor.RunAdministrativeDrainSliceAsync(async _ =>
            {
                commandStarted.SetResult();
                await commandRelease.Task;
                return DocumentCacheProjectionDrainPageResult.PageProcessed(0);
            });
        await commandStarted.Task;

        DocumentCacheProjectionTargetRuntimeContext cancelled = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-D", 1),
            generation: 1,
            observationSink
        );
        cancelled.Cancel();
        DocumentCacheProjectionTargetRuntimeContext ready = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-E", 1),
            generation: 1,
            observationSink
        );

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results =
            await scheduler.RunReadyTargetsOnceAsync([
                pollSleeping,
                backoffSleeping,
                commandOwned,
                cancelled,
                ready,
            ]);

        drainPageProcessor.Calls.Should().ContainSingle().Which.Context.Should().BeSameAs(ready);
        results.Should().ContainSingle().Which.ContextKey.Should().Be(ready.ContextKey);

        commandRelease.SetResult();
        await commandTask;
    }

    [Test]
    public async Task It_keeps_a_no_work_target_out_of_the_gate_until_poll_sleep_expires()
    {
        Queue<DocumentCacheProjectionDrainPageResult> resultsToReturn = new([
            DocumentCacheProjectionDrainPageResult.NoEligibleWork,
            DocumentCacheProjectionDrainPageResult.PageProcessed(1),
        ]);
        RecordingDrainPageProcessor drainPageProcessor = new(_ => resultsToReturn.Dequeue());
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext context = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1,
            observationSink
        );

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> firstPass =
            await scheduler.RunReadyTargetsOnceAsync([context]);
        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> secondPass =
            await scheduler.RunReadyTargetsOnceAsync([context]);

        firstPass
            .Should()
            .ContainSingle()
            .Which.DrainResult.Should()
            .BeSameAs(DocumentCacheProjectionDrainPageResult.NoEligibleWork);
        secondPass.Should().BeEmpty();
        drainPageProcessor.Calls.Should().ContainSingle();
    }

    [Test]
    public async Task It_lets_administrative_drain_own_the_per_target_executor()
    {
        BlockingDrainPageProcessor drainPageProcessor = new();
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext context = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1,
            observationSink
        );

        Task<DocumentCacheProjectionSchedulerDispatchResult> administrativeTask =
            scheduler.RunAdministrativeDrainSliceAsync(context);
        await drainPageProcessor.WaitForCallCountAsync(1);
        drainPageProcessor
            .Calls.Should()
            .ContainSingle()
            .Which.InvocationKind.Should()
            .Be(DocumentCacheProjectionDrainInvocationKind.Administrative);

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> ordinaryResults =
            await scheduler.RunReadyTargetsOnceAsync([context]);

        ordinaryResults.Should().BeEmpty();
        drainPageProcessor.ReleaseAll(DocumentCacheProjectionDrainPageResult.PageProcessed(1));
        DocumentCacheProjectionSchedulerDispatchResult administrativeResult = await administrativeTask;

        administrativeResult.Status.Should().Be(DocumentCacheProjectionSchedulerDispatchStatus.Dispatched);
        drainPageProcessor.Calls.Should().ContainSingle();
    }

    private static DocumentCacheProjectionScheduler CreateScheduler(
        IDocumentCacheProjectionDrainPageProcessor drainPageProcessor,
        IDocumentCacheProjectionObservationSink observationSink,
        int maxConcurrentTargets
    )
    {
        DocumentCacheOptions options = new()
        {
            Projector = new DocumentCacheProjectorOptions { MaxConcurrentTargets = maxConcurrentTargets },
        };

        return new DocumentCacheProjectionScheduler(
            Options.Create(options),
            drainPageProcessor,
            observationSink,
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );
    }

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetKey targetKey,
        long generation,
        IDocumentCacheProjectionObservationSink? observationSink = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(generation),
            EffectiveSettings(),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
            Fingerprint,
            TrackingLifecycle,
            SatisfiedInventory,
            SatisfiedEnqueueTrigger,
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                new StubDocumentCacheMaterializer(),
                new StubDocumentCacheWriter()
            ),
            observationSink ?? new RecordingObservationSink()
        );
    }

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

    private sealed class RecordingDrainPageProcessor(
        Func<DocumentCacheProjectionDrainPageRequest, DocumentCacheProjectionDrainPageResult> resultFactory
    ) : IDocumentCacheProjectionDrainPageProcessor
    {
        public List<DrainCall> Calls { get; } = [];

        public Task<DocumentCacheProjectionDrainPageResult> ProcessPageAsync(
            DocumentCacheProjectionDrainPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new DrainCall(request.TargetContext, request.InvocationKind));
            return Task.FromResult(resultFactory(request));
        }
    }

    private sealed class BlockingDrainPageProcessor : IDocumentCacheProjectionDrainPageProcessor
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource<DocumentCacheProjectionDrainPageResult>> _pending = [];
        private readonly List<Waiter> _waiters = [];
        private int _activeCount;
        private int _completedCount;
        private int _maxActiveCount;

        public List<DrainCall> Calls { get; } = [];

        public int ActiveCount
        {
            get
            {
                lock (_sync)
                {
                    return _activeCount;
                }
            }
        }

        public int CompletedCount
        {
            get
            {
                lock (_sync)
                {
                    return _completedCount;
                }
            }
        }

        public int MaxActiveCount
        {
            get
            {
                lock (_sync)
                {
                    return _maxActiveCount;
                }
            }
        }

        public async Task<DocumentCacheProjectionDrainPageResult> ProcessPageAsync(
            DocumentCacheProjectionDrainPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            TaskCompletionSource<DocumentCacheProjectionDrainPageResult> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            lock (_sync)
            {
                Calls.Add(new DrainCall(request.TargetContext, request.InvocationKind));
                _pending.Enqueue(completion);
                _activeCount++;
                _maxActiveCount = Math.Max(_maxActiveCount, _activeCount);
                CompleteSatisfiedWaiters();
            }

            try
            {
                return await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_sync)
                {
                    _activeCount--;
                    _completedCount++;
                    CompleteSatisfiedWaiters();
                }
            }
        }

        public Task WaitForCallCountAsync(int callCount)
        {
            lock (_sync)
            {
                if (Calls.Count >= callCount)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new Waiter(callCount, waiter));
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public void ReleaseOne(DocumentCacheProjectionDrainPageResult result)
        {
            TaskCompletionSource<DocumentCacheProjectionDrainPageResult> completion;
            lock (_sync)
            {
                completion = _pending.Dequeue();
            }

            completion.SetResult(result);
        }

        public void ReleaseAll(DocumentCacheProjectionDrainPageResult result)
        {
            while (true)
            {
                TaskCompletionSource<DocumentCacheProjectionDrainPageResult>? completion;
                lock (_sync)
                {
                    completion = _pending.Count == 0 ? null : _pending.Dequeue();
                }

                if (completion is null)
                {
                    return;
                }

                completion.SetResult(result);
            }
        }

        private void CompleteSatisfiedWaiters()
        {
            foreach (Waiter waiter in _waiters.Where(waiter => Calls.Count >= waiter.CallCount).ToArray())
            {
                waiter.Completion.SetResult();
                _waiters.Remove(waiter);
            }
        }

        private sealed record Waiter(int CallCount, TaskCompletionSource Completion);
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
        ) => _ = endedAt;

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }

    private sealed record DrainCall(
        DocumentCacheProjectionTargetRuntimeContext Context,
        DocumentCacheProjectionDrainInvocationKind InvocationKind
    );

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
