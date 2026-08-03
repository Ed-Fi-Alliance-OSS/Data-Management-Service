// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
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
[Category("DocumentCacheBaselineSeeding")]
public class Given_A_Postgresql_DocumentCacheBaselineSeeding_Primitive
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset OldFirstEnqueuedAt = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OldLastEnqueuedAt = OldFirstEnqueuedAt.AddMinutes(1);
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 8, 1, 12, 1, 0, TimeSpan.Zero);

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private PostgresqlDocumentCacheAdministrativeMutex _mutex = null!;
    private PostgresqlDocumentCacheAdministrativePrimitives _primitives = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheBaselineSeeding_Primitive)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _mutex = new PostgresqlDocumentCacheAdministrativeMutex(
            _dataSourceCache,
            NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance
        );
        _primitives = new PostgresqlDocumentCacheAdministrativePrimitives();
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
    public async Task It_seeds_missing_work_and_conditionally_repairs_mismatched_work()
    {
        SourceDocument missingWork = await InsertDocumentAsync(contentVersion: 10);
        SourceDocument staleWork = await InsertDocumentAsync(contentVersion: 20);
        SourceDocument aheadWork = await InsertDocumentAsync(contentVersion: 30);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(staleWork, requiredContentVersion: 15);
        await InsertProjectionWorkAsync(aheadWork, requiredContentVersion: 35);

        await using IDocumentCacheAdministrativeMutexLease lease = await AcquireMutexAsync();

        DocumentCacheAdministrativeBaselineBoundaryResult boundary = await CaptureBoundaryAndCommitAsync(
            lease
        );
        DocumentCacheAdministrativeWorkHighWaterObservationResult highWater =
            await ObserveHighWaterAndCommitAsync(lease, highWaterMark: 3, diagnosticCapacity: 2);
        DocumentCacheAdministrativeBaselineSeedPageResult firstPage = await SeedPageAndCommitAsync(
            lease,
            boundary.BoundaryDocumentId!.Value,
            afterDocumentId: 0,
            pageSize: 2
        );
        DocumentCacheAdministrativeBaselineSeedPageResult finalPage = await SeedPageAndCommitAsync(
            lease,
            boundary.BoundaryDocumentId.Value,
            afterDocumentId: firstPage.LastVisitedDocumentId!.Value,
            pageSize: 2
        );

        boundary.BoundaryDocumentId.Should().Be(aheadWork.DocumentId);
        highWater.ObservedWorkRows.Should().Be(2);
        highWater.IsAtOrAboveHighWater.Should().BeFalse();
        highWater.DiagnosticDocumentIds.Should().Equal(staleWork.DocumentId, aheadWork.DocumentId);
        firstPage
            .Documents.Select(document => document.MutationKind)
            .Should()
            .Equal(
                DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted,
                DocumentCacheAdministrativeBaselineWorkMutationKind.Advanced
            );
        finalPage.Documents.Should().ContainSingle();
        finalPage
            .Documents[0]
            .MutationKind.Should()
            .Be(DocumentCacheAdministrativeBaselineWorkMutationKind.Lowered);

        IReadOnlyDictionary<long, WorkRow> workRows = await ReadWorkRowsAsync();
        workRows[missingWork.DocumentId].RequiredContentVersion.Should().Be(10);
        workRows[staleWork.DocumentId].RequiredContentVersion.Should().Be(20);
        workRows[staleWork.DocumentId].FirstEnqueuedAt.Should().Be(OldFirstEnqueuedAt);
        workRows[staleWork.DocumentId].LastEnqueuedAt.Should().BeAfter(OldLastEnqueuedAt);
        workRows[aheadWork.DocumentId].RequiredContentVersion.Should().Be(30);
        workRows[aheadWork.DocumentId].FirstEnqueuedAt.Should().Be(OldFirstEnqueuedAt);
        workRows[aheadWork.DocumentId].LastEnqueuedAt.Should().Be(OldLastEnqueuedAt);
    }

    [Test]
    public async Task It_retries_when_observed_work_changes_before_page_repair()
    {
        SourceDocument racedWork = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(racedWork, requiredContentVersion: 5);

        long advisoryLockKey = 1_314_000_000L + racedWork.DocumentId;
        await InstallBaselineSeedRaceTriggerAsync(racedWork.DocumentId, advisoryLockKey);

        await using var blocker = new NpgsqlConnection(_database.ConnectionString);
        await blocker.OpenAsync();
        await AcquireAdvisoryLockAsync(blocker, advisoryLockKey);

        await using IDocumentCacheAdministrativeMutexLease lease = await AcquireMutexAsync();
        Task<DocumentCacheAdministrativeBaselineSeedPageResult> seedTask = SeedPageAndRollbackOnRetryAsync(
            lease,
            boundaryDocumentId: racedWork.DocumentId,
            afterDocumentId: 0,
            pageSize: 1
        );

        try
        {
            await WaitForBlockedAdvisoryLockAsync(seedTask);
            await UpdateProjectionWorkAsync(racedWork, requiredContentVersion: 99);
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(blocker, advisoryLockKey);
        }

        DocumentCacheAdministrativeBaselineSeedPageResult result = await seedTask;

        result
            .Status.Should()
            .Be(DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey);
        result.Mutated.Should().BeFalse();
        result.Documents.Should().ContainSingle();
        result.Documents[0].DocumentId.Should().Be(racedWork.DocumentId);
        result.Documents[0].SourceContentVersion.Should().Be(10);
        result.Documents[0].PreviousRequiredContentVersion.Should().Be(5);
        result
            .Documents[0]
            .MutationKind.Should()
            .Be(DocumentCacheAdministrativeBaselineWorkMutationKind.Retry);

        IReadOnlyDictionary<long, WorkRow> workRows = await ReadWorkRowsAsync();
        workRows[racedWork.DocumentId].RequiredContentVersion.Should().Be(99);
    }

    private Task<IDocumentCacheAdministrativeMutexLease> AcquireMutexAsync() =>
        _mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
                _database.ConnectionString
            )
        );

    private async Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBoundaryAndCommitAsync(
        IDocumentCacheAdministrativeMutexLease lease
    )
    {
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );

        try
        {
            DocumentCacheAdministrativeBaselineBoundaryResult result =
                await _primitives.CaptureBaselineBoundaryAsync(session);
            await session.CommitAsync();
            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveHighWaterAndCommitAsync(
        IDocumentCacheAdministrativeMutexLease lease,
        int highWaterMark,
        int diagnosticCapacity
    )
    {
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );

        try
        {
            DocumentCacheAdministrativeWorkHighWaterObservationResult result =
                await _primitives.ObserveWorkHighWaterAsync(
                    session,
                    new DocumentCacheAdministrativeWorkHighWaterObservationRequest(
                        highWaterMark,
                        diagnosticCapacity
                    )
                );
            await session.CommitAsync();
            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedPageAndCommitAsync(
        IDocumentCacheAdministrativeMutexLease lease,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize
    )
    {
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.Serializable
        );

        try
        {
            DocumentCacheAdministrativeBaselineSeedPageResult result =
                await _primitives.SeedBaselinePageAsync(
                    session,
                    new DocumentCacheAdministrativeBaselineSeedPageRequest(
                        boundaryDocumentId,
                        afterDocumentId,
                        pageSize
                    )
                );
            await session.CommitAsync();
            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedPageAndRollbackOnRetryAsync(
        IDocumentCacheAdministrativeMutexLease lease,
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize
    )
    {
        // Let the harness observe the SQL retry classification before PostgreSQL SSI aborts the race.
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );

        try
        {
            await using var enableRaceCommand = session.CreateCommand(
                new RelationalCommand("""SET LOCAL edfi.baseline_seed_race = 'on';""")
            );
            await enableRaceCommand.ExecuteNonQueryAsync();

            DocumentCacheAdministrativeBaselineSeedPageResult result =
                await _primitives.SeedBaselinePageAsync(
                    session,
                    new DocumentCacheAdministrativeBaselineSeedPageRequest(
                        boundaryDocumentId,
                        afterDocumentId,
                        pageSize
                    )
                );

            if (result.Status == DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey)
            {
                await session.RollbackAsync(CancellationToken.None);
            }
            else
            {
                await session.CommitAsync();
            }

            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
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
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt }
        );

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid, contentVersion);
    }

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
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = OldFirstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = OldLastEnqueuedAt }
        );

    private Task UpdateProjectionWorkAsync(SourceDocument source, long requiredContentVersion) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentProjectionWork"
            SET
                "RequiredContentVersion" = @requiredContentVersion,
                "LastEnqueuedAt" = @lastEnqueuedAt
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = source.DocumentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
            {
                Value = requiredContentVersion,
            },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz)
            {
                Value = OldLastEnqueuedAt.AddMinutes(5),
            }
        );

    private Task InstallBaselineSeedRaceTriggerAsync(long documentId, long advisoryLockKey) =>
        _database.ExecuteNonQueryAsync(
            $$"""
            CREATE OR REPLACE FUNCTION "dms"."block_baseline_seed_race"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW."DocumentId" = {{documentId}}
                   AND current_setting('edfi.baseline_seed_race', true) = 'on' THEN
                    PERFORM pg_advisory_lock({{advisoryLockKey}});
                    PERFORM pg_advisory_unlock({{advisoryLockKey}});
                END IF;

                RETURN NEW;
            END;
            $$;

            DROP TRIGGER IF EXISTS "BlockBaselineSeedRace" ON "dms"."DocumentProjectionWork";

            CREATE TRIGGER "BlockBaselineSeedRace"
            BEFORE INSERT ON "dms"."DocumentProjectionWork"
            FOR EACH ROW
            EXECUTE FUNCTION "dms"."block_baseline_seed_race"();
            """
        );

    private static async Task AcquireAdvisoryLockAsync(NpgsqlConnection connection, long advisoryLockKey)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(@advisoryLockKey);";
        command.Parameters.Add(
            new NpgsqlParameter("advisoryLockKey", NpgsqlDbType.Bigint) { Value = advisoryLockKey }
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection, long advisoryLockKey)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@advisoryLockKey);";
        command.Parameters.Add(
            new NpgsqlParameter("advisoryLockKey", NpgsqlDbType.Bigint) { Value = advisoryLockKey }
        );
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForBlockedAdvisoryLockAsync(Task seedTask)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellation.Token);

        while (!seedTask.IsCompleted)
        {
            if (await HasWaitingAdvisoryLockAsync(connection, cancellation.Token))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }

        await seedTask;
        Assert.Fail("Baseline seed page completed before the race trigger blocked on the advisory lock.");
    }

    private static async Task<bool> HasWaitingAdvisoryLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND granted = false
            );
            """;

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private async Task<IReadOnlyDictionary<long, WorkRow>> ReadWorkRowsAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            FROM "dms"."DocumentProjectionWork"
            ORDER BY "DocumentId";
            """
        );

        return rows.ToDictionary(
            row => Convert.ToInt64(row["DocumentId"]),
            row => new WorkRow(
                Convert.ToInt64(row["RequiredContentVersion"]),
                NormalizeUtcTimestamp(row["FirstEnqueuedAt"]!),
                NormalizeUtcTimestamp(row["LastEnqueuedAt"]!)
            )
        );
    }

    private static DateTimeOffset NormalizeUtcTimestamp(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value.GetType().FullName}'."
            ),
        };

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);

    private sealed record WorkRow(
        long RequiredContentVersion,
        DateTimeOffset FirstEnqueuedAt,
        DateTimeOffset LastEnqueuedAt
    );
}
