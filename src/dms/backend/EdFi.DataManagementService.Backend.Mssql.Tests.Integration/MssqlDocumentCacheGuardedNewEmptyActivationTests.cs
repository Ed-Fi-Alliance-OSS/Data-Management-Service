// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
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
[Category("GuardedNewEmptyActivation")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheGuardedNewEmptyActivation_Command
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly DateTime ObservedAt = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
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
            $"{nameof(Given_A_Mssql_DocumentCacheGuardedNewEmptyActivation_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
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
            Assert.Ignore("SQL Server guarded activation tests require nested triggers to be enabled.");
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
    public async Task It_transitions_empty_disabled_state_to_tracking_through_the_command_runner()
    {
        RecordingObservationSink observationSink = new();
        DocumentCacheGuardedNewEmptyActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Disabled,
                CacheAheadRecoveryRequired: false
            ),
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheGuardedNewEmptyActivationRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Command.Should().Be(DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.OfflineWriterAdmission.Should().BeNull();

        DocumentCacheLifecycleObservation lifecycle = await ReadLifecycleAsync();
        lifecycle
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));

        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.Command == DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation
                && snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterTracking
                && snapshot.Mutated
            );
        observationSink.EndedAdministrativeCommandIds.Should().ContainSingle();
    }

    [Test]
    public async Task It_rejects_nonempty_canonical_state_without_mutation()
    {
        await InsertDocumentAsync(contentVersion: 10);
        DocumentCacheGuardedNewEmptyActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Disabled,
                CacheAheadRecoveryRequired: false
            ),
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheGuardedNewEmptyActivationRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.NonemptyGuardedActivationState);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);

        (await ReadCountAsync("Document")).Should().Be(1);
        DocumentCacheLifecycleObservation lifecycle = await ReadLifecycleAsync();
        lifecycle
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false));
    }

    [Test]
    public async Task It_revalidates_provider_prerequisites_before_tracking_mutation()
    {
        DocumentCacheGuardedNewEmptyActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Disabled,
                CacheAheadRecoveryRequired: false
            ),
            new RecordingObservationSink()
        );
        await SetReadCommittedSnapshotAsync(_database.DatabaseName, enabled: false);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheGuardedNewEmptyActivationRequest(AdministrativeTargetKey, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed);
        result.Mutated.Should().BeFalse();

        DocumentCacheLifecycleObservation lifecycle = await ReadLifecycleAsync();
        lifecycle
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false));
        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
    }

    [Test]
    public async Task It_blocks_racing_canonical_insert_until_after_tracking()
    {
        TaskCompletionSource lockAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DocumentCacheGuardedNewEmptyActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Disabled,
                CacheAheadRecoveryRequired: false
            ),
            new RecordingObservationSink(),
            new DelayingGuardedActivationPrimitives(
                DocumentCacheAdministrativePrimitives.ForSqlServer(),
                lockAcquired,
                releaseLock
            )
        );

        Task<DocumentCacheAdministrativeCommandResult> commandTask = command.ExecuteAsync(
            new DocumentCacheGuardedNewEmptyActivationRequest(AdministrativeTargetKey, Fingerprint)
        );

        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task insertTask = InsertDocumentAsync(contentVersion: 20);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            insertTask.IsCompleted.Should().BeFalse();

            releaseLock.SetResult();

            DocumentCacheAdministrativeCommandResult result = await commandTask.WaitAsync(
                TimeSpan.FromSeconds(5)
            );
            await insertTask.WaitAsync(TimeSpan.FromSeconds(5));

            result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
            result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
            (await ReadCountAsync("DocumentProjectionWork")).Should().Be(1);
        }
        finally
        {
            releaseLock.TrySetResult();
        }
    }

    private DocumentCacheGuardedNewEmptyActivationCommand CreateCommand(
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
                DateTimeOffset.UtcNow
            ),
            new DocumentCacheTargetRuntimeSnapshot([executionContext], DateTimeOffset.UtcNow)
        );
        var runner = new DocumentCacheAdministrativeCommandRunner(
            new StubProjectionSupervisor([runtimeContext]),
            registry,
            new MssqlDocumentCacheAdministrativeMutex(
                NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
            ),
            primitives ?? DocumentCacheAdministrativePrimitives.ForSqlServer(),
            observationSink,
            new FixedTimeProvider(new DateTimeOffset(ObservedAt)),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );

        return new DocumentCacheGuardedNewEmptyActivationCommand(runner);
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
            projectorPollInterval: TimeSpan.FromMilliseconds(250),
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
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(new DateTimeOffset(ObservedAt)))
        );

    private async Task InsertDocumentAsync(long contentVersion)
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            );
            """,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt }
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

        public List<DocumentCacheAdministrativeCommandExecutionId> EndedAdministrativeCommandIds { get; } =
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
            EndedAdministrativeCommandIds.Add(executionId);
    }

    private sealed class DelayingGuardedActivationPrimitives(
        IDocumentCacheAdministrativePrimitives inner,
        TaskCompletionSource lockAcquired,
        TaskCompletionSource releaseLock
    ) : IDocumentCacheAdministrativePrimitives
    {
        public RelationalProviderToken ProviderToken => inner.ProviderToken;

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        ) => inner.ReadLifecycleAsync(mutexSession, lockMode, cancellationToken);

        public async Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            await inner
                .LockCanonicalDocumentsForGuardedActivationAsync(mutexSession, cancellationToken)
                .ConfigureAwait(false);
            lockAcquired.SetResult();
            await releaseLock.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

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

        public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ScrubPageAsync(mutexSession, request, cancellationToken);
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
}
