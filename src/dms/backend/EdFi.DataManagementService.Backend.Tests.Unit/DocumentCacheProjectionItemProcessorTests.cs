// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
[Category("DocumentCacheProjectionItemProcessor")]
public class Given_DocumentCacheProjectionItemProcessor
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
    public async Task It_calls_the_writer_fast_path_first_with_null_candidate_and_clears_failure_on_acknowledgement()
    {
        RecordingDocumentCacheWriter writer = new(
            new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(10)
        );
        RecordingDocumentCacheMaterializer materializer = new();
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(materializer, writer);
        targetContext.FailureBackoffState.RecordFailure(
            101,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "previous failure",
            ObservedAt.AddSeconds(-1),
            TimeSpan.FromSeconds(10)
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.Continue);
        result.AcknowledgedOrRemovedDurableWork.Should().BeTrue();
        result.DocumentScopedFailureRecorded.Should().BeFalse();
        targetContext.FailureBackoffState.Count.Should().Be(0);
        materializer.Calls.Should().BeEmpty();
        DocumentCacheWriterRequest writerRequest = writer.Calls.Should().ContainSingle().Subject;
        writerRequest.Candidate.Should().BeNull();
        writerRequest.Purpose.Should().Be(DocumentCacheWriterPurpose.DurableWorkProjection);
        writerRequest.SelectedRequiredContentVersion.Should().Be(10);
        writerRequest.TargetContext.Should().BeSameAs(targetContext.MaterializationTargetContext);
    }

    [Test]
    public async Task It_materializes_only_when_the_writer_requests_materialization_and_then_writes_the_candidate()
    {
        DocumentCacheMaterializationCandidate candidate = Candidate(101, contentVersion: 11);
        RecordingDocumentCacheWriter writer = new(
            new DocumentCacheWriterResult.NeedsMaterialization(11),
            new DocumentCacheWriterResult.CandidateWrittenAcknowledged(candidate, 11)
        );
        RecordingDocumentCacheMaterializer materializer = new(
            new DocumentCacheMaterializationResult.Success(candidate)
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(materializer, writer);

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 11)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.Continue);
        result.AcknowledgedOrRemovedDurableWork.Should().BeTrue();
        result.DocumentScopedFailureRecorded.Should().BeFalse();
        materializer
            .Calls.Should()
            .ContainSingle()
            .Which.Purpose.Should()
            .Be(DocumentCacheMaterializationPurpose.DurableWorkProjection);
        writer.Calls.Should().HaveCount(2);
        writer.Calls[0].Candidate.Should().BeNull();
        writer.Calls[1].Candidate.Should().BeSameAs(candidate);
        writer.Calls[1].Purpose.Should().Be(DocumentCacheWriterPurpose.DurableWorkProjection);
    }

    [TestCaseSource(nameof(MaterializerNoCandidateOutcomes))]
    public async Task It_leaves_work_visible_when_materialization_produces_no_candidate(
        DocumentCacheMaterializationResult materializationResult
    )
    {
        RecordingDocumentCacheWriter writer = new(new DocumentCacheWriterResult.NeedsMaterialization(10));
        RecordingDocumentCacheMaterializer materializer = new(materializationResult);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(materializer, writer);

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.Continue);
        result.AcknowledgedOrRemovedDurableWork.Should().BeFalse();
        result.DocumentScopedFailureRecorded.Should().BeFalse();
        writer.Calls.Should().ContainSingle();
        targetContext.FailureBackoffState.Count.Should().Be(0);
    }

    [TestCaseSource(nameof(ContinuingWriterOutcomes))]
    public async Task It_continues_without_projector_repair_for_writer_outcomes_that_leave_work_to_durable_state(
        DocumentCacheWriterResult writerResult
    )
    {
        RecordingDocumentCacheWriter writer = new(writerResult);
        RecordingDocumentCacheMaterializer materializer = new();
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(materializer, writer);

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.Continue);
        result.AcknowledgedOrRemovedDurableWork.Should().BeFalse();
        result.DocumentScopedFailureRecorded.Should().BeFalse();
        materializer.Calls.Should().BeEmpty();
        targetContext.FailureBackoffState.Count.Should().Be(0);
    }

    [Test]
    public async Task It_records_document_backoff_for_work_anomaly_and_continues()
    {
        RecordingDocumentCacheWriter writer = new(
            new DocumentCacheWriterResult.WorkAnomaly(
                DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch,
                DocumentCacheLifecycleState.Tracking,
                currentSourceContentVersion: 10,
                workRequiredContentVersion: 11
            )
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 11)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.Continue);
        result.AcknowledgedOrRemovedDurableWork.Should().BeFalse();
        result.DocumentScopedFailureRecorded.Should().BeTrue();
        DocumentCacheProjectionDocumentDiagnostic diagnostic = targetContext
            .FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Should()
            .ContainSingle()
            .Subject;
        diagnostic.Category.Should().Be(DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly);
        diagnostic.NextRetryAt.Should().Be(ObservedAt.AddSeconds(10));
    }

    [Test]
    public async Task It_records_writer_retry_exhaustion_as_document_backoff_and_continues()
    {
        RecordingDocumentCacheWriter writer = new(new DocumentCacheWriterResult.RetryBudgetExhausted(3));
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.Continue);
        result.AcknowledgedOrRemovedDurableWork.Should().BeFalse();
        result.DocumentScopedFailureRecorded.Should().BeTrue();
        targetContext
            .FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome);
    }

    [Test]
    public async Task It_returns_lifecycle_fenced_without_document_backoff()
    {
        RecordingDocumentCacheWriter writer = new(
            new DocumentCacheWriterResult.LifecycleOrLatchFenced(
                DocumentCacheWriterFenceReason.LifecycleNotEligible,
                DocumentCacheLifecycleState.Resetting,
                cacheAheadRecoveryRequired: false
            )
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Should().BeSameAs(DocumentCacheProjectionItemProcessResult.LifecycleFenced);
        targetContext.FailureBackoffState.Count.Should().Be(0);
    }

    [Test]
    public async Task It_pauses_the_target_when_the_writer_sets_the_cache_ahead_latch()
    {
        RecordingDocumentCacheWriter writer = new(new DocumentCacheWriterResult.CacheAheadLatchSet(10, 11));
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Should().BeSameAs(DocumentCacheProjectionItemProcessResult.TargetPaused);
        targetContext.SchedulingState.IsTargetPaused.Should().BeTrue();
        targetContext
            .FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(DocumentCacheProjectionDocumentDiagnosticCategory.WriterOutcome);
    }

    [Test]
    public async Task It_pauses_the_target_for_deterministic_writer_invariant_failures()
    {
        RecordingDocumentCacheWriter writer = new(
            new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                DocumentCacheWriterInvariantFailureReason.TargetMappingMismatch,
                currentContentVersion: 10,
                candidateContentVersion: 10
            )
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Should().BeSameAs(DocumentCacheProjectionItemProcessResult.TargetPaused);
        targetContext.SchedulingState.IsTargetPaused.Should().BeTrue();
        targetContext
            .FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(DocumentCacheProjectionDocumentDiagnosticCategory.DeterministicInvariantFailure);
    }

    [Test]
    public async Task It_pauses_the_target_for_deterministic_materializer_failures()
    {
        RecordingDocumentCacheWriter writer = new(new DocumentCacheWriterResult.NeedsMaterialization(10));
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(
                new DocumentCacheProjectionProcessingException(
                    DocumentCacheProjectionProcessingFailureReason.DocumentJsonNotObject,
                    FailureMetadata(documentId: 101)
                )
            ),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Should().BeSameAs(DocumentCacheProjectionItemProcessResult.TargetPaused);
        targetContext.SchedulingState.IsTargetPaused.Should().BeTrue();
        targetContext
            .FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .DocumentDiagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(DocumentCacheProjectionDocumentDiagnosticCategory.DeterministicInvariantFailure);
    }

    [Test]
    public async Task It_applies_target_backoff_for_provider_or_runtime_failures()
    {
        RecordingDocumentCacheWriter writer = new(new InvalidOperationException("connection lost"));
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            new RecordingDocumentCacheMaterializer(),
            writer
        );

        DocumentCacheProjectionItemProcessResult result = await CreateProcessor()
            .ProcessItemAsync(Request(targetContext, WorkItem(101, requiredContentVersion: 10)));

        result.Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.TargetBackoff);
        result.BackoffUntil.Should().Be(ObservedAt.AddSeconds(10));
        targetContext.FailureBackoffState.Count.Should().Be(0);
    }

    private static IEnumerable<TestCaseData> MaterializerNoCandidateOutcomes()
    {
        yield return new TestCaseData(DocumentCacheMaterializationResult.MissingSource.Instance).SetName(
            "MissingSource"
        );
        yield return new TestCaseData(
            DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance
        ).SetName("SourceChangedDuringHydration");
    }

    private static IEnumerable<TestCaseData> ContinuingWriterOutcomes()
    {
        yield return new TestCaseData(DocumentCacheWriterResult.SourceMissingOrDeleted.Instance).SetName(
            "SourceMissingOrDeleted"
        );
        yield return new TestCaseData(new DocumentCacheWriterResult.StaleCandidateSuppressed(11, 10)).SetName(
            "StaleCandidateSuppressed"
        );
        yield return new TestCaseData(DocumentCacheWriterResult.CacheAheadDisappeared.Instance).SetName(
            "CacheAheadDisappeared"
        );
        yield return new TestCaseData(DocumentCacheWriterResult.RacingWriterLost.Instance).SetName(
            "RacingWriterLost"
        );
    }

    private static DocumentCacheProjectionItemProcessor CreateProcessor() =>
        new(new FixedTimeProvider(ObservedAt), NullLogger<DocumentCacheProjectionItemProcessor>.Instance);

    private static DocumentCacheProjectionItemProcessRequest Request(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentProjectionWorkPageItem workItem
    ) => new(targetContext, workItem, DocumentCacheProjectionDrainInvocationKind.Ordinary);

    private static DocumentProjectionWorkPageItem WorkItem(long documentId, long requiredContentVersion) =>
        new(documentId, requiredContentVersion, FirstEnqueuedAt, FirstEnqueuedAt.AddSeconds(5));

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        IDocumentCacheMaterializer materializer,
        IDocumentCacheWriter writer
    )
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
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

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                MaterializationTargetContext(targetKey),
                materializer,
                writer
            ),
            new NoOpObservationSink()
        );
    }

    private static DocumentCacheMaterializationCandidate Candidate(long documentId, long contentVersion) =>
        new(
            documentId,
            new DocumentUuid(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            "Ed-Fi",
            "School",
            "5.2.0",
            contentVersion,
            ObservedAt,
            $"etag-{contentVersion}",
            new JsonObject { ["id"] = documentId }
        );

    private static DocumentCacheMaterializerFailureMetadata FailureMetadata(long documentId) =>
        new(
            MaterializationTargetContext(DocumentCacheTargetKey.Create("Tenant-A", 1)).TargetKey,
            MappingSet().Key,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            documentId
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

    private sealed class RecordingDocumentCacheWriter(params object[] results) : IDocumentCacheWriter
    {
        private readonly Queue<object> _results = new(results);

        public List<DocumentCacheWriterRequest> Calls { get; } = [];

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
        {
            Calls.Add(request);
            object result = _results.Dequeue();
            if (result is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult((DocumentCacheWriterResult)result);
        }
    }

    private sealed class RecordingDocumentCacheMaterializer(params object[] results)
        : IDocumentCacheMaterializer
    {
        private readonly Queue<object> _results = new(results);

        public List<DocumentCacheMaterializationRequest> Calls { get; } = [];

        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            Calls.Add(request);
            object result = _results.Dequeue();
            if (result is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult((DocumentCacheMaterializationResult)result);
        }
    }

    private sealed class NoOpObservationSink : IDocumentCacheProjectionObservationSink
    {
        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
