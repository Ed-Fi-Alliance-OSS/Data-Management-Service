// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text;
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
[Category("DocumentCacheQueryPlan")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheQueryPlan_Evidence
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";
    private const int ScaleDocumentCount = 160;
    private const int ScaleWorkRowCount = 80;
    private const int PageSize = 5;

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTime FirstEnqueuedAt = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastModifiedAt = new(2026, 8, 1, 12, 1, 0, DateTimeKind.Utc);
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
    private MssqlDocumentProjectionWorkPager _pager = null!;
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
            $"{nameof(Given_A_Mssql_DocumentCacheQueryPlan_Evidence)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _pager = new MssqlDocumentProjectionWorkPager(NullLogger<MssqlDocumentProjectionWorkPager>.Instance);
        _mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );
        _primitives = DocumentCacheAdministrativePrimitives.ForSqlServer(
            new MssqlDocumentCacheProviderCommandTimeoutClassifier()
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

    [Test]
    public async Task It_uses_the_oldest_work_index_for_ordinary_queue_paging_without_source_or_cache_scans()
    {
        await InsertScaleDocumentsAndWorkAsync();
        DocumentCacheProjectionCursorState cursor = new();

        string initialPlan = await ReadStatisticsXmlPlanAsync(
            MssqlDocumentProjectionWorkPager.InitialPageSql,
            new SqlParameter("@pageSize", SqlDbType.Int) { Value = PageSize }
        );
        DocumentProjectionWorkPage firstPage = await _pager.ReadPageAsync(
            new(CreateExecutionContext(), cursor)
        );
        cursor.Advance(firstPage.Items[^1].FirstEnqueuedAt, firstPage.Items[^1].DocumentId);
        string cursorPlan = await ReadStatisticsXmlPlanAsync(
            MssqlDocumentProjectionWorkPager.CursorPageSql,
            new SqlParameter("@pageSize", SqlDbType.Int) { Value = PageSize },
            new SqlParameter("@lastFirstEnqueuedAt", SqlDbType.DateTime2)
            {
                Value = firstPage.Items[^1].FirstEnqueuedAt.UtcDateTime,
            },
            new SqlParameter("@lastDocumentId", SqlDbType.BigInt) { Value = firstPage.Items[^1].DocumentId }
        );

        initialPlan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        cursorPlan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        MssqlDocumentProjectionWorkPager.InitialPageSql.Should().NotContain("FROM [dms].[Document]");
        MssqlDocumentProjectionWorkPager.InitialPageSql.Should().NotContain("FROM [dms].[DocumentCache]");
        MssqlDocumentProjectionWorkPager.CursorPageSql.Should().NotContain("FROM [dms].[Document]");
        MssqlDocumentProjectionWorkPager.CursorPageSql.Should().NotContain("FROM [dms].[DocumentCache]");

        cursor.Clear();
        int pageCount = 0;
        int rowCount = 0;
        while (true)
        {
            DocumentProjectionWorkPage page = await _pager.ReadPageAsync(
                new(CreateExecutionContext(), cursor)
            );
            if (page.IsEmpty)
            {
                break;
            }

            page.Items.Should().HaveCountLessThanOrEqualTo(PageSize);
            rowCount += page.Items.Length;
            pageCount++;
            cursor.Advance(page.Items[^1].FirstEnqueuedAt, page.Items[^1].DocumentId);
        }

        rowCount.Should().Be(ScaleWorkRowCount);
        pageCount.Should().Be((ScaleWorkRowCount + PageSize - 1) / PageSize);
    }

    [Test]
    public async Task It_keeps_administrative_queries_bounded_to_projected_state_or_document_id_windows()
    {
        await InsertScaleDocumentsAndWorkAsync();
        DocumentCacheAdministrativePrimitiveCommands commands =
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Mssql);

        string highWaterPlan = await ReadStatisticsXmlPlanAsync(
            commands.ObserveWorkHighWaterCommandText,
            new SqlParameter("@highWaterPlusOne", SqlDbType.Int) { Value = PageSize + 1 }
        );
        DocumentCacheAdministrativeWorkHighWaterObservationResult highWater =
            await ObserveHighWaterAndCommitAsync(highWaterMark: PageSize, diagnosticCapacity: PageSize);

        highWaterPlan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        highWater.ObservedWorkRows.Should().Be(PageSize + 1);
        highWater.DiagnosticDocumentIds.Should().HaveCount(PageSize);

        commands.ProjectedStateEmptinessCommandText.Should().Contain("NOT EXISTS");
        commands.ProjectedStateEmptinessCommandText.Should().Contain("SELECT TOP (1) 1");
        commands.ProjectedStateEmptinessCommandText.Should().NotContain("COUNT(");
        commands.ObserveWorkHighWaterCommandText.Should().Contain("TOP (@highWaterPlusOne)");
        commands.ObserveWorkHighWaterCommandText.Should().Contain("ORDER BY [FirstEnqueuedAt], [DocumentId]");
        commands.ObserveWorkHighWaterCommandText.Should().NotContain("COUNT(");
        commands.ObserveWorkHighWaterCommandText.Should().NotContain("FROM [dms].[Document]");
        commands.ClearDocumentCacheBatchCommandText.Should().Contain("ORDER BY [DocumentId]");
        commands.ClearDocumentCacheBatchCommandText.Should().Contain("SELECT TOP (@pageSize)");
        commands.ClearDocumentProjectionWorkBatchCommandText.Should().Contain("ORDER BY [DocumentId]");
        commands.ClearDocumentProjectionWorkBatchCommandText.Should().Contain("SELECT TOP (@pageSize)");

        AssertDocumentIdWindowedSourceScan(commands.SeedBaselinePageCommandText);
        AssertDocumentIdWindowedSourceScan(commands.ScrubPageCommandText);
    }

    private async Task InsertScaleDocumentsAndWorkAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];

        await _database.ExecuteNonQueryAsync(
            """
            WITH numbers AS (
                SELECT TOP (@documentCount)
                    ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [Number]
                FROM sys.all_objects AS first_source
                CROSS JOIN sys.all_objects AS second_source
            )
            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            SELECT
                NEWID(),
                @resourceKeyId,
                [Number],
                @lastModifiedAt
            FROM numbers
            ORDER BY [Number];

            INSERT INTO [dms].[DocumentProjectionWork] (
                [DocumentId],
                [RequiredContentVersion],
                [FirstEnqueuedAt],
                [LastEnqueuedAt]
            )
            SELECT TOP (@workRowCount)
                source.[DocumentId],
                source.[ContentVersion],
                DATEADD(millisecond, CONVERT(int, source.[DocumentId]), @firstEnqueuedAt),
                DATEADD(millisecond, CONVERT(int, source.[DocumentId]), @firstEnqueuedAt)
            FROM [dms].[Document] AS source
            ORDER BY source.[DocumentId];

            UPDATE STATISTICS [dms].[DocumentProjectionWork];
            """,
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@documentCount", SqlDbType.Int) { Value = ScaleDocumentCount },
            new SqlParameter("@workRowCount", SqlDbType.Int) { Value = ScaleWorkRowCount },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt },
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = FirstEnqueuedAt }
        );
    }

    private async Task<string> ReadStatisticsXmlPlanAsync(string sql, params SqlParameter[] parameters)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SetStatisticsXmlAsync(connection, enabled: true);
        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 300;
            command.Parameters.AddRange(parameters);

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

    private async Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveHighWaterAndCommitAsync(
        int highWaterMark,
        int diagnosticCapacity
    )
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
                _database.ConnectionString
            )
        );
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

    private DocumentCacheTargetExecutionContext CreateExecutionContext()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("tenant-cache-query-plan", 7);
        return new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: PageSize,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
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
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
    }

    private static void AssertDocumentIdWindowedSourceScan(string sql)
    {
        sql.Should().Contain("FROM [dms].[Document] AS source");
        sql.Should().Contain("WHERE source.[DocumentId] > @afterDocumentId");
        sql.Should().Contain("AND source.[DocumentId] <= @boundaryDocumentId");
        sql.Should().Contain("ORDER BY source.[DocumentId]");
        sql.Should().Contain("TOP (@pageSize)");
    }
}
