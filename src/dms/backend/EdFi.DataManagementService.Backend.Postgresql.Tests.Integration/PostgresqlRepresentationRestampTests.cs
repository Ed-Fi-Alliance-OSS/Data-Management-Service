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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_RepresentationRestampStore
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
    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _cache = null!;
    private PostgresqlDocumentCacheAdministrativeMutex _mutex = null!;
    private PostgresqlRepresentationRestampStore _store = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            nameof(Given_A_Postgresql_RepresentationRestampStore),
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _cache = new(NullLogger<NpgsqlDataSourceCache>.Instance);
        _mutex = new(_cache, NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance);
        _store = new(new FixedProvider(_fixture.MappingSet), new FixedCompiler(_fixture.MappingSet.Key));
    }

    [TearDown]
    public async Task TearDown()
    {
        _cache.Dispose();
        await _database.DisposeAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _baseline.DisposeAsync();

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
            NpgsqlCommand command = new(
                "DELETE FROM \"dms\".\"DocumentCacheState\";",
                (NpgsqlConnection)session.Connection,
                (NpgsqlTransaction)session.Transaction
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
        await stampPage.Should().ThrowAsync<PostgresException>();
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
            """DELETE FROM "dms"."Document" WHERE "DocumentId" = @id;""",
            new NpgsqlParameter("id", second.DocumentId)
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

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
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
        await InstallDocumentUpdateDelayAsync(second.DocumentId);

        DocumentCacheAdministrativeCommandResult interrupted = await CreateCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                workflowTimeout: TimeSpan.FromSeconds(2),
                pageSize: 1
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

        await RemoveDocumentUpdateDelayAsync();
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
        await InstallDocumentUpdateDelayAsync(second.DocumentId);
        Task<DocumentCacheAdministrativeCommandResult> interruptedTask = CreateCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                workflowTimeout: TimeSpan.FromSeconds(30),
                pageSize: 1
            )
            .ExecuteAsync(ExecuteRequest(operation.OperationId));
        await WaitForCommittedCountAsync(operation.OperationId, expectedCount: 1);

        int mutexBackendPid = await ReadMutexBackendPidAsync();
        bool terminated = await _database.ExecuteScalarAsync<bool>(
            "SELECT pg_terminate_backend(@backendPid);",
            new NpgsqlParameter("backendPid", NpgsqlDbType.Integer) { Value = mutexBackendPid }
        );
        terminated.Should().BeTrue();
        DocumentCacheAdministrativeCommandResult interrupted = await interruptedTask.WaitAsync(
            TimeSpan.FromSeconds(5)
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

        await RemoveDocumentUpdateDelayAsync();
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
        await using IDocumentCacheAdministrativeMutexLease first = await LeaseAsync();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
        Func<Task> acquireSecond = async () =>
        {
            await using IDocumentCacheAdministrativeMutexLease ignored = await _mutex.AcquireAsync(
                new(
                    RelationalProviderToken.Postgresql,
                    _database.ConnectionString + ";Application Name=restamp-alias"
                ),
                cancellation.Token
            );
        };
        await acquireSecond.Should().ThrowAsync<OperationCanceledException>();
    }

    private RepresentationRestampCommand CreateCommand(
        DocumentCacheLifecycleObservation observedLifecycle,
        TimeSpan? workflowTimeout = null,
        int pageSize = 1
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
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
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
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
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
                RelationalProviderToken.Postgresql,
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
        var timeoutClassifier = new PostgresqlDocumentCacheProviderCommandTimeoutClassifier();
        var runner = new DocumentCacheAdministrativeCommandRunner(
            supervisor,
            registry,
            _mutex,
            DocumentCacheAdministrativePrimitives.ForPostgresql(timeoutClassifier),
            observationSink,
            TimeProvider.System,
            timeoutClassifier,
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance,
            writeExceptionClassifier: new PostgresqlRelationalWriteExceptionClassifier()
        );

        return new RepresentationRestampCommand(runner, _store, TimeProvider.System);
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

    private Task InstallDocumentUpdateDelayAsync(long documentId) =>
        _database.ExecuteNonQueryAsync(
            """
            CREATE SCHEMA "restamp_test";
            CREATE TABLE "restamp_test"."DelayedDocument" (
                "DocumentId" bigint PRIMARY KEY
            );
            INSERT INTO "restamp_test"."DelayedDocument" ("DocumentId") VALUES (@documentId);
            CREATE FUNCTION "restamp_test"."DelaySelectedDocumentUpdate"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "restamp_test"."DelayedDocument"
                    WHERE "DocumentId" = OLD."DocumentId"
                ) THEN
                    PERFORM pg_sleep(30);
                END IF;
                RETURN NEW;
            END;
            $function$;
            CREATE TRIGGER "TR_Document_RestampTestDelay"
            BEFORE UPDATE OF "ContentVersion" ON "dms"."Document"
            FOR EACH ROW
            EXECUTE FUNCTION "restamp_test"."DelaySelectedDocumentUpdate"();
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private Task RemoveDocumentUpdateDelayAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            DROP TRIGGER IF EXISTS "TR_Document_RestampTestDelay" ON "dms"."Document";
            DROP SCHEMA IF EXISTS "restamp_test" CASCADE;
            """
        );

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

    private async Task<int> ReadMutexBackendPidAsync()
    {
        return await _database.ExecuteScalarAsync<int>(
            """
            SELECT pid
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND database = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND classid = 811646948::oid
              AND objid = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND mode = 'ExclusiveLock'
              AND granted;
            """
        );
    }

    private async Task<ManifestProgress> ReadManifestProgressAsync(Guid operationId)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT "CommittedDocumentCount", "State", "PreRestampBoundary"
            FROM "dms"."RepresentationRestampOperation"
            WHERE "OperationId" = @operationId;
            """,
            new NpgsqlParameter("operationId", NpgsqlDbType.Uuid) { Value = operationId }
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
        _mutex.AcquireAsync(new(RelationalProviderToken.Postgresql, _database.ConnectionString));

    private async Task<Source> InsertAsync(long version)
    {
        Guid uuid = Guid.NewGuid();
        long id = await _database.ExecuteScalarAsync<long>(
            """INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId", "ContentVersion") VALUES (@uuid, @resourceKeyId, @version) RETURNING "DocumentId";""",
            new NpgsqlParameter("uuid", uuid),
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = StudentResourceKeyId },
            new NpgsqlParameter("version", version)
        );
        await _database.ExecuteNonQueryAsync(
            """
            SELECT setval(
                '"dms"."ChangeVersionSequence"',
                GREATEST(
                    (SELECT last_value FROM "dms"."ChangeVersionSequence"),
                    @version
                ),
                true
            );
            """,
            new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = version }
        );
        await _database.ExecuteNonQueryAsync(
            """INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName") VALUES (@id, @studentUniqueId, 'Test');""",
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("studentUniqueId", $"student-{id}")
        );
        long currentVersion = (await CanonicalAsync(id)).Version;
        return new(id, uuid, currentVersion);
    }

    private async Task<Source> InsertDescriptorAsync(long version)
    {
        Guid uuid = Guid.NewGuid();
        long id = await _database.ExecuteScalarAsync<long>(
            """INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId", "ContentVersion") VALUES (@uuid, @resourceKeyId, @version) RETURNING "DocumentId";""",
            new NpgsqlParameter("uuid", uuid),
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = DescriptorResourceKeyId },
            new NpgsqlParameter("version", version)
        );
        await _database.ExecuteNonQueryAsync(
            """
            SELECT setval(
                '"dms"."ChangeVersionSequence"',
                GREATEST(
                    (SELECT last_value FROM "dms"."ChangeVersionSequence"),
                    @version
                ),
                true
            );
            """,
            new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = version }
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId", "ResourceKeyId", "Namespace", "CodeValue",
                "ShortDescription", "Discriminator", "Uri"
            )
            VALUES (
                @id, @resourceKeyId, 'uri://ed-fi.org/SchoolTypeDescriptor', @codeValue,
                @codeValue, 'Ed-Fi:SchoolTypeDescriptor', @uri
            );
            """,
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = DescriptorResourceKeyId },
            new NpgsqlParameter("codeValue", $"code-{id}"),
            new NpgsqlParameter("uri", $"uri://ed-fi.org/SchoolTypeDescriptor#code-{id}")
        );
        long currentVersion = (await CanonicalAsync(id)).Version;
        return new(id, uuid, currentVersion);
    }

    private Task LifecycleAsync(string state, bool latch) =>
        _database.ExecuteNonQueryAsync(
            """UPDATE "dms"."DocumentCacheState" SET "ProjectionLifecycleState" = @state, "CacheAheadRecoveryRequired" = @latch;""",
            new NpgsqlParameter("state", state),
            new NpgsqlParameter("latch", latch)
        );

    private Task<long> CountAsync(string table, string schema = "dms") =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM "{{schema}}"."{{table}}";""");

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
            $$"""SELECT "ContentVersion", "ContentLastModifiedAt" FROM "{{schema}}"."{{table}}" WHERE "DocumentId" = @id;""",
            new NpgsqlParameter("id", id)
        );
        var row = rows.Single();
        return (
            Convert.ToInt64(row["ContentVersion"]),
            new DateTimeOffset(Convert.ToDateTime(row["ContentLastModifiedAt"]), TimeSpan.Zero)
        );
    }

    private Task<(long Version, DateTimeOffset At)> CanonicalAsync(long id) =>
        ReadAsync("Document", "dms", id);

    private Task<(long Version, DateTimeOffset At)> RootAsync(long id) => ReadAsync("Student", "edfi", id);

    private Task<(long Version, DateTimeOffset At)> DescriptorMirrorAsync(long id) =>
        ReadAsync("Descriptor", "dms", id);

    private Task<long> WorkAsync(long id) =>
        _database.ExecuteScalarAsync<long>(
            """SELECT "RequiredContentVersion" FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @id;""",
            new NpgsqlParameter("id", id)
        );

    private sealed record Source(long DocumentId, Guid Uuid, long Version);

    private sealed record ManifestProgress(
        long CommittedDocumentCount,
        DocumentCacheRepresentationRestampOperationState State,
        long PreRestampBoundary
    );

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
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public MappingSetKey GetCurrentKey() => key;

        public Task<MappingSet> CompileAsync(
            MappingSetKey expectedKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
