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
[Category("ExplicitIntegrityScrub")]
public class Given_A_Postgresql_DocumentCacheExplicitIntegrityScrub_Command
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

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheExplicitIntegrityScrub_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
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

    [Test]
    public async Task It_repairs_missing_and_mismatched_work_without_changing_lifecycle_or_cache()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        SourceDocument missingWork = await InsertDocumentAsync(contentVersion: 10);
        SourceDocument staleWork = await InsertDocumentAsync(contentVersion: 20);
        SourceDocument aheadWork = await InsertDocumentAsync(contentVersion: 30);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(staleWork, requiredContentVersion: 15);
        await InsertProjectionWorkAsync(aheadWork, requiredContentVersion: 35);
        RecordingObservationSink observationSink = new();
        DocumentCacheExplicitIntegrityScrubCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Command.Should().Be(DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeFalse();

        IReadOnlyDictionary<long, long> workRows = await ReadWorkVersionsByDocumentIdAsync();
        workRows[missingWork.DocumentId].Should().Be(10);
        workRows[staleWork.DocumentId].Should().Be(20);
        workRows[aheadWork.DocumentId].Should().Be(30);
        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));

        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.Command == DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub
                && snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
                && snapshot.Mutated
            );
    }

    [Test]
    public async Task It_ignores_canonical_rows_inserted_above_the_captured_boundary()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        SourceDocument existing = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        SourceDocument? insertedAfterBoundary = null;
        var primitives = new BeforeFirstScrubPagePrimitives(
            DocumentCacheAdministrativePrimitives.Postgresql(),
            async () =>
            {
                insertedAfterBoundary = await InsertDocumentAsync(contentVersion: 99);
                await SetWorkRequiredContentVersionAsync(insertedAfterBoundary.DocumentId, 1);
            }
        );
        DocumentCacheExplicitIntegrityScrubCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new RecordingObservationSink(),
            primitives
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Mutated.Should().BeTrue();
        insertedAfterBoundary.Should().NotBeNull();

        IReadOnlyDictionary<long, long> workRows = await ReadWorkVersionsByDocumentIdAsync();
        workRows[existing.DocumentId].Should().Be(10);
        workRows[insertedAfterBoundary!.DocumentId].Should().Be(1);
    }

    [Test]
    public async Task It_sets_the_cache_ahead_latch_and_stops_without_repairing_that_work_row()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        SourceDocument source = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(source, requiredContentVersion: 5);
        await InsertCacheRowAsync(source, cacheContentVersion: 11);
        DocumentCacheExplicitIntegrityScrubCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SetCacheAheadLatch
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet
                && diagnostic.AffectedDocumentIds.Contains(source.DocumentId)
            );

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        (await ReadWorkVersionsByDocumentIdAsync())[source.DocumentId].Should().Be(5);
    }

    [Test]
    public async Task It_rechecks_cache_ahead_latch_after_preflight_before_boundary_capture()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        SourceDocument source = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        var primitives = new BeforeNthLifecycleReadPrimitives(
            DocumentCacheAdministrativePrimitives.Postgresql(),
            lifecycleReadNumber: 2,
            () => SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true)
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheExplicitIntegrityScrubCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            observationSink,
            primitives
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet
            );

        primitives.CaptureBoundaryCallCount.Should().Be(0);
        primitives.ScrubPageCallCount.Should().Be(0);
        (await ReadWorkVersionsByDocumentIdAsync()).Should().BeEmpty();
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .NotContain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.CaptureBoundary
                || snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
            );
        source.DocumentId.Should().BePositive();
    }

    [Test]
    public async Task It_stops_before_repairing_a_later_page_when_the_latch_becomes_set_between_pages()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        SourceDocument first = await InsertDocumentAsync(contentVersion: 10);
        SourceDocument second = await InsertDocumentAsync(contentVersion: 20);
        SourceDocument third = await InsertDocumentAsync(contentVersion: 30);
        SourceDocument fourth = await InsertDocumentAsync(contentVersion: 40);
        await ClearProjectionWorkAsync();
        var primitives = new BeforeNthLifecycleReadPrimitives(
            DocumentCacheAdministrativePrimitives.Postgresql(),
            lifecycleReadNumber: 4,
            () => SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true)
        );
        RecordingObservationSink observationSink = new();
        DocumentCacheExplicitIntegrityScrubCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            observationSink,
            primitives
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet
            );

        primitives.ScrubPageCallCount.Should().Be(1);
        IReadOnlyDictionary<long, long> workRows = await ReadWorkVersionsByDocumentIdAsync();
        workRows.Should().Contain(first.DocumentId, 10);
        workRows.Should().Contain(second.DocumentId, 20);
        workRows.Should().Contain(third.DocumentId, 30);
        workRows.Should().NotContainKey(fourth.DocumentId);
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
                && snapshot.Mutated
                && snapshot.CacheAheadRecoveryRequired == true
            );
    }

    [TestCase(DocumentCacheLifecycleState.Disabled, false)]
    [TestCase(DocumentCacheLifecycleState.Resetting, false)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding, false)]
    [TestCase(DocumentCacheLifecycleState.Tracking, true)]
    public async Task It_rejects_before_scan_when_lifecycle_or_latch_is_not_admitted(
        DocumentCacheLifecycleState lifecycle,
        bool cacheAheadRecoveryRequired
    )
    {
        await SetLifecycleAsync(lifecycle, cacheAheadRecoveryRequired);
        SourceDocument source = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        RecordingObservationSink observationSink = new();
        DocumentCacheExplicitIntegrityScrubCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(lifecycle, cacheAheadRecoveryRequired),
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(lifecycle);
        result.CacheAheadRecoveryRequired.Should().Be(cacheAheadRecoveryRequired);
        (await ReadWorkVersionsByDocumentIdAsync()).Should().BeEmpty();
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(lifecycle, cacheAheadRecoveryRequired));
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .NotContain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.CaptureBoundary
                || snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
            );
        source.DocumentId.Should().BePositive();
    }

    private DocumentCacheExplicitIntegrityScrubCommand CreateCommand(
        DocumentCacheLifecycleObservation lifecycle,
        RecordingObservationSink observationSink,
        IDocumentCacheAdministrativePrimitives? primitives = null
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
            primitives ?? DocumentCacheAdministrativePrimitives.Postgresql(),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );

        return new(runner);
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
                new ThrowingDocumentCacheMaterializer(),
                new ThrowingDocumentCacheWriter()
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt))
        );

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

    private Task SetWorkRequiredContentVersionAsync(long documentId, long requiredContentVersion) =>
        _database.ExecuteNonQueryAsync(
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

    private async Task InsertCacheRowAsync(SourceDocument source, long cacheContentVersion)
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
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = cacheContentVersion },
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar) { Value = $"etag-{cacheContentVersion}" },
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

    private Task<long> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM "dms"."{{tableName}}";""");

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

    private sealed class BeforeNthLifecycleReadPrimitives(
        IDocumentCacheAdministrativePrimitives inner,
        int lifecycleReadNumber,
        Func<Task> beforeLifecycleRead
    ) : IDocumentCacheAdministrativePrimitives
    {
        private int _captureBoundaryCallCount;
        private int _lifecycleReadCount;
        private int _scrubPageCallCount;

        public int CaptureBoundaryCallCount => _captureBoundaryCallCount;

        public int ScrubPageCallCount => _scrubPageCallCount;

        public RelationalProviderToken ProviderToken => inner.ProviderToken;

        public async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Increment(ref _lifecycleReadCount) == lifecycleReadNumber)
            {
                await beforeLifecycleRead().ConfigureAwait(false);
            }

            return await inner
                .ReadLifecycleAsync(mutexSession, lockMode, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.LockCanonicalDocumentsForGuardedActivationAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.ReadGuardedNewEmptyActivationStateAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.ValidateActivationPrerequisitesAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        ) => inner.TryTransitionLifecycleAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ClearDocumentCacheBatchAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        ) => inner.ClearDocumentProjectionWorkBatchAsync(mutexSession, request, clearance, cancellationToken);

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.ReadProjectedStateEmptinessAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _captureBoundaryCallCount);
            return inner.CaptureBaselineBoundaryAsync(mutexSession, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ObserveWorkHighWaterAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        ) => inner.SeedBaselinePageAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _scrubPageCallCount);
            return inner.ScrubPageAsync(mutexSession, request, cancellationToken);
        }
    }

    private sealed class BeforeFirstScrubPagePrimitives(
        IDocumentCacheAdministrativePrimitives inner,
        Func<Task> beforeFirstScrubPage
    ) : IDocumentCacheAdministrativePrimitives
    {
        private int _called;

        public RelationalProviderToken ProviderToken => inner.ProviderToken;

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        ) => inner.ReadLifecycleAsync(mutexSession, lockMode, cancellationToken);

        public Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.LockCanonicalDocumentsForGuardedActivationAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.ReadGuardedNewEmptyActivationStateAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.ValidateActivationPrerequisitesAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        ) => inner.TryTransitionLifecycleAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ClearDocumentCacheBatchAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        ) => inner.ClearDocumentProjectionWorkBatchAsync(mutexSession, request, clearance, cancellationToken);

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.ReadProjectedStateEmptinessAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        ) => inner.CaptureBaselineBoundaryAsync(mutexSession, cancellationToken);

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ObserveWorkHighWaterAsync(mutexSession, request, cancellationToken);

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        ) => inner.SeedBaselinePageAsync(mutexSession, request, cancellationToken);

        public async Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Exchange(ref _called, 1) == 0)
            {
                await beforeFirstScrubPage().ConfigureAwait(false);
            }

            return await inner.ScrubPageAsync(mutexSession, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ThrowingDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
