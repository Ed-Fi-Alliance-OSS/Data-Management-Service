// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheStatus")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheStatusCurrentSourceObserver
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset FirstEnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
    private static readonly DateTimeOffset LaterEnqueuedAt = FirstEnqueuedAt.AddMinutes(1);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlDocumentCacheStatusCurrentSourceObserver _observer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheStatusCurrentSourceObserver)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _observer = new MssqlDocumentCacheStatusCurrentSourceObserver(
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<MssqlDocumentCacheStatusCurrentSourceObserver>.Instance
        );
    }

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

    [TestCase(DocumentCacheLifecycleState.Tracking)]
    [TestCase(DocumentCacheLifecycleState.Disabled)]
    [TestCase(DocumentCacheLifecycleState.Resetting)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding)]
    public async Task It_observes_lifecycle_and_empty_queue_from_the_current_source(
        DocumentCacheLifecycleState lifecycleState
    )
    {
        await SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired: false);

        DocumentCacheStatusCurrentSourceObservationResult result = await _observer.ObserveAsync(
            new(CreateExecutionContext())
        );

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded);
        result.LifecycleState.Should().Be(lifecycleState);
        result.CacheAheadRecoveryRequired.Should().BeFalse();
        result.QueuePresence.Should().Be(DocumentCacheStatusDurableQueuePresence.Empty);
        result.OldestWorkFirstEnqueuedAt.Should().BeNull();
        result.OldestWorkAgeSeconds.Should().BeNull();
        result.DurableObservedAt.Should().NotBeNull();
        result.DurableObservedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public async Task It_observes_cache_ahead_latch_nonempty_queue_oldest_work_and_provider_age()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        long laterDocumentId = await InsertDocumentAsync(contentVersion: 20);
        long oldestDocumentId = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(
            laterDocumentId,
            requiredContentVersion: 20,
            LaterEnqueuedAt,
            LaterEnqueuedAt.AddSeconds(5)
        );
        await InsertProjectionWorkAsync(
            oldestDocumentId,
            requiredContentVersion: 10,
            FirstEnqueuedAt,
            FirstEnqueuedAt.AddSeconds(5)
        );

        DocumentCacheStatusCurrentSourceObservationResult result = await _observer.ObserveAsync(
            new(CreateExecutionContext())
        );

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded);
        result.CacheAheadRecoveryRequired.Should().BeTrue();
        result.QueuePresence.Should().Be(DocumentCacheStatusDurableQueuePresence.NotEmpty);
        result.OldestWorkFirstEnqueuedAt.Should().BeCloseTo(FirstEnqueuedAt, TimeSpan.FromSeconds(1));
        result.OldestWorkAgeSeconds.Should().BeGreaterThan(0);
        result.DurableObservedAt.Should().NotBeNull();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_preserves_same_statement_queue_facts_when_state_is_missing_or_invalid(
        bool deleteState
    )
    {
        long documentId = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(
            documentId,
            requiredContentVersion: 10,
            FirstEnqueuedAt,
            FirstEnqueuedAt.AddSeconds(5)
        );

        if (deleteState)
        {
            await _database.ExecuteNonQueryAsync(
                """
                DELETE FROM [dms].[DocumentCacheState];
                """
            );
        }
        else
        {
            await _database.ExecuteNonQueryAsync(
                """
                ALTER TABLE [dms].[DocumentCacheState]
                DROP CONSTRAINT [CK_DocumentCacheState_Lifecycle];

                UPDATE [dms].[DocumentCacheState]
                SET [ProjectionLifecycleState] = N'InvalidLifecycle'
                WHERE [StateId] = 1;
                """
            );
        }

        DocumentCacheStatusCurrentSourceObservationResult result = await _observer.ObserveAsync(
            new(CreateExecutionContext())
        );

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.StateMissingOrInvalid);
        result.DurableObservedAt.Should().NotBeNull();
        result.LifecycleState.Should().BeNull();
        result.CacheAheadRecoveryRequired.Should().BeNull();
        result.QueuePresence.Should().Be(DocumentCacheStatusDurableQueuePresence.NotEmpty);
        result.OldestWorkFirstEnqueuedAt.Should().BeCloseTo(FirstEnqueuedAt, TimeSpan.FromSeconds(1));
        result.OldestWorkAgeSeconds.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task It_returns_cancelled_without_durable_facts_when_cancelled_before_starting()
    {
        using CancellationTokenSource cancellationSource = new();
        await cancellationSource.CancelAsync();

        DocumentCacheStatusCurrentSourceObservationResult result = await _observer.ObserveAsync(
            new(CreateExecutionContext()),
            cancellationSource.Token
        );

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled);
        result.DurableObservedAt.Should().BeNull();
        result.LifecycleState.Should().BeNull();
        result.QueuePresence.Should().BeNull();
    }

    [Test]
    public async Task It_returns_provider_timeout_without_stale_facts_when_the_statement_times_out()
    {
        await using SqlConnection blockerConnection = new(_database.ConnectionString);
        await blockerConnection.OpenAsync();
        await using SqlTransaction blockerTransaction = (SqlTransaction)
            await blockerConnection.BeginTransactionAsync();
        await using SqlCommand blockerCommand = blockerConnection.CreateCommand();
        blockerCommand.Transaction = blockerTransaction;
        blockerCommand.CommandText = """
            ALTER TABLE [dms].[DocumentProjectionWork]
            ADD [StatusObservationBlocker] int NULL;
            """;
        await blockerCommand.ExecuteNonQueryAsync();

        try
        {
            DocumentCacheStatusCurrentSourceObservationResult result = await _observer
                .ObserveAsync(new(CreateExecutionContext(statusObservationTimeout: TimeSpan.FromSeconds(1))))
                .WaitAsync(TimeSpan.FromSeconds(10));

            result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout);
            result.DurableObservedAt.Should().BeNull();
            result.LifecycleState.Should().BeNull();
            result.QueuePresence.Should().BeNull();
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }
    }

    [Test]
    public async Task It_returns_failed_without_stale_facts_when_the_provider_statement_fails()
    {
        await _database.ExecuteNonQueryAsync(
            """
            EXEC sp_rename N'dms.DocumentProjectionWork', N'DocumentProjectionWork_Renamed';
            """
        );

        DocumentCacheStatusCurrentSourceObservationResult result = await _observer.ObserveAsync(
            new(CreateExecutionContext())
        );

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.Failed);
        result.DurableObservedAt.Should().BeNull();
        result.LifecycleState.Should().BeNull();
        result.QueuePresence.Should().BeNull();
    }

    [Test]
    public async Task It_uses_the_ordered_single_row_projection_work_index_and_not_source_or_cache_scans()
    {
        long laterDocumentId = await InsertDocumentAsync(contentVersion: 20);
        long oldestDocumentId = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(
            laterDocumentId,
            requiredContentVersion: 20,
            LaterEnqueuedAt,
            LaterEnqueuedAt.AddSeconds(5)
        );
        await InsertProjectionWorkAsync(
            oldestDocumentId,
            requiredContentVersion: 10,
            FirstEnqueuedAt,
            FirstEnqueuedAt.AddSeconds(5)
        );
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE STATISTICS [dms].[DocumentProjectionWork];
            """
        );

        string plan = await ReadStatisticsXmlPlanAsync(
            MssqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql
        );

        plan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        plan.Should().NotContain("[dms].[Document]");
        plan.Should().NotContain("[dms].[DocumentCache]");
        MssqlDocumentCacheStatusCurrentSourceObserver
            .StatusObservationSql.ToUpperInvariant()
            .Should()
            .NotContain("COUNT");
        MssqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql.Should().Contain("TOP (1)");
    }

    private DocumentCacheTargetExecutionContext CreateExecutionContext(
        TimeSpan? statusObservationTimeout = null
    )
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("tenant-status", 7);
        return new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 2,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24),
                statusObservationTimeout: statusObservationTimeout,
                statusEndpointTimeout: TimeSpan.FromSeconds(30)
            ),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "sqlserver"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            ),
            Fingerprint,
            TrackingLifecycle,
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
    }

    private async Task<long> InsertDocumentAsync(long contentVersion)
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
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
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = FirstEnqueuedAt.UtcDateTime }
        );

        return Convert.ToInt64(rows.Single()["DocumentId"]);
    }

    private Task SetLifecycleAsync(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired
    ) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycleState,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@lifecycleState", lifecycleState.ToString()),
            new SqlParameter("@cacheAheadRecoveryRequired", SqlDbType.Bit)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[DocumentProjectionWork];
            """
        );

    private Task InsertProjectionWorkAsync(
        long documentId,
        long requiredContentVersion,
        DateTimeOffset firstEnqueuedAt,
        DateTimeOffset lastEnqueuedAt
    ) =>
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
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = requiredContentVersion },
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = firstEnqueuedAt.UtcDateTime },
            new SqlParameter("@lastEnqueuedAt", SqlDbType.DateTime2) { Value = lastEnqueuedAt.UtcDateTime }
        );

    private async Task<string> ReadStatisticsXmlPlanAsync(string sql)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SetStatisticsXmlAsync(connection, enabled: true);
        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 300;

            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            StringBuilder plan = new();
            do
            {
                while (await reader.ReadAsync())
                {
                    if (
                        reader.FieldCount == 1
                        && reader.GetFieldType(0) == typeof(string)
                        && !await reader.IsDBNullAsync(0)
                    )
                    {
                        plan.AppendLine(reader.GetString(0));
                    }
                }
            } while (await reader.NextResultAsync());

            return plan.ToString();
        }
        finally
        {
            await SetStatisticsXmlAsync(connection, enabled: false);
        }
    }

    private static async Task SetStatisticsXmlAsync(SqlConnection connection, bool enabled)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = enabled ? "SET STATISTICS XML ON;" : "SET STATISTICS XML OFF;";
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
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
}
