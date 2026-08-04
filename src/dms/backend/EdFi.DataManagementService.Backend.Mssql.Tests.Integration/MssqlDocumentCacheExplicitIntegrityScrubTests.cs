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
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("ExplicitIntegrityScrub")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheExplicitIntegrityScrub_Command
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

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheExplicitIntegrityScrub_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
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
            Assert.Ignore("SQL Server explicit integrity scrub tests require nested triggers to be enabled.");
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
            new MssqlDocumentCacheAdministrativePrimitives(),
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
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
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
            new MssqlDocumentCacheAdministrativePrimitives(),
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
            new MssqlDocumentCacheAdministrativePrimitives(),
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
            primitives ?? new MssqlDocumentCacheAdministrativePrimitives(),
            observationSink,
            new FixedTimeProvider(ObservedAtOffset),
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
        DocumentCacheTargetExecutionContext executionContext
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
                new ThrowingDocumentCacheMaterializer(),
                new ThrowingDocumentCacheWriter()
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAtOffset))
        );

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

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid, contentVersion);
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

    private Task InsertProjectionWorkAsync(SourceDocument source, long requiredContentVersion) =>
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
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = requiredContentVersion },
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = FirstEnqueuedAt.UtcDateTime },
            new SqlParameter("@lastEnqueuedAt", SqlDbType.DateTime2) { Value = ObservedAt }
        );

    private Task SetWorkRequiredContentVersionAsync(long documentId, long requiredContentVersion) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentProjectionWork]
            SET [RequiredContentVersion] = @requiredContentVersion
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = requiredContentVersion }
        );

    private async Task InsertCacheRowAsync(SourceDocument source, long cacheContentVersion)
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
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = cacheContentVersion },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64) { Value = $"etag-{cacheContentVersion}" },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = $"cache-{source.DocumentId}" }.ToJsonString(),
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

    private async Task<IReadOnlyDictionary<long, long>> ReadWorkVersionsByDocumentIdAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT [DocumentId], [RequiredContentVersion]
            FROM [dms].[DocumentProjectionWork]
            ORDER BY [DocumentId];
            """
        );

        return rows.ToDictionary(
            row => Convert.ToInt64(row["DocumentId"]),
            row => Convert.ToInt64(row["RequiredContentVersion"])
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
