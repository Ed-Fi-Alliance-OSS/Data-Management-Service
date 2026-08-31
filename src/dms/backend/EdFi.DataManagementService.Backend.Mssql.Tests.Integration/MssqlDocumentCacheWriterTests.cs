// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheWriter")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheWriter
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";
    private const int PerformanceEvidenceBatchSize = 50;
    private const int PerformanceEvidenceContentionCount = 5;

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DocumentCacheProjectionTargetKey TargetKey = new(
        "tenant-cache-writer",
        new DataStoreId(7)
    );
    private static readonly string ExpectedTelemetryTargetLabel =
        DocumentCacheTelemetryTargetLabel.FromProjectionTargetKey(TargetKey);
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlDocumentCacheWriter _writer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheWriter)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _writer = CreateWriter();
    }

    private MssqlDocumentCacheWriter CreateWriter(
        ITransactionFaultInjectionObserver? faultInjectionObserver = null,
        IDocumentCacheWriterTelemetry? telemetry = null,
        int maxRetryAttempts = 0,
        string? sessionInitializationCommandText = null
    ) =>
        new(
            new DocumentCacheWriterRetryAdapter(
                new DeadlockRetrySettings
                {
                    MaxRetryAttempts = maxRetryAttempts,
                    BaseDelayMilliseconds = 1,
                    UseJitter = false,
                },
                new MssqlRelationalWriteExceptionClassifier(),
                NullLogger<DocumentCacheWriterRetryAdapter>.Instance,
                telemetry
            ),
            NullLogger<MssqlDocumentCacheWriter>.Instance,
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            faultInjectionObserver,
            telemetry,
            sessionInitializationCommandText
        );

    [TearDown]
    public async Task TearDown()
    {
        if (_lease is not null)
        {
            await _lease.DisposeAsync();
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
    public async Task It_writes_a_current_candidate_and_deletes_matching_work_in_one_attempt()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-current");

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);

        CacheRow cacheRow = await ReadCacheRowAsync(source.DocumentId);
        cacheRow.ContentVersion.Should().Be(10);
        cacheRow.StreamEtag.Should().Be("etag-10");
        JsonNode.Parse(cacheRow.DocumentJson)!["value"]!.GetValue<string>().Should().Be("candidate-current");
    }

    [Test]
    [Category("DocumentCacheSessionBoundWriter")]
    public async Task DocumentCacheSessionBoundWriter_it_writes_candidate_and_acknowledges_on_the_mutex_session()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "session-bound");
        var mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );

        await using IDocumentCacheAdministrativeMutexLease lease = await mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            )
        );

        DocumentCacheSessionBoundWriterResult result = await (
            (IDocumentCacheSessionBoundWriter)_writer
        ).WriteAsync(
            new DocumentCacheSessionBoundWriterRequest(
                lease,
                CreateRequest(source, candidate),
                commandExecutionMutated: false
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.WriterResult.Should().BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        JsonNode.Parse((await ReadCacheRowAsync(source.DocumentId)).DocumentJson)!["value"]!
            .GetValue<string>()
            .Should()
            .Be("session-bound");
    }

    [Test]
    [Category("DocumentCacheSessionBoundWriter")]
    public async Task DocumentCacheSessionBoundWriter_it_returns_non_mutating_already_current_without_work_on_the_mutex_session()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 10);
        await DeleteWorkAsync(source.DocumentId);
        var mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );

        await using IDocumentCacheAdministrativeMutexLease lease = await mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            )
        );

        DocumentCacheSessionBoundWriterResult result = await (
            (IDocumentCacheSessionBoundWriter)_writer
        ).WriteAsync(
            new DocumentCacheSessionBoundWriterRequest(
                lease,
                CreateRequest(source, candidate: null),
                commandExecutionMutated: false
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeFalse();
        result
            .WriterResult.Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentNoWork>()
            .Which.CurrentContentVersion.Should()
            .Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
    }

    [TestCaseSource(nameof(CancellationRollbackHookCases))]
    [Category("DocumentCacheWriterCancellation")]
    public async Task DocumentCacheWriterCancellation_it_finishes_started_transactions_after_caller_cancellation(
        string hookName
    )
    {
        DocumentCacheWriterFaultInjectionHook hook = Enum.Parse<DocumentCacheWriterFaultInjectionHook>(
            hookName
        );
        (SourceDocument source, DocumentCacheMaterializationCandidate? candidate) =
            await PrepareCancellationRollbackScenarioAsync(hook);
        using CancellationTokenSource cancellationSource = new();
        CancellingFaultInjectionObserver observer = new(hook, cancellationSource);
        MssqlDocumentCacheWriter writer = CreateWriter(observer);

        DocumentCacheWriterResult result = await writer.WriteAsync(
            CreateRequest(
                source,
                candidate,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                cancellationSource.Token
            )
        );

        AssertCancellationCompletedWriterResult(hook, result);
        observer.Contexts.Should().ContainSingle(context => context.Hook == hook);
        cancellationSource.IsCancellationRequested.Should().BeTrue();
        await AssertCancellationCompletedStateAsync(source);
    }

    [TestCaseSource(nameof(CancellationRollbackHookCases))]
    [Category("DocumentCacheSessionBoundWriter")]
    [Category("DocumentCacheWriterCancellation")]
    public async Task DocumentCacheSessionBoundWriterCancellation_it_commits_on_the_mutex_session_without_the_canceled_token(
        string hookName
    )
    {
        DocumentCacheWriterFaultInjectionHook hook = Enum.Parse<DocumentCacheWriterFaultInjectionHook>(
            hookName
        );
        (SourceDocument source, DocumentCacheMaterializationCandidate? candidate) =
            await PrepareCancellationRollbackScenarioAsync(hook);
        using CancellationTokenSource cancellationSource = new();
        CancellingFaultInjectionObserver observer = new(hook, cancellationSource);
        MssqlDocumentCacheWriter writer = CreateWriter(observer);
        var mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );

        await using IDocumentCacheAdministrativeMutexLease realLease = await mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            )
        );
        RecordingAdministrativeMutexLease recordingLease = new(realLease);

        DocumentCacheSessionBoundWriterResult result = await (
            (IDocumentCacheSessionBoundWriter)writer
        ).WriteAsync(
            new DocumentCacheSessionBoundWriterRequest(
                recordingLease,
                CreateRequest(
                    source,
                    candidate,
                    DocumentCacheWriterPurpose.DurableWorkProjection,
                    cancellationSource.Token
                ),
                commandExecutionMutated: true
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        AssertCancellationCompletedWriterResult(hook, result.WriterResult!);
        recordingLease
            .CommitCancellationTokens.Should()
            .ContainSingle()
            .Which.Should()
            .Be(CancellationToken.None);
        recordingLease.RollbackCancellationTokens.Should().BeEmpty();

        await using IRelationalWriteSession session = await recordingLease.BeginTransactionAsync();
        await session.CommitAsync(CancellationToken.None);

        await AssertCancellationCompletedStateAsync(source);
    }

    [Test]
    public async Task It_acknowledges_equal_version_work_without_refreshing_cache()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DateTime originalComputedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await InsertCacheRowAsync(source, contentVersion: 10, computedAt: originalComputedAt);
        DateTime beforeComputedAt = await ReadCacheComputedAtAsync(source.DocumentId);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheComputedAtAsync(source.DocumentId)).Should().Be(beforeComputedAt);
    }

    [Test]
    public async Task It_acknowledges_equal_version_work_without_applying_a_stale_candidate()
    {
        var telemetry = new RecordingDocumentCacheWriterTelemetry();
        _writer = CreateWriter(telemetry: telemetry);
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DateTime originalComputedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await InsertCacheRowAsync(source, contentVersion: 10, computedAt: originalComputedAt);
        DateTime beforeComputedAt = await ReadCacheComputedAtAsync(source.DocumentId);
        DocumentCacheMaterializationCandidate staleCandidate = CreateCandidate(
            source,
            "stale-candidate-should-not-write",
            contentVersion: 9
        );

        DocumentCacheWriterResult result = await WriteAsync(source, staleCandidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        CacheRow cacheRow = await ReadCacheRowAsync(source.DocumentId);
        cacheRow.ContentVersion.Should().Be(10);
        cacheRow.StreamEtag.Should().Be("etag-10");
        JsonNode.Parse(cacheRow.DocumentJson)!["value"]!.GetValue<string>().Should().Be("cache-10");
        (await ReadCacheComputedAtAsync(source.DocumentId)).Should().Be(beforeComputedAt);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
        telemetry
            .Records.Should()
            .Contain(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.Outcome
                && record.Context.Outcome == nameof(DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged)
            );
    }

    [Test]
    public async Task It_returns_already_current_without_work_when_cache_matches_source()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 10);
        await DeleteWorkAsync(source.DocumentId);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentNoWork>()
            .Which.CurrentContentVersion.Should()
            .Be(10);
        (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    public async Task It_reports_missing_work_without_cache_dml_for_direct_fill_when_work_is_absent()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await DeleteWorkAsync(source.DocumentId);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "direct-fill-missing-work");

        DocumentCacheWriterResult result = await WriteAsync(
            source,
            candidate,
            DocumentCacheWriterPurpose.DirectFill
        );

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.Kind.Should()
            .Be(DocumentCacheWriterWorkAnomalyKind.MissingWork);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    public async Task It_reports_needs_materialization_without_candidate_for_current_pending_work()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.NeedsMaterialization>()
            .Which.CurrentContentVersion.Should()
            .Be(10);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    public async Task It_writes_a_current_candidate_in_rebuilding_lifecycle()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Rebuilding);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-rebuilding");

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    public async Task It_suppresses_a_stale_candidate_without_cache_dml_or_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(
            source,
            "candidate-stale",
            contentVersion: 9
        );

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        var stale = result.Should().BeOfType<DocumentCacheWriterResult.StaleCandidateSuppressed>().Subject;
        stale.CurrentContentVersion.Should().Be(10);
        stale.CandidateContentVersion.Should().Be(9);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    public async Task It_reports_matching_version_uuid_mismatch_as_an_invariant_failure()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(
            source,
            "candidate-wrong-uuid",
            documentUuid: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        );

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.MatchingVersionDocumentUuidMismatch);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_reports_matching_version_resource_metadata_mismatch_as_an_invariant_failure()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(
            source,
            "candidate-wrong-resource",
            resourceName: "School"
        );

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.MatchingVersionResourceMetadataMismatch);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_returns_a_lifecycle_fence_without_classifying_or_acknowledging_when_disabled()
    {
        DocumentCacheMaterializationTargetContext targetContext = CreateTargetContext();

        DocumentCacheWriterResult result = await _writer.WriteAsync(
            new DocumentCacheWriterRequest(
                targetContext,
                documentId: 77,
                selectedRequiredContentVersion: null,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate: null,
                CancellationToken.None
            )
        );

        var fenced = result.Should().BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>().Subject;
        fenced.Reason.Should().Be(DocumentCacheWriterFenceReason.LifecycleNotEligible);
        fenced.LifecycleState.Should().Be(DocumentCacheLifecycleState.Disabled);
    }

    [TestCase(DocumentCacheLifecycleState.Disabled)]
    [TestCase(DocumentCacheLifecycleState.Resetting)]
    public async Task It_fences_ineligible_lifecycle_states_without_cache_dml_or_acknowledgement(
        DocumentCacheLifecycleState lifecycleState
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-fenced");
        await SetLifecycleAsync(lifecycleState);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        DocumentCacheWriterResult.LifecycleOrLatchFenced fenced = AssertLifecycleFence(
            result,
            DocumentCacheWriterFenceReason.LifecycleNotEligible
        );
        fenced.LifecycleState.Should().Be(lifecycleState);
        fenced.CacheAheadRecoveryRequired.Should().BeFalse();
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_fences_set_latch_without_cache_dml_or_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-latch-fenced");
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        DocumentCacheWriterResult.LifecycleOrLatchFenced fenced = AssertLifecycleFence(
            result,
            DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired
        );
        fenced.LifecycleState.Should().Be(DocumentCacheLifecycleState.Tracking);
        fenced.CacheAheadRecoveryRequired.Should().BeTrue();
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_fences_missing_lifecycle_state_without_cache_dml_or_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-missing-state");
        await DeleteLifecycleStateAsync();

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        DocumentCacheWriterResult.LifecycleOrLatchFenced fenced = AssertLifecycleFence(
            result,
            DocumentCacheWriterFenceReason.StateMissing
        );
        fenced.LifecycleState.Should().BeNull();
        fenced.CacheAheadRecoveryRequired.Should().BeNull();
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_fences_invalid_lifecycle_state_without_cache_dml_or_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-invalid-state");
        await SetInvalidLifecycleStateAsync();

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        DocumentCacheWriterResult.LifecycleOrLatchFenced fenced = AssertLifecycleFence(
            result,
            DocumentCacheWriterFenceReason.StateInvalid
        );
        fenced.LifecycleState.Should().BeNull();
        fenced.CacheAheadRecoveryRequired.Should().BeNull();
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_fences_unreadable_lifecycle_state_without_cache_dml_or_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(
            source,
            "candidate-unreadable-state"
        );
        await MakeLifecycleStateUnreadableAsync();

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        DocumentCacheWriterResult.LifecycleOrLatchFenced fenced = AssertLifecycleFence(
            result,
            DocumentCacheWriterFenceReason.StateUnreadable
        );
        fenced.LifecycleState.Should().BeNull();
        fenced.CacheAheadRecoveryRequired.Should().BeNull();
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_retries_transient_locked_lifecycle_read_failures_before_classification()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-after-retry");
        var telemetry = new RecordingDocumentCacheWriterTelemetry();
        MssqlDocumentCacheWriter writer = CreateWriter(
            telemetry: telemetry,
            maxRetryAttempts: 3,
            sessionInitializationCommandText: "SET LOCK_TIMEOUT 100;"
        );

        await using SqlConnection blockerConnection = new(_database.ConnectionString);
        await blockerConnection.OpenAsync();
        await using SqlTransaction blockerTransaction = (SqlTransaction)
            await blockerConnection.BeginTransactionAsync();
        await LockLifecycleStateForUpdateAsync(blockerConnection, blockerTransaction);

        Task<DocumentCacheWriterResult> writeTask = writer.WriteAsync(CreateRequest(source, candidate));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await blockerTransaction.RollbackAsync();

        DocumentCacheWriterResult result = await writeTask.WaitAsync(TimeSpan.FromSeconds(30));

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        telemetry
            .Records.Should()
            .Contain(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.Retry && record.AttemptCount > 1
            );
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(1);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
    }

    [Test]
    public async Task It_sets_the_cache_ahead_latch_only_after_reclassifying_current_cache_ahead()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 11);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        var cacheAhead = result.Should().BeOfType<DocumentCacheWriterResult.CacheAheadLatchSet>().Subject;
        cacheAhead.SourceContentVersion.Should().Be(10);
        cacheAhead.CacheContentVersion.Should().Be(11);
        (await ReadCacheAheadLatchAsync()).Should().BeTrue();
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_returns_cache_ahead_disappeared_when_source_catches_cache_before_latch_mutation()
    {
        await SetReadCommittedSnapshotAsync(enabled: true);

        try
        {
            await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
            await InsertCacheRowAsync(source, contentVersion: 11);

            await using SqlConnection sourceAdvanceConnection = new(_database.ConnectionString);
            await sourceAdvanceConnection.OpenAsync();
            await using SqlTransaction sourceAdvanceTransaction = (SqlTransaction)
                await sourceAdvanceConnection.BeginTransactionAsync();
            await AdvanceSourceAndWorkVersionAsync(
                sourceAdvanceConnection,
                sourceAdvanceTransaction,
                source.DocumentId,
                contentVersion: 11
            );

            Task<DocumentCacheWriterResult> writeTask = WriteAsync(source, candidate: null);

            await WaitForMssqlLatchUpdateToWaitOnSourceRowAsync(writeTask);
            await sourceAdvanceTransaction.CommitAsync();

            DocumentCacheWriterResult result = await writeTask.WaitAsync(TimeSpan.FromSeconds(30));

            result.Should().BeSameAs(DocumentCacheWriterResult.CacheAheadDisappeared.Instance);
            (await ReadCacheAheadLatchAsync()).Should().BeFalse();
            (await ReadSourceContentVersionAsync(source.DocumentId)).Should().Be(11);
            (await ReadWorkRequiredContentVersionAsync(source.DocumentId)).Should().Be(11);
        }
        finally
        {
            await SetReadCommittedSnapshotAsync(enabled: false);
        }
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_holds_cache_ahead_source_locks_until_latch_commit()
    {
        await SetReadCommittedSnapshotAsync(enabled: true);

        try
        {
            await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
            await InsertCacheRowAsync(source, contentVersion: 11);
            PausingFaultInjectionObserver observer = new(
                DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit
            );
            MssqlDocumentCacheWriter pausedWriter = CreateWriter(observer);

            Task<DocumentCacheWriterResult> writeTask = pausedWriter.WriteAsync(
                CreateRequest(source, candidate: null)
            );

            await observer.WaitUntilReachedAsync(TimeSpan.FromSeconds(10));

            SqlException exception = (
                await FluentActions
                    .Awaiting(() =>
                        AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
                            source.DocumentId,
                            contentVersion: 11
                        )
                    )
                    .Should()
                    .ThrowAsync<SqlException>()
            ).Which;

            exception.Number.Should().Be(1222);
            (await ReadSourceContentVersionAsync(source.DocumentId)).Should().Be(10);
            (await ReadCacheAheadLatchAsync()).Should().BeFalse();

            observer.Release();

            DocumentCacheWriterResult result = await writeTask.WaitAsync(TimeSpan.FromSeconds(30));

            result.Should().BeOfType<DocumentCacheWriterResult.CacheAheadLatchSet>();
            (await ReadCacheAheadLatchAsync()).Should().BeTrue();
            (await ReadSourceContentVersionAsync(source.DocumentId)).Should().Be(10);
        }
        finally
        {
            await SetReadCommittedSnapshotAsync(enabled: false);
        }
    }

    [Test]
    public async Task It_does_not_set_the_cache_ahead_latch_for_non_cache_ahead_anomalies()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument missingWorkSource = await InsertSourceDocumentAsync(contentVersion: 10);
        await DeleteWorkAsync(missingWorkSource.DocumentId);

        DocumentCacheWriterResult missingWork = await WriteAsync(missingWorkSource, candidate: null);

        missingWork
            .Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.Kind.Should()
            .Be(DocumentCacheWriterWorkAnomalyKind.MissingWork);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();

        SourceDocument mismatchedWorkSource = await InsertSourceDocumentAsync(contentVersion: 10);
        await SetWorkRequiredContentVersionAsync(mismatchedWorkSource.DocumentId, requiredContentVersion: 11);

        DocumentCacheWriterResult mismatchedWork = await WriteAsync(mismatchedWorkSource, candidate: null);

        mismatchedWork
            .Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.Kind.Should()
            .Be(DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();

        DocumentCacheWriterResult missingSource = await _writer.WriteAsync(
            new DocumentCacheWriterRequest(
                CreateTargetContext(),
                documentId: 9_999_999,
                selectedRequiredContentVersion: null,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate: null,
                CancellationToken.None
            )
        );

        missingSource.Should().BeSameAs(DocumentCacheWriterResult.SourceMissingOrDeleted.Instance);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [TestCaseSource(nameof(CrashHookCases))]
    [Category("DocumentCacheWriterCrash")]
    public async Task DocumentCacheWriterCrash_it_interrupts_each_hook_without_committing_partial_state(
        string hookName,
        string interruptionName
    )
    {
        DocumentCacheWriterFaultInjectionHook hook = Enum.Parse<DocumentCacheWriterFaultInjectionHook>(
            hookName
        );
        FaultInjectionInterruption interruption = Enum.Parse<FaultInjectionInterruption>(interruptionName);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate? candidate = CreateCandidate(source, "candidate-current");

        if (hook == DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit)
        {
            await InsertCacheRowAsync(source, contentVersion: 11);
            candidate = null;
        }

        InterruptingFaultInjectionObserver observer = new(hook, interruption);
        MssqlDocumentCacheWriter writer = CreateWriter(observer);

        Func<Task> act = async () =>
        {
            _ = await writer.WriteAsync(
                new DocumentCacheWriterRequest(
                    CreateTargetContext(),
                    source.DocumentId,
                    selectedRequiredContentVersion: source.ContentVersion,
                    DocumentCacheWriterPurpose.DurableWorkProjection,
                    candidate,
                    CancellationToken.None
                )
            );
        };

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected DocumentCache writer fault*");

        DocumentCacheWriterFaultInjectionContext interruptedContext = observer.Contexts[^1];
        interruptedContext.Hook.Should().Be(hook);
        interruptedContext.Provider.Should().Be(RelationalProviderToken.SqlServerValue);
        interruptedContext.TargetKey.Should().Be("tenant-cache-writer:7");
        interruptedContext.Purpose.Should().Be(DocumentCacheWriterPurpose.DurableWorkProjection);
        interruptedContext.LifecycleState.Should().Be(DocumentCacheLifecycleState.Tracking);

        if (hook == DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit)
        {
            interruptedContext.CacheAheadLatchRowCount.Should().Be(1);
            (await ReadCacheAheadLatchAsync()).Should().BeFalse();
            (await ReadCacheCountAsync(source.DocumentId)).Should().Be(1);
            (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(11);
        }
        else
        {
            (await ReadCacheAheadLatchAsync()).Should().BeFalse();
            (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        }

        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_reclassifies_after_materialization_and_preserves_newer_work()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate staleAfterMaterializationCandidate = CreateCandidate(
            source,
            "candidate-before-backoff"
        );

        await AdvanceSourceAndWorkVersionAsync(source.DocumentId, contentVersion: 11);

        DocumentCacheWriterResult result = await WriteAsync(source, staleAfterMaterializationCandidate);

        var stale = result.Should().BeOfType<DocumentCacheWriterResult.StaleCandidateSuppressed>().Subject;
        stale.CurrentContentVersion.Should().Be(11);
        stale.CandidateContentVersion.Should().Be(10);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkRequiredContentVersionAsync(source.DocumentId)).Should().Be(11);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_fences_post_delete_materialized_candidates_without_manual_cache_delete()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidateMaterializedBeforeDelete = CreateCandidate(
            source,
            "candidate-before-delete"
        );

        await DeleteSourceDocumentAsync(source.DocumentId);

        DocumentCacheWriterResult result = await WriteAsync(source, candidateMaterializedBeforeDelete);

        result.Should().BeSameAs(DocumentCacheWriterResult.SourceMissingOrDeleted.Instance);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [TestCase(MssqlDeleteRaceProviderFailure.ForeignKeyViolation)]
    [TestCase(MssqlDeleteRaceProviderFailure.DocumentCacheUuidTrigger)]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_replays_post_delete_provider_failures_to_source_missing(
        MssqlDeleteRaceProviderFailure providerFailure
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidateMaterializedBeforeDelete = CreateCandidate(
            source,
            "candidate-before-delete"
        );
        var telemetry = new RecordingDocumentCacheWriterTelemetry();
        var observer = new ThrowOnceMssqlDeleteRaceFaultInjectionObserver(
            () => DeleteSourceDocumentAsync(source.DocumentId),
            providerFailure
        );
        MssqlDocumentCacheWriter writer = CreateWriter(observer, telemetry, maxRetryAttempts: 1);

        DocumentCacheWriterResult result = await writer.WriteAsync(
            CreateRequest(source, candidateMaterializedBeforeDelete)
        );

        result.Should().BeSameAs(DocumentCacheWriterResult.SourceMissingOrDeleted.Instance);
        observer.InjectedFaultCount.Should().Be(1);
        observer.DeletedSourceBeforeFault.Should().BeTrue();
        telemetry
            .Records.Should()
            .Contain(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.Retry
                && record.AttemptCount == 2
                && record.Context.Outcome == nameof(DocumentCacheWriterOutcome.SourceMissingOrDeleted)
            );
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_serializes_duplicate_absent_cache_writers_without_partial_cache_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-current");
        PausingFaultInjectionObserver observer = new(
            DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement
        );
        MssqlDocumentCacheWriter pausedWriter = CreateWriter(observer);

        Task<DocumentCacheWriterResult> firstWrite = pausedWriter.WriteAsync(
            CreateRequest(source, candidate)
        );

        await observer.WaitUntilReachedAsync(TimeSpan.FromSeconds(10));

        Task<DocumentCacheWriterResult> duplicateWrite = _writer.WriteAsync(CreateRequest(source, candidate));

        observer.Release();

        DocumentCacheWriterResult[] results = await Task.WhenAll(firstWrite, duplicateWrite)
            .WaitAsync(TimeSpan.FromSeconds(30));
        DocumentCacheWriterOutcome[] outcomes = results.Select(result => result.Outcome).ToArray();

        outcomes
            .Count(outcome => outcome == DocumentCacheWriterOutcome.CandidateWrittenAcknowledged)
            .Should()
            .Be(1);
        outcomes
            .Should()
            .BeSubsetOf([
                DocumentCacheWriterOutcome.CandidateWrittenAcknowledged,
                DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged,
                DocumentCacheWriterOutcome.AlreadyCurrentNoWork,
                DocumentCacheWriterOutcome.RacingWriterLost,
            ]);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(1);
        (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_resolves_projector_and_direct_fill_racing_same_stale_cache_work()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 9);
        DocumentCacheMaterializationCandidate projectorCandidate = CreateCandidate(
            source,
            "projector-race-winner"
        );
        DocumentCacheMaterializationCandidate directFillCandidate = CreateCandidate(
            source,
            "direct-fill-race-contender"
        );
        PausingFaultInjectionObserver observer = new(
            DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement
        );
        MssqlDocumentCacheWriter pausedProjectorWriter = CreateWriter(observer);

        Task<DocumentCacheWriterResult> projectorWrite = pausedProjectorWriter.WriteAsync(
            CreateRequest(source, projectorCandidate, DocumentCacheWriterPurpose.DurableWorkProjection)
        );

        await observer.WaitUntilReachedAsync(TimeSpan.FromSeconds(10));

        Task<DocumentCacheWriterResult> directFillWrite = _writer.WriteAsync(
            CreateRequest(source, directFillCandidate, DocumentCacheWriterPurpose.DirectFill)
        );

        observer.Release();

        DocumentCacheWriterResult[] results = await Task.WhenAll(projectorWrite, directFillWrite)
            .WaitAsync(TimeSpan.FromSeconds(30));
        DocumentCacheWriterOutcome[] outcomes = results.Select(result => result.Outcome).ToArray();

        outcomes
            .Count(outcome => outcome == DocumentCacheWriterOutcome.CandidateWrittenAcknowledged)
            .Should()
            .Be(1);
        outcomes
            .Should()
            .BeSubsetOf([
                DocumentCacheWriterOutcome.CandidateWrittenAcknowledged,
                DocumentCacheWriterOutcome.AlreadyCurrentNoWork,
                DocumentCacheWriterOutcome.RacingWriterLost,
            ]);

        results[0]
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);

        CacheRow cacheRow = await ReadCacheRowAsync(source.DocumentId);
        cacheRow.ContentVersion.Should().Be(10);
        JsonNode.Parse(cacheRow.DocumentJson)!["value"]!
            .GetValue<string>()
            .Should()
            .Be("projector-race-winner");
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_rolls_back_cache_write_when_source_and_work_advance_before_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-current");
        PausingFaultInjectionObserver observer = new(
            DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement
        );
        MssqlDocumentCacheWriter pausedWriter = CreateWriter(observer);

        Task<DocumentCacheWriterResult> firstWrite = pausedWriter.WriteAsync(
            CreateRequest(source, candidate)
        );

        await observer.WaitUntilReachedAsync(TimeSpan.FromSeconds(10));
        await AdvanceSourceAndWorkVersionAsync(source.DocumentId, contentVersion: 11);

        observer.Release();

        DocumentCacheWriterResult result = await firstWrite.WaitAsync(TimeSpan.FromSeconds(30));

        result.Should().BeSameAs(DocumentCacheWriterResult.RacingWriterLost.Instance);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkRequiredContentVersionAsync(source.DocumentId)).Should().Be(11);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    [Test]
    [Category("DocumentCacheWriterConcurrency")]
    public async Task DocumentCacheWriterConcurrency_it_blocks_same_document_canonical_enqueue_during_acknowledgement_without_blocking_unrelated_documents()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 10);
        SourceDocument unrelatedSource = await InsertSourceDocumentAsync(contentVersion: 20);
        PausingFaultInjectionObserver observer = new(
            DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit
        );
        MssqlDocumentCacheWriter pausedWriter = CreateWriter(observer);

        Task<DocumentCacheWriterResult> acknowledgement = pausedWriter.WriteAsync(
            CreateRequest(source, candidate: null)
        );

        await observer.WaitUntilReachedAsync(TimeSpan.FromSeconds(10));

        await AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
            unrelatedSource.DocumentId,
            contentVersion: 21
        );

        SqlException exception = (
            await FluentActions
                .Awaiting(() =>
                    AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
                        source.DocumentId,
                        contentVersion: 11
                    )
                )
                .Should()
                .ThrowAsync<SqlException>()
        ).Which;

        exception.Number.Should().Be(1222);
        new MssqlRelationalWriteExceptionClassifier().IsTransientFailure(exception).Should().BeTrue();

        observer.Release();

        DocumentCacheWriterResult result = await acknowledgement.WaitAsync(TimeSpan.FromSeconds(30));

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadSourceContentVersionAsync(source.DocumentId)).Should().Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadSourceContentVersionAsync(unrelatedSource.DocumentId)).Should().Be(21);
        (await ReadWorkRequiredContentVersionAsync(unrelatedSource.DocumentId)).Should().Be(21);
    }

    [Test]
    [Category("DocumentCacheWriterTelemetry")]
    public async Task DocumentCacheWriterTelemetry_it_records_expected_metric_coverage()
    {
        var telemetry = new RecordingDocumentCacheWriterTelemetry();
        _writer = CreateWriter(telemetry: telemetry, maxRetryAttempts: 1);
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);

        SourceDocument equalVersion = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(equalVersion, contentVersion: 10);
        (await WriteAsync(equalVersion, candidate: null, DocumentCacheWriterPurpose.DurableWorkProjection))
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>();

        SourceDocument directFillCandidate = await InsertSourceDocumentAsync(contentVersion: 20);
        (
            await WriteAsync(
                directFillCandidate,
                CreateCandidate(directFillCandidate, "performance-direct-fill"),
                DocumentCacheWriterPurpose.DirectFill
            )
        )
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();

        SourceDocument directFillNoCandidate = await InsertSourceDocumentAsync(contentVersion: 30);
        (await WriteAsync(directFillNoCandidate, candidate: null, DocumentCacheWriterPurpose.DirectFill))
            .Should()
            .BeOfType<DocumentCacheWriterResult.NeedsMaterialization>();

        SourceDocument staleCandidate = await InsertSourceDocumentAsync(contentVersion: 40);
        (
            await WriteAsync(
                staleCandidate,
                CreateCandidate(staleCandidate, "performance-stale", contentVersion: 39),
                DocumentCacheWriterPurpose.DurableWorkProjection
            )
        )
            .Should()
            .BeOfType<DocumentCacheWriterResult.StaleCandidateSuppressed>();

        SourceDocument duplicate = await InsertSourceDocumentAsync(contentVersion: 50);
        DocumentCacheMaterializationCandidate duplicateCandidate = CreateCandidate(
            duplicate,
            "performance-duplicate"
        );
        var pauseBeforeAcknowledgement = new PausingFaultInjectionObserver(
            DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement
        );
        MssqlDocumentCacheWriter pausedWriter = CreateWriter(
            pauseBeforeAcknowledgement,
            telemetry,
            maxRetryAttempts: 1
        );
        Task<DocumentCacheWriterResult> firstDuplicateWrite = pausedWriter.WriteAsync(
            CreateRequest(duplicate, duplicateCandidate, DocumentCacheWriterPurpose.DurableWorkProjection)
        );
        await pauseBeforeAcknowledgement.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));
        Task<DocumentCacheWriterResult> secondDuplicateWrite = _writer.WriteAsync(
            CreateRequest(duplicate, duplicateCandidate, DocumentCacheWriterPurpose.DurableWorkProjection)
        );
        pauseBeforeAcknowledgement.Release();
        DocumentCacheWriterResult[] duplicateResults = await Task.WhenAll(
                firstDuplicateWrite,
                secondDuplicateWrite
            )
            .WaitAsync(TimeSpan.FromSeconds(10));

        DocumentCacheWriterOutcome[] duplicateOutcomes = duplicateResults
            .Select(result => result.Outcome)
            .ToArray();
        duplicateOutcomes
            .Count(outcome => outcome == DocumentCacheWriterOutcome.CandidateWrittenAcknowledged)
            .Should()
            .Be(1);
        duplicateOutcomes
            .Should()
            .BeSubsetOf([
                DocumentCacheWriterOutcome.CandidateWrittenAcknowledged,
                DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged,
                DocumentCacheWriterOutcome.AlreadyCurrentNoWork,
                DocumentCacheWriterOutcome.RacingWriterLost,
            ]);
        (await ReadCacheCountAsync(duplicate.DocumentId)).Should().Be(1);
        (await ReadWorkCountAsync(duplicate.DocumentId)).Should().Be(0);

        SourceDocument canonicalContention = await InsertSourceDocumentAsync(contentVersion: 60);
        await InsertCacheRowAsync(canonicalContention, contentVersion: 60);
        SourceDocument unrelatedContention = await InsertSourceDocumentAsync(contentVersion: 65);
        var pauseAfterAcknowledgement = new PausingFaultInjectionObserver(
            DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit
        );
        MssqlDocumentCacheWriter acknowledgementHoldingWriter = CreateWriter(
            pauseAfterAcknowledgement,
            telemetry,
            maxRetryAttempts: 1
        );
        Task<DocumentCacheWriterResult> acknowledgement = acknowledgementHoldingWriter.WriteAsync(
            CreateRequest(
                canonicalContention,
                candidate: null,
                DocumentCacheWriterPurpose.DurableWorkProjection
            )
        );
        await pauseAfterAcknowledgement.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));
        await AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
            unrelatedContention.DocumentId,
            contentVersion: 66
        );
        (
            await FluentActions
                .Awaiting(() =>
                    AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
                        canonicalContention.DocumentId,
                        contentVersion: 61
                    )
                )
                .Should()
                .ThrowAsync<SqlException>()
        )
            .Which.Number.Should()
            .Be(1222);
        pauseAfterAcknowledgement.Release();
        (await acknowledgement.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>();
        (await ReadSourceContentVersionAsync(canonicalContention.DocumentId)).Should().Be(60);
        (await ReadSourceContentVersionAsync(unrelatedContention.DocumentId)).Should().Be(66);

        SourceDocument retry = await InsertSourceDocumentAsync(contentVersion: 70);
        var transientFault = new ThrowOnceMssqlTransientFaultInjectionObserver();
        MssqlDocumentCacheWriter retryingWriter = CreateWriter(
            transientFault,
            telemetry,
            maxRetryAttempts: 1
        );
        (
            await retryingWriter.WriteAsync(
                CreateRequest(
                    retry,
                    CreateCandidate(retry, "performance-retry"),
                    DocumentCacheWriterPurpose.DurableWorkProjection
                )
            )
        )
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();
        transientFault.ObservedAttemptCount.Should().Be(2);

        SourceDocument cacheAhead = await InsertSourceDocumentAsync(contentVersion: 80);
        await InsertCacheRowAsync(cacheAhead, contentVersion: 81);
        (await WriteAsync(cacheAhead, candidate: null, DocumentCacheWriterPurpose.DirectFill))
            .Should()
            .BeOfType<DocumentCacheWriterResult.CacheAheadLatchSet>();
        (await ReadCacheAheadLatchAsync()).Should().BeTrue();

        AssertDocumentCacheWriterTelemetryCoverage(telemetry, "sqlserver");
    }

    [Test]
    [Explicit("Component-level performance evidence for DMS-1313; not a correctness gate.")]
    [Category("DocumentCacheWriterPerformanceEvidence")]
    public async Task DocumentCacheWriterPerformanceEvidence_it_compares_projector_and_direct_fill_workload_modes()
    {
        var telemetry = new RecordingDocumentCacheWriterTelemetry();
        _writer = CreateWriter(telemetry: telemetry, maxRetryAttempts: 1);
        List<DocumentCacheWriterPerformanceEvidenceRow> evidence = [];

        foreach (DocumentCacheWriterPurpose purpose in PerformanceEvidencePurposes())
        {
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "candidate-write",
                    purpose,
                    telemetry,
                    PerformanceEvidenceBatchSize,
                    () => RunCandidateWritePerformanceEvidenceAsync(purpose, PerformanceEvidenceBatchSize)
                )
            );
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "equal-version-acknowledgement",
                    purpose,
                    telemetry,
                    PerformanceEvidenceBatchSize,
                    () =>
                        RunEqualVersionAcknowledgementPerformanceEvidenceAsync(
                            purpose,
                            PerformanceEvidenceBatchSize
                        )
                )
            );
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "needs-materialization",
                    purpose,
                    telemetry,
                    PerformanceEvidenceBatchSize,
                    () =>
                        RunNeedsMaterializationPerformanceEvidenceAsync(purpose, PerformanceEvidenceBatchSize)
                )
            );
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "duplicate-writer-contention",
                    purpose,
                    telemetry,
                    PerformanceEvidenceContentionCount,
                    () =>
                        RunDuplicateWriterContentionPerformanceEvidenceAsync(
                            telemetry,
                            purpose,
                            PerformanceEvidenceContentionCount
                        )
                )
            );
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "canonical-acknowledgement-contention",
                    purpose,
                    telemetry,
                    operationCount: 1,
                    () => RunCanonicalContentionPerformanceEvidenceAsync(telemetry, purpose)
                )
            );
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "retry-candidate-write",
                    purpose,
                    telemetry,
                    operationCount: 1,
                    () => RunRetryPerformanceEvidenceAsync(telemetry, purpose)
                )
            );
            evidence.Add(
                await MeasurePerformanceEvidenceScenarioAsync(
                    "sqlserver",
                    "cache-ahead-incident",
                    purpose,
                    telemetry,
                    operationCount: 1,
                    () => RunCacheAheadIncidentPerformanceEvidenceAsync(purpose)
                )
            );
        }

        AssertDocumentCacheWriterPerformanceEvidence(evidence, telemetry, "sqlserver");
        WriteDocumentCacheWriterPerformanceEvidence(evidence, "sqlserver");
    }

    private async Task<DocumentCacheWriterResult> WriteAsync(
        SourceDocument source,
        DocumentCacheMaterializationCandidate? candidate,
        DocumentCacheWriterPurpose purpose = DocumentCacheWriterPurpose.DurableWorkProjection
    ) => await _writer.WriteAsync(CreateRequest(source, candidate, purpose));

    private DocumentCacheWriterRequest CreateRequest(
        SourceDocument source,
        DocumentCacheMaterializationCandidate? candidate,
        DocumentCacheWriterPurpose purpose = DocumentCacheWriterPurpose.DurableWorkProjection,
        CancellationToken cancellationToken = default
    ) =>
        new(
            CreateTargetContext(),
            source.DocumentId,
            selectedRequiredContentVersion: source.ContentVersion,
            purpose,
            candidate,
            cancellationToken
        );

    private async Task RunCandidateWritePerformanceEvidenceAsync(
        DocumentCacheWriterPurpose purpose,
        int count
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);

        for (int index = 0; index < count; index++)
        {
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
            DocumentCacheWriterResult result = await WriteAsync(
                source,
                CreateCandidate(source, $"performance-candidate-{purpose}-{index}"),
                purpose
            );

            result.Should().BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();
        }
    }

    private async Task RunEqualVersionAcknowledgementPerformanceEvidenceAsync(
        DocumentCacheWriterPurpose purpose,
        int count
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);

        for (int index = 0; index < count; index++)
        {
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 20);
            await InsertCacheRowAsync(source, contentVersion: 20);

            DocumentCacheWriterResult result = await WriteAsync(source, candidate: null, purpose);

            result.Should().BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>();
        }
    }

    private async Task RunNeedsMaterializationPerformanceEvidenceAsync(
        DocumentCacheWriterPurpose purpose,
        int count
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);

        for (int index = 0; index < count; index++)
        {
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 30);

            DocumentCacheWriterResult result = await WriteAsync(source, candidate: null, purpose);

            result.Should().BeOfType<DocumentCacheWriterResult.NeedsMaterialization>();
        }
    }

    private async Task RunDuplicateWriterContentionPerformanceEvidenceAsync(
        RecordingDocumentCacheWriterTelemetry telemetry,
        DocumentCacheWriterPurpose purpose,
        int count
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);

        for (int index = 0; index < count; index++)
        {
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 40);
            DocumentCacheMaterializationCandidate candidate = CreateCandidate(
                source,
                $"performance-duplicate-{purpose}-{index}"
            );
            var pauseBeforeAcknowledgement = new PausingFaultInjectionObserver(
                DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement
            );
            MssqlDocumentCacheWriter pausedWriter = CreateWriter(
                pauseBeforeAcknowledgement,
                telemetry,
                maxRetryAttempts: 1
            );

            Task<DocumentCacheWriterResult> firstWrite = pausedWriter.WriteAsync(
                CreateRequest(source, candidate, purpose)
            );
            await pauseBeforeAcknowledgement.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));
            Task<DocumentCacheWriterResult> secondWrite = _writer.WriteAsync(
                CreateRequest(source, candidate, purpose)
            );

            pauseBeforeAcknowledgement.Release();
            DocumentCacheWriterResult[] results = await Task.WhenAll(firstWrite, secondWrite)
                .WaitAsync(TimeSpan.FromSeconds(10));
            DocumentCacheWriterOutcome[] outcomes = results.Select(result => result.Outcome).ToArray();

            outcomes
                .Count(outcome => outcome == DocumentCacheWriterOutcome.CandidateWrittenAcknowledged)
                .Should()
                .Be(1);
            outcomes
                .Should()
                .BeSubsetOf([
                    DocumentCacheWriterOutcome.CandidateWrittenAcknowledged,
                    DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged,
                    DocumentCacheWriterOutcome.RacingWriterLost,
                ]);
        }
    }

    private async Task RunCanonicalContentionPerformanceEvidenceAsync(
        RecordingDocumentCacheWriterTelemetry telemetry,
        DocumentCacheWriterPurpose purpose
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 50);
        await InsertCacheRowAsync(source, contentVersion: 50);
        var pauseAfterAcknowledgement = new PausingFaultInjectionObserver(
            DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit
        );
        MssqlDocumentCacheWriter acknowledgementHoldingWriter = CreateWriter(
            pauseAfterAcknowledgement,
            telemetry,
            maxRetryAttempts: 1
        );

        Task<DocumentCacheWriterResult> acknowledgement = acknowledgementHoldingWriter.WriteAsync(
            CreateRequest(source, candidate: null, purpose)
        );
        await pauseAfterAcknowledgement.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));

        try
        {
            (
                await FluentActions
                    .Awaiting(() =>
                        AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
                            source.DocumentId,
                            contentVersion: 51
                        )
                    )
                    .Should()
                    .ThrowAsync<SqlException>()
            )
                .Which.Number.Should()
                .Be(1222);
        }
        finally
        {
            pauseAfterAcknowledgement.Release();
        }

        (await acknowledgement.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>();
    }

    private async Task RunRetryPerformanceEvidenceAsync(
        RecordingDocumentCacheWriterTelemetry telemetry,
        DocumentCacheWriterPurpose purpose
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 60);
        var transientFault = new ThrowOnceMssqlTransientFaultInjectionObserver();
        MssqlDocumentCacheWriter retryingWriter = CreateWriter(
            transientFault,
            telemetry,
            maxRetryAttempts: 1
        );

        (
            await retryingWriter.WriteAsync(
                CreateRequest(source, CreateCandidate(source, $"performance-retry-{purpose}"), purpose)
            )
        )
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();
        transientFault.ObservedAttemptCount.Should().Be(2);
    }

    private async Task RunCacheAheadIncidentPerformanceEvidenceAsync(DocumentCacheWriterPurpose purpose)
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 70);
        await InsertCacheRowAsync(source, contentVersion: 71);

        (await WriteAsync(source, candidate: null, purpose))
            .Should()
            .BeOfType<DocumentCacheWriterResult.CacheAheadLatchSet>();
        (await ReadCacheAheadLatchAsync()).Should().BeTrue();
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
    }

    private static async Task<DocumentCacheWriterPerformanceEvidenceRow> MeasurePerformanceEvidenceScenarioAsync(
        string provider,
        string scenario,
        DocumentCacheWriterPurpose purpose,
        RecordingDocumentCacheWriterTelemetry telemetry,
        int operationCount,
        Func<Task> runScenario
    )
    {
        int startingRecordCount = telemetry.Records.Count;
        long startTimestamp = Stopwatch.GetTimestamp();

        await runScenario();

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        TelemetryRecord[] scenarioRecords = telemetry.Records.Skip(startingRecordCount).ToArray();

        return new DocumentCacheWriterPerformanceEvidenceRow(
            provider,
            scenario,
            purpose,
            operationCount,
            elapsed,
            OutcomeCount: scenarioRecords.Count(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.Outcome
            ),
            TransactionDurationCount: scenarioRecords.Count(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.TransactionDuration
            ),
            CacheDmlDurationCount: scenarioRecords.Count(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.CacheDmlDuration
            ),
            AcknowledgementDurationCount: scenarioRecords.Count(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.AcknowledgementDuration
            ),
            RetryCount: scenarioRecords.Count(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.Retry
            ),
            SameDocumentWaitCount: scenarioRecords.Count(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.SameDocumentWait
            )
        );
    }

    private async Task<(
        SourceDocument Source,
        DocumentCacheMaterializationCandidate? Candidate
    )> PrepareCancellationRollbackScenarioAsync(DocumentCacheWriterFaultInjectionHook hook)
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);

        if (hook == DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit)
        {
            await InsertCacheRowAsync(source, contentVersion: 10);
            return (source, Candidate: null);
        }

        return (source, CreateCandidate(source, "candidate-canceled"));
    }

    private static void AssertCancellationCompletedWriterResult(
        DocumentCacheWriterFaultInjectionHook hook,
        DocumentCacheWriterResult result
    )
    {
        if (hook == DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit)
        {
            result
                .Should()
                .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>()
                .Which.AcknowledgedContentVersion.Should()
                .Be(10);
            return;
        }

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
    }

    private async Task AssertCancellationCompletedStateAsync(SourceDocument source)
    {
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(1);
        (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheAheadLatchAsync()).Should().BeFalse();
    }

    private DocumentCacheMaterializationTargetContext CreateTargetContext() =>
        new(
            TargetKey,
            _fixture.MappingSet,
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            _database.ConnectionString
        );

    private async Task SetLifecycleAsync(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired = false
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycleState,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@lifecycleState", SqlDbType.VarChar, 16) { Value = lifecycleState.ToString() },
            new SqlParameter("@cacheAheadRecoveryRequired", SqlDbType.Bit)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );
    }

    private async Task DeleteLifecycleStateAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        );
    }

    private async Task SetInvalidLifecycleStateAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE [dms].[DocumentCacheState]
            DROP CONSTRAINT [CK_DocumentCacheState_Lifecycle];

            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = 'Paused'
            WHERE [StateId] = 1;
            """
        );
    }

    private async Task MakeLifecycleStateUnreadableAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE [dms].[DocumentCacheState]
            DROP COLUMN [CacheAheadRecoveryRequired];
            """
        );
    }

    private static async Task LockLifecycleStateForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM [dms].[DocumentCacheState] WITH (XLOCK, HOLDLOCK)
            WHERE [StateId] = 1;
            """;

        (await command.ExecuteScalarAsync()).Should().NotBeNull();
    }

    private async Task DeleteWorkAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[DocumentProjectionWork]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );
    }

    private async Task DeleteSourceDocumentAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );
    }

    private async Task SetWorkRequiredContentVersionAsync(long documentId, long requiredContentVersion)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentProjectionWork]
            SET [RequiredContentVersion] = @requiredContentVersion
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = requiredContentVersion }
        );
    }

    private async Task AdvanceSourceAndWorkVersionAsync(long documentId, long contentVersion)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await AdvanceSourceAndWorkVersionAsync(connection, transaction, documentId, contentVersion);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task AdvanceSourceAndWorkVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId,
        long contentVersion
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ContentVersion] = @contentVersion,
                [ContentLastModifiedAt] = @lastModifiedAt
            WHERE [DocumentId] = @documentId;

            UPDATE [dms].[DocumentProjectionWork]
            SET [RequiredContentVersion] = @contentVersion
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });
        command.Parameters.Add(
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion }
        );
        command.Parameters.Add(
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2)
            {
                Value = new DateTime(2026, 7, 31, 12, 5, 0, DateTimeKind.Utc),
            }
        );

        await command.ExecuteNonQueryAsync();
    }

    private async Task AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
        long documentId,
        long contentVersion
    )
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SET LOCK_TIMEOUT 100;

                UPDATE [dms].[Document]
                SET [ContentVersion] = @contentVersion,
                    [ContentLastModifiedAt] = @lastModifiedAt
                WHERE [DocumentId] = @documentId;
                """;
            command.Parameters.Add(
                new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion }
            );
            command.Parameters.Add(
                new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2)
                {
                    Value = new DateTime(2026, 7, 31, 12, 10, 0, DateTimeKind.Utc),
                }
            );
            command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });

            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<SourceDocument> InsertSourceDocumentAsync(long contentVersion)
    {
        var documentUuid = Guid.NewGuid();
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            DECLARE @insertedDocument TABLE ([DocumentId] bigint NOT NULL);

            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            OUTPUT INSERTED.[DocumentId] INTO @insertedDocument
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            );

            SELECT [DocumentId]
            FROM @insertedDocument;
            """,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = documentUuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.UtcDateTime }
        );

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid, contentVersion);
    }

    private async Task InsertCacheRowAsync(
        SourceDocument source,
        long contentVersion,
        DateTime? computedAt = null
    )
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[
            _fixture.MappingSet.ResourceKeyIdByResource[PersonResource]
        ];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[DocumentCache] (
                [DocumentId],
                [DocumentUuid],
                [ProjectName],
                [ResourceName],
                [ResourceVersion],
                [ContentVersion],
                [StreamEtag],
                [LastModifiedAt],
                [DocumentJson],
                [ComputedAt]
            )
            VALUES (
                @documentId,
                @documentUuid,
                @projectName,
                @resourceName,
                @resourceVersion,
                @contentVersion,
                @streamEtag,
                @lastModifiedAt,
                @documentJson,
                @computedAt
            );
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = source.DocumentId },
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = source.DocumentUuid },
            new SqlParameter("@projectName", SqlDbType.VarChar, 256)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new SqlParameter("@resourceName", SqlDbType.VarChar, 256)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new SqlParameter("@resourceVersion", SqlDbType.VarChar, 32)
            {
                Value = resourceKey.ResourceVersion,
            },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64) { Value = $"etag-{contentVersion}" },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.UtcDateTime },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = $"cache-{contentVersion}" }.ToJsonString(),
            },
            new SqlParameter("@computedAt", SqlDbType.DateTime2)
            {
                Value = computedAt ?? new DateTime(2026, 7, 31, 12, 1, 0, DateTimeKind.Utc),
            }
        );
    }

    private static DocumentCacheMaterializationCandidate CreateCandidate(
        SourceDocument source,
        string value,
        long? contentVersion = null,
        Guid? documentUuid = null,
        string projectName = "Ed-Fi",
        string resourceName = "Person",
        string resourceVersion = "5.0.0"
    ) =>
        new(
            source.DocumentId,
            new DocumentUuid(documentUuid ?? source.DocumentUuid),
            projectName,
            resourceName,
            resourceVersion,
            contentVersion ?? source.ContentVersion,
            LastModifiedAt,
            $"etag-{contentVersion ?? source.ContentVersion}",
            new JsonObject { ["value"] = value }
        );

    private async Task<long> ReadWorkCountAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM [dms].[DocumentProjectionWork]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private async Task<long> ReadSourceContentVersionAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private async Task<long> ReadWorkRequiredContentVersionAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT [RequiredContentVersion]
            FROM [dms].[DocumentProjectionWork]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private async Task<long> ReadCacheCountAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM [dms].[DocumentCache]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private async Task<CacheRow> ReadCacheRowAsync(long documentId)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT
                [ContentVersion],
                [StreamEtag],
                [DocumentJson]
            FROM [dms].[DocumentCache]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );
        IReadOnlyDictionary<string, object?> row = rows.Should().ContainSingle().Subject;

        return new CacheRow(
            Convert.ToInt64(row["ContentVersion"]),
            (string)row["StreamEtag"]!,
            (string)row["DocumentJson"]!
        );
    }

    private async Task<DateTime> ReadCacheComputedAtAsync(long documentId) =>
        await _database.ExecuteScalarAsync<DateTime>(
            """
            SELECT [ComputedAt]
            FROM [dms].[DocumentCache]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private async Task<bool> ReadCacheAheadLatchAsync() =>
        await _database.ExecuteScalarAsync<bool>(
            """
            SELECT [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        );

    private async Task SetReadCommittedSnapshotAsync(bool enabled)
    {
        SqlConnection.ClearAllPools();
        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            ALTER DATABASE {MssqlTestDatabaseHelper.QuoteIdentifier(_database.DatabaseName)}
            SET READ_COMMITTED_SNAPSHOT {(enabled ? "ON" : "OFF")} WITH ROLLBACK IMMEDIATE;
            """
        );
    }

    private async Task WaitForMssqlLatchUpdateToWaitOnSourceRowAsync(
        Task<DocumentCacheWriterResult> writeTask
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (writeTask.IsCompleted)
            {
                throw new AssertionException(
                    "DocumentCache writer completed before waiting on the current cache-ahead latch predicate."
                );
            }

            long waitingSessionCount = await _database.ExecuteScalarAsync<long>(
                """
                SELECT COUNT_BIG(*)
                FROM sys.dm_exec_requests AS requests
                CROSS APPLY sys.dm_exec_sql_text(requests.sql_handle) AS sql_text
                WHERE requests.database_id = DB_ID()
                  AND requests.session_id <> @@SPID
                  AND requests.wait_type LIKE 'LCK_M_%'
                  AND sql_text.text LIKE '%CacheAheadRecoveryRequired%'
                  AND sql_text.text LIKE '%HOLDLOCK%';
                """
            );

            if (waitingSessionCount > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new AssertionException(
            "Timed out waiting for DocumentCache writer latch update to wait on the source row."
        );
    }

    private static DocumentCacheWriterResult.LifecycleOrLatchFenced AssertLifecycleFence(
        DocumentCacheWriterResult result,
        DocumentCacheWriterFenceReason reason
    )
    {
        DocumentCacheWriterResult.LifecycleOrLatchFenced fenced = result
            .Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Subject;
        fenced.Reason.Should().Be(reason);
        return fenced;
    }

    private static void AssertDocumentCacheWriterPerformanceEvidence(
        IReadOnlyCollection<DocumentCacheWriterPerformanceEvidenceRow> evidence,
        RecordingDocumentCacheWriterTelemetry telemetry,
        string provider
    )
    {
        string[] requiredScenarios =
        [
            "candidate-write",
            "equal-version-acknowledgement",
            "needs-materialization",
            "duplicate-writer-contention",
            "canonical-acknowledgement-contention",
            "retry-candidate-write",
            "cache-ahead-incident",
        ];

        evidence.Should().HaveCount(requiredScenarios.Length * PerformanceEvidencePurposes().Length);
        evidence.Should().OnlyContain(row => row.Provider == provider);
        evidence.Should().OnlyContain(row => row.OperationCount > 0);
        evidence.Should().OnlyContain(row => row.Elapsed >= TimeSpan.Zero);
        evidence.Should().OnlyContain(row => row.OutcomeCount > 0);
        evidence.Should().OnlyContain(row => row.TransactionDurationCount > 0);

        foreach (DocumentCacheWriterPurpose purpose in PerformanceEvidencePurposes())
        {
            foreach (string scenario in requiredScenarios)
            {
                evidence.Should().ContainSingle(row => row.Scenario == scenario && row.Purpose == purpose);
            }

            string purposeLabel = purpose.ToString();
            telemetry
                .Records.Should()
                .Contain(record =>
                    record.Name == RecordingDocumentCacheWriterTelemetry.CacheDmlDuration
                    && record.Context.Purpose == purposeLabel
                );
            telemetry
                .Records.Should()
                .Contain(record =>
                    record.Name == RecordingDocumentCacheWriterTelemetry.AcknowledgementDuration
                    && record.Context.Purpose == purposeLabel
                );
            telemetry
                .Records.Should()
                .Contain(record =>
                    record.Name == RecordingDocumentCacheWriterTelemetry.Retry
                    && record.AttemptCount == 2
                    && record.Context.Purpose == purposeLabel
                );
            telemetry
                .Records.Should()
                .Contain(record =>
                    record.Name == RecordingDocumentCacheWriterTelemetry.SameDocumentWait
                    && record.Context.Purpose == purposeLabel
                );
        }

        telemetry
            .Records.Select(record => record.Context)
            .Where(context => !IsExpectedTelemetryContext(context, provider))
            .Should()
            .BeEmpty();
    }

    private static void WriteDocumentCacheWriterPerformanceEvidence(
        IReadOnlyCollection<DocumentCacheWriterPerformanceEvidenceRow> evidence,
        string provider
    )
    {
        List<string> lines = [$"DocumentCacheWriter performance evidence provider={provider}"];

        foreach (
            IGrouping<string, DocumentCacheWriterPerformanceEvidenceRow> scenarioGroup in evidence
                .GroupBy(row => row.Scenario)
                .OrderBy(group => group.Key)
        )
        {
            foreach (
                DocumentCacheWriterPerformanceEvidenceRow row in scenarioGroup.OrderBy(row => row.Purpose)
            )
            {
                lines.Add(FormatPerformanceEvidenceRow(row));
            }

            DocumentCacheWriterPerformanceEvidenceRow projector = scenarioGroup.Single(row =>
                row.Purpose == DocumentCacheWriterPurpose.DurableWorkProjection
            );
            DocumentCacheWriterPerformanceEvidenceRow directFill = scenarioGroup.Single(row =>
                row.Purpose == DocumentCacheWriterPurpose.DirectFill
            );

            lines.Add(
                "provider="
                    + provider
                    + " scenario="
                    + scenarioGroup.Key
                    + " directfill_to_projector_elapsed_ratio="
                    + FormatElapsedRatio(directFill, projector)
            );
        }

        foreach (string line in lines)
        {
            TestContext.Progress.WriteLine(line);
        }

        string attachmentPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"document-cache-writer-performance-evidence-{provider}.txt"
        );
        File.WriteAllLines(attachmentPath, lines);
        TestContext.AddTestAttachment(
            attachmentPath,
            $"DocumentCacheWriter component performance evidence for {provider}."
        );
    }

    private static string FormatPerformanceEvidenceRow(DocumentCacheWriterPerformanceEvidenceRow row) =>
        "provider="
        + row.Provider
        + " scenario="
        + row.Scenario
        + " purpose="
        + row.Purpose
        + " count="
        + row.OperationCount.ToString(CultureInfo.InvariantCulture)
        + " elapsed_ms="
        + FormatMilliseconds(row.Elapsed)
        + " avg_ms="
        + FormatAverageMilliseconds(row)
        + " outcome_samples="
        + row.OutcomeCount.ToString(CultureInfo.InvariantCulture)
        + " transaction_samples="
        + row.TransactionDurationCount.ToString(CultureInfo.InvariantCulture)
        + " cache_dml_samples="
        + row.CacheDmlDurationCount.ToString(CultureInfo.InvariantCulture)
        + " acknowledgement_samples="
        + row.AcknowledgementDurationCount.ToString(CultureInfo.InvariantCulture)
        + " retry_samples="
        + row.RetryCount.ToString(CultureInfo.InvariantCulture)
        + " same_document_wait_samples="
        + row.SameDocumentWaitCount.ToString(CultureInfo.InvariantCulture);

    private static string FormatAverageMilliseconds(DocumentCacheWriterPerformanceEvidenceRow row) =>
        (row.Elapsed.TotalMilliseconds / row.OperationCount).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatElapsedRatio(
        DocumentCacheWriterPerformanceEvidenceRow numerator,
        DocumentCacheWriterPerformanceEvidenceRow denominator
    ) =>
        denominator.Elapsed == TimeSpan.Zero
            ? "undefined"
            : (numerator.Elapsed.TotalMilliseconds / denominator.Elapsed.TotalMilliseconds).ToString(
                "0.###",
                CultureInfo.InvariantCulture
            );

    private static string FormatMilliseconds(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static DocumentCacheWriterPurpose[] PerformanceEvidencePurposes() =>
        [DocumentCacheWriterPurpose.DurableWorkProjection, DocumentCacheWriterPurpose.DirectFill];

    private static void AssertDocumentCacheWriterTelemetryCoverage(
        RecordingDocumentCacheWriterTelemetry telemetry,
        string provider
    )
    {
        telemetry
            .Records.Where(record => record.Name == RecordingDocumentCacheWriterTelemetry.Outcome)
            .Select(record => record.Context.Outcome)
            .Should()
            .Contain([
                nameof(DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged),
                nameof(DocumentCacheWriterOutcome.CandidateWrittenAcknowledged),
                nameof(DocumentCacheWriterOutcome.NeedsMaterialization),
                nameof(DocumentCacheWriterOutcome.StaleCandidateSuppressed),
                nameof(DocumentCacheWriterOutcome.CacheAheadLatchSet),
            ]);

        telemetry
            .Records.Select(record => record.Context.Purpose)
            .Should()
            .Contain([
                nameof(DocumentCacheWriterPurpose.DurableWorkProjection),
                nameof(DocumentCacheWriterPurpose.DirectFill),
            ]);
        telemetry
            .Records.Should()
            .Contain(record => record.Name == RecordingDocumentCacheWriterTelemetry.TransactionDuration);
        telemetry
            .Records.Should()
            .Contain(record => record.Name == RecordingDocumentCacheWriterTelemetry.CacheDmlDuration);
        telemetry
            .Records.Should()
            .Contain(record => record.Name == RecordingDocumentCacheWriterTelemetry.AcknowledgementDuration);
        AssertDurationCoverageByPurpose(telemetry);
        telemetry
            .Records.Should()
            .Contain(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.Retry
                && record.AttemptCount == 2
                && record.Context.Outcome == nameof(DocumentCacheWriterOutcome.CandidateWrittenAcknowledged)
            );
        telemetry
            .Records.Should()
            .Contain(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.SameDocumentWait
                && record.Participant == DocumentCacheWriterContentionParticipant.CacheWriter
                && record.Phase == DocumentCacheWriterContentionPhase.CacheDml
            );
        telemetry
            .Records.Should()
            .Contain(record =>
                record.Name == RecordingDocumentCacheWriterTelemetry.SameDocumentWait
                && record.Participant == DocumentCacheWriterContentionParticipant.CacheWriter
                && record.Phase == DocumentCacheWriterContentionPhase.Acknowledgement
            );

        telemetry
            .Records.Select(record => record.Context)
            .Where(context => !IsExpectedTelemetryContext(context, provider))
            .Should()
            .BeEmpty();
        string labels = string.Join(
            "|",
            telemetry
                .Records.SelectMany(record =>
                    new[]
                    {
                        record.Context.Provider,
                        record.Context.TargetKey,
                        record.Context.Purpose,
                        record.Context.Lifecycle,
                        record.Context.Outcome,
                    }
                )
                .Distinct()
        );
        labels.Should().NotContain("DocumentId");
        labels.Should().NotContain("DocumentUuid");
        labels.Should().NotContain("DocumentJson");
        labels.Should().NotContain("authorization");
        labels.Should().NotContain("Person");
    }

    private static void AssertDurationCoverageByPurpose(RecordingDocumentCacheWriterTelemetry telemetry)
    {
        var totalDurationByPurpose = telemetry
            .Records.Where(record => record.Duration is not null)
            .GroupBy(record => record.Context.Purpose)
            .ToDictionary(
                group => group.Key,
                group => TimeSpan.FromTicks(group.Sum(record => record.Duration!.Value.Ticks))
            );

        totalDurationByPurpose
            .Keys.Should()
            .Contain([
                nameof(DocumentCacheWriterPurpose.DurableWorkProjection),
                nameof(DocumentCacheWriterPurpose.DirectFill),
            ]);
        totalDurationByPurpose.Values.Should().OnlyContain(duration => duration >= TimeSpan.Zero);
    }

    private static bool IsExpectedTelemetryContext(DocumentCacheWriterMetricContext context, string provider)
    {
        string joinedLabels = string.Join(
            "|",
            context.Provider,
            context.TargetKey,
            context.Purpose,
            context.Lifecycle,
            context.Outcome
        );

        return context.Provider == provider
            && context.TargetKey == ExpectedTelemetryTargetLabel
            && context.Provider.Length <= 128
            && context.TargetKey.Length <= 128
            && context.Purpose.Length <= 128
            && context.Lifecycle.Length <= 128
            && context.Outcome.Length <= 128
            && !joinedLabels.Contains('\n');
    }

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);

    private sealed record CacheRow(long ContentVersion, string StreamEtag, string DocumentJson);

    private sealed record DocumentCacheWriterPerformanceEvidenceRow(
        string Provider,
        string Scenario,
        DocumentCacheWriterPurpose Purpose,
        int OperationCount,
        TimeSpan Elapsed,
        int OutcomeCount,
        int TransactionDurationCount,
        int CacheDmlDurationCount,
        int AcknowledgementDurationCount,
        int RetryCount,
        int SameDocumentWaitCount
    );

    private static IEnumerable<TestCaseData> CrashHookCases()
    {
        yield return new TestCaseData(
            nameof(DocumentCacheWriterFaultInjectionHook.AfterMainStateLockAndClassificationBeforeCacheDml),
            nameof(FaultInjectionInterruption.CloseConnection)
        ).SetName("DocumentCacheWriterCrash_Mssql_before_cache_dml");
        yield return new TestCaseData(
            nameof(DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement),
            nameof(FaultInjectionInterruption.RollbackTransaction)
        ).SetName("DocumentCacheWriterCrash_Mssql_after_cache_dml");
        yield return new TestCaseData(
            nameof(DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit),
            nameof(FaultInjectionInterruption.CloseConnection)
        ).SetName("DocumentCacheWriterCrash_Mssql_after_acknowledgement");
        yield return new TestCaseData(
            nameof(DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit),
            nameof(FaultInjectionInterruption.RollbackTransaction)
        ).SetName("DocumentCacheWriterCrash_Mssql_after_cache_ahead_latch");
    }

    private static IEnumerable<TestCaseData> CancellationRollbackHookCases()
    {
        yield return new TestCaseData(
            nameof(DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement)
        ).SetName("DocumentCacheWriterCancellation_Mssql_after_cache_dml");
        yield return new TestCaseData(
            nameof(DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit)
        ).SetName("DocumentCacheWriterCancellation_Mssql_after_acknowledgement");
    }

    private enum FaultInjectionInterruption
    {
        CloseConnection = 1,
        RollbackTransaction = 2,
    }

    public enum MssqlDeleteRaceProviderFailure
    {
        ForeignKeyViolation = 1,
        DocumentCacheUuidTrigger = 2,
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, message);

        var errorList = new List<object> { sqlError };
        var errorCollection = (SqlErrorCollection)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errorCollection, errorList);

        var sqlException = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, errorCollection);

        return sqlException;
    }

    private sealed class InterruptingFaultInjectionObserver(
        DocumentCacheWriterFaultInjectionHook hookToInterrupt,
        FaultInjectionInterruption interruption
    ) : ITransactionFaultInjectionObserver
    {
        public List<DocumentCacheWriterFaultInjectionContext> Contexts { get; } = [];

        public async ValueTask ObserveAsync(
            DocumentCacheWriterFaultInjectionContext context,
            DocumentCacheWriterFaultInjectionControl control,
            CancellationToken cancellationToken
        )
        {
            Contexts.Add(context);

            if (context.Hook != hookToInterrupt)
            {
                return;
            }

            switch (interruption)
            {
                case FaultInjectionInterruption.CloseConnection:
                    await control.CloseConnectionAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case FaultInjectionInterruption.RollbackTransaction:
                    await control.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(interruption), interruption, null);
            }

            throw new InvalidOperationException($"Injected DocumentCache writer fault at {context.Hook}.");
        }
    }

    private sealed class PausingFaultInjectionObserver(DocumentCacheWriterFaultInjectionHook hookToPause)
        : ITransactionFaultInjectionObserver
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async ValueTask ObserveAsync(
            DocumentCacheWriterFaultInjectionContext context,
            DocumentCacheWriterFaultInjectionControl control,
            CancellationToken cancellationToken
        )
        {
            if (context.Hook != hookToPause)
            {
                return;
            }

            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task WaitUntilReachedAsync(TimeSpan timeout)
        {
            await _reached.Task.WaitAsync(timeout).ConfigureAwait(false);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class ThrowOnceMssqlDeleteRaceFaultInjectionObserver(
        Func<Task> deleteSourceAsync,
        MssqlDeleteRaceProviderFailure providerFailure
    ) : ITransactionFaultInjectionObserver
    {
        private bool _throwDeleteRace = true;

        public int InjectedFaultCount { get; private set; }

        public bool DeletedSourceBeforeFault { get; private set; }

        public async ValueTask ObserveAsync(
            DocumentCacheWriterFaultInjectionContext context,
            DocumentCacheWriterFaultInjectionControl control,
            CancellationToken cancellationToken
        )
        {
            if (
                context.Hook
                    != DocumentCacheWriterFaultInjectionHook.AfterMainStateLockAndClassificationBeforeCacheDml
                || !_throwDeleteRace
            )
            {
                return;
            }

            _throwDeleteRace = false;
            await deleteSourceAsync().ConfigureAwait(false);
            DeletedSourceBeforeFault = true;
            InjectedFaultCount++;
            throw CreateMssqlDeleteRaceException(providerFailure);
        }

        private static SqlException CreateMssqlDeleteRaceException(
            MssqlDeleteRaceProviderFailure providerFailure
        ) =>
            providerFailure switch
            {
                MssqlDeleteRaceProviderFailure.ForeignKeyViolation => CreateSqlException(
                    547,
                    "The INSERT statement conflicted with the FOREIGN KEY constraint "
                        + "\"FK_DocumentCache_Document_DocumentId\"."
                ),
                MssqlDeleteRaceProviderFailure.DocumentCacheUuidTrigger => CreateSqlException(
                    50000,
                    DocumentCacheInventoryDefinition
                        .DocumentCacheTriggers
                        .MssqlValidateDocumentUuidFailureMessage
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(providerFailure), providerFailure, null),
            };
    }

    private sealed class CancellingFaultInjectionObserver(
        DocumentCacheWriterFaultInjectionHook hookToCancel,
        CancellationTokenSource cancellationSource
    ) : ITransactionFaultInjectionObserver
    {
        public List<DocumentCacheWriterFaultInjectionContext> Contexts { get; } = [];

        public ValueTask ObserveAsync(
            DocumentCacheWriterFaultInjectionContext context,
            DocumentCacheWriterFaultInjectionControl control,
            CancellationToken cancellationToken
        )
        {
            _ = control;
            Contexts.Add(context);

            if (context.Hook != hookToCancel)
            {
                return ValueTask.CompletedTask;
            }

            cancellationSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAdministrativeMutexLease(IDocumentCacheAdministrativeMutexLease innerLease)
        : IDocumentCacheAdministrativeMutexLease
    {
        public List<CancellationToken> CommitCancellationTokens { get; } = [];

        public List<CancellationToken> RollbackCancellationTokens { get; } = [];

        public RelationalProviderToken ProviderToken => innerLease.ProviderToken;

        public DbConnection Connection => innerLease.Connection;

        public bool IsSessionOpen => innerLease.IsSessionOpen;

        public async Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        )
        {
            IRelationalWriteSession session = await innerLease
                .BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);

            return new RecordingRelationalWriteSession(
                session,
                CommitCancellationTokens,
                RollbackCancellationTokens
            );
        }

        public ValueTask DisposeAsync() => innerLease.DisposeAsync();
    }

    private sealed class RecordingRelationalWriteSession(
        IRelationalWriteSession innerSession,
        List<CancellationToken> commitCancellationTokens,
        List<CancellationToken> rollbackCancellationTokens
    ) : IRelationalWriteSession
    {
        public DbConnection Connection => innerSession.Connection;

        public DbTransaction Transaction => innerSession.Transaction;

        public DbCommand CreateCommand(RelationalCommand command) => innerSession.CreateCommand(command);

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            commitCancellationTokens.Add(cancellationToken);
            await innerSession.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            rollbackCancellationTokens.Add(cancellationToken);
            await innerSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => innerSession.DisposeAsync();
    }

    private sealed class ThrowOnceMssqlTransientFaultInjectionObserver : ITransactionFaultInjectionObserver
    {
        private bool _throwTransient = true;

        public int ObservedAttemptCount { get; private set; }

        public ValueTask ObserveAsync(
            DocumentCacheWriterFaultInjectionContext context,
            DocumentCacheWriterFaultInjectionControl control,
            CancellationToken cancellationToken
        )
        {
            if (
                context.Hook
                != DocumentCacheWriterFaultInjectionHook.AfterMainStateLockAndClassificationBeforeCacheDml
            )
            {
                return ValueTask.CompletedTask;
            }

            ObservedAttemptCount++;
            if (!_throwTransient)
            {
                return ValueTask.CompletedTask;
            }

            _throwTransient = false;
            throw CreateSqlException(1222, "simulated lock timeout");
        }
    }

    private sealed class RecordingDocumentCacheWriterTelemetry : IDocumentCacheWriterTelemetry
    {
        public const string Outcome = nameof(Outcome);
        public const string TransactionDuration = nameof(TransactionDuration);
        public const string CacheDmlDuration = nameof(CacheDmlDuration);
        public const string AcknowledgementDuration = nameof(AcknowledgementDuration);
        public const string Retry = nameof(Retry);
        public const string SameDocumentWait = nameof(SameDocumentWait);
        private readonly object _recordsLock = new();

        public List<TelemetryRecord> Records { get; } = [];

        public void RecordOutcome(DocumentCacheWriterMetricContext context)
        {
            Add(new TelemetryRecord(Outcome, context));
        }

        public void RecordTransactionDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
        {
            Add(new TelemetryRecord(TransactionDuration, context, Duration: duration));
        }

        public void RecordCacheDmlDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
        {
            Add(new TelemetryRecord(CacheDmlDuration, context, Duration: duration));
        }

        public void RecordAcknowledgementDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
        {
            Add(new TelemetryRecord(AcknowledgementDuration, context, Duration: duration));
        }

        public void RecordRetry(DocumentCacheWriterMetricContext context, TimeSpan duration, int attemptCount)
        {
            Add(new TelemetryRecord(Retry, context, Duration: duration, AttemptCount: attemptCount));
        }

        public void RecordSameDocumentWait(
            DocumentCacheWriterMetricContext context,
            DocumentCacheWriterContentionParticipant participant,
            DocumentCacheWriterContentionPhase phase,
            TimeSpan duration
        )
        {
            Add(
                new TelemetryRecord(
                    SameDocumentWait,
                    context,
                    Duration: duration,
                    Participant: participant,
                    Phase: phase
                )
            );
        }

        private void Add(TelemetryRecord record)
        {
            lock (_recordsLock)
            {
                Records.Add(record);
            }
        }
    }

    private sealed record TelemetryRecord(
        string Name,
        DocumentCacheWriterMetricContext Context,
        TimeSpan? Duration = null,
        int? AttemptCount = null,
        DocumentCacheWriterContentionParticipant? Participant = null,
        DocumentCacheWriterContentionPhase? Phase = null
    );
}
