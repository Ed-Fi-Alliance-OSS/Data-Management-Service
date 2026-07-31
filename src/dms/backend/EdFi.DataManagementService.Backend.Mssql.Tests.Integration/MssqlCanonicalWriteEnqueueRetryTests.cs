// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("CanonicalWriteEnqueueRetry")]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_Canonical_Write_Enqueue_Retry
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTime LastModifiedAt = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlRelationalWriteExceptionClassifier _classifier = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_Mssql_Canonical_Write_Enqueue_Retry)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _classifier = new MssqlRelationalWriteExceptionClassifier();
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

    [Test]
    public async Task It_classifies_enqueue_work_lock_timeout_as_retryable_and_rolls_back_the_canonical_update()
    {
        await SetTrackingLifecycleAsync();
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);

        await using SqlConnection blockerConnection = new(_database.ConnectionString);
        await blockerConnection.OpenAsync();
        await using SqlTransaction blockerTransaction = (SqlTransaction)
            await blockerConnection.BeginTransactionAsync();
        await LockProjectionWorkRowAsync(blockerConnection, blockerTransaction, source.DocumentId);

        try
        {
            SqlException exception = (
                await FluentActions
                    .Awaiting(() =>
                        AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
                            source.DocumentId,
                            contentVersion: 11
                        )
                    )
                    .Should()
                    .ThrowAsync<SqlException>()
            ).Which;

            exception.Number.Should().Be(1222);
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
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = 'Tracking',
                [CacheAheadRecoveryRequired] = 0
            WHERE [StateId] = 1;
            """
        );
    }

    private async Task<SourceDocument> InsertSourceDocumentAsync(long contentVersion)
    {
        var documentUuid = Guid.NewGuid();
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            DECLARE @insertedDocument TABLE ([DocumentId] bigint NOT NULL);

            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            OUTPUT INSERTED.[DocumentId] INTO @insertedDocument
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            );

            SELECT [DocumentId]
            FROM @insertedDocument;
            """,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = documentUuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt }
        );

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid);
    }

    private static async Task LockProjectionWorkRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [dms].[DocumentProjectionWork]
            SET [RequiredContentVersion] = [RequiredContentVersion]
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });

        int rowsAffected = await command.ExecuteNonQueryAsync();
        rowsAffected
            .Should()
            .Be(1, "the seed insert should enqueue projection work before the lock is taken");
    }

    private async Task AttemptContentVersionAdvanceWithShortLockTimeoutAsync(
        long documentId,
        long contentVersion
    )
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SET LOCK_TIMEOUT 100;

                UPDATE [dms].[Document]
                SET [ContentVersion] = @contentVersion,
                    [ContentLastModifiedAt] = @lastModifiedAt
                WHERE [DocumentId] = @documentId;
                """;
            command.Parameters.Add(
                new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion }
            );
            command.Parameters.Add(
                new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt }
            );
            command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });

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
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private Task<long> ReadRequiredContentVersionAsync(long documentId) =>
        _database.ExecuteScalarAsync<long>(
            """
            SELECT [RequiredContentVersion]
            FROM [dms].[DocumentProjectionWork]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid);
}
