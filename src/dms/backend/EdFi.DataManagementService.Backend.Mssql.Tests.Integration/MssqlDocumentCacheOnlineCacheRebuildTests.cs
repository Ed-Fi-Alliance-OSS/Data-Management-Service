// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
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
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("OnlineCacheRebuild")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheOnlineCacheRebuild_Command
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly DateTime ObservedAt = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset ObservedAtOffset = new(ObservedAt);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAtOffset.AddMinutes(-5);
    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 1);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private Dictionary<long, SourceDocument> _sourcesByDocumentId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheOnlineCacheRebuild_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _sourcesByDocumentId = [];
        await SetReadCommittedSnapshotAsync(_database.DatabaseName, enabled: true);

        if (!await NestedTriggersEnabledAsync())
        {
            Assert.Ignore("SQL Server online rebuild tests require nested triggers to be enabled.");
        }
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
    public async Task It_rebuilds_tracking_cache_and_preserves_work_until_command_owned_drain()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        IReadOnlyList<SourceDocument> sources = await InsertProjectedRowsAsync(documentCount: 3);
        RecordingObservationSink observationSink = new();
        var drainerProbe = new DrainStartProbe(async () =>
        {
            (await ReadCountAsync("DocumentCache")).Should().Be(0);
            (await ReadCountAsync("DocumentProjectionWork")).Should().Be(sources.Count);
        });
        DocumentCacheOnlineCacheRebuildCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            observationSink,
            drainerProbe
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Command.Should().Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        drainerProbe.StartCount.Should().Be(1);

        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        (await ReadCountAsync("DocumentCache")).Should().Be(sources.Count);
        IReadOnlyDictionary<long, string> cachedJsonByDocumentId = await ReadCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId.Values.Should().OnlyContain(json => json.Contains("rebuilt"));

        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.Command == DocumentCacheAdministrativeCommand.OnlineCacheRebuild
                && snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearCache
                && snapshot.Mutated
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterTracking
            );
    }

    [Test]
    public async Task It_rejects_a_set_latch_without_mutating_lifecycle_cache_or_work()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheOnlineCacheRebuildCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeTrue();

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        (await ReadCountAsync("DocumentCache")).Should().Be(2);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(2);
    }

    [Test]
    public async Task It_resumes_rebuilding_without_repeating_cache_clearing()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Rebuilding, cacheAheadRecoveryRequired: false);
        SourceDocument source = await InsertDocumentAsync(contentVersion: 21);
        await ClearProjectionWorkAsync();
        await InsertCacheRowAsync(source, jsonPrefix: "kept-cache");
        RecordingObservationSink observationSink = new();
        DocumentCacheOnlineCacheRebuildCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false),
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        IReadOnlyDictionary<long, string> cachedJsonByDocumentId = await ReadCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId[source.DocumentId].Should().Contain("kept-cache");
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .NotContain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearCache
            );
    }

    private DocumentCacheOnlineCacheRebuildCommand CreateCommand(
        DocumentCacheLifecycleObservation lifecycle,
        RecordingObservationSink observationSink,
        DrainStartProbe? drainStartProbe = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(lifecycle);
        MssqlDocumentCacheWriter writer = CreateWriter();
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext, writer);
        var registry = new StubTargetRegistry(
            new DocumentCacheTargetRegistrySnapshot(
                [EligibleObservation(executionContext)],
                ObservedAtOffset
            ),
            new DocumentCacheTargetRuntimeSnapshot([executionContext], ObservedAtOffset)
        );
        var runner = new DocumentCacheAdministrativeCommandRunner(
            new StubProjectionSupervisor([runtimeContext]),
            registry,
            new MssqlDocumentCacheAdministrativeMutex(
                NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
            ),
            new MssqlDocumentCacheAdministrativePrimitives(),
            observationSink,
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(observationSink);
        IDocumentCacheAdministrativeDrainer effectiveDrainer = drainStartProbe is null
            ? drainer
            : new ProbingDrainer(drainStartProbe, drainer);

        return new(runner, CreateBaselineSeeder(effectiveDrainer), effectiveDrainer);
    }

    private static MssqlDocumentCacheWriter CreateWriter() =>
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
            NullLogger<MssqlDocumentCacheWriter>.Instance
        );

    private static DocumentCacheBaselineSeeder CreateBaselineSeeder(
        IDocumentCacheAdministrativeDrainer? drainer = null
    ) =>
        new(
            new DocumentCacheBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheBaselineSeeder>.Instance,
            drainer
        );

    private static DocumentCacheAdministrativeDrainer CreateDrainer(
        IDocumentCacheProjectionObservationSink observationSink
    )
    {
        DocumentCacheOptions options = new()
        {
            Projector = new DocumentCacheProjectorOptions { MaxConcurrentTargets = 1, PageSize = 3 },
        };
        var scheduler = new DocumentCacheProjectionScheduler(
            Options.Create(options),
            new DocumentCacheProjectionDrainPageProcessor(
                new MssqlDocumentProjectionWorkPager(NullLogger<MssqlDocumentProjectionWorkPager>.Instance),
                new DocumentCacheProjectionItemProcessor(
                    new FixedTimeProvider(ObservedAtOffset),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new FixedTimeProvider(ObservedAtOffset)
            ),
            observationSink,
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );

        return new(
            scheduler,
            new DocumentCacheAdministrativeDrainDelay(),
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheAdministrativeDrainer>.Instance
        );
    }

    private DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheLifecycleObservation lifecycle
    ) =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            EffectiveSettings(),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "mssql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            ),
            Fingerprint,
            lifecycle,
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

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings() =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromMilliseconds(10),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 1,
            projectorFailureBackoff: TimeSpan.FromSeconds(1),
            projectorBaselineHighWaterMark: 1000,
            administrationWorkflowTimeout: TimeSpan.FromSeconds(30)
        );

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

    private static DocumentCacheTargetObservation EligibleObservation(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
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

    private DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetExecutionContext executionContext,
        MssqlDocumentCacheWriter writer
    ) =>
        new(
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
                new CandidateMaterializer(_fixture.MappingSet, _sourcesByDocumentId),
                writer
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAtOffset)),
            writer
        );

    private async Task<IReadOnlyList<SourceDocument>> InsertProjectedRowsAsync(int documentCount)
    {
        List<SourceDocument> sources = [];
        for (var index = 0; index < documentCount; index++)
        {
            SourceDocument source = await InsertDocumentAsync(contentVersion: 10 + index);
            sources.Add(source);
        }

        await ClearProjectionWorkAsync();

        foreach (SourceDocument source in sources)
        {
            await InsertProjectionWorkAsync(source);
            await InsertCacheRowAsync(source, jsonPrefix: "stale-cache");
        }

        return sources;
    }

    private async Task<SourceDocument> InsertDocumentAsync(long contentVersion)
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        Guid documentUuid = Guid.NewGuid();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            DECLARE @inserted TABLE ([DocumentId] bigint);

            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            );

            SELECT [DocumentId] FROM @inserted;
            """,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = documentUuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt }
        );

        var source = new SourceDocument(
            Convert.ToInt64(rows.Single()["DocumentId"]),
            documentUuid,
            contentVersion
        );
        _sourcesByDocumentId[source.DocumentId] = source;
        return source;
    }

    private Task SetLifecycleAsync(DocumentCacheLifecycleState lifecycle, bool cacheAheadRecoveryRequired) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentCacheState]
            SET
                [ProjectionLifecycleState] = @lifecycle,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@lifecycle", SqlDbType.VarChar, 32) { Value = lifecycle.ToString() },
            new SqlParameter("@cacheAheadRecoveryRequired", SqlDbType.Bit)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM [dms].[DocumentProjectionWork];""");

    private Task InsertProjectionWorkAsync(SourceDocument source) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[DocumentProjectionWork] (
                [DocumentId],
                [RequiredContentVersion],
                [FirstEnqueuedAt],
                [LastEnqueuedAt]
            )
            VALUES (
                @documentId,
                @requiredContentVersion,
                @firstEnqueuedAt,
                @lastEnqueuedAt
            );
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = source.DocumentId },
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = source.ContentVersion },
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = FirstEnqueuedAt.UtcDateTime },
            new SqlParameter("@lastEnqueuedAt", SqlDbType.DateTime2) { Value = ObservedAt }
        );

    private async Task InsertCacheRowAsync(SourceDocument source, string jsonPrefix)
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
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = source.ContentVersion },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64)
            {
                Value = $"etag-{source.ContentVersion}",
            },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = $"{jsonPrefix}-{source.DocumentId}" }.ToJsonString(),
            },
            new SqlParameter("@computedAt", SqlDbType.DateTime2) { Value = ObservedAt.AddMinutes(1) }
        );
    }

    private async Task<DocumentCacheLifecycleObservation> ReadLifecycleAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        );

        IReadOnlyDictionary<string, object?> row = rows.Single();
        return new(
            Enum.Parse<DocumentCacheLifecycleState>((string)row["ProjectionLifecycleState"]!),
            Convert.ToBoolean(row["CacheAheadRecoveryRequired"])
        );
    }

    private async Task<IReadOnlyDictionary<long, string>> ReadCachedJsonByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentJson]
            FROM [dms].[DocumentCache]
            ORDER BY [DocumentId];
            """
        );

        return rows.ToDictionary(
            row => Convert.ToInt64(row["DocumentId"]),
            row => (string)row["DocumentJson"]!
        );
    }

    private Task<long> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM [dms].[{{tableName}}];""");

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

    private sealed class StubTargetRegistry(
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
        public List<DocumentCacheAdministrativeCommandObservationSnapshot> AdministrativeCommandSnapshots { get; } =
        [];

        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => _ = (contextKey, endReason, endedAt);

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => AdministrativeCommandSnapshots.Add(snapshot);

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }

    private sealed class DrainStartProbe(Func<Task> onStart)
    {
        public int StartCount { get; private set; }

        public async Task RecordAsync()
        {
            StartCount++;
            await onStart().ConfigureAwait(false);
        }
    }

    private sealed class ProbingDrainer(DrainStartProbe probe, IDocumentCacheAdministrativeDrainer inner)
        : IDocumentCacheAdministrativeDrainer
    {
        public async Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            await probe.RecordAsync().ConfigureAwait(false);
            return await inner.DrainToEmptyAsync(context, cancellationToken).ConfigureAwait(false);
        }

        public async Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            await probe.RecordAsync().ConfigureAwait(false);
            return await inner
                .DrainBackpressureReliefSliceAsync(context, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class CandidateMaterializer(
        MappingSet mappingSet,
        IReadOnlyDictionary<long, SourceDocument> sourcesByDocumentId
    ) : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            SourceDocument source = sourcesByDocumentId[request.DocumentId];
            ResourceKeyEntry resourceKey = mappingSet.ResourceKeyById[
                mappingSet.ResourceKeyIdByResource[PersonResource]
            ];

            return Task.FromResult<DocumentCacheMaterializationResult>(
                new DocumentCacheMaterializationResult.Success(
                    new DocumentCacheMaterializationCandidate(
                        source.DocumentId,
                        new DocumentUuid(source.DocumentUuid),
                        resourceKey.Resource.ProjectName,
                        resourceKey.Resource.ResourceName,
                        resourceKey.ResourceVersion,
                        source.ContentVersion,
                        ObservedAtOffset,
                        $"etag-{source.ContentVersion}",
                        new JsonObject { ["value"] = $"rebuilt-{source.DocumentId}" }
                    )
                )
            );
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
