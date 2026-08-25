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
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("OfflineDeactivation")]
public class Given_A_Postgresql_DocumentCacheOfflineDeactivation_Command
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
        confirmation: DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheOfflineDeactivation_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
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

    [TestCase(DocumentCacheLifecycleState.Tracking)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding)]
    public async Task It_deactivates_a_tracking_or_rebuilding_internal_only_target_by_clearing_cache_and_work(
        DocumentCacheLifecycleState lifecycleState
    )
    {
        await SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired: false);
        IReadOnlyList<SourceDocument> sources = await InsertProjectedRowsAsync(documentCount: 5);
        RecordingObservationSink observationSink = new();
        DocumentCacheOfflineDeactivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(lifecycleState, false),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineDeactivationRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation
            )
        );

        result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineDeactivation);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
        result
            .OfflineWriterAdmission.Should()
            .Be(DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained);

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false));
        (await ReadCountAsync("Document")).Should().Be(sources.Count);
        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.Command == DocumentCacheAdministrativeCommand.OfflineDeactivation
                && snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterResetting
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearWork && snapshot.Mutated
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterDisabled
            );
    }

    [Test]
    public async Task It_resumes_resetting_with_clear_latch_by_clearing_cache_and_work()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Resetting, cacheAheadRecoveryRequired: false);
        await InsertProjectedRowsAsync(documentCount: 2);
        RecordingObservationSink observationSink = new();
        DocumentCacheOfflineDeactivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, false),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineDeactivationRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .NotContain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterResetting
            );
    }

    [Test]
    public async Task It_rejects_a_set_latch_without_mutating_lifecycle_cache_or_work()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheOfflineDeactivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineDeactivationRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation
            )
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
    public async Task It_rejects_non_internal_downstream_proof_without_mutation()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheOfflineDeactivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            DocumentCacheDownstreamPublicationStatus.Historical,
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineDeactivationRequest(
                AdministrativeTargetKey,
                Admission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
        (await ReadCountAsync("DocumentCache")).Should().Be(2);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(2);
    }

    private DocumentCacheOfflineDeactivationCommand CreateCommand(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus,
        RecordingObservationSink observationSink
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(lifecycle);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
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

        return new(runner, new FixedDownstreamPublicationHistoryProvider(downstreamPublicationStatus));
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
        DocumentCacheTargetExecutionContext executionContext
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
                ThrowingMaterializer.Instance,
                ThrowingWriter.Instance
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt))
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
            await InsertCacheRowAsync(source);
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

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid, contentVersion);
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

    private Task InsertProjectionWorkAsync(SourceDocument source) =>
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
                Value = source.ContentVersion,
            },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = FirstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt }
        );

    private async Task InsertCacheRowAsync(SourceDocument source)
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
                Value = new JsonObject { ["value"] = $"cache-{source.DocumentId}" }.ToJsonString(),
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
                    evidenceSource: "postgresql-offline-deactivation-test",
                    evidenceGenerationIdentifier: null,
                    ObservedAt,
                    "PostgreSQL offline deactivation test downstream publication proof."
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
        ) => throw new InvalidOperationException("Refresh is unused by offline deactivation command tests.");
    }

    private sealed record StubTargetRegistry(
        DocumentCacheTargetRegistrySnapshot CurrentSnapshot,
        DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Refresh is unused by offline deactivation command tests.");
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
        ) => _ = endedAt;

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => AdministrativeCommandSnapshots.Add(snapshot);

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }

    private sealed class ThrowingMaterializer : IDocumentCacheMaterializer
    {
        public static ThrowingMaterializer Instance { get; } = new();

        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new InvalidOperationException("Offline deactivation tests do not materialize.");
    }

    private sealed class ThrowingWriter : IDocumentCacheWriter
    {
        public static ThrowingWriter Instance { get; } = new();

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new InvalidOperationException("Offline deactivation tests do not write cache rows.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
