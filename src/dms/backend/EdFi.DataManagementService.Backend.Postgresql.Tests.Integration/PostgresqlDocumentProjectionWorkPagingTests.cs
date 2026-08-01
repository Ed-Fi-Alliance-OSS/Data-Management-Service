// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
[Category("DocumentProjectionWorkPaging")]
public class Given_A_Postgresql_DocumentProjectionWorkPaging_Pager
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset FirstEnqueuedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
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
    private PostgresqlDocumentProjectionWorkPager _pager = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentProjectionWorkPaging_Pager)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
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
    public async Task It_pages_durable_work_in_first_enqueued_at_and_document_id_keyset_order()
    {
        long laterDocumentId = await InsertDocumentAsync(contentVersion: 30);
        long firstDocumentId = await InsertDocumentAsync(contentVersion: 10);
        long secondDocumentId = await InsertDocumentAsync(contentVersion: 20);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(
            laterDocumentId,
            requiredContentVersion: 30,
            LaterEnqueuedAt,
            LaterEnqueuedAt.AddSeconds(5)
        );
        await InsertProjectionWorkAsync(
            firstDocumentId,
            requiredContentVersion: 10,
            FirstEnqueuedAt,
            FirstEnqueuedAt.AddSeconds(5)
        );
        await InsertProjectionWorkAsync(
            secondDocumentId,
            requiredContentVersion: 20,
            FirstEnqueuedAt,
            FirstEnqueuedAt.AddSeconds(10)
        );
        DocumentCacheProjectionCursorState cursor = new();

        DocumentProjectionWorkPage firstPage = await _pager.ReadPageAsync(
            new(CreateExecutionContext(), cursor)
        );

        firstPage.Items.Select(item => item.DocumentId).Should().Equal(firstDocumentId, secondDocumentId);
        firstPage.Items.Select(item => item.RequiredContentVersion).Should().Equal(10, 20);
        firstPage.Items[0].FirstEnqueuedAt.Should().Be(FirstEnqueuedAt);
        firstPage.Items[0].LastEnqueuedAt.Should().Be(FirstEnqueuedAt.AddSeconds(5));

        DocumentProjectionWorkPageItem lastFirstPageItem = firstPage.Items[^1];
        cursor.Advance(lastFirstPageItem.FirstEnqueuedAt, lastFirstPageItem.DocumentId);

        DocumentProjectionWorkPage secondPage = await _pager.ReadPageAsync(
            new(CreateExecutionContext(), cursor)
        );

        secondPage.Items.Should().ContainSingle().Which.DocumentId.Should().Be(laterDocumentId);
        secondPage.Items[0].RequiredContentVersion.Should().Be(30);
        secondPage.Items[0].FirstEnqueuedAt.Should().Be(LaterEnqueuedAt);
    }

    private DocumentCacheTargetExecutionContext CreateExecutionContext()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("tenant-cache-paging", 7);
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
}
