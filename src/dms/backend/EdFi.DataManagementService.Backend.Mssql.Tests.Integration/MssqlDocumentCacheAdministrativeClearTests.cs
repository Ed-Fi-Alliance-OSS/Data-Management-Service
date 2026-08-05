// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
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
[Category("DocumentCacheAdministrativeClear")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheAdministrativeClear_Primitive
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset FirstEnqueuedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LastModifiedAt = new(2026, 8, 1, 12, 1, 0, DateTimeKind.Utc);

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlDocumentCacheAdministrativeMutex _mutex = null!;
    private DocumentCacheAdministrativePrimitives _primitives = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheAdministrativeClear_Primitive)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );
        _primitives = DocumentCacheAdministrativePrimitives.ForSqlServer();
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
    public async Task It_clears_cache_in_bounded_document_id_batches_and_preserves_work()
    {
        IReadOnlyList<SourceDocument> sources = await InsertProjectedRowsAsync(documentCount: 5);

        await using IDocumentCacheAdministrativeMutexLease lease = await AcquireMutexAsync();

        DocumentCacheAdministrativeClearBatchResult firstBatch = await ClearCacheBatchAndCommitAsync(
            lease,
            pageSize: 2
        );
        (await ReadCountAsync("DocumentCache")).Should().Be(3);

        DocumentCacheAdministrativeClearBatchResult secondBatch = await ClearCacheBatchAndCommitAsync(
            lease,
            pageSize: 2
        );
        DocumentCacheAdministrativeClearBatchResult finalBatch = await ClearCacheBatchAndCommitAsync(
            lease,
            pageSize: 2
        );

        firstBatch.RowsCleared.Should().Be(2);
        firstBatch.FilledBatch.Should().BeTrue();
        firstBatch.ClearedDocumentIds.Should().Equal(sources.Take(2).Select(source => source.DocumentId));
        secondBatch
            .ClearedDocumentIds.Should()
            .Equal(sources.Skip(2).Take(2).Select(source => source.DocumentId));
        finalBatch.ClearedDocumentIds.Should().Equal(sources.Skip(4).Select(source => source.DocumentId));
        finalBatch.FilledBatch.Should().BeFalse();

        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(5);

        DocumentCacheAdministrativeProjectedStateEmptinessResult emptiness =
            await ReadProjectedStateEmptinessAndCommitAsync(lease);
        emptiness.DocumentCacheEmpty.Should().BeTrue();
        emptiness.DocumentProjectionWorkEmpty.Should().BeFalse();
    }

    [Test]
    public async Task It_clears_work_only_with_internal_only_offline_clearance()
    {
        IReadOnlyList<SourceDocument> sources = await InsertProjectedRowsAsync(documentCount: 3);
        DocumentCacheAdministrativeWorkClearance clearance = DocumentCacheAdministrativeWorkClearance.Require(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
        );

        await using IDocumentCacheAdministrativeMutexLease lease = await AcquireMutexAsync();

        DocumentCacheAdministrativeClearBatchResult firstBatch = await ClearWorkBatchAndCommitAsync(
            lease,
            pageSize: 2,
            clearance
        );
        DocumentCacheAdministrativeClearBatchResult finalBatch = await ClearWorkBatchAndCommitAsync(
            lease,
            pageSize: 2,
            clearance
        );

        firstBatch.Target.Should().Be(DocumentCacheAdministrativeClearTarget.DocumentProjectionWork);
        firstBatch.ClearedDocumentIds.Should().Equal(sources.Take(2).Select(source => source.DocumentId));
        finalBatch.ClearedDocumentIds.Should().Equal(sources.Skip(2).Select(source => source.DocumentId));
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        (await ReadCountAsync("DocumentCache")).Should().Be(3);

        DocumentCacheAdministrativeProjectedStateEmptinessResult emptiness =
            await ReadProjectedStateEmptinessAndCommitAsync(lease);
        emptiness.DocumentCacheEmpty.Should().BeFalse();
        emptiness.DocumentProjectionWorkEmpty.Should().BeTrue();
    }

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

    private Task<IDocumentCacheAdministrativeMutexLease> AcquireMutexAsync() =>
        _mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            )
        );

    private async Task<DocumentCacheAdministrativeClearBatchResult> ClearCacheBatchAndCommitAsync(
        IDocumentCacheAdministrativeMutexLease lease,
        int pageSize
    )
    {
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );

        try
        {
            DocumentCacheAdministrativeClearBatchResult result =
                await _primitives.ClearDocumentCacheBatchAsync(
                    session,
                    new DocumentCacheAdministrativeClearBatchRequest(pageSize)
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

    private async Task<DocumentCacheAdministrativeClearBatchResult> ClearWorkBatchAndCommitAsync(
        IDocumentCacheAdministrativeMutexLease lease,
        int pageSize,
        DocumentCacheAdministrativeWorkClearance clearance
    )
    {
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );

        try
        {
            DocumentCacheAdministrativeClearBatchResult result =
                await _primitives.ClearDocumentProjectionWorkBatchAsync(
                    session,
                    new DocumentCacheAdministrativeClearBatchRequest(pageSize),
                    clearance
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

    private async Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAndCommitAsync(
        IDocumentCacheAdministrativeMutexLease lease
    )
    {
        await using IRelationalWriteSession session = await lease.BeginTransactionAsync(
            IsolationLevel.ReadCommitted
        );

        try
        {
            DocumentCacheAdministrativeProjectedStateEmptinessResult result =
                await _primitives.ReadProjectedStateEmptinessAsync(session);
            await session.CommitAsync();
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
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt }
        );

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid, contentVersion);
    }

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM [dms].[DocumentProjectionWork];""");

    private Task InsertProjectionWorkAsync(SourceDocument source) =>
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
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = source.ContentVersion },
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = FirstEnqueuedAt.UtcDateTime },
            new SqlParameter("@lastEnqueuedAt", SqlDbType.DateTime2)
            {
                Value = FirstEnqueuedAt.AddMinutes(1).UtcDateTime,
            }
        );

    private async Task InsertCacheRowAsync(SourceDocument source)
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
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = source.ContentVersion },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64)
            {
                Value = $"etag-{source.ContentVersion}",
            },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = $"cache-{source.ContentVersion}" }.ToJsonString(),
            },
            new SqlParameter("@computedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.AddMinutes(1) }
        );
    }

    private Task<long> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM [dms].[{{tableName}}];""");

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
