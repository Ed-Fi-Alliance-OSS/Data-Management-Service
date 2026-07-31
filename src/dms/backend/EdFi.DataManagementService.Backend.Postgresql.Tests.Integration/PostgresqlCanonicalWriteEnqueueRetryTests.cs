// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CanonicalWriteEnqueueRetry")]
public class Given_Postgresql_Canonical_Write_Enqueue_Retry
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private PostgresqlRelationalWriteExceptionClassifier _classifier = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_Postgresql_Canonical_Write_Enqueue_Retry)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _classifier = new PostgresqlRelationalWriteExceptionClassifier();
    }

    [TearDown]
    public async Task TearDown()
    {
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
    public async Task It_classifies_enqueue_work_lock_timeout_as_retryable_and_rolls_back_the_canonical_update()
    {
        await SetTrackingLifecycleAsync();
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);

        await using NpgsqlConnection blockerConnection = new(_database.ConnectionString);
        await blockerConnection.OpenAsync();
        await using NpgsqlTransaction blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await LockProjectionWorkRowAsync(blockerConnection, blockerTransaction, source.DocumentId);

        try
        {
            PostgresException exception = (
                await FluentActions
                    .Awaiting(() =>
                        AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
                            source.DocumentId,
                            contentVersion: 11
                        )
                    )
                    .Should()
                    .ThrowAsync<PostgresException>()
            ).Which;

            exception.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
            _classifier.IsTransientFailure(exception).Should().BeTrue();
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }

        (await ReadContentVersionAsync(source.DocumentId)).Should().Be(10);
        (await ReadRequiredContentVersionAsync(source.DocumentId)).Should().Be(10);
    }

    private async Task SetTrackingLifecycleAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Tracking',
                "CacheAheadRecoveryRequired" = FALSE
            WHERE "StateId" = 1;
            """
        );
    }

    private async Task<SourceDocument> InsertSourceDocumentAsync(long contentVersion)
    {
        var documentUuid = Guid.NewGuid();
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
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt }
        );

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid);
    }

    private static async Task LockProjectionWorkRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId
            FOR UPDATE;
            """;
        command.Parameters.Add(new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId });

        object? result = await command.ExecuteScalarAsync();
        result.Should().NotBeNull("the seed insert should enqueue projection work before the lock is taken");
    }

    private async Task AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
        long documentId,
        long contentVersion
    )
    {
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        try
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SET LOCAL lock_timeout = '100ms';

                UPDATE "dms"."Document"
                SET "ContentVersion" = @contentVersion,
                    "ContentLastModifiedAt" = @lastModifiedAt
                WHERE "DocumentId" = @documentId;
                """;
            command.Parameters.Add(
                new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion }
            );
            command.Parameters.Add(
                new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt }
            );
            command.Parameters.Add(
                new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
            );

            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private Task<long> ReadContentVersionAsync(long documentId) =>
        _database.ExecuteScalarAsync<long>(
            """
            SELECT "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private Task<long> ReadRequiredContentVersionAsync(long documentId) =>
        _database.ExecuteScalarAsync<long>(
            """
            SELECT "RequiredContentVersion"
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid);
}
