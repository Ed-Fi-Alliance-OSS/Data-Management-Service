// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("DocumentCacheProjector")]
public class Given_A_Postgresql_DocumentCacheProjector
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";
    private const int DefaultPageSize = 3;

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAt.AddMinutes(-5);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private MutableTimeProvider _timeProvider = null!;
    private CandidateMaterializer _materializer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheProjector)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _timeProvider = new MutableTimeProvider(ObservedAt);
        _materializer = new CandidateMaterializer();
    }

    [TearDown]
    public async Task TearDown()
    {
        _dataSourceCache?.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_baseline is not null)
        {
            await _baseline.DisposeAsync();
        }
    }

    [Test]
    public async Task It_drains_long_outage_backlog_in_bounded_pages_and_restarts_from_durable_work()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        for (int index = 0; index < 7; index++)
        {
            await InsertSourceDocumentAsync(
                _database,
                targetKey,
                contentVersion: 100 + index,
                firstEnqueuedAt: FirstEnqueuedAt.AddSeconds(index)
            );
        }

        DocumentCacheProjectionDrainPageProcessor processor = CreateDrainPageProcessor();
        DocumentCacheProjectionTargetRuntimeContext originalContext = CreateRuntimeContext(
            _database,
            targetKey,
            pageSize: DefaultPageSize
        );
        DocumentCacheProjectionDrainPageResult firstPage = await ProcessPageAsync(processor, originalContext);

        DocumentCacheProjectionTargetRuntimeContext restartedContext = CreateRuntimeContext(
            _database,
            targetKey,
            pageSize: DefaultPageSize
        );
        List<DocumentCacheProjectionDrainPageResult> restartedResults = [];
        while (true)
        {
            DocumentCacheProjectionDrainPageResult result = await ProcessPageAsync(
                processor,
                restartedContext
            );
            restartedResults.Add(result);
            if (result.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
            {
                break;
            }
        }

        firstPage.ProcessedItemCount.Should().Be(DefaultPageSize);
        restartedContext.Cursor.HasValue.Should().BeFalse();
        restartedResults
            .Where(result => result.Outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed)
            .Select(result => result.ProcessedItemCount)
            .Should()
            .Equal(3, 1);
        restartedResults
            .Where(result => result.Outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed)
            .Should()
            .OnlyContain(result => result.ProcessedItemCount <= DefaultPageSize);
        (await ReadCacheCountAsync(_database)).Should().Be(7);
        (await ReadWorkCountAsync(_database)).Should().Be(0);

        string pagingSql =
            PostgresqlDocumentProjectionWorkPager.InitialPageSql
            + PostgresqlDocumentProjectionWorkPager.CursorPageSql;
        string normalizedPagingSql = pagingSql.ToUpperInvariant();
        normalizedPagingSql.Should().NotContain(" JOIN ");
        normalizedPagingSql.Should().NotContain("\"DMS\".\"DOCUMENTCACHE\"");
        normalizedPagingSql.Should().NotContain(" FOR UPDATE");
        PostgresqlDocumentProjectionWorkPager
            .InitialPageSql.Should()
            .Contain("""FROM "dms"."DocumentProjectionWork" AS work""");
        PostgresqlDocumentProjectionWorkPager
            .CursorPageSql.Should()
            .Contain(
                """WHERE (work."FirstEnqueuedAt", work."DocumentId") > (@lastFirstEnqueuedAt, @lastDocumentId)"""
            );
        (await ReadProjectionWorkColumnsAsync(_database))
            .Should()
            .NotContain(column =>
                column.Contains("Lease", StringComparison.OrdinalIgnoreCase)
                || column.Contains("Claim", StringComparison.OrdinalIgnoreCase)
                || column.Contains("Attempt", StringComparison.OrdinalIgnoreCase)
                || column.Contains("DeadLetter", StringComparison.OrdinalIgnoreCase)
            );
    }

    [Test]
    public async Task It_wraps_the_cursor_and_processes_the_oldest_visible_work()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument source = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        DocumentCacheProjectionTargetRuntimeContext context = CreateRuntimeContext(_database, targetKey);
        context.Cursor.Advance(ObservedAt.AddHours(1), documentId: 999_999);

        DocumentCacheProjectionDrainPageResult result = await ProcessPageAsync(
            CreateDrainPageProcessor(),
            context
        );

        result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        result.ProcessedItemCount.Should().Be(1);
        context.Cursor.LastDocumentId.Should().Be(source.DocumentId);
        (await ReadWorkCountAsync(_database, source.DocumentId)).Should().Be(0);
        (await ReadCacheCountAsync(_database, source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_serializes_duplicate_replica_visible_work_through_shared_writer_outcomes()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument source = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        CandidateMaterializer barrierMaterializer = new(releaseAfterCallCount: 2);
        barrierMaterializer.Register(targetKey, source);
        DocumentCacheProjectionDrainPageProcessor processor = CreateDrainPageProcessor();
        DocumentCacheProjectionTargetRuntimeContext firstReplica = CreateRuntimeContext(
            _database,
            targetKey,
            materializer: barrierMaterializer
        );
        DocumentCacheProjectionTargetRuntimeContext secondReplica = CreateRuntimeContext(
            _database,
            targetKey,
            materializer: barrierMaterializer
        );
        using CancellationTokenSource cancellationSource = new(TimeSpan.FromSeconds(30));

        DocumentCacheProjectionDrainPageResult[] results = await Task.WhenAll(
                ProcessPageAsync(processor, firstReplica, cancellationSource.Token),
                ProcessPageAsync(processor, secondReplica, cancellationSource.Token)
            )
            .WaitAsync(TimeSpan.FromSeconds(35));

        results
            .Select(result => result.Outcome)
            .Should()
            .OnlyContain(outcome => outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        results.Select(result => result.ProcessedItemCount).Should().OnlyContain(count => count == 1);
        barrierMaterializer.MaterializationCallCount.Should().Be(2);
        (await ReadCacheCountAsync(_database, source.DocumentId)).Should().Be(1);
        (await ReadWorkCountAsync(_database, source.DocumentId)).Should().Be(0);
    }

    [Test]
    public async Task It_observes_cancellation_during_materialization_without_acknowledging_work()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument source = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        using CancellationTokenSource cancellationSource = new();
        DocumentCacheProjectionTargetRuntimeContext context = CreateRuntimeContext(
            _database,
            targetKey,
            materializer: new CancellingMaterializer(cancellationSource)
        );
        DocumentCacheProjectionDrainPageProcessor processor = CreateDrainPageProcessor();

        Func<Task> act = async () => await ProcessPageAsync(processor, context, cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cancellationSource.IsCancellationRequested.Should().BeTrue();
        (await ReadWorkCountAsync(_database, source.DocumentId)).Should().Be(1);
        (await ReadCacheCountAsync(_database, source.DocumentId)).Should().Be(0);
    }

    [Test]
    public async Task It_keeps_poison_rows_visible_without_starving_later_work_and_retries_after_backoff()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument poison = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt,
            requiredContentVersion: 11
        );
        SourceDocument later = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 20,
            firstEnqueuedAt: FirstEnqueuedAt.AddSeconds(1)
        );
        DocumentCacheProjectionTargetRuntimeContext context = CreateRuntimeContext(
            _database,
            targetKey,
            failureBackoff: TimeSpan.FromSeconds(2)
        );
        DocumentCacheProjectionDrainPageProcessor processor = CreateDrainPageProcessor();

        DocumentCacheProjectionDrainPageResult firstPass = await ProcessPageAsync(processor, context);

        firstPass.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        firstPass.ProcessedItemCount.Should().Be(2);
        firstPass.AcknowledgedOrRemovedItemCount.Should().Be(1);
        firstPass.DocumentScopedFailureCount.Should().Be(1);
        firstPass.DocumentScopedFailureIds.Should().Equal(poison.DocumentId);
        (await ReadWorkCountAsync(_database, poison.DocumentId)).Should().Be(1);
        (await ReadWorkCountAsync(_database, later.DocumentId)).Should().Be(0);
        context
            .FailureBackoffState.CreateFailureDiagnosticsSnapshot()
            .EarliestRetryAt.Should()
            .Be(ObservedAt.AddSeconds(2));

        await SetWorkRequiredContentVersionAsync(_database, poison.DocumentId, poison.ContentVersion);
        _timeProvider.SetUtcNow(ObservedAt.AddSeconds(3));

        DocumentCacheProjectionDrainPageResult retryPass = await ProcessPageAsync(processor, context);

        retryPass.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        retryPass.ProcessedItemCount.Should().Be(1);
        retryPass.AcknowledgedOrRemovedItemCount.Should().Be(1);
        context.FailureBackoffState.Count.Should().Be(0);
        (await ReadWorkCountAsync(_database, poison.DocumentId)).Should().Be(0);
        (await ReadCacheCountAsync(_database)).Should().Be(2);
    }

    [Test]
    public async Task It_caps_failure_state_at_page_size_and_evicts_the_oldest_poison_diagnostic()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument first = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt,
            requiredContentVersion: 11
        );
        SourceDocument second = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 20,
            firstEnqueuedAt: FirstEnqueuedAt.AddSeconds(1),
            requiredContentVersion: 21
        );
        SourceDocument third = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 30,
            firstEnqueuedAt: FirstEnqueuedAt.AddSeconds(2),
            requiredContentVersion: 31
        );
        DocumentCacheProjectionTargetRuntimeContext context = CreateRuntimeContext(
            _database,
            targetKey,
            pageSize: 2
        );
        DocumentCacheProjectionDrainPageProcessor processor = CreateDrainPageProcessor();

        _ = await ProcessPageAsync(processor, context);
        _timeProvider.SetUtcNow(ObservedAt.AddSeconds(1));
        _ = await ProcessPageAsync(processor, context);

        DocumentCacheProjectionFailureDiagnostics diagnostics =
            context.FailureBackoffState.CreateFailureDiagnosticsSnapshot();
        diagnostics.FailureCount.Should().Be(2);
        diagnostics.EvictionCount.Should().Be(1);
        diagnostics.DocumentIds.Should().Equal(second.DocumentId, third.DocumentId);
        context
            .FailureBackoffState.IsSuppressed(first.DocumentId, _timeProvider.GetUtcNow())
            .Should()
            .BeFalse();
        context
            .FailureBackoffState.IsSuppressed(second.DocumentId, _timeProvider.GetUtcNow())
            .Should()
            .BeTrue();
        context
            .FailureBackoffState.IsSuppressed(third.DocumentId, _timeProvider.GetUtcNow())
            .Should()
            .BeTrue();
        (await ReadWorkDocumentIdsAsync(_database))
            .Should()
            .Equal(first.DocumentId, second.DocumentId, third.DocumentId);
    }

    [Test]
    public async Task It_fences_set_latch_and_resumes_after_clear_latch_observation()
    {
        await SetLifecycleAsync(
            _database,
            DocumentCacheLifecycleState.Tracking,
            cacheAheadRecoveryRequired: true
        );
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument source = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionTargetRuntimeContext context = CreateRuntimeContext(
            _database,
            targetKey,
            observationSink: observationSink
        );
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            CreateDrainPageProcessor(),
            observationSink,
            maxConcurrentTargets: 1
        );

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> fenced =
            await scheduler.RunReadyTargetsOnceAsync([context]);

        DocumentCacheProjectionDrainPageResult fencedResult = fenced
            .Should()
            .ContainSingle()
            .Subject.DrainResult!;
        fencedResult.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.LifecycleFenced);
        context.SchedulingState.IsTargetPaused.Should().BeTrue();
        (await ReadWorkCountAsync(_database, source.DocumentId)).Should().Be(1);
        (await ReadCacheCountAsync(_database, source.DocumentId)).Should().Be(0);

        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        context.SchedulingState.ObserveLifecycleFence(
            DocumentCacheProjectionLifecycleFenceSnapshotFactory.FromLifecycle(
                new DocumentCacheLifecycleObservation(
                    DocumentCacheLifecycleState.Tracking,
                    CacheAheadRecoveryRequired: false
                ),
                _timeProvider.GetUtcNow()
            )
        );

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> resumed =
            await scheduler.RunReadyTargetsOnceAsync([context]);

        DocumentCacheProjectionDrainPageResult resumedResult = resumed
            .Should()
            .ContainSingle()
            .Subject.DrainResult!;
        resumedResult.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        resumedResult.ProcessedItemCount.Should().Be(1);
        context.SchedulingState.IsTargetPaused.Should().BeFalse();
        (await ReadWorkCountAsync(_database, source.DocumentId)).Should().Be(0);
        (await ReadCacheCountAsync(_database, source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_processes_rebuilding_clear_latch_work_without_completing_the_lifecycle()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Rebuilding);
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument source = await InsertSourceDocumentAsync(
            _database,
            targetKey,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        DocumentCacheProjectionTargetRuntimeContext context = CreateRuntimeContext(
            _database,
            targetKey,
            lifecycleState: DocumentCacheLifecycleState.Rebuilding
        );

        DocumentCacheProjectionDrainPageResult result = await ProcessPageAsync(
            CreateDrainPageProcessor(),
            context
        );

        result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        result.ProcessedItemCount.Should().Be(1);
        (await ReadWorkCountAsync(_database, source.DocumentId)).Should().Be(0);
        (await ReadCacheCountAsync(_database, source.DocumentId)).Should().Be(1);
        (await ReadLifecycleAsync(_database))
            .Should()
            .Be(new LifecycleRow(DocumentCacheLifecycleState.Rebuilding, CacheAheadRecoveryRequired: false));
    }

    [Test]
    public async Task It_schedules_ready_targets_in_deterministic_target_key_order()
    {
        await SetLifecycleAsync(_database, DocumentCacheLifecycleState.Tracking);
        await using PostgresqlGeneratedDdlTestDatabase secondDatabase =
            await _baseline.CreateIsolatedDatabaseAsync();
        await SetLifecycleAsync(secondDatabase, DocumentCacheLifecycleState.Tracking);
        DocumentCacheTargetKey targetB = DocumentCacheTargetKey.Create("Tenant-B", 2);
        DocumentCacheTargetKey targetA = DocumentCacheTargetKey.Create("Tenant-A", 1);
        SourceDocument sourceB = await InsertSourceDocumentAsync(
            _database,
            targetB,
            contentVersion: 10,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        SourceDocument sourceA = await InsertSourceDocumentAsync(
            secondDatabase,
            targetA,
            contentVersion: 20,
            firstEnqueuedAt: FirstEnqueuedAt
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheProjectionScheduler scheduler = CreateScheduler(
            CreateDrainPageProcessor(),
            observationSink,
            maxConcurrentTargets: 1
        );
        DocumentCacheProjectionTargetRuntimeContext contextB = CreateRuntimeContext(_database, targetB);
        DocumentCacheProjectionTargetRuntimeContext contextA = CreateRuntimeContext(secondDatabase, targetA);

        ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult> results =
            await scheduler.RunReadyTargetsOnceAsync([contextB, contextA]);

        results.Select(result => result.TargetKey).Should().Equal(targetA, targetB);
        results
            .Select(result => result.DrainResult!.Outcome)
            .Should()
            .OnlyContain(outcome => outcome == DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        (await ReadWorkCountAsync(secondDatabase, sourceA.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(_database, sourceB.DocumentId)).Should().Be(0);
        (await ReadCacheCountAsync(secondDatabase, sourceA.DocumentId)).Should().Be(1);
        (await ReadCacheCountAsync(_database, sourceB.DocumentId)).Should().Be(1);
    }

    private DocumentCacheProjectionDrainPageProcessor CreateDrainPageProcessor()
    {
        PostgresqlDocumentProjectionWorkPager pager = new(
            _dataSourceCache,
            NullLogger<PostgresqlDocumentProjectionWorkPager>.Instance
        );
        DocumentCacheProjectionItemProcessor itemProcessor = new(
            _timeProvider,
            NullLogger<DocumentCacheProjectionItemProcessor>.Instance
        );

        return new DocumentCacheProjectionDrainPageProcessor(
            pager,
            itemProcessor,
            NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
            _timeProvider
        );
    }

    private DocumentCacheProjectionScheduler CreateScheduler(
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
            _timeProvider,
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );
    }

    private static Task<DocumentCacheProjectionDrainPageResult> ProcessPageAsync(
        DocumentCacheProjectionDrainPageProcessor processor,
        DocumentCacheProjectionTargetRuntimeContext context,
        CancellationToken cancellationToken = default
    ) =>
        processor.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                context,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            ),
            cancellationToken
        );

    private DocumentCacheProjectionTargetRuntimeContext CreateRuntimeContext(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentCacheTargetKey targetKey,
        int pageSize = DefaultPageSize,
        TimeSpan? failureBackoff = null,
        DocumentCacheLifecycleState lifecycleState = DocumentCacheLifecycleState.Tracking,
        bool cacheAheadRecoveryRequired = false,
        IDocumentCacheMaterializer? materializer = null,
        IDocumentCacheProjectionObservationSink? observationSink = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: pageSize,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: failureBackoff ?? TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
                database.ConnectionString
            ),
            Fingerprint,
            new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired),
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
                new DocumentCacheMaterializationTargetContext(
                    new DocumentCacheProjectionTargetKey(
                        targetKey.TenantKey,
                        new DataStoreId(targetKey.DataStoreId)
                    ),
                    _fixture.MappingSet,
                    DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                    database.ConnectionString
                ),
                materializer ?? _materializer,
                CreateWriter()
            ),
            observationSink ?? new RecordingObservationSink()
        );
    }

    private PostgresqlDocumentCacheWriter CreateWriter() =>
        new(
            _dataSourceCache,
            new DocumentCacheWriterRetryAdapter(
                new DeadlockRetrySettings
                {
                    MaxRetryAttempts = 0,
                    BaseDelayMilliseconds = 1,
                    UseJitter = false,
                },
                new PostgresqlRelationalWriteExceptionClassifier(),
                NullLogger<DocumentCacheWriterRetryAdapter>.Instance
            ),
            NullLogger<PostgresqlDocumentCacheWriter>.Instance
        );

    private async Task<SourceDocument> InsertSourceDocumentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentCacheTargetKey targetKey,
        long contentVersion,
        DateTimeOffset firstEnqueuedAt,
        long? requiredContentVersion = null
    )
    {
        var documentUuid = Guid.NewGuid();
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        DateTimeOffset lastModifiedAt = firstEnqueuedAt.AddSeconds(30);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            INSERT INTO "dms"."Document" (
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = lastModifiedAt }
        );
        SourceDocument source = new(
            Convert.ToInt64(rows.Single()["DocumentId"]),
            documentUuid,
            contentVersion,
            lastModifiedAt
        );

        await UpsertProjectionWorkAsync(
            database,
            source.DocumentId,
            requiredContentVersion ?? contentVersion,
            firstEnqueuedAt
        );
        _materializer.Register(targetKey, source);

        return source;
    }

    private static Task UpsertProjectionWorkAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId,
        long requiredContentVersion,
        DateTimeOffset firstEnqueuedAt
    ) =>
        database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentProjectionWork" (
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            )
            VALUES (
                @documentId,
                @requiredContentVersion,
                @firstEnqueuedAt,
                @lastEnqueuedAt
            )
            ON CONFLICT ("DocumentId") DO UPDATE
            SET "RequiredContentVersion" = EXCLUDED."RequiredContentVersion",
                "FirstEnqueuedAt" = EXCLUDED."FirstEnqueuedAt",
                "LastEnqueuedAt" = EXCLUDED."LastEnqueuedAt";
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
            {
                Value = requiredContentVersion,
            },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = firstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz)
            {
                Value = firstEnqueuedAt.AddSeconds(5),
            }
        );

    private static Task SetWorkRequiredContentVersionAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId,
        long requiredContentVersion
    ) =>
        database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentProjectionWork"
            SET "RequiredContentVersion" = @requiredContentVersion
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
            {
                Value = requiredContentVersion,
            }
        );

    private static Task SetLifecycleAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired = false
    ) =>
        database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycleState", lifecycleState.ToString()),
            new NpgsqlParameter("cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
        );

    private static async Task<LifecycleRow> ReadLifecycleAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
            FROM "dms"."DocumentCacheState"
            WHERE "StateId" = 1;
            """
        );
        IReadOnlyDictionary<string, object?> row = rows.Should().ContainSingle().Subject;

        return new LifecycleRow(
            Enum.Parse<DocumentCacheLifecycleState>((string)row["ProjectionLifecycleState"]!),
            (bool)row["CacheAheadRecoveryRequired"]!
        );
    }

    private static Task<long> ReadWorkCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentProjectionWork";
            """
        );

    private static Task<long> ReadWorkCountAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    ) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private static async Task<IReadOnlyList<long>> ReadWorkDocumentIdsAsync(
        PostgresqlGeneratedDdlTestDatabase database
    )
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId"
            FROM "dms"."DocumentProjectionWork"
            ORDER BY "FirstEnqueuedAt", "DocumentId";
            """
        );

        return rows.Select(row => Convert.ToInt64(row["DocumentId"])).ToList();
    }

    private static Task<long> ReadCacheCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentCache";
            """
        );

    private static Task<long> ReadCacheCountAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    ) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private static async Task<IReadOnlyList<string>> ReadProjectionWorkColumnsAsync(
        PostgresqlGeneratedDdlTestDatabase database
    )
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await database.QueryRowsAsync(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'dms'
              AND table_name = 'DocumentProjectionWork'
            ORDER BY ordinal_position;
            """
        );

        return rows.Select(row => (string)row["column_name"]!).ToList();
    }

    private sealed record SourceDocument(
        long DocumentId,
        Guid DocumentUuid,
        long ContentVersion,
        DateTimeOffset LastModifiedAt
    );

    private sealed record LifecycleRow(
        DocumentCacheLifecycleState ProjectionLifecycleState,
        bool CacheAheadRecoveryRequired
    );

    private sealed class CandidateMaterializer(int? releaseAfterCallCount = null) : IDocumentCacheMaterializer
    {
        private readonly object _sync = new();
        private readonly Dictionary<(string TargetKey, long DocumentId), SourceDocument> _sources = [];
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _materializationCallCount;

        public int MaterializationCallCount => Volatile.Read(ref _materializationCallCount);

        public void Register(DocumentCacheTargetKey targetKey, SourceDocument source)
        {
            _sources[(ProjectionTargetKey(targetKey), source.DocumentId)] = source;
        }

        public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            int callCount = Interlocked.Increment(ref _materializationCallCount);
            if (releaseAfterCallCount is not null)
            {
                if (callCount >= releaseAfterCallCount.Value)
                {
                    _release.TrySetResult();
                }

                await _release.Task.WaitAsync(request.CancellationToken).ConfigureAwait(false);
            }

            SourceDocument source;
            lock (_sync)
            {
                source = _sources[(request.TargetContext.TargetKey.ToString(), request.DocumentId)];
            }
            long contentVersion =
                request.SelectedRequiredContentVersion
                ?? throw new InvalidOperationException(
                    "Projector materialization requests must carry the selected work content version."
                );

            return new DocumentCacheMaterializationResult.Success(
                new DocumentCacheMaterializationCandidate(
                    source.DocumentId,
                    new DocumentUuid(source.DocumentUuid),
                    "Ed-Fi",
                    "Person",
                    "5.0.0",
                    contentVersion,
                    source.LastModifiedAt,
                    $"etag-{contentVersion}",
                    new JsonObject
                    {
                        ["documentId"] = source.DocumentId,
                        ["target"] = request.TargetContext.TargetKey.ToString(),
                    }
                )
            );
        }

        private static string ProjectionTargetKey(DocumentCacheTargetKey targetKey) =>
            $"{targetKey.TenantKey}/{targetKey.DataStoreId}";
    }

    private sealed class CancellingMaterializer(CancellationTokenSource cancellationSource)
        : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            cancellationSource.Cancel();
            request.CancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should have been observed.");
        }
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

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
