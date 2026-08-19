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
[Category("DocumentCacheProjectionFailureBackoff")]
public class Given_DocumentCacheProjectionFailureBackoff
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAt.AddSeconds(-30);
    private static readonly DateTimeOffset LaterEnqueuedAt = FirstEnqueuedAt.AddSeconds(1);

    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public void It_caps_failure_state_at_page_size_and_evicts_by_oldest_observation_then_document_id()
    {
        DocumentCacheProjectionFailureBackoffState state = new(capacity: 2);

        state.RecordFailure(
            20,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "first tied failure",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );
        state.RecordFailure(
            10,
            DocumentCacheProjectionDocumentDiagnosticCategory.ProviderFailure,
            "second tied failure",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );
        state.RecordFailure(
            30,
            DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome,
            "newer failure",
            ObservedAt.AddSeconds(1),
            TimeSpan.FromSeconds(10)
        );

        DocumentCacheProjectionFailureDiagnostics snapshot = state.CreateFailureDiagnosticsSnapshot();

        state.Count.Should().Be(2);
        state.EvictionCount.Should().Be(1);
        state.IsSuppressed(10, ObservedAt).Should().BeFalse();
        state.IsSuppressed(20, ObservedAt).Should().BeTrue();
        snapshot.FailureCount.Should().Be(2);
        snapshot.EvictionCount.Should().Be(1);
        snapshot.DocumentIds.Should().Equal(20, 30);
    }

    [Test]
    public void It_calculates_next_retry_from_the_observed_time_plus_failure_backoff_without_jitter()
    {
        DocumentCacheProjectionFailureBackoffState state = new(capacity: 3);
        TimeSpan failureBackoff = TimeSpan.FromSeconds(17);

        state.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "work anomaly",
            ObservedAt,
            failureBackoff
        );

        DocumentCacheProjectionFailureDiagnostics snapshot = state.CreateFailureDiagnosticsSnapshot();

        snapshot.EarliestRetryAt.Should().Be(ObservedAt + failureBackoff);
        snapshot
            .DocumentDiagnostics.Should()
            .ContainSingle()
            .Which.NextRetryAt.Should()
            .Be(ObservedAt + failureBackoff);
    }

    [Test]
    public void It_records_retry_scheduled_events_when_failures_enter_backoff()
    {
        DocumentCacheProjectionFailureBackoffState state = new(capacity: 2);

        state.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "first work anomaly",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );
        state.RecordFailure(
            102,
            DocumentCacheProjectionDocumentDiagnosticCategory.ProviderFailure,
            "second provider failure",
            ObservedAt.AddSeconds(1),
            TimeSpan.FromSeconds(20)
        );
        state.RecordFailure(
            103,
            DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome,
            "third writer outcome",
            ObservedAt.AddSeconds(2),
            TimeSpan.FromSeconds(30)
        );

        DocumentCacheProjectionPoisonTraversalSnapshot snapshot = state.CreatePoisonTraversalSnapshot();

        snapshot.DiagnosticEvents.Select(diagnostic => diagnostic.DocumentId).Should().Equal(102, 103);
        snapshot
            .DiagnosticEvents.Select(diagnostic => diagnostic.Category)
            .Should()
            .AllBeEquivalentTo(DocumentCacheProjectionPoisonTraversalDiagnosticCategory.RetryScheduled);
        snapshot
            .DiagnosticEvents.Select(diagnostic => diagnostic.ObservedAt)
            .Should()
            .Equal(ObservedAt.AddSeconds(1), ObservedAt.AddSeconds(2));
        snapshot
            .DiagnosticEvents.Select(diagnostic => diagnostic.NextRetryAt)
            .Should()
            .Equal(ObservedAt.AddSeconds(21), ObservedAt.AddSeconds(32));
        snapshot.DiagnosticEventEvictionCount.Should().Be(1);
    }

    [Test]
    public void It_clears_failure_state_for_a_document_success()
    {
        DocumentCacheProjectionFailureBackoffState state = new(capacity: 3);
        state.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "work anomaly",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );

        bool removed = state.ClearFailure(101);

        removed.Should().BeTrue();
        state.Count.Should().Be(0);
        state.CreateFailureDiagnosticsSnapshot().DocumentIds.Should().BeEmpty();
    }

    [Test]
    public async Task It_skips_suppressed_rows_advances_the_cursor_and_processes_later_eligible_rows()
    {
        RecordingObservationSink observationSink = new();
        ScriptedWorkPager pager = new(
            RelationalProviderToken.Postgresql,
            [
                Page(
                    WorkItem(101, requiredContentVersion: 10, FirstEnqueuedAt),
                    WorkItem(102, requiredContentVersion: 11, LaterEnqueuedAt)
                ),
            ]
        );
        DocumentCacheProjectionDrainPageProcessor sut = CreateProcessor(pager);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            RelationalProviderToken.Postgresql,
            observationSink: observationSink
        );
        targetContext.FailureBackoffState.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "work anomaly",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );

        DocumentCacheProjectionDrainPageResult result = await sut.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                targetContext,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );

        result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        result.ProcessedItemCount.Should().Be(1);
        targetContext.Cursor.LastDocumentId.Should().Be(102);
        targetContext.Cursor.LastFirstEnqueuedAt.Should().Be(LaterEnqueuedAt);

        DocumentCacheProjectionTargetHealthSnapshot snapshot = observationSink
            .TargetSnapshots.Should()
            .ContainSingle()
            .Subject;
        snapshot.FailureDiagnostics.FailureCount.Should().Be(1);
        snapshot.FailureDiagnostics.EarliestRetryAt.Should().Be(ObservedAt.AddSeconds(10));
        snapshot.FailureDiagnostics.DocumentIds.Should().Equal(101);
        snapshot.PoisonTraversal.SuppressedDocumentCount.Should().Be(1);
        snapshot.PoisonTraversal.EarliestRetryAt.Should().Be(ObservedAt.AddSeconds(10));
        snapshot.PoisonTraversal.SuppressedDocumentIds.Should().Equal(101);
        snapshot
            .PoisonTraversal.DiagnosticEvents.Select(diagnostic => diagnostic.Category)
            .Should()
            .Equal(
                DocumentCacheProjectionPoisonTraversalDiagnosticCategory.RetryScheduled,
                DocumentCacheProjectionPoisonTraversalDiagnosticCategory.SkippedUntilRetry
            );
        snapshot
            .PoisonTraversal.DiagnosticEvents.Select(diagnostic => diagnostic.DocumentId)
            .Should()
            .Equal(101, 101);
        snapshot.PoisonTraversal.DiagnosticEventEvictionCount.Should().Be(0);
        snapshot
            .PoisonTraversal.DiagnosticEvents.Should()
            .NotContain(diagnostic =>
                diagnostic.Category
                == DocumentCacheProjectionPoisonTraversalDiagnosticCategory.PageCapacityExhausted
            );
    }

    [Test]
    public async Task It_records_page_capacity_exhausted_only_when_suppressed_rows_fill_a_page()
    {
        RecordingObservationSink observationSink = new();
        ScriptedWorkPager pager = new(
            RelationalProviderToken.Postgresql,
            [
                Page(
                    WorkItem(101, requiredContentVersion: 10, FirstEnqueuedAt),
                    WorkItem(102, requiredContentVersion: 11, LaterEnqueuedAt),
                    WorkItem(103, requiredContentVersion: 12, LaterEnqueuedAt.AddSeconds(1))
                ),
                Page(WorkItem(201, requiredContentVersion: 20, FirstEnqueuedAt)),
            ]
        );
        DocumentCacheProjectionDrainPageProcessor sut = CreateProcessor(pager);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            RelationalProviderToken.Postgresql,
            observationSink: observationSink
        );
        targetContext.FailureBackoffState.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "work anomaly",
            ObservedAt,
            TimeSpan.FromSeconds(10)
        );
        targetContext.FailureBackoffState.RecordFailure(
            102,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "work anomaly",
            ObservedAt,
            TimeSpan.FromSeconds(20)
        );

        DocumentCacheProjectionDrainPageResult fullPageResult = await sut.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                targetContext,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );
        DocumentCacheProjectionDrainPageResult partialPageResult = await sut.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                targetContext,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );

        fullPageResult.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        fullPageResult.ProcessedItemCount.Should().Be(1);
        partialPageResult.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);

        DocumentCacheProjectionPoisonTraversalSnapshot firstSnapshot = observationSink
            .TargetSnapshots[0]
            .PoisonTraversal;
        firstSnapshot
            .DiagnosticEvents.Select(diagnostic => diagnostic.Category)
            .Should()
            .Equal(
                DocumentCacheProjectionPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
                DocumentCacheProjectionPoisonTraversalDiagnosticCategory.SkippedUntilRetry,
                DocumentCacheProjectionPoisonTraversalDiagnosticCategory.PageCapacityExhausted
            );
        firstSnapshot
            .DiagnosticEvents.Select(diagnostic => diagnostic.DocumentId)
            .Should()
            .Equal(101, 102, 102);
        firstSnapshot.DiagnosticEventEvictionCount.Should().Be(2);

        DocumentCacheProjectionPoisonTraversalSnapshot secondSnapshot = observationSink
            .TargetSnapshots[1]
            .PoisonTraversal;
        secondSnapshot
            .DiagnosticEvents.Should()
            .NotContain(diagnostic =>
                diagnostic.DocumentId == 201
                && diagnostic.Category
                    == DocumentCacheProjectionPoisonTraversalDiagnosticCategory.PageCapacityExhausted
            );
    }

    [Test]
    public async Task It_sleeps_until_the_earlier_retry_after_a_wrapped_cursor_pass_without_eligible_work()
    {
        TimeSpan failureBackoff = TimeSpan.FromSeconds(2);
        TimeSpan pollInterval = TimeSpan.FromSeconds(5);
        FixedTimeProvider timeProvider = new(ObservedAt);
        RecordingObservationSink observationSink = new();
        ScriptedWorkPager pager = new(
            RelationalProviderToken.Postgresql,
            [Page(WorkItem(101, requiredContentVersion: 10, FirstEnqueuedAt)), Page()]
        );
        DocumentCacheProjectionDrainPageProcessor processor = CreateProcessor(pager, timeProvider);
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            processor,
            observationSink,
            timeProvider,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            RelationalProviderToken.Postgresql,
            pollInterval,
            failureBackoff,
            observationSink
        );
        targetContext.FailureBackoffState.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "work anomaly",
            ObservedAt,
            failureBackoff
        );

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> firstPass =
            await scheduler.RunReadyTargetsOnceAsync([targetContext]);
        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> secondPass =
            await scheduler.RunReadyTargetsOnceAsync([targetContext]);

        firstPass.Should().ContainSingle().Which.DrainResult!.ProcessedItemCount.Should().Be(0);
        DocumentCacheProjectionDrainPageResult secondDrainResult = secondPass
            .Should()
            .ContainSingle()
            .Subject.DrainResult!;
        secondDrainResult.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.NoEligibleWork);
        secondDrainResult.NextRetryAt.Should().Be(ObservedAt + failureBackoff);
        targetContext.SchedulingState.PollSleepUntil.Should().Be(ObservedAt + failureBackoff);
        pager.Calls.Should().HaveCount(2);
    }

    private static DocumentCacheProjectionScheduler CreateScheduler(
        IDocumentCacheProjectionDrainPageProcessor drainPageProcessor,
        IDocumentCacheProjectionObservationSink observationSink,
        TimeProvider timeProvider,
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
            timeProvider,
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );
    }

    private static DocumentCacheProjectionDrainPageProcessor CreateProcessor(
        IDocumentProjectionWorkPager pager
    ) => CreateProcessor(pager, new FixedTimeProvider(ObservedAt));

    private static DocumentCacheProjectionDrainPageProcessor CreateProcessor(
        IDocumentProjectionWorkPager pager,
        TimeProvider timeProvider
    ) =>
        new(
            pager,
            new AcknowledgingItemProcessor(),
            NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
            timeProvider
        );

    private static DocumentProjectionWorkPage Page(params DocumentProjectionWorkPageItem[] items) =>
        new(items, pageSize: 3);

    private static DocumentProjectionWorkPageItem WorkItem(
        long documentId,
        long requiredContentVersion,
        DateTimeOffset firstEnqueuedAt
    ) => new(documentId, requiredContentVersion, firstEnqueuedAt, firstEnqueuedAt.AddSeconds(5));

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        RelationalProviderToken providerToken,
        TimeSpan? pollInterval = null,
        TimeSpan? failureBackoff = null,
        IDocumentCacheProjectionObservationSink? observationSink = null
    )
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: pollInterval ?? TimeSpan.FromSeconds(5),
                projectorPageSize: 3,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: failureBackoff ?? TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, providerToken.Value),
            new DocumentCacheTargetConnectionInput(providerToken, "connection"),
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

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                providerToken,
                MaterializationTargetContext(targetKey, providerToken),
                new StubDocumentCacheMaterializer(),
                new StubDocumentCacheWriter()
            ),
            observationSink ?? new RecordingObservationSink()
        );
    }

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken
    ) =>
        new(
            new DocumentCacheProjectionTargetKey(targetKey.TenantKey, new DataStoreId(targetKey.DataStoreId)),
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

    private sealed class AcknowledgingItemProcessor : IDocumentCacheProjectionItemProcessor
    {
        public Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
            DocumentCacheProjectionItemProcessRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.TargetContext.FailureBackoffState.ClearFailure(request.WorkItem.DocumentId);
            return Task.FromResult(DocumentCacheProjectionItemProcessResult.Continue);
        }
    }

    private sealed class ScriptedWorkPager(
        RelationalProviderToken providerToken,
        IEnumerable<DocumentProjectionWorkPage> pages
    ) : IDocumentProjectionWorkPager
    {
        private readonly Queue<DocumentProjectionWorkPage> _pages = new(pages);

        public RelationalProviderToken ProviderToken { get; } = providerToken;

        public List<PagingCall> Calls { get; } = [];

        public Task<DocumentProjectionWorkPage> ReadPageAsync(
            DocumentProjectionWorkPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                new PagingCall(
                    request.Cursor.HasValue,
                    request.Cursor.LastFirstEnqueuedAt,
                    request.Cursor.LastDocumentId,
                    request.PageSize
                )
            );

            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed record PagingCall(
        bool HasCursor,
        DateTimeOffset? LastFirstEnqueuedAt,
        long? LastDocumentId,
        int PageSize
    );

    private sealed class RecordingObservationSink : IDocumentCacheProjectionObservationSink
    {
        public List<DocumentCacheProjectionTargetHealthSnapshot> TargetSnapshots { get; } = [];

        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) =>
            TargetSnapshots.Add(snapshot);

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
