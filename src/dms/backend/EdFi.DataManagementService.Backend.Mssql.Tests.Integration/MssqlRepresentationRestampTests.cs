// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
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
[Category("RepresentationRestamp")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_RepresentationRestampStore
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge";
    private const short DescriptorResourceKeyId = 2;
    private const short StudentResourceKeyId = 3;
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("tenant", 1);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheOfflineWriterAdmission OfflineWriterAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlDocumentCacheAdministrativeMutex _mutex = null!;
    private MssqlRepresentationRestampStore _store = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_RepresentationRestampStore)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        await SetReadCommittedSnapshotAsync(_database.DatabaseName, enabled: true);

        if (!await NestedTriggersEnabledAsync())
        {
            Assert.Ignore("SQL Server representation restamp tests require nested triggers to be enabled.");
        }

        _mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );
        _store = new(new FixedProvider(_fixture.MappingSet), new FixedCompiler(_fixture.MappingSet.Key));
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await SetReadCommittedSnapshotAsync(_database.DatabaseName, enabled: true);
        }

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
    public async Task It_previews_resource_and_UUID_scopes_without_document_mirror_work_or_cache_mutation()
    {
        Source source = await InsertAsync(10);
        (long documentCount, long mirrorCount, long workCount, long cacheCount) before = await CountsAsync();
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        (
            await _store.ResolveSelectionAsync(
                session,
                new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
                10,
                CancellationToken.None
            )
        )
            .PreviewDocumentCount.Should()
            .Be(1);
        (
            await _store.ResolveSelectionAsync(
                session,
                new DocumentCacheRepresentationRestampDocumentUuidsScope([source.Uuid]),
                10,
                CancellationToken.None
            )
        )
            .PreviewDocumentCount.Should()
            .Be(1);
        await session.CommitAsync();
        (await CountsAsync()).Should().Be(before);
    }

    [Test]
    public async Task It_persists_and_loads_a_manifest_for_a_64_bit_data_store_identifier()
    {
        var target = new DocumentCacheAdministrativeTargetKey("tenant", (long)int.MaxValue + 1);
        DocumentCacheRepresentationRestampOperation operation = Operation(
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            boundary: 0,
            previewDocumentCount: 0
        ) with
        {
            TargetKey = target,
        };

        await using (IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync())
        await using (
            IRelationalWriteSession session = await lease.BeginTransactionAsync(IsolationLevel.Serializable)
        )
        {
            await _store.CreateDraftAsync(session, operation, CancellationToken.None);
            await session.CommitAsync();
        }

        await using IDocumentCacheAdministrativeMutexLease readLease = await LeaseAsync();
        await using IRelationalWriteSession readSession = await readLease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );
        DocumentCacheRepresentationRestampOperation? loaded = await _store.LoadAsync(
            readSession,
            operation.OperationId,
            CancellationToken.None
        );

        loaded.Should().NotBeNull();
        loaded!.TargetKey.Should().Be(target);
    }

    [Test]
    public async Task It_atomically_updates_canonical_root_mirror_and_tracking_work_for_a_resource_page()
    {
        Source first = await InsertAsync(10);
        Source second = await InsertAsync(11);
        await LifecycleAsync("Tracking", false);
        RepresentationRestampPageCommit commit = await StampAsync(
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            11
        );
        commit.CanonicalStamps.Select(stamp => stamp.ContentVersion).Should().OnlyHaveUniqueItems();
        foreach (RepresentationRestampStamp stamp in commit.CanonicalStamps)
        {
            (await RootAsync(stamp.DocumentId))
                .Should()
                .Be((stamp.ContentVersion, stamp.ContentLastModifiedAt));
        }
        (await WorkAsync(first.DocumentId)).Should().BeGreaterThan(first.Version);
        (await WorkAsync(second.DocumentId)).Should().BeGreaterThan(second.Version);
    }

    [Test]
    public async Task It_updates_descriptor_mirror_with_the_exact_canonical_version_and_timestamp()
    {
        Source source = await InsertDescriptorAsync(10);
        RepresentationRestampPage page = new([
            new RepresentationRestampDocument(
                source.DocumentId,
                source.Uuid,
                new RepresentationRestampMirrorRoute(DescriptorResourceKeyId, "dms", "Descriptor")
            ),
        ]);
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        RepresentationRestampPageCommit commit = await _store.StampPageAsync(
            session,
            page,
            CancellationToken.None
        );
        await session.CommitAsync();
        RepresentationRestampStamp stamp = commit.CanonicalStamps.Single();
        (await DescriptorMirrorAsync(source.DocumentId))
            .Should()
            .Be((stamp.ContentVersion, stamp.ContentLastModifiedAt));
    }

    [Test]
    public async Task It_selects_and_stamps_descriptor_resource_scope_through_the_real_store_path()
    {
        Source first = await InsertDescriptorAsync(10);
        Source second = await InsertDescriptorAsync(11);
        await LifecycleAsync("Disabled", false);
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        var scope = new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "SchoolTypeDescriptor");

        (await _store.ResolveSelectionAsync(session, scope, 10, CancellationToken.None))
            .PreviewDocumentCount.Should()
            .Be(2);
        RepresentationRestampPage page = await _store.SelectNextPageAsync(
            session,
            Operation(scope, second.Version),
            10,
            CancellationToken.None
        );

        page.Documents.Select(document => document.DocumentId)
            .Should()
            .Equal(first.DocumentId, second.DocumentId);
        page.Documents.Select(document => document.MirrorRoute)
            .Should()
            .OnlyContain(route =>
                route.ResourceKeyId == DescriptorResourceKeyId
                && route.MirrorStampTargetSchema == "dms"
                && route.MirrorStampTargetTable == "Descriptor"
            );
        RepresentationRestampPageCommit commit = await _store.StampPageAsync(
            session,
            page,
            CancellationToken.None
        );
        await session.CommitAsync();

        foreach (RepresentationRestampStamp stamp in commit.CanonicalStamps)
        {
            (await DescriptorMirrorAsync(stamp.DocumentId))
                .Should()
                .Be((stamp.ContentVersion, stamp.ContentLastModifiedAt));
        }
    }

    [Test]
    public async Task It_selects_and_stamps_descriptor_UUID_scope_through_the_real_store_path()
    {
        Source source = await InsertDescriptorAsync(10);
        await LifecycleAsync("Disabled", false);
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        var scope = new DocumentCacheRepresentationRestampDocumentUuidsScope([source.Uuid]);

        (await _store.ResolveSelectionAsync(session, scope, 10, CancellationToken.None))
            .PreviewDocumentCount.Should()
            .Be(1);
        RepresentationRestampPage page = await _store.SelectNextPageAsync(
            session,
            Operation(scope, source.Version),
            10,
            CancellationToken.None
        );

        page.Documents.Single().DocumentId.Should().Be(source.DocumentId);
        page.Documents.Single().MirrorRoute.MirrorStampTargetTable.Should().Be("Descriptor");
        RepresentationRestampPageCommit commit = await _store.StampPageAsync(
            session,
            page,
            CancellationToken.None
        );
        await session.CommitAsync();

        RepresentationRestampStamp stamp = commit.CanonicalStamps.Single();
        (await DescriptorMirrorAsync(source.DocumentId))
            .Should()
            .Be((stamp.ContentVersion, stamp.ContentLastModifiedAt));
    }

    [Test]
    public async Task It_rolls_back_stamp_mirror_work_and_manifest_progress_when_enqueue_fails()
    {
        Source source = await InsertAsync(10);
        await LifecycleAsync("Tracking", false);
        (long Version, DateTimeOffset At) before = await CanonicalAsync(source.DocumentId);
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        await using (
            SqlCommand command = new(
                "DELETE FROM [dms].[DocumentCacheState];",
                (SqlConnection)session.Connection,
                (SqlTransaction)session.Transaction
            )
        )
        {
            await command.ExecuteNonQueryAsync();
        }

        RepresentationRestampPage page = await _store.SelectNextPageAsync(
            session,
            Operation(new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"), 10),
            1,
            CancellationToken.None
        );
        Func<Task> stampPage = async () => await _store.StampPageAsync(session, page, CancellationToken.None);
        await stampPage.Should().ThrowAsync<DbException>();
        await session.RollbackAsync();
        (await CanonicalAsync(source.DocumentId)).Should().Be(before);
        (await RootAsync(source.DocumentId)).Should().Be(before);
        (await CountAsync("DocumentProjectionWork")).Should().Be(0);
    }

    [Test]
    public async Task It_does_not_create_or_advance_work_when_disabled()
    {
        Source source = await InsertAsync(10);
        await LifecycleAsync("Disabled", false);
        await StampAsync(new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"), 10);
        (await CountAsync("DocumentProjectionWork")).Should().Be(0);
        (await CanonicalAsync(source.DocumentId)).Version.Should().BeGreaterThan(10);
    }

    [Test]
    public async Task It_bounds_a_SQL_Server_restamp_page_below_the_mirror_parameter_limit()
    {
        await InsertManyAsync(701);
        await LifecycleAsync("Disabled", false);
        DocumentCacheRepresentationRestampOperation operation = Operation(
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            boundary: 701,
            previewDocumentCount: 701
        );

        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        RepresentationRestampPage firstPage = await _store.SelectNextPageAsync(
            session,
            operation,
            pageSize: 701,
            CancellationToken.None
        );

        firstPage.Count.Should().Be(700);
        await _store.StampPageAsync(session, firstPage, CancellationToken.None);

        RepresentationRestampPage finalPage = await _store.SelectNextPageAsync(
            session,
            operation,
            pageSize: 701,
            CancellationToken.None
        );
        finalPage.Count.Should().Be(1);
        await _store.StampPageAsync(session, finalPage, CancellationToken.None);
        await session.CommitAsync();

        (await CountAsync("DocumentProjectionWork")).Should().Be(0);
    }

    [Test]
    public async Task It_resumes_at_the_immutable_boundary_without_restamping_a_committed_page()
    {
        Source first = await InsertAsync(10);
        Source second = await InsertAsync(11);
        await LifecycleAsync("Disabled", false);
        DocumentCacheRepresentationRestampOperation operation = Operation(
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            11
        );
        await using (IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync())
        await using (
            IRelationalWriteSession session = await lease.BeginTransactionAsync(IsolationLevel.Serializable)
        )
        {
            RepresentationRestampPage page = await _store.SelectNextPageAsync(
                session,
                operation,
                1,
                CancellationToken.None
            );
            page.Documents.Single().DocumentId.Should().Be(first.DocumentId);
            await _store.StampPageAsync(session, page, CancellationToken.None);
            await session.CommitAsync();
        }
        long firstVersion = (await CanonicalAsync(first.DocumentId)).Version;
        await using IDocumentCacheAdministrativeMutexLease resumeLease = await LeaseAsync();
        await using IRelationalWriteSession resume = await resumeLease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        RepresentationRestampPage resumed = await _store.SelectNextPageAsync(
            resume,
            operation,
            10,
            CancellationToken.None
        );
        resumed.Documents.Select(document => document.DocumentId).Should().Equal(second.DocumentId);
        (await CanonicalAsync(first.DocumentId)).Version.Should().Be(firstVersion);
        await resume.RollbackAsync();
    }

    [Test]
    public async Task It_detects_completion_count_drift_after_a_selected_document_disappears()
    {
        await InsertAsync(10);
        Source second = await InsertAsync(11);
        await LifecycleAsync("Disabled", false);
        DocumentCacheRepresentationRestampOperation operation = Operation(
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            11
        );
        await using (IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync())
        await using (
            IRelationalWriteSession session = await lease.BeginTransactionAsync(IsolationLevel.Serializable)
        )
        {
            RepresentationRestampPage page = await _store.SelectNextPageAsync(
                session,
                operation,
                1,
                CancellationToken.None
            );
            await _store.StampPageAsync(session, page, CancellationToken.None);
            await session.CommitAsync();
        }

        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [dms].[Document] WHERE [DocumentId] = @id;",
            new SqlParameter("@id", SqlDbType.BigInt) { Value = second.DocumentId }
        );
        await using IDocumentCacheAdministrativeMutexLease checkLease = await LeaseAsync();
        await using IRelationalWriteSession check = await checkLease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        (await _store.CountRemainingEligibleAsync(check, operation, CancellationToken.None)).Should().Be(0);
        (1L + await _store.CountRemainingEligibleAsync(check, operation, CancellationToken.None))
            .Should()
            .NotBe(2);
        await check.RollbackAsync();
    }

    [TestCase(
        DocumentCacheLifecycleState.Resetting,
        false,
        DocumentCacheRepresentationRestampMode.Tracking,
        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch
    )]
    [TestCase(
        DocumentCacheLifecycleState.Rebuilding,
        false,
        DocumentCacheRepresentationRestampMode.Tracking,
        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch
    )]
    [TestCase(
        DocumentCacheLifecycleState.Tracking,
        true,
        DocumentCacheRepresentationRestampMode.Tracking,
        DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet
    )]
    [TestCase(
        DocumentCacheLifecycleState.Disabled,
        false,
        DocumentCacheRepresentationRestampMode.Tracking,
        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch
    )]
    [TestCase(
        DocumentCacheLifecycleState.Tracking,
        false,
        DocumentCacheRepresentationRestampMode.Disabled,
        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch
    )]
    public async Task It_rejects_lifecycle_latch_and_mode_mismatches_before_a_restamp_page(
        DocumentCacheLifecycleState lifecycle,
        bool cacheAheadRecoveryRequired,
        DocumentCacheRepresentationRestampMode mode,
        DocumentCacheAdministrativeCommandClassification expectedClassification
    )
    {
        Source source = await InsertAsync(10);
        DocumentCacheRepresentationRestampOperation operation = await CreateDraftAsync(
            mode,
            boundary: 10,
            previewDocumentCount: 1
        );
        await LifecycleAsync(lifecycle.ToString(), cacheAheadRecoveryRequired);
        (long Version, DateTimeOffset At) canonicalBefore = await CanonicalAsync(source.DocumentId);
        (long Version, DateTimeOffset At) mirrorBefore = await RootAsync(source.DocumentId);

        DocumentCacheAdministrativeCommandResult result = await CreateCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false)
            )
            .ExecuteAsync(ExecuteRequest(operation.OperationId));

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(expectedClassification);
        result.Mutated.Should().BeFalse();
        (await CanonicalAsync(source.DocumentId)).Should().Be(canonicalBefore);
        (await RootAsync(source.DocumentId)).Should().Be(mirrorBefore);
        (await CountAsync("DocumentProjectionWork")).Should().Be(0);
        (await ReadManifestProgressAsync(operation.OperationId))
            .Should()
            .Be(
                new ManifestProgress(
                    CommittedDocumentCount: 0,
                    DocumentCacheRepresentationRestampOperationState.Draft,
                    PreRestampBoundary: 10
                )
            );
    }

    [Test]
    public async Task It_times_out_after_one_committed_page_and_resumes_without_restamping_it()
    {
        await LifecycleAsync("Disabled", false);
        Source first = await InsertAsync(10);
        Source second = await InsertAsync(11);
        DocumentCacheRepresentationRestampOperation operation = await CreateDraftAsync(
            DocumentCacheRepresentationRestampMode.Disabled,
            boundary: 11,
            previewDocumentCount: 2
        );
        DelayingRepresentationRestampStore delayingStore = new(_store, second.DocumentId, "00:00:30");

        DocumentCacheAdministrativeCommandResult interrupted = await CreateCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                workflowTimeout: TimeSpan.FromSeconds(2),
                pageSize: 1,
                store: delayingStore
            )
            .ExecuteAsync(ExecuteRequest(operation.OperationId));

        interrupted.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        interrupted
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        interrupted.Mutated.Should().BeTrue();
        ManifestProgress progress = await ReadManifestProgressAsync(operation.OperationId);
        progress
            .Should()
            .Be(
                new ManifestProgress(
                    CommittedDocumentCount: 1,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    PreRestampBoundary: 11
                )
            );
        long firstCommittedVersion = (await CanonicalAsync(first.DocumentId)).Version;
        firstCommittedVersion.Should().BeGreaterThan(11);
        (await CanonicalAsync(second.DocumentId)).Version.Should().Be(11);

        await AssertResumeCompletesWithoutRestampingFirstAsync(
            operation,
            first,
            second,
            firstCommittedVersion
        );
    }

    [Test]
    public async Task It_aborts_on_mutex_session_loss_after_one_page_and_a_new_invocation_resumes()
    {
        await LifecycleAsync("Disabled", false);
        Source first = await InsertAsync(10);
        Source second = await InsertAsync(11);
        DocumentCacheRepresentationRestampOperation operation = await CreateDraftAsync(
            DocumentCacheRepresentationRestampMode.Disabled,
            boundary: 11,
            previewDocumentCount: 2
        );
        DelayingRepresentationRestampStore delayingStore = new(_store, second.DocumentId, "00:00:20");
        Task<DocumentCacheAdministrativeCommandResult> interruptedTask = CreateCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                workflowTimeout: TimeSpan.FromSeconds(30),
                pageSize: 1,
                store: delayingStore
            )
            .ExecuteAsync(ExecuteRequest(operation.OperationId));
        await WaitForCommittedCountAsync(operation.OperationId, expectedCount: 1);

        int mutexSessionId = await ReadMutexSessionIdAsync();
        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync($"KILL {mutexSessionId};");
        DocumentCacheAdministrativeCommandResult interrupted = await interruptedTask.WaitAsync(
            TimeSpan.FromSeconds(15)
        );

        interrupted.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        interrupted
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        interrupted.Mutated.Should().BeTrue();
        (await ReadManifestProgressAsync(operation.OperationId))
            .Should()
            .Be(
                new ManifestProgress(
                    CommittedDocumentCount: 1,
                    DocumentCacheRepresentationRestampOperationState.Incomplete,
                    PreRestampBoundary: 11
                )
            );
        long firstCommittedVersion = (await CanonicalAsync(first.DocumentId)).Version;
        firstCommittedVersion.Should().BeGreaterThan(11);
        (await CanonicalAsync(second.DocumentId)).Version.Should().Be(11);

        await AssertResumeCompletesWithoutRestampingFirstAsync(
            operation,
            first,
            second,
            firstCommittedVersion
        );
    }

    [Test]
    public async Task It_serializes_aliases_of_the_same_physical_database_through_the_administrative_mutex()
    {
        IDocumentCacheAdministrativeMutexLease? first = await LeaseAsync();
        try
        {
            Task<IDocumentCacheAdministrativeMutexLease> blockedAcquire = _mutex.AcquireAsync(
                new(RelationalProviderToken.SqlServer, AliasConnectionString(_database.ConnectionString))
            );

            Task completedTask = await Task.WhenAny(
                blockedAcquire,
                Task.Delay(TimeSpan.FromMilliseconds(250))
            );
            completedTask.Should().NotBe(blockedAcquire);

            await first.DisposeAsync();
            first = null;

            await using IDocumentCacheAdministrativeMutexLease second = await blockedAcquire.WaitAsync(
                TimeSpan.FromSeconds(5)
            );
            second.IsSessionOpen.Should().BeTrue();
        }
        finally
        {
            if (first is not null)
            {
                await first.DisposeAsync();
            }
        }
    }

    private RepresentationRestampCommand CreateCommand(
        DocumentCacheLifecycleObservation observedLifecycle,
        TimeSpan? workflowTimeout = null,
        int pageSize = 1,
        IRepresentationRestampStore? store = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext = new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromMilliseconds(10),
                projectorPageSize: pageSize,
                projectorMaxConcurrentTargets: 1,
                projectorFailureBackoff: TimeSpan.FromSeconds(1),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: workflowTimeout ?? TimeSpan.FromSeconds(30)
            ),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "mssql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            ),
            Fingerprint,
            observedLifecycle,
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            SatisfiedSqlServerPrerequisites()
        );
        DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedEligible(
            executionContext.TargetKey,
            executionContext.EffectiveSettings,
            executionContext.Generation,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint,
            executionContext.Lifecycle,
            executionContext.Inventory,
            executionContext.EnqueueTrigger,
            executionContext.SqlServerPrerequisites
        );
        var observationSink = new RecordingObservationSink();
        var runtimeContext = new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.SqlServer,
                new DocumentCacheMaterializationTargetContext(
                    new DocumentCacheProjectionTargetKey(
                        TargetKey.TenantKey,
                        new DataStoreId(TargetKey.DataStoreId)
                    ),
                    _fixture.MappingSet,
                    DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                    _database.ConnectionString
                ),
                new UnusedMaterializer(),
                new UnusedWriter()
            ),
            observationSink
        );
        var supervisor = new StubProjectionSupervisor([runtimeContext]);
        var registry = new FixedTargetRegistry(
            new DocumentCacheTargetRegistrySnapshot([observation], DateTimeOffset.UtcNow),
            new DocumentCacheTargetRuntimeSnapshot([executionContext], DateTimeOffset.UtcNow)
        );
        var timeoutClassifier = new MssqlDocumentCacheProviderCommandTimeoutClassifier();
        var runner = new DocumentCacheAdministrativeCommandRunner(
            supervisor,
            registry,
            _mutex,
            DocumentCacheAdministrativePrimitives.ForSqlServer(timeoutClassifier),
            observationSink,
            TimeProvider.System,
            timeoutClassifier,
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance,
            writeExceptionClassifier: new MssqlRelationalWriteExceptionClassifier()
        );

        return new RepresentationRestampCommand(runner, store ?? _store, TimeProvider.System);
    }

    private async Task<DocumentCacheRepresentationRestampOperation> CreateDraftAsync(
        DocumentCacheRepresentationRestampMode mode,
        long boundary,
        long previewDocumentCount
    )
    {
        DocumentCacheRepresentationRestampOperation operation = Operation(
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
            boundary,
            mode,
            previewDocumentCount
        );
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        await _store.CreateDraftAsync(session, operation, CancellationToken.None);
        await session.CommitAsync();
        return operation;
    }

    private static DocumentCacheRepresentationRestampExecuteRequest ExecuteRequest(Guid operationId) =>
        new(
            AdministrativeTargetKey,
            operationId,
            OfflineWriterAdmission,
            DocumentCacheAdministrativeCommandConfirmation.RepresentationRestamp
        );

    private async Task AssertResumeCompletesWithoutRestampingFirstAsync(
        DocumentCacheRepresentationRestampOperation operation,
        Source first,
        Source second,
        long firstCommittedVersion
    )
    {
        DocumentCacheAdministrativeCommandResult resumed = await CreateCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                workflowTimeout: TimeSpan.FromSeconds(10),
                pageSize: 1
            )
            .ExecuteAsync(ExecuteRequest(operation.OperationId));

        resumed.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        resumed.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        resumed.RepresentationRestampResult.Should().NotBeNull();
        resumed.RepresentationRestampResult!.PreRestampBoundary.Should().Be(11);
        resumed.RepresentationRestampResult.CommittedDocumentCount.Should().Be(2);
        (await CanonicalAsync(first.DocumentId)).Version.Should().Be(firstCommittedVersion);
        (await CanonicalAsync(second.DocumentId)).Version.Should().BeGreaterThan(11);
        (await ReadManifestProgressAsync(operation.OperationId))
            .Should()
            .Be(
                new ManifestProgress(
                    CommittedDocumentCount: 2,
                    DocumentCacheRepresentationRestampOperationState.Completed,
                    PreRestampBoundary: 11
                )
            );
    }

    private async Task WaitForCommittedCountAsync(Guid operationId, long expectedCount)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            ManifestProgress progress = await ReadManifestProgressAsync(operationId);
            if (progress.CommittedDocumentCount == expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new AssertionException(
            $"Representation restamp manifest did not reach committed count {expectedCount}."
        );
    }

    private async Task<int> ReadMutexSessionIdAsync()
    {
        return await _database.ExecuteScalarAsync<int>(
            """
            SELECT locks.request_session_id
            FROM sys.dm_tran_locks AS locks
            WHERE locks.resource_type = 'APPLICATION'
              AND locks.request_mode = 'X'
              AND locks.request_status = 'GRANT'
              AND locks.resource_database_id = DB_ID();
            """
        );
    }

    private async Task<ManifestProgress> ReadManifestProgressAsync(Guid operationId)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT [CommittedDocumentCount], [State], [PreRestampBoundary]
            FROM [dms].[RepresentationRestampOperation]
            WHERE [OperationId] = @operationId;
            """,
            new SqlParameter("@operationId", SqlDbType.UniqueIdentifier) { Value = operationId }
        );
        IReadOnlyDictionary<string, object?> row = rows.Single();
        return new ManifestProgress(
            Convert.ToInt64(row["CommittedDocumentCount"]),
            Enum.Parse<DocumentCacheRepresentationRestampOperationState>((string)row["State"]!),
            Convert.ToInt64(row["PreRestampBoundary"])
        );
    }

    private async Task<RepresentationRestampPageCommit> StampAsync(
        DocumentCacheRepresentationRestampScope scope,
        long boundary
    )
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await LeaseAsync();
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );
        RepresentationRestampPage page = await _store.SelectNextPageAsync(
            session,
            Operation(scope, boundary),
            10,
            CancellationToken.None
        );
        RepresentationRestampPageCommit commit = await _store.StampPageAsync(
            session,
            page,
            CancellationToken.None
        );
        await session.CommitAsync();
        return commit;
    }

    private static DocumentCacheRepresentationRestampOperation Operation(
        DocumentCacheRepresentationRestampScope scope,
        long boundary,
        DocumentCacheRepresentationRestampMode mode = DocumentCacheRepresentationRestampMode.Tracking,
        long previewDocumentCount = 2
    ) =>
        new(
            Guid.NewGuid(),
            1,
            AdministrativeTargetKey,
            Fingerprint,
            scope,
            "test",
            mode,
            boundary,
            previewDocumentCount,
            0,
            DocumentCacheRepresentationRestampOperationState.Draft,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

    private Task<IDocumentCacheAdministrativeMutexLease> LeaseAsync() =>
        _mutex.AcquireAsync(new(RelationalProviderToken.SqlServer, _database.ConnectionString));

    private async Task<Source> InsertAsync(long version)
    {
        Guid uuid = Guid.NewGuid();
        long id = await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @inserted TABLE ([DocumentId] bigint);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId], [ContentVersion], [ContentLastModifiedAt])
            OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
            VALUES (@uuid, @resourceKeyId, @version, SYSUTCDATETIME());

            SELECT [DocumentId] FROM @inserted;
            """,
            new SqlParameter("@uuid", SqlDbType.UniqueIdentifier) { Value = uuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = StudentResourceKeyId },
            new SqlParameter("@version", SqlDbType.BigInt) { Value = version }
        );
        await AdvanceSequencePastAsync(version);
        await _database.ExecuteNonQueryAsync(
            "INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName]) VALUES (@id, @studentUniqueId, N'Test');",
            new SqlParameter("@id", SqlDbType.BigInt) { Value = id },
            new SqlParameter("@studentUniqueId", SqlDbType.NVarChar, 32) { Value = $"student-{id}" }
        );
        long currentVersion = (await CanonicalAsync(id)).Version;
        return new(id, uuid, currentVersion);
    }

    private async Task InsertManyAsync(int count)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DECLARE @documents TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);

            WITH [numbers] AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [Value]
                FROM sys.all_objects AS [first]
                CROSS JOIN sys.all_objects AS [second]
            )
            INSERT INTO [dms].[Document] (
                [DocumentUuid], [ResourceKeyId], [ContentVersion], [ContentLastModifiedAt]
            )
            OUTPUT INSERTED.[DocumentId] INTO @documents ([DocumentId])
            SELECT NEWID(), @resourceKeyId, [Value], SYSUTCDATETIME()
            FROM [numbers];

            INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName])
            SELECT [DocumentId], CONCAT(N'student-', [DocumentId]), N'Test'
            FROM @documents;

            ALTER SEQUENCE [dms].[ChangeVersionSequence] RESTART WITH 702;
            """,
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = StudentResourceKeyId },
            new SqlParameter("@count", SqlDbType.Int) { Value = count }
        );
    }

    // Test data always inserts documents with monotonically increasing content versions, so an
    // unconditional restart is safe and avoids relying on sys.sequences.current_value semantics
    // before the sequence has ever produced a value.
    private Task AdvanceSequencePastAsync(long version) =>
        _database.ExecuteNonQueryAsync(
            $"ALTER SEQUENCE [dms].[ChangeVersionSequence] RESTART WITH {version + 1};"
        );

    private async Task<Source> InsertDescriptorAsync(long version)
    {
        Guid uuid = Guid.NewGuid();
        long id = await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @inserted TABLE ([DocumentId] bigint);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId], [ContentVersion], [ContentLastModifiedAt])
            OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
            VALUES (@uuid, @resourceKeyId, @version, SYSUTCDATETIME());

            SELECT [DocumentId] FROM @inserted;
            """,
            new SqlParameter("@uuid", SqlDbType.UniqueIdentifier) { Value = uuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = DescriptorResourceKeyId },
            new SqlParameter("@version", SqlDbType.BigInt) { Value = version }
        );
        await AdvanceSequencePastAsync(version);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor] (
                [DocumentId], [ResourceKeyId], [Namespace], [CodeValue],
                [ShortDescription], [Discriminator], [Uri]
            )
            VALUES (
                @id, @resourceKeyId, N'uri://ed-fi.org/SchoolTypeDescriptor', @codeValue,
                @codeValue, N'Ed-Fi:SchoolTypeDescriptor', @uri
            );
            """,
            new SqlParameter("@id", SqlDbType.BigInt) { Value = id },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = DescriptorResourceKeyId },
            new SqlParameter("@codeValue", SqlDbType.NVarChar, 50) { Value = $"code-{id}" },
            new SqlParameter("@uri", SqlDbType.NVarChar, 306)
            {
                Value = $"uri://ed-fi.org/SchoolTypeDescriptor#code-{id}",
            }
        );
        long currentVersion = (await CanonicalAsync(id)).Version;
        return new(id, uuid, currentVersion);
    }

    private Task LifecycleAsync(string state, bool latch) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @state, [CacheAheadRecoveryRequired] = @latch
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@state", SqlDbType.NVarChar, 32) { Value = state },
            new SqlParameter("@latch", SqlDbType.Bit) { Value = latch }
        );

    private Task<long> CountAsync(string table, string schema = "dms") =>
        _database.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM [{schema}].[{table}];");

    private async Task<(long, long, long, long)> CountsAsync() =>
        (
            await CountAsync("Document"),
            await CountAsync("Student", "edfi"),
            await CountAsync("DocumentProjectionWork"),
            await CountAsync("DocumentCache")
        );

    private async Task<(long Version, DateTimeOffset At)> ReadAsync(string table, string schema, long id)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            $"SELECT [ContentVersion], [ContentLastModifiedAt] FROM [{schema}].[{table}] WHERE [DocumentId] = @id;",
            new SqlParameter("@id", SqlDbType.BigInt) { Value = id }
        );
        var row = rows.Single();
        return (
            Convert.ToInt64(row["ContentVersion"]),
            new DateTimeOffset(
                DateTime.SpecifyKind(Convert.ToDateTime(row["ContentLastModifiedAt"]), DateTimeKind.Utc)
            )
        );
    }

    private Task<(long Version, DateTimeOffset At)> CanonicalAsync(long id) =>
        ReadAsync("Document", "dms", id);

    private Task<(long Version, DateTimeOffset At)> RootAsync(long id) => ReadAsync("Student", "edfi", id);

    private Task<(long Version, DateTimeOffset At)> DescriptorMirrorAsync(long id) =>
        ReadAsync("Descriptor", "dms", id);

    private Task<long> WorkAsync(long id) =>
        _database.ExecuteScalarAsync<long>(
            "SELECT [RequiredContentVersion] FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @id;",
            new SqlParameter("@id", SqlDbType.BigInt) { Value = id }
        );

    private static string AliasConnectionString(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString)
        {
            DataSource = LoopbackDataSourceAlias(new SqlConnectionStringBuilder(connectionString).DataSource),
            ApplicationName = "restamp-alias",
        };

        return builder.ConnectionString;
    }

    private static string LoopbackDataSourceAlias(string dataSource)
    {
        if (dataSource.StartsWith("tcp:localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"tcp:127.0.0.1{dataSource["tcp:localhost".Length..]}";
        }

        if (dataSource.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"127.0.0.1{dataSource["localhost".Length..]}";
        }

        if (dataSource.StartsWith("tcp:127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return $"tcp:localhost{dataSource["tcp:127.0.0.1".Length..]}";
        }

        if (dataSource.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return $"localhost{dataSource["127.0.0.1".Length..]}";
        }

        throw new InconclusiveException(
            "The SQL Server representation restamp alias test requires a localhost or 127.0.0.1 data source."
        );
    }

    private static async Task SetReadCommittedSnapshotAsync(string databaseName, bool enabled)
    {
        SqlConnection.ClearAllPools();

        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
        string enabledSql = enabled ? "ON" : "OFF";

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            ALTER DATABASE {quotedDatabaseName}
            SET READ_COMMITTED_SNAPSHOT {enabledSql} WITH ROLLBACK IMMEDIATE;
            """
        );

        SqlConnection.ClearAllPools();
    }

    private static async Task<bool> NestedTriggersEnabledAsync()
    {
        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(int, [value_in_use])
            FROM [sys].[configurations]
            WHERE [name] = N'nested triggers';
            """;

        object? value = await command.ExecuteScalarAsync();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value) == 1;
    }

    private static DocumentCacheSqlServerPrerequisiteDetails SatisfiedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server READ_COMMITTED_SNAPSHOT is enabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server nested triggers are enabled."
            )
        );

    private sealed record Source(long DocumentId, Guid Uuid, long Version);

    private sealed record ManifestProgress(
        long CommittedDocumentCount,
        DocumentCacheRepresentationRestampOperationState State,
        long PreRestampBoundary
    );

    // Delays via an actual in-session SQL WAITFOR rather than an in-process Task.Delay. The
    // administrative workflow disables caller cancellation once a transaction is open
    // (DocumentCacheAdministrativeWorkflow.ExecuteInTransactionAsync passes
    // CancellationToken.None to the transaction body) and instead relies on the provider
    // command timeout to interrupt a slow statement; a busy, in-flight command is also what a
    // real KILL on the owning session interrupts immediately. An idle in-process delay would
    // observe neither.
    private sealed class DelayingRepresentationRestampStore(
        IRepresentationRestampStore inner,
        long delayedDocumentId,
        string waitForDelayLiteral
    ) : IRepresentationRestampStore
    {
        public Task<long> GetMaxChangeVersionAsync(
            IRelationalWriteSession session,
            CancellationToken cancellationToken
        ) => inner.GetMaxChangeVersionAsync(session, cancellationToken);

        public Task<RepresentationRestampSelection> ResolveSelectionAsync(
            IRelationalWriteSession session,
            DocumentCacheRepresentationRestampScope scope,
            int pageSize,
            CancellationToken cancellationToken
        ) => inner.ResolveSelectionAsync(session, scope, pageSize, cancellationToken);

        public Task CreateDraftAsync(
            IRelationalWriteSession session,
            DocumentCacheRepresentationRestampOperation operation,
            CancellationToken cancellationToken
        ) => inner.CreateDraftAsync(session, operation, cancellationToken);

        public Task<DocumentCacheRepresentationRestampOperation?> LoadAsync(
            IRelationalWriteSession session,
            Guid operationId,
            CancellationToken cancellationToken
        ) => inner.LoadAsync(session, operationId, cancellationToken);

        public Task<RepresentationRestampPage> SelectNextPageAsync(
            IRelationalWriteSession session,
            DocumentCacheRepresentationRestampOperation operation,
            int pageSize,
            CancellationToken cancellationToken
        ) => inner.SelectNextPageAsync(session, operation, pageSize, cancellationToken);

        public async Task<RepresentationRestampPageCommit> StampPageAsync(
            IRelationalWriteSession session,
            RepresentationRestampPage page,
            CancellationToken cancellationToken
        )
        {
            if (page.Documents.Any(document => document.DocumentId == delayedDocumentId))
            {
                await using DbCommand delayCommand = session.CreateCommand(
                    new RelationalCommand($"WAITFOR DELAY '{waitForDelayLiteral}';")
                );
                await delayCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.StampPageAsync(session, page, cancellationToken).ConfigureAwait(false);
        }

        public Task UpdateProgressAsync(
            IRelationalWriteSession session,
            Guid operationId,
            long committedCount,
            DocumentCacheRepresentationRestampOperationState state,
            CancellationToken cancellationToken
        ) => inner.UpdateProgressAsync(session, operationId, committedCount, state, cancellationToken);

        public Task<long> CountRemainingEligibleAsync(
            IRelationalWriteSession session,
            DocumentCacheRepresentationRestampOperation operation,
            CancellationToken cancellationToken
        ) => inner.CountRemainingEligibleAsync(session, operation, cancellationToken);

        public Task MarkCompletedAsync(
            IRelationalWriteSession session,
            Guid operationId,
            CancellationToken cancellationToken
        ) => inner.MarkCompletedAsync(session, operationId, cancellationToken);
    }

    private sealed class StubProjectionSupervisor(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> contexts
    ) : IDocumentCacheProjectionSupervisor
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            contexts.ToImmutableArray();

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FixedTargetRegistry(
        DocumentCacheTargetRegistrySnapshot currentSnapshot,
        DocumentCacheTargetRuntimeSnapshot currentRuntimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = currentSnapshot;

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = currentRuntimeSnapshot;

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class RecordingObservationSink : IDocumentCacheProjectionObservationSink
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

    private sealed class UnusedMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotSupportedException();
    }

    private sealed class UnusedWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class FixedProvider(MappingSet set) : IMappingSetProvider
    {
        public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken) =>
            Task.FromResult(set);
    }

    private sealed class FixedCompiler(MappingSetKey key) : IRuntimeMappingSetCompiler
    {
        public SqlDialect Dialect => SqlDialect.Mssql;

        public MappingSetKey GetCurrentKey() => key;

        public Task<MappingSet> CompileAsync(
            MappingSetKey expectedKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
