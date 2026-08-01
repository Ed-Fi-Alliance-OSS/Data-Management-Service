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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheBaselineSeeding")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheBaselineSeeding_Primitive
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTime OldFirstEnqueuedAt = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldLastEnqueuedAt = OldFirstEnqueuedAt.AddMinutes(1);
    private static readonly DateTime LastModifiedAt = new(2026, 8, 1, 12, 1, 0, DateTimeKind.Utc);

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlDocumentCacheAdministrativeMutex _mutex = null!;
    private MssqlDocumentCacheAdministrativePrimitives _primitives = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheBaselineSeeding_Primitive)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
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
        _primitives = new MssqlDocumentCacheAdministrativePrimitives();
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

    private Task<IDocumentCacheAdministrativeMutexLease> AcquireMutexAsync() =>
        _mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
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
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = OldFirstEnqueuedAt },
            new SqlParameter("@lastEnqueuedAt", SqlDbType.DateTime2) { Value = OldLastEnqueuedAt }
        );

    private async Task<IReadOnlyDictionary<long, WorkRow>> ReadWorkRowsAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT
                [DocumentId],
                [RequiredContentVersion],
                [FirstEnqueuedAt],
                [LastEnqueuedAt]
            FROM [dms].[DocumentProjectionWork]
            ORDER BY [DocumentId];
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

    private static DateTime NormalizeUtcTimestamp(object value) =>
        value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value.GetType().FullName}'."
            ),
        };

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);

    private sealed record WorkRow(
        long RequiredContentVersion,
        DateTime FirstEnqueuedAt,
        DateTime LastEnqueuedAt
    );
}
