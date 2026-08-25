// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
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
[Category("CacheAheadRecovery")]
public class Given_A_Postgresql_DocumentCacheInternalOnlyCacheAheadRecovery_Command
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAt.AddMinutes(-5);
    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 1);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheOfflineWriterAdmission Admission = new(
        confirmed: true,
        confirmation: DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private Dictionary<long, SourceDocument> _sourcesByDocumentId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheInternalOnlyCacheAheadRecovery_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _sourcesByDocumentId = [];
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
    public async Task It_recovers_a_tracking_set_latch_target_by_clearing_stale_work_then_draining_seeded_baseline()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        IReadOnlyList<SourceDocument> sources = await InsertProjectedRowsAsync(documentCount: 3);
        RecordingObservationSink observationSink = new();
        var drainerProbe = new DrainStartProbe(async () =>
        {
            (await ReadCountAsync("DocumentCache")).Should().Be(0);
            (await ReadWorkVersionsByDocumentIdAsync())
                .Should()
                .Equal(sources.ToDictionary(source => source.DocumentId, source => source.ContentVersion));
            (await ReadLifecycleAsync())
                .Should()
                .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false));
        });
        DocumentCacheInternalOnlyCacheAheadRecoveryCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink,
            drainerProbe
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
            )
        );

        result.Command.Should().Be(DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeFalse();
        result
            .OfflineWriterAdmission.Should()
            .Be(
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
            );
        drainerProbe.StartCount.Should().Be(1);

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        (await ReadCountAsync("DocumentCache")).Should().Be(sources.Count);
        IReadOnlyDictionary<long, string> cachedJsonByDocumentId = await ReadCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId.Values.Should().OnlyContain(json => json.Contains("recovered"));

        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.Command == DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery
                && snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterResetting
                && snapshot.CacheAheadRecoveryRequired == true
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearWork
                && snapshot.Lifecycle == DocumentCacheLifecycleState.Resetting
                && snapshot.CacheAheadRecoveryRequired == true
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterRebuilding
                && snapshot.CacheAheadRecoveryRequired == false
            );
    }

    [Test]
    public async Task It_resumes_resetting_with_the_latch_set_without_reentering_resetting()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Resetting, cacheAheadRecoveryRequired: true);
        await InsertProjectedRowsAsync(documentCount: 2);
        RecordingObservationSink observationSink = new();
        DocumentCacheInternalOnlyCacheAheadRecoveryCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, true),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeFalse();
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .NotContain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterResetting
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearWork
                && snapshot.Lifecycle == DocumentCacheLifecycleState.Resetting
                && snapshot.CacheAheadRecoveryRequired == true
            );
    }

    [Test]
    public async Task It_resumes_rebuilding_with_clear_latch_without_repeating_destructive_clearing()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Rebuilding, cacheAheadRecoveryRequired: false);
        SourceDocument source = await InsertDocumentAsync(contentVersion: 21);
        await ClearProjectionWorkAsync();
        await InsertCacheRowAsync(source, jsonPrefix: "kept-cache");
        RecordingObservationSink observationSink = new();
        DocumentCacheInternalOnlyCacheAheadRecoveryCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeFalse();
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        IReadOnlyDictionary<long, string> cachedJsonByDocumentId = await ReadCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId[source.DocumentId].Should().Contain("kept-cache");
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .NotContain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearCache
                || snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearWork
                || snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterRebuilding
            );
    }

    [Test]
    public async Task It_rejects_non_internal_downstream_proof_without_mutation()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheInternalOnlyCacheAheadRecoveryCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
            DocumentCacheDownstreamPublicationStatus.Possible,
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeTrue();

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        (await ReadCountAsync("DocumentCache")).Should().Be(2);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(2);
    }

    private DocumentCacheInternalOnlyCacheAheadRecoveryCommand CreateCommand(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus,
        RecordingObservationSink observationSink,
        DrainStartProbe? drainStartProbe = null
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(lifecycle);
        PostgresqlDocumentCacheWriter writer = CreateWriter();
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext, writer);
        var registry = new StubTargetRegistry(
            new DocumentCacheTargetRegistrySnapshot([EligibleObservation(executionContext)], ObservedAt),
            new DocumentCacheTargetRuntimeSnapshot([executionContext], ObservedAt)
        );
        var runner = new DocumentCacheAdministrativeCommandRunner(
            new StubProjectionSupervisor([runtimeContext]),
            registry,
            new PostgresqlDocumentCacheAdministrativeMutex(
                _dataSourceCache,
                NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance
            ),
            DocumentCacheAdministrativePrimitives.ForPostgresql(
                new PostgresqlDocumentCacheProviderCommandTimeoutClassifier()
            ),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(observationSink);
        IDocumentCacheAdministrativeDrainer effectiveDrainer = drainStartProbe is null
            ? drainer
            : new ProbingDrainer(drainStartProbe, drainer);

        return new(
            runner,
            new FixedDownstreamPublicationHistoryProvider(downstreamPublicationStatus),
            CreateBaselineSeeder(effectiveDrainer),
            effectiveDrainer
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
            NullLogger<PostgresqlDocumentCacheWriter>.Instance,
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier()
        );

    private static DocumentCacheBaselineSeeder CreateBaselineSeeder(
        IDocumentCacheAdministrativeDrainer? drainer = null
    ) =>
        new(
            new DocumentCacheBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheBaselineSeeder>.Instance,
            drainer
        );

    private DocumentCacheAdministrativeDrainer CreateDrainer(
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
                new PostgresqlDocumentProjectionWorkPager(
                    _dataSourceCache,
                    NullLogger<PostgresqlDocumentProjectionWorkPager>.Instance
                ),
                new DocumentCacheProjectionItemProcessor(
                    new FixedTimeProvider(ObservedAt),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new FixedTimeProvider(ObservedAt)
            ),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );

        return new(
            scheduler,
            new DocumentCacheAdministrativeDrainDelay(),
            new FixedTimeProvider(ObservedAt),
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
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
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
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
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
        PostgresqlDocumentCacheWriter writer
    ) =>
        new(
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
                new CandidateMaterializer(_fixture.MappingSet, _sourcesByDocumentId),
                writer
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt)),
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
            await InsertProjectionWorkAsync(source, requiredContentVersion: source.ContentVersion + 100);
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
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt }
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
            UPDATE "dms"."DocumentCacheState"
            SET
                "ProjectionLifecycleState" = @lifecycle,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycle", NpgsqlDbType.Varchar) { Value = lifecycle.ToString() },
            new NpgsqlParameter("cacheAheadRecoveryRequired", NpgsqlDbType.Boolean)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM "dms"."DocumentProjectionWork";""");

    private Task InsertProjectionWorkAsync(SourceDocument source, long requiredContentVersion) =>
        _database.ExecuteNonQueryAsync(
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
            );
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = source.DocumentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
            {
                Value = requiredContentVersion,
            },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = FirstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt }
        );

    private async Task InsertCacheRowAsync(SourceDocument source, string jsonPrefix)
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[
            _fixture.MappingSet.ResourceKeyIdByResource[PersonResource]
        ];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentCache" (
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson",
                "ComputedAt"
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
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = source.DocumentId },
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = source.DocumentUuid },
            new NpgsqlParameter("projectName", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new NpgsqlParameter("resourceName", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.ResourceVersion,
            },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = source.ContentVersion },
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar)
            {
                Value = $"etag-{source.ContentVersion}",
            },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt },
            new NpgsqlParameter("documentJson", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["value"] = $"{jsonPrefix}-{source.DocumentId}" }.ToJsonString(),
            },
            new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt.AddMinutes(1) }
        );
    }

    private async Task<DocumentCacheLifecycleObservation> ReadLifecycleAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
            FROM "dms"."DocumentCacheState"
            WHERE "StateId" = 1;
            """
        );

        IReadOnlyDictionary<string, object?> row = rows.Single();
        return new(
            Enum.Parse<DocumentCacheLifecycleState>((string)row["ProjectionLifecycleState"]!),
            Convert.ToBoolean(row["CacheAheadRecoveryRequired"])
        );
    }

    private async Task<IReadOnlyDictionary<long, long>> ReadWorkVersionsByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "RequiredContentVersion"
            FROM "dms"."DocumentProjectionWork"
            ORDER BY "DocumentId";
            """
        );

        return rows.ToDictionary(
            row => Convert.ToInt64(row["DocumentId"]),
            row => Convert.ToInt64(row["RequiredContentVersion"])
        );
    }

    private async Task<IReadOnlyDictionary<long, string>> ReadCachedJsonByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentJson"::text AS "DocumentJson"
            FROM "dms"."DocumentCache"
            ORDER BY "DocumentId";
            """
        );

        return rows.ToDictionary(
            row => Convert.ToInt64(row["DocumentId"]),
            row => (string)row["DocumentJson"]!
        );
    }

    private Task<long> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM "dms"."{{tableName}}";""");

    private sealed class FixedDownstreamPublicationHistoryProvider(
        DocumentCacheDownstreamPublicationStatus status
    ) : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new DocumentCacheDownstreamPublicationHistoryObservation(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    status,
                    evidenceSource: "postgresql-cache-ahead-recovery-test",
                    evidenceGenerationIdentifier: null,
                    ObservedAt,
                    "PostgreSQL cache-ahead recovery test downstream publication proof."
                )
            );
        }
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
                        ObservedAt,
                        $"etag-{source.ContentVersion}",
                        new JsonObject { ["value"] = $"recovered-{source.DocumentId}" }
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
