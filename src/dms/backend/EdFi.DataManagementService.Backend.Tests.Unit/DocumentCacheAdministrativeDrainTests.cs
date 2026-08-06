// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
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
[Category("DocumentCacheAdministrativeDrain")]
public class Given_DocumentCacheAdministrativeDrain
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAt.AddSeconds(-30);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheLifecycleObservation RebuildingLifecycle = new(
        DocumentCacheLifecycleState.Rebuilding,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public async Task It_completes_only_after_durable_work_is_observed_empty()
    {
        RecordingScheduler scheduler = new(
            DocumentCacheProjectionDrainPageResult.NoEligibleWork,
            DocumentCacheProjectionDrainPageResult.NoEligibleWork
        );
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: false,
                "work remains"
            ),
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: true,
                "empty"
            )
        );
        RecordingDrainDelay delay = new(new MutableTimeProvider(ObservedAt));
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease(),
            RuntimeContext()
        );

        DocumentCacheAdministrativeDrainToEmptyResult result = await CreateDrainer(scheduler, delay)
            .DrainToEmptyAsync(context);

        result.Completed.Should().BeTrue();
        result.DrainSliceCount.Should().Be(2);
        primitives.EmptinessReadCount.Should().Be(2);
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(5));
        context.CurrentPhase.Should().Be(DocumentCacheAdministrativeCommandPhase.DrainWork);
        context.LastCompletedPhase.Should().Be(DocumentCacheAdministrativeCommandPhase.DrainWork);
    }

    [Test]
    public async Task It_delays_after_a_completed_cursor_pass_with_no_acknowledgements_or_failures()
    {
        MutableTimeProvider timeProvider = new(ObservedAt);
        var pager = new RecordingWorkPager(
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3)
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext();
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: true,
                "empty"
            )
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease(),
            targetContext
        );
        DocumentCacheProjectionScheduler scheduler = CreateRealScheduler(
            new DocumentCacheProjectionDrainPageProcessor(
                pager,
                new ScriptedItemProcessor(
                    DocumentCacheProjectionItemProcessResult.Continue,
                    DocumentCacheProjectionItemProcessResult.Continue
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                timeProvider
            )
        );
        RecordingDrainDelay delay = new(timeProvider);

        DocumentCacheAdministrativeDrainToEmptyResult result = await CreateDrainer(
                scheduler,
                delay,
                timeProvider
            )
            .DrainToEmptyAsync(context);

        result.Completed.Should().BeTrue();
        result.ProcessedItemCount.Should().Be(2);
        result.AcknowledgedOrRemovedItemCount.Should().Be(0);
        result.DocumentScopedFailureCount.Should().Be(0);
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(5));
        pager.Requests.Should().HaveCount(5);
    }

    [Test]
    public async Task It_does_not_add_a_guard_delay_when_the_next_page_makes_durable_progress()
    {
        MutableTimeProvider timeProvider = new(ObservedAt);
        var pager = new RecordingWorkPager(
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3)
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext();
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: true,
                "empty"
            )
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease(),
            targetContext
        );
        DocumentCacheProjectionScheduler scheduler = CreateRealScheduler(
            new DocumentCacheProjectionDrainPageProcessor(
                pager,
                new ScriptedItemProcessor(
                    DocumentCacheProjectionItemProcessResult.Continue,
                    DocumentCacheProjectionItemProcessResult.Continue,
                    DocumentCacheProjectionItemProcessResult.AcknowledgedOrRemoved
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                timeProvider
            )
        );
        RecordingDrainDelay delay = new(timeProvider);

        DocumentCacheAdministrativeDrainToEmptyResult result = await CreateDrainer(
                scheduler,
                delay,
                timeProvider
            )
            .DrainToEmptyAsync(context);

        result.Completed.Should().BeTrue();
        result.ProcessedItemCount.Should().Be(3);
        result.AcknowledgedOrRemovedItemCount.Should().Be(1);
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(5));
        pager.Requests.Should().HaveCount(7);
    }

    [Test]
    public async Task It_uses_the_session_bound_writer_fast_path_for_equal_version_work()
    {
        var pager = new RecordingWorkPager(
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3)
        );
        var materializer = new RecordingDocumentCacheMaterializer();
        var ordinaryWriter = new RecordingDocumentCacheWriter();
        var sessionBoundWriter = new RecordingSessionBoundWriter(
            DocumentCacheSessionBoundWriterResult.FromWriterResult(
                new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(10),
                commandExecutionMutated: false
            )
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            materializer,
            ordinaryWriter,
            sessionBoundWriter
        );
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: true,
                "empty"
            )
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease(),
            targetContext
        );
        DocumentCacheProjectionScheduler scheduler = CreateRealScheduler(
            new DocumentCacheProjectionDrainPageProcessor(
                pager,
                new DocumentCacheProjectionItemProcessor(
                    new MutableTimeProvider(ObservedAt),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new MutableTimeProvider(ObservedAt)
            )
        );
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(
            scheduler,
            new RecordingDrainDelay(new MutableTimeProvider(ObservedAt))
        );

        DocumentCacheAdministrativeDrainToEmptyResult result =
            await targetContext.DrainExecutor.RunAdministrativeCommandAsync(async cancellationToken =>
            {
                using IDisposable binding = targetContext.BindAdministrativeCommand(context);
                return await drainer.DrainToEmptyAsync(context, cancellationToken).ConfigureAwait(false);
            });

        result.Completed.Should().BeTrue();
        result.AcknowledgedOrRemovedItemCount.Should().Be(1);
        context.Mutated.Should().BeTrue();
        ordinaryWriter.Calls.Should().BeEmpty();
        materializer.Calls.Should().BeEmpty();
        DocumentCacheSessionBoundWriterRequest writerRequest = sessionBoundWriter
            .Calls.Should()
            .ContainSingle()
            .Subject;
        writerRequest.MutexLease.Should().BeSameAs(context.MutexLease);
        writerRequest.WriterRequest.Candidate.Should().BeNull();
        writerRequest.WriterRequest.SelectedRequiredContentVersion.Should().Be(10);
    }

    [Test]
    public async Task It_does_not_mark_mutated_or_acknowledged_when_selected_work_disappears_before_writer_classification()
    {
        var pager = new RecordingWorkPager(
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3),
            new DocumentProjectionWorkPage([], pageSize: 3)
        );
        var materializer = new RecordingDocumentCacheMaterializer();
        var ordinaryWriter = new RecordingDocumentCacheWriter();
        var sessionBoundWriter = new RecordingSessionBoundWriter(
            DocumentCacheSessionBoundWriterResult.FromWriterResult(
                new DocumentCacheWriterResult.AlreadyCurrentNoWork(10),
                commandExecutionMutated: false
            )
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            materializer,
            ordinaryWriter,
            sessionBoundWriter
        );
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: true,
                "empty"
            )
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease(),
            targetContext
        );
        DocumentCacheProjectionScheduler scheduler = CreateRealScheduler(
            new DocumentCacheProjectionDrainPageProcessor(
                pager,
                new DocumentCacheProjectionItemProcessor(
                    new MutableTimeProvider(ObservedAt),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new MutableTimeProvider(ObservedAt)
            )
        );
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(
            scheduler,
            new RecordingDrainDelay(new MutableTimeProvider(ObservedAt))
        );

        DocumentCacheAdministrativeDrainToEmptyResult result =
            await targetContext.DrainExecutor.RunAdministrativeCommandAsync(async cancellationToken =>
            {
                using IDisposable binding = targetContext.BindAdministrativeCommand(context);
                return await drainer.DrainToEmptyAsync(context, cancellationToken).ConfigureAwait(false);
            });

        result.Completed.Should().BeTrue();
        result.ProcessedItemCount.Should().Be(1);
        result.AcknowledgedOrRemovedItemCount.Should().Be(0);
        context.Mutated.Should().BeFalse();
        ordinaryWriter.Calls.Should().BeEmpty();
        materializer.Calls.Should().BeEmpty();
        sessionBoundWriter.Calls.Should().ContainSingle();
    }

    [Test]
    public async Task It_marks_command_mutated_before_session_bound_writer_reports_session_loss()
    {
        var pager = new RecordingWorkPager(
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3)
        );
        var materializer = new RecordingDocumentCacheMaterializer();
        var ordinaryWriter = new RecordingDocumentCacheWriter();
        var sessionBoundWriter = new SessionLosingAfterMutationWriter();
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            materializer,
            ordinaryWriter,
            sessionBoundWriter
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            new RecordingAdministrativePrimitives(),
            new RecordingMutexLease(),
            targetContext
        );
        DocumentCacheProjectionScheduler scheduler = CreateRealScheduler(
            new DocumentCacheProjectionDrainPageProcessor(
                pager,
                new DocumentCacheProjectionItemProcessor(
                    new MutableTimeProvider(ObservedAt),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new MutableTimeProvider(ObservedAt)
            )
        );
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(
            scheduler,
            new RecordingDrainDelay(new MutableTimeProvider(ObservedAt))
        );

        DocumentCacheAdministrativeDrainToEmptyResult result =
            await targetContext.DrainExecutor.RunAdministrativeCommandAsync(async cancellationToken =>
            {
                using IDisposable binding = targetContext.BindAdministrativeCommand(context);
                return await drainer.DrainToEmptyAsync(context, cancellationToken).ConfigureAwait(false);
            });

        result.Completed.Should().BeFalse();
        result
            .FailureResult!.Status.Should()
            .Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .FailureResult.Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        result.FailureResult.Mutated.Should().BeTrue();
        context.Mutated.Should().BeTrue();
        sessionBoundWriter.Calls.Should().ContainSingle();
        ordinaryWriter.Calls.Should().BeEmpty();
        materializer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_immediately_when_session_bound_writer_delete_race_retry_is_exhausted()
    {
        var pager = new RecordingWorkPager(
            new DocumentProjectionWorkPage([WorkItem(101, requiredContentVersion: 10)], pageSize: 3)
        );
        var materializer = new RecordingDocumentCacheMaterializer();
        var ordinaryWriter = new RecordingDocumentCacheWriter();
        var sessionBoundWriter = new RecordingSessionBoundWriter(
            DocumentCacheSessionBoundWriterResult.FromWriterResult(
                new DocumentCacheWriterResult.DeleteRaceRetryExhausted(attemptCount: 3),
                commandExecutionMutated: false
            )
        );
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            materializer,
            ordinaryWriter,
            sessionBoundWriter
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            new RecordingAdministrativePrimitives(),
            new RecordingMutexLease(),
            targetContext
        );
        DocumentCacheProjectionScheduler scheduler = CreateRealScheduler(
            new DocumentCacheProjectionDrainPageProcessor(
                pager,
                new DocumentCacheProjectionItemProcessor(
                    new MutableTimeProvider(ObservedAt),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new MutableTimeProvider(ObservedAt)
            )
        );
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(
            scheduler,
            new RecordingDrainDelay(new MutableTimeProvider(ObservedAt))
        );

        DocumentCacheAdministrativeDrainToEmptyResult result =
            await targetContext.DrainExecutor.RunAdministrativeCommandAsync(async cancellationToken =>
            {
                using IDisposable binding = targetContext.BindAdministrativeCommand(context);
                return await drainer.DrainToEmptyAsync(context, cancellationToken).ConfigureAwait(false);
            });

        result.Completed.Should().BeFalse();
        result.DrainSliceCount.Should().Be(1);
        result.ProcessedItemCount.Should().Be(1);
        result.DocumentScopedFailureCount.Should().Be(0);
        result.FailureResult!.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .FailureResult.Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.WriterRetryBudgetExhausted);
        result
            .FailureResult.PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WriterRetryBudgetExhausted
                && diagnostic.AffectedDocumentIds.SequenceEqual(new long[] { 101L })
            );
        result.FailureResult.Mutated.Should().BeFalse();
        targetContext.FailureBackoffState.Count.Should().Be(0);
        ordinaryWriter.Calls.Should().BeEmpty();
        materializer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_classifies_persistent_poison_after_a_retry_due_pass_repeats_document_failures()
    {
        MutableTimeProvider timeProvider = new(ObservedAt);
        RecordingScheduler scheduler = new(
            DocumentCacheProjectionDrainPageResult.PageProcessed(
                processedItemCount: 1,
                acknowledgedOrRemovedItemCount: 0,
                documentScopedFailureCount: 1,
                documentScopedFailureIds: [101]
            ),
            DocumentCacheProjectionDrainPageResult.NoEligibleWorkWithRetry(ObservedAt.AddSeconds(10)),
            DocumentCacheProjectionDrainPageResult.PageProcessed(
                processedItemCount: 1,
                acknowledgedOrRemovedItemCount: 0,
                documentScopedFailureCount: 1,
                documentScopedFailureIds: [101]
            ),
            DocumentCacheProjectionDrainPageResult.NoEligibleWorkWithRetry(ObservedAt.AddSeconds(20))
        );
        var primitives = new RecordingAdministrativePrimitives(
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: false,
                "work remains"
            ),
            new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                documentCacheEmpty: true,
                documentProjectionWorkEmpty: false,
                "work remains"
            )
        );
        RecordingDrainDelay delay = new(timeProvider);
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            primitives,
            new RecordingMutexLease(),
            RuntimeContext()
        );
        context.MarkMutated(RebuildingLifecycle);

        DocumentCacheAdministrativeDrainToEmptyResult result = await CreateDrainer(
                scheduler,
                delay,
                timeProvider
            )
            .DrainToEmptyAsync(context);

        result.Completed.Should().BeFalse();
        result.FailureResult.Should().NotBeNull();
        result
            .FailureResult!.Status.Should()
            .Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .FailureResult.Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.PersistentPoison);
        result.FailureResult.Mutated.Should().BeTrue();
        result.FailureResult.Lifecycle.Should().Be(DocumentCacheLifecycleState.Rebuilding);
        result
            .FailureResult.PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison
                && diagnostic.AffectedDocumentIds.SequenceEqual(new long[] { 101L })
            );
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task It_preserves_administrative_failure_classification_instead_of_reporting_poison()
    {
        RecordingScheduler scheduler = new(
            DocumentCacheProjectionDrainPageResult.AdministrativeFailureResult(
                processedItemCount: 1,
                acknowledgedOrRemovedItemCount: 0,
                documentScopedFailureCount: 0,
                documentScopedFailureIds: [],
                new DocumentCacheAdministrativeDrainFailure(
                    DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                    DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout,
                    DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
                    "provider timeout",
                    retryable: false,
                    affectedDocumentIds: [101]
                )
            )
        );
        DocumentCacheAdministrativeCommandExecutionContext context = CreateCommandContext(
            new RecordingAdministrativePrimitives(),
            new RecordingMutexLease(),
            RuntimeContext()
        );

        DocumentCacheAdministrativeDrainToEmptyResult result = await CreateDrainer(scheduler)
            .DrainToEmptyAsync(context);

        result.Completed.Should().BeFalse();
        result
            .FailureResult!.Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result
            .FailureResult.Classification.Should()
            .NotBe(DocumentCacheAdministrativeCommandClassification.PersistentPoison);
        result
            .FailureResult.PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout
                && diagnostic.AffectedDocumentIds.SequenceEqual(new long[] { 101L })
            );
    }

    private static DocumentCacheAdministrativeDrainer CreateDrainer(
        IDocumentCacheProjectionScheduler scheduler,
        IDocumentCacheAdministrativeDrainDelay? delay = null,
        TimeProvider? timeProvider = null
    ) =>
        new(
            scheduler,
            delay ?? new RecordingDrainDelay(new MutableTimeProvider(ObservedAt)),
            timeProvider ?? new MutableTimeProvider(ObservedAt),
            NullLogger<DocumentCacheAdministrativeDrainer>.Instance
        );

    private static DocumentCacheProjectionScheduler CreateRealScheduler(
        IDocumentCacheProjectionDrainPageProcessor drainPageProcessor
    )
    {
        DocumentCacheOptions options = new()
        {
            Projector = new DocumentCacheProjectorOptions { MaxConcurrentTargets = 1, PageSize = 3 },
        };

        return new DocumentCacheProjectionScheduler(
            Options.Create(options),
            drainPageProcessor,
            new NoOpObservationSink(),
            new MutableTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );
    }

    private static DocumentCacheAdministrativeCommandExecutionContext CreateCommandContext(
        IDocumentCacheAdministrativePrimitives primitives,
        IDocumentCacheAdministrativeMutexLease lease,
        DocumentCacheProjectionTargetRuntimeContext targetContext
    ) =>
        new(
            DocumentCacheAdministrativeCommandExecutionId.New(),
            new DocumentCacheAdministrativeCommandRunnerRequest(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                AdministrativeTargetKey,
                Fingerprint
            ),
            targetContext,
            lease,
            primitives,
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            new NoOpObservationSink(),
            new MutableTimeProvider(ObservedAt),
            ObservedAt,
            CancellationToken.None
        );

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        IDocumentCacheMaterializer? materializer = null,
        IDocumentCacheWriter? writer = null,
        IDocumentCacheSessionBoundWriter? sessionBoundWriter = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext = new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 3,
                projectorMaxConcurrentTargets: 1,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 100,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
            Fingerprint,
            RebuildingLifecycle,
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
                MaterializationTargetContext(),
                materializer ?? new RecordingDocumentCacheMaterializer(),
                writer ?? new RecordingDocumentCacheWriter()
            ),
            new NoOpObservationSink(),
            sessionBoundWriter,
            disposeScopeAsync: null
        );
    }

    private static DocumentProjectionWorkPageItem WorkItem(long documentId, long requiredContentVersion) =>
        new(documentId, requiredContentVersion, FirstEnqueuedAt, FirstEnqueuedAt.AddSeconds(1));

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

    private sealed class RecordingScheduler(params DocumentCacheProjectionDrainPageResult[] results)
        : IDocumentCacheProjectionScheduler
    {
        private readonly Queue<DocumentCacheProjectionDrainPageResult> _results = new(results);

        public List<DocumentCacheProjectionTargetRuntimeContext> AdministrativeDrainCalls { get; } = [];

        public Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
            IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        )
        {
            AdministrativeDrainCalls.Add(targetContext);
            return Task.FromResult(
                DocumentCacheProjectionSchedulerDispatchResult.Dispatched(
                    targetContext,
                    _results.Dequeue(),
                    ObservedAt,
                    ObservedAt
                )
            );
        }
    }

    private sealed class RecordingWorkPager(params DocumentProjectionWorkPage[] pages)
        : IDocumentProjectionWorkPager
    {
        private readonly Queue<DocumentProjectionWorkPage> _pages = new(pages);

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public List<DocumentProjectionWorkPageRequest> Requests { get; } = [];

        public Task<DocumentProjectionWorkPage> ReadPageAsync(
            DocumentProjectionWorkPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class ScriptedItemProcessor(params DocumentCacheProjectionItemProcessResult[] results)
        : IDocumentCacheProjectionItemProcessor
    {
        private readonly Queue<DocumentCacheProjectionItemProcessResult> _results = new(results);

        public List<DocumentCacheProjectionItemProcessRequest> Requests { get; } = [];

        public Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
            DocumentCacheProjectionItemProcessRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = cancellationToken;
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingAdministrativePrimitives(
        params DocumentCacheAdministrativeProjectedStateEmptinessResult[] emptinessResults
    ) : IDocumentCacheAdministrativePrimitives
    {
        private readonly Queue<DocumentCacheAdministrativeProjectedStateEmptinessResult> _emptiness = new(
            emptinessResults
        );

        public int EmptinessReadCount { get; private set; }

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            EmptinessReadCount++;
            return Task.FromResult(_emptiness.Dequeue());
        }

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

    private sealed class RecordingMutexLease : IDocumentCacheAdministrativeMutexLease
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen => true;

        public List<RecordingWriteSession> Sessions { get; } = [];

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        )
        {
            var session = new RecordingWriteSession(isolationLevel);
            Sessions.Add(session);
            return Task.FromResult<IRelationalWriteSession>(session);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWriteSession(IsolationLevel isolationLevel) : IRelationalWriteSession
    {
        public IsolationLevel IsolationLevel { get; } = isolationLevel;

        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDrainDelay(MutableTimeProvider clock)
        : IDocumentCacheAdministrativeDrainDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            _ = timeProvider;
            Delays.Add(delay);
            clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionBoundWriter(params DocumentCacheSessionBoundWriterResult[] results)
        : IDocumentCacheSessionBoundWriter
    {
        private readonly Queue<DocumentCacheSessionBoundWriterResult> _results = new(results);

        public List<DocumentCacheSessionBoundWriterRequest> Calls { get; } = [];

        public Task<DocumentCacheSessionBoundWriterResult> WriteAsync(
            DocumentCacheSessionBoundWriterRequest request
        )
        {
            Calls.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class SessionLosingAfterMutationWriter : IDocumentCacheSessionBoundWriter
    {
        public List<DocumentCacheSessionBoundWriterRequest> Calls { get; } = [];

        public Task<DocumentCacheSessionBoundWriterResult> WriteAsync(
            DocumentCacheSessionBoundWriterRequest request
        )
        {
            Calls.Add(request);
            request.MarkMutationBeforeCommit?.Invoke();
            return Task.FromResult(
                DocumentCacheSessionBoundWriterResult.SessionLoss(
                    request.CommandExecutionMutated,
                    "Administrative mutex session closed during writer commit."
                )
            );
        }
    }

    private sealed class RecordingDocumentCacheWriter(params DocumentCacheWriterResult[] results)
        : IDocumentCacheWriter
    {
        private readonly Queue<DocumentCacheWriterResult> _results = new(results);

        public List<DocumentCacheWriterRequest> Calls { get; } = [];

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
        {
            Calls.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingDocumentCacheMaterializer(
        params DocumentCacheMaterializationResult[] results
    ) : IDocumentCacheMaterializer
    {
        private readonly Queue<DocumentCacheMaterializationResult> _results = new(results);

        public List<DocumentCacheMaterializationRequest> Calls { get; } = [];

        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            Calls.Add(request);
            return Task.FromResult(_results.Dequeue());
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delay)
        {
            _utcNow += delay;
        }
    }
}
