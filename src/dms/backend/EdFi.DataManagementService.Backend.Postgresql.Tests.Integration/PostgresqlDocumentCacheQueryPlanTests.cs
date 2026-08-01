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
[Category("DocumentCacheQueryPlan")]
public class Given_A_Postgresql_DocumentCacheQueryPlan_Evidence
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";
    private const int ScaleDocumentCount = 160;
    private const int ScaleWorkRowCount = 80;
    private const int PageSize = 5;

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset FirstEnqueuedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 8, 1, 12, 1, 0, TimeSpan.Zero);
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
    private PostgresqlDocumentProjectionWorkPager _pager = null!;
    private PostgresqlDocumentCacheAdministrativeMutex _mutex = null!;
    private PostgresqlDocumentCacheAdministrativePrimitives _primitives = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheQueryPlan_Evidence)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _pager = new PostgresqlDocumentProjectionWorkPager(
            _dataSourceCache,
            NullLogger<PostgresqlDocumentProjectionWorkPager>.Instance
        );
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
    public async Task It_uses_the_oldest_work_index_for_ordinary_queue_paging_without_source_or_cache_scans()
    {
        await InsertScaleDocumentsAndWorkAsync();
        DocumentCacheProjectionCursorState cursor = new();

        string initialPlan = await ExplainAsync(
            PostgresqlDocumentProjectionWorkPager.InitialPageSql,
            new NpgsqlParameter("pageSize", NpgsqlDbType.Integer) { Value = PageSize }
        );
        DocumentProjectionWorkPage firstPage = await _pager.ReadPageAsync(
            new(CreateExecutionContext(), cursor)
        );
        cursor.Advance(firstPage.Items[^1].FirstEnqueuedAt, firstPage.Items[^1].DocumentId);
        string cursorPlan = await ExplainAsync(
            PostgresqlDocumentProjectionWorkPager.CursorPageSql,
            new NpgsqlParameter("pageSize", NpgsqlDbType.Integer) { Value = PageSize },
            new NpgsqlParameter("lastFirstEnqueuedAt", NpgsqlDbType.TimestampTz)
            {
                Value = firstPage.Items[^1].FirstEnqueuedAt,
            },
            new NpgsqlParameter("lastDocumentId", NpgsqlDbType.Bigint)
            {
                Value = firstPage.Items[^1].DocumentId,
            }
        );

        initialPlan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        cursorPlan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        initialPlan.Should().NotContain("Seq Scan on \"DocumentProjectionWork\"");
        cursorPlan.Should().NotContain("Seq Scan on \"DocumentProjectionWork\"");
        PostgresqlDocumentProjectionWorkPager.InitialPageSql.Should().NotContain("\"dms\".\"Document\"");
        PostgresqlDocumentProjectionWorkPager.InitialPageSql.Should().NotContain("\"dms\".\"DocumentCache\"");
        PostgresqlDocumentProjectionWorkPager.CursorPageSql.Should().NotContain("\"dms\".\"Document\"");
        PostgresqlDocumentProjectionWorkPager.CursorPageSql.Should().NotContain("\"dms\".\"DocumentCache\"");

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
            DocumentCacheAdministrativePrimitivesSupport.GetCommands(SqlDialect.Pgsql);

        string highWaterPlan = await ExplainAsync(
            commands.ObserveWorkHighWaterCommandText,
            new NpgsqlParameter("highWaterPlusOne", NpgsqlDbType.Integer) { Value = PageSize + 1 }
        );
        DocumentCacheAdministrativeWorkHighWaterObservationResult highWater =
            await ObserveHighWaterAndCommitAsync(highWaterMark: PageSize, diagnosticCapacity: PageSize);

        highWaterPlan.Should().Contain("IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId");
        highWater.ObservedWorkRows.Should().Be(PageSize + 1);
        highWater.DiagnosticDocumentIds.Should().HaveCount(PageSize);

        commands.ProjectedStateEmptinessCommandText.Should().Contain("NOT EXISTS");
        commands.ProjectedStateEmptinessCommandText.Should().Contain("LIMIT 1");
        commands.ProjectedStateEmptinessCommandText.Should().NotContain("COUNT(");
        commands.ObserveWorkHighWaterCommandText.Should().Contain("LIMIT @highWaterPlusOne");
        commands
            .ObserveWorkHighWaterCommandText.Should()
            .Contain("ORDER BY \"FirstEnqueuedAt\", \"DocumentId\"");
        commands.ObserveWorkHighWaterCommandText.Should().NotContain("COUNT(");
        commands.ObserveWorkHighWaterCommandText.Should().NotContain("\"dms\".\"Document\"");
        commands.ClearDocumentCacheBatchCommandText.Should().Contain("ORDER BY \"DocumentId\"");
        commands.ClearDocumentCacheBatchCommandText.Should().Contain("LIMIT @pageSize");
        commands.ClearDocumentProjectionWorkBatchCommandText.Should().Contain("ORDER BY \"DocumentId\"");
        commands.ClearDocumentProjectionWorkBatchCommandText.Should().Contain("LIMIT @pageSize");

        AssertDocumentIdWindowedSourceScan(commands.SeedBaselinePageCommandText);
        AssertDocumentIdWindowedSourceScan(commands.ScrubPageCommandText);
    }

    private async Task InsertScaleDocumentsAndWorkAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Document" (
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            SELECT
                ('00000000-0000-0000-0000-' || lpad(series::text, 12, '0'))::uuid,
                @resourceKeyId,
                series,
                @lastModifiedAt
            FROM generate_series(1, @documentCount) AS series;

            INSERT INTO "dms"."DocumentProjectionWork" (
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            )
            SELECT
                source."DocumentId",
                source."ContentVersion",
                @firstEnqueuedAt + (source."DocumentId" * INTERVAL '1 millisecond'),
                @firstEnqueuedAt + (source."DocumentId" * INTERVAL '1 millisecond')
            FROM "dms"."Document" AS source
            ORDER BY source."DocumentId"
            LIMIT @workRowCount;

            ANALYZE "dms"."DocumentProjectionWork";
            """,
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("documentCount", NpgsqlDbType.Integer) { Value = ScaleDocumentCount },
            new NpgsqlParameter("workRowCount", NpgsqlDbType.Integer) { Value = ScaleWorkRowCount },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = FirstEnqueuedAt }
        );
    }

    private async Task<string> ExplainAsync(string sql, params NpgsqlParameter[] parameters)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            $"EXPLAIN (COSTS OFF) {sql}",
            parameters
        );

        return string.Join(Environment.NewLine, rows.Select(row => row["QUERY PLAN"]?.ToString()));
    }

    private async Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveHighWaterAndCommitAsync(
        int highWaterMark,
        int diagnosticCapacity
    )
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
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

    private static void AssertDocumentIdWindowedSourceScan(string sql)
    {
        sql.Should().Contain("FROM \"dms\".\"Document\" AS source");
        sql.Should().Contain("WHERE source.\"DocumentId\" > @afterDocumentId");
        sql.Should().Contain("AND source.\"DocumentId\" <= @boundaryDocumentId");
        sql.Should().Contain("ORDER BY source.\"DocumentId\"");
        sql.Should().Contain("LIMIT @pageSize");
    }
}
