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
[Category("DocumentCacheProjectionShutdown")]
public class Given_DocumentCacheProjectionShutdown
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAt.AddSeconds(-10);

    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public async Task It_returns_cancellation_pending_when_a_target_is_cancelled_while_waiting_for_the_worker_gate()
    {
        BlockingDrainPageProcessor drainPageProcessor = new();
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            drainPageProcessor,
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext administrativeContext = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1,
            observationSink
        );
        DocumentCacheProjectionTargetRuntimeContext ordinaryContext = RuntimeContext(
            DocumentCacheTargetKey.Create("Tenant-B", 1),
            generation: 1,
            observationSink
        );

        Task<DocumentCacheProjectionSchedulerDispatchResult> administrativeTask =
            scheduler.RunAdministrativeDrainSliceAsync(administrativeContext);
        await drainPageProcessor.WaitForCallCountAsync(1);

        Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> ordinaryTask =
            scheduler.RunReadyTargetsOnceAsync([ordinaryContext]);
        await observationSink.WaitForWaitingGateAsync(ordinaryContext.ContextKey);

        ordinaryContext.Cancel();

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> ordinaryResults =
            await ordinaryTask.WaitAsync(TimeSpan.FromSeconds(5));

        DocumentCacheProjectionSchedulerDispatchResult ordinaryResult = ordinaryResults
            .Should()
            .ContainSingle()
            .Subject;
        ordinaryResult.Status.Should().Be(DocumentCacheProjectionSchedulerDispatchStatus.Skipped);
        ordinaryResult
            .BlockReason.Should()
            .Be(DocumentCacheProjectionTargetReadinessBlockReason.CancellationPending);
        drainPageProcessor
            .Calls.Should()
            .ContainSingle()
            .Which.InvocationKind.Should()
            .Be(DocumentCacheProjectionDrainInvocationKind.Administrative);

        drainPageProcessor.ReleaseAll(DocumentCacheProjectionDrainPageResult.PageProcessed(1));
        await administrativeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task It_starts_no_new_item_after_cancellation_is_requested_between_items()
    {
        CancellingAfterFirstItemProcessor itemProcessor = new();
        ScriptedWorkPager pager = new(
            RelationalProviderToken.Postgresql,
            [Page(WorkItem(101, requiredContentVersion: 10), WorkItem(102, requiredContentVersion: 11))]
        );
        DocumentCacheProjectionDrainPageProcessor processor = CreateDrainPageProcessor(pager, itemProcessor);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext();

        Func<Task> act = async () =>
            await processor.ProcessPageAsync(
                new DocumentCacheProjectionDrainPageRequest(
                    targetContext,
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                )
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        itemProcessor.Calls.Select(call => call.WorkItem.DocumentId).Should().Equal(101);
        targetContext.Cursor.LastDocumentId.Should().Be(101);
    }

    [Test]
    public async Task It_does_not_start_materialization_when_cancellation_is_requested_after_the_writer_fast_path()
    {
        using CancellationTokenSource shutdown = new();
        CancellingWriter writer = new(
            () => shutdown.Cancel(),
            new DocumentCacheWriterResult.NeedsMaterialization(10)
        );
        RecordingMaterializer materializer = new();
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            materializer: materializer,
            writer: writer
        );
        DocumentCacheProjectionItemProcessor processor = new(
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionItemProcessor>.Instance
        );

        Func<Task> act = async () =>
            await processor.ProcessItemAsync(
                new DocumentCacheProjectionItemProcessRequest(
                    targetContext,
                    WorkItem(101, requiredContentVersion: 10),
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                ),
                shutdown.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        writer.Calls.Should().ContainSingle();
        materializer.Calls.Should().BeEmpty();
    }

    [Test]
    public void It_creates_fresh_process_local_cursor_and_failure_state_for_a_new_runtime_context()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            DocumentCacheTargetKey.Create("Tenant-A", 1),
            generation: 1
        );
        DocumentCacheProjectionTargetRuntimeContext oldContext = RuntimeContext(executionContext);
        oldContext.Cursor.Advance(FirstEnqueuedAt, 101);
        oldContext.FailureBackoffState.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "previous failure",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );
        oldContext.Cancel();

        DocumentCacheProjectionTargetRuntimeContext newContext = RuntimeContext(executionContext);

        newContext.Cursor.HasValue.Should().BeFalse();
        newContext.FailureBackoffState.Count.Should().Be(0);
        newContext.CancellationRequested.Should().BeFalse();
        newContext
            .FailureBackoffState.Capacity.Should()
            .Be(executionContext.EffectiveSettings.ProjectorPageSize);
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

    private static DocumentCacheProjectionDrainPageProcessor CreateDrainPageProcessor(
        IDocumentProjectionWorkPager pager,
        IDocumentCacheProjectionItemProcessor itemProcessor
    ) =>
        new(
            pager,
            itemProcessor,
            NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
            new FixedTimeProvider(ObservedAt)
        );

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetKey? targetKey = null,
        long generation = 1,
        IDocumentCacheProjectionObservationSink? observationSink = null,
        IDocumentCacheMaterializer? materializer = null,
        IDocumentCacheWriter? writer = null
    ) =>
        RuntimeContext(
            ExecutionContext(targetKey ?? DocumentCacheTargetKey.Create("Tenant-A", 1), generation),
            observationSink,
            materializer,
            writer
        );

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetExecutionContext executionContext,
        IDocumentCacheProjectionObservationSink? observationSink = null,
        IDocumentCacheMaterializer? materializer = null,
        IDocumentCacheWriter? writer = null
    ) =>
        new(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                MaterializationTargetContext(executionContext.TargetKey),
                materializer ?? new StubDocumentCacheMaterializer(),
                writer ?? new StubDocumentCacheWriter()
            ),
            observationSink ?? new RecordingObservationSink()
        );

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheTargetKey targetKey,
        long generation
    ) =>
        new(
            targetKey,
            new DocumentCacheTargetContextGeneration(generation),
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
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "postgresql"),
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

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
        DocumentCacheTargetKey targetKey
    ) =>
        new(
            new DocumentCacheProjectionTargetKey(targetKey.TenantKey, new DataStoreId(targetKey.DataStoreId)),
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

    private static DocumentProjectionWorkPage Page(params DocumentProjectionWorkPageItem[] items) =>
        new(items, pageSize: 3);

    private static DocumentProjectionWorkPageItem WorkItem(long documentId, long requiredContentVersion) =>
        new(documentId, requiredContentVersion, FirstEnqueuedAt, FirstEnqueuedAt.AddSeconds(5));

    private sealed class BlockingDrainPageProcessor : IDocumentCacheProjectionDrainPageProcessor
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource<DocumentCacheProjectionDrainPageResult>> _pending = [];
        private readonly List<Waiter> _waiters = [];

        public List<DrainCall> Calls { get; } = [];

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
                CompleteSatisfiedWaiters();
            }

            return await completion.Task.WaitAsync(cancellationToken);
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

    private sealed class ScriptedWorkPager(
        RelationalProviderToken providerToken,
        IEnumerable<DocumentProjectionWorkPage> pages
    ) : IDocumentProjectionWorkPager
    {
        private readonly Queue<DocumentProjectionWorkPage> _pages = new(pages);

        public RelationalProviderToken ProviderToken { get; } = providerToken;

        public Task<DocumentProjectionWorkPage> ReadPageAsync(
            DocumentProjectionWorkPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class CancellingAfterFirstItemProcessor : IDocumentCacheProjectionItemProcessor
    {
        public List<DocumentCacheProjectionItemProcessRequest> Calls { get; } = [];

        public Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
            DocumentCacheProjectionItemProcessRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request);
            if (Calls.Count == 1)
            {
                request.TargetContext.Cancel();
            }

            return Task.FromResult(DocumentCacheProjectionItemProcessResult.Continue);
        }
    }

    private sealed class CancellingWriter(Action beforeResult, DocumentCacheWriterResult result)
        : IDocumentCacheWriter
    {
        public List<DocumentCacheWriterRequest> Calls { get; } = [];

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
        {
            Calls.Add(request);
            beforeResult();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingMaterializer : IDocumentCacheMaterializer
    {
        public List<DocumentCacheMaterializationRequest> Calls { get; } = [];

        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            Calls.Add(request);
            return Task.FromResult<DocumentCacheMaterializationResult>(
                DocumentCacheMaterializationResult.MissingSource.Instance
            );
        }
    }

    private sealed class RecordingObservationSink : IDocumentCacheProjectionObservationSink
    {
        private readonly object _sync = new();
        private readonly List<Waiter> _waiters = [];

        public List<DocumentCacheProjectionTargetHealthSnapshot> TargetSnapshots { get; } = [];

        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot)
        {
            lock (_sync)
            {
                TargetSnapshots.Add(snapshot);
                CompleteSatisfiedWaiters();
            }
        }

        public Task WaitForWaitingGateAsync(DocumentCacheProjectionTargetContextKey contextKey)
        {
            lock (_sync)
            {
                if (HasWaitingGateSnapshot(contextKey))
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new Waiter(contextKey, waiter));
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

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

        private bool HasWaitingGateSnapshot(DocumentCacheProjectionTargetContextKey contextKey) =>
            TargetSnapshots.Exists(snapshot =>
                snapshot.ContextKey == contextKey && snapshot.ExecutionState.IsWaitingForWorkerGate
            );

        private void CompleteSatisfiedWaiters()
        {
            foreach (
                Waiter waiter in _waiters.Where(waiter => HasWaitingGateSnapshot(waiter.ContextKey)).ToArray()
            )
            {
                waiter.Completion.SetResult();
                _waiters.Remove(waiter);
            }
        }

        private sealed record Waiter(
            DocumentCacheProjectionTargetContextKey ContextKey,
            TaskCompletionSource Completion
        );
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
