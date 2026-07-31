// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
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

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
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
        ITransactionFaultInjectionObserver? faultInjectionObserver = null
    ) =>
        new(
            new DocumentCacheWriterRetryAdapter(
                new DeadlockRetrySettings
                {
                    MaxRetryAttempts = 0,
                    BaseDelayMilliseconds = 1,
                    UseJitter = false,
                },
                new MssqlRelationalWriteExceptionClassifier(),
                NullLogger<DocumentCacheWriterRetryAdapter>.Instance
            ),
            NullLogger<MssqlDocumentCacheWriter>.Instance,
            faultInjectionObserver
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
    public async Task It_returns_already_current_without_work_when_cache_matches_source()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 10);
        await DeleteWorkAsync(source.DocumentId);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
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
                DocumentCacheWriterOutcome.RacingWriterLost,
            ]);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(1);
        (await ReadCacheRowAsync(source.DocumentId)).ContentVersion.Should().Be(10);
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

    private async Task<DocumentCacheWriterResult> WriteAsync(
        SourceDocument source,
        DocumentCacheMaterializationCandidate? candidate
    ) => await _writer.WriteAsync(CreateRequest(source, candidate));

    private DocumentCacheWriterRequest CreateRequest(
        SourceDocument source,
        DocumentCacheMaterializationCandidate? candidate
    ) =>
        new(
            CreateTargetContext(),
            source.DocumentId,
            selectedRequiredContentVersion: source.ContentVersion,
            DocumentCacheWriterPurpose.DurableWorkProjection,
            candidate,
            CancellationToken.None
        );

    private DocumentCacheMaterializationTargetContext CreateTargetContext() =>
        new(
            new DocumentCacheProjectionTargetKey("tenant-cache-writer", new DataStoreId(7)),
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
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Document]
            SET [ContentVersion] = @contentVersion,
                [ContentLastModifiedAt] = @lastModifiedAt
            WHERE [DocumentId] = @documentId;

            UPDATE [dms].[DocumentProjectionWork]
            SET [RequiredContentVersion] = @contentVersion
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2)
            {
                Value = new DateTime(2026, 7, 31, 12, 5, 0, DateTimeKind.Utc),
            }
        );
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
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 128) { Value = $"etag-{contentVersion}" },
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

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);

    private sealed record CacheRow(long ContentVersion, string StreamEtag, string DocumentJson);

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

    private enum FaultInjectionInterruption
    {
        CloseConnection = 1,
        RollbackTransaction = 2,
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
}
