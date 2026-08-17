// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
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
[Category("DocumentCacheStatus")]
public class Given_A_Postgresql_DocumentCacheStatusCurrentSourceObserver
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

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private PostgresqlDocumentCacheStatusCurrentSourceObserver _observer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheStatusCurrentSourceObserver)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _observer = new PostgresqlDocumentCacheStatusCurrentSourceObserver(
            _dataSourceCache,
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<PostgresqlDocumentCacheStatusCurrentSourceObserver>.Instance
        );
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

    [Test]
    public async Task It_returns_state_missing_or_invalid_without_reusing_queue_facts()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "dms"."DocumentCacheState";
            """
        );

        DocumentCacheStatusCurrentSourceObservationResult result = await _observer.ObserveAsync(
            new(CreateExecutionContext())
        );

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.StateMissingOrInvalid);
        result.DurableObservedAt.Should().NotBeNull();
        result.LifecycleState.Should().BeNull();
        result.CacheAheadRecoveryRequired.Should().BeNull();
        result.QueuePresence.Should().BeNull();
        result.OldestWorkFirstEnqueuedAt.Should().BeNull();
        result.OldestWorkAgeSeconds.Should().BeNull();
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
        await using NpgsqlConnection blockerConnection = new(_database.ConnectionString);
        await blockerConnection.OpenAsync();
        await using NpgsqlTransaction blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using NpgsqlCommand blockerCommand = blockerConnection.CreateCommand();
        blockerCommand.Transaction = blockerTransaction;
        blockerCommand.CommandText =
            """LOCK TABLE "dms"."DocumentProjectionWork" IN ACCESS EXCLUSIVE MODE;""";
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
            ALTER TABLE "dms"."DocumentProjectionWork" RENAME TO "DocumentProjectionWork_Renamed";
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

        string plan = await ExplainStatusObservationSqlAsync();

        plan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        plan.Should().NotContain(" on \"Document\" ");
        plan.Should().NotContain(" on \"DocumentCache\" ");
        PostgresqlDocumentCacheStatusCurrentSourceObserver
            .StatusObservationSql.ToUpperInvariant()
            .Should()
            .NotContain("COUNT");
        PostgresqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql.Should().Contain("LIMIT 1");
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
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
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
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
    }

    private async Task<long> InsertDocumentAsync(long contentVersion)
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
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
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = FirstEnqueuedAt }
        );

        return Convert.ToInt64(rows.Single()["DocumentId"]);
    }

    private Task SetLifecycleAsync(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired
    ) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycleState", lifecycleState.ToString()),
            new NpgsqlParameter("cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
        );

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "dms"."DocumentProjectionWork";
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
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
            {
                Value = requiredContentVersion,
            },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = firstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = lastEnqueuedAt }
        );

    private async Task<string> ExplainStatusObservationSqlAsync()
    {
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand disableSeqScanCommand = connection.CreateCommand();
        disableSeqScanCommand.CommandText = "SET enable_seqscan = off;";
        await disableSeqScanCommand.ExecuteNonQueryAsync();

        await using NpgsqlCommand explainCommand = connection.CreateCommand();
        explainCommand.CommandText =
            "EXPLAIN (COSTS OFF) " + PostgresqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql;

        List<string> lines = [];
        await using NpgsqlDataReader reader = await explainCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
