// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
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
[Category("OfflineActivation")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheOfflineActivation_Command
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly DateTime ObservedAt = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset ObservedAtOffset = new(ObservedAt);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 1);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheOfflineWriterAdmission Admission = new(
        confirmed: true,
        confirmation: DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
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
            $"{nameof(Given_A_Mssql_DocumentCacheOfflineActivation_Command)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
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
            Assert.Ignore("SQL Server offline activation tests require nested triggers to be enabled.");
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
    public async Task It_activates_a_disabled_empty_internal_only_target()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
        RecordingObservationSink observationSink = new();
        DocumentCacheOfflineActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineActivationRequest(AdministrativeTargetKey, Admission, Fingerprint)
        );

        result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineActivation);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result
            .OfflineWriterAdmission.Should()
            .Be(DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained);

        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.Command == DocumentCacheAdministrativeCommand.OfflineActivation
                && snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearWork
            );
        observationSink
            .AdministrativeCommandSnapshots.Should()
            .Contain(snapshot =>
                snapshot.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterTracking
            );
    }

    [Test]
    public async Task It_resumes_rebuilding_without_repeating_destructive_clearing()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Rebuilding, cacheAheadRecoveryRequired: false);
        RecordingObservationSink observationSink = new();
        DocumentCacheOfflineActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false),
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            observationSink
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineActivationRequest(AdministrativeTargetKey, Admission, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
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
        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
        DocumentCacheOfflineActivationCommand command = CreateCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
            DocumentCacheDownstreamPublicationStatus.Unknown,
            new RecordingObservationSink()
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOfflineActivationRequest(AdministrativeTargetKey, Admission, Fingerprint)
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false));
    }

    private DocumentCacheOfflineActivationCommand CreateCommand(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus,
        RecordingObservationSink observationSink
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
            DocumentCacheAdministrativePrimitives.ForSqlServer(),
            observationSink,
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );

        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(observationSink);

        return new(
            runner,
            new FixedDownstreamPublicationHistoryProvider(downstreamPublicationStatus),
            CreateBaselineSeeder(drainer),
            drainer
        );
    }

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
                ThrowingMaterializer.Instance,
                ThrowingWriter.Instance
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAtOffset))
        );

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
                    evidenceSource: "mssql-offline-activation-test",
                    evidenceGenerationIdentifier: null,
                    ObservedAtOffset,
                    "SQL Server offline activation test downstream publication proof."
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
        ) => throw new InvalidOperationException("Refresh is unused by offline activation command tests.");
    }

    private sealed record StubTargetRegistry(
        DocumentCacheTargetRegistrySnapshot CurrentSnapshot,
        DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Refresh is unused by offline activation command tests.");
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
        ) => throw new InvalidOperationException("Offline activation empty-work tests do not materialize.");
    }

    private sealed class ThrowingWriter : IDocumentCacheWriter
    {
        public static ThrowingWriter Instance { get; } = new();

        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new InvalidOperationException(
                "Offline activation empty-work tests do not write cache rows."
            );
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
