// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
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
[Category("DocumentCacheWriter")]
public class Given_A_Postgresql_DocumentCacheWriter
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private PostgresqlDocumentCacheWriter _writer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheWriter)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _writer = new PostgresqlDocumentCacheWriter(
            _dataSourceCache,
            new DocumentCacheWriterRetryAdapter(
                new DeadlockRetrySettings
                {
                    MaxRetryAttempts = 0,
                    BaseDelayMilliseconds = 1,
                    UseJitter = false,
                },
                new PostgresqlRelationalWriteExceptionClassifier(),
                NullLogger<DocumentCacheWriterRetryAdapter>.Instance
            ),
            NullLogger<PostgresqlDocumentCacheWriter>.Instance
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
    public async Task It_writes_a_current_candidate_and_deletes_matching_work_in_one_attempt()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(source, "candidate-current");

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);

        CacheRow cacheRow = await ReadCacheRowAsync(source.DocumentId);
        cacheRow.ContentVersion.Should().Be(10);
        cacheRow.StreamEtag.Should().Be("etag-10");
        JsonNode.Parse(cacheRow.DocumentJson)!["value"]!.GetValue<string>().Should().Be("candidate-current");
    }

    [Test]
    public async Task It_acknowledges_equal_version_work_without_refreshing_cache()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DateTimeOffset originalComputedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await InsertCacheRowAsync(source, contentVersion: 10, computedAt: originalComputedAt);
        string beforeComputedAt = await ReadCacheComputedAtTextAsync(source.DocumentId);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>()
            .Which.AcknowledgedContentVersion.Should()
            .Be(10);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadCacheComputedAtTextAsync(source.DocumentId)).Should().Be(beforeComputedAt);
    }

    [Test]
    public async Task It_suppresses_a_stale_candidate_without_cache_dml_or_acknowledgement()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(
            source,
            "candidate-stale",
            contentVersion: 9
        );

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        var stale = result.Should().BeOfType<DocumentCacheWriterResult.StaleCandidateSuppressed>().Subject;
        stale.CurrentContentVersion.Should().Be(10);
        stale.CandidateContentVersion.Should().Be(9);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_reports_matching_version_uuid_mismatch_as_an_invariant_failure()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(
            source,
            "candidate-wrong-uuid",
            documentUuid: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        );

        DocumentCacheWriterResult result = await WriteAsync(source, candidate);

        result
            .Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.MatchingVersionDocumentUuidMismatch);
        (await ReadCacheCountAsync(source.DocumentId)).Should().Be(0);
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_returns_a_lifecycle_fence_without_classifying_or_acknowledging_when_disabled()
    {
        DocumentCacheMaterializationTargetContext targetContext = CreateTargetContext();

        DocumentCacheWriterResult result = await _writer.WriteAsync(
            new DocumentCacheWriterRequest(
                targetContext,
                documentId: 77,
                selectedRequiredContentVersion: null,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate: null,
                CancellationToken.None
            )
        );

        var fenced = result.Should().BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>().Subject;
        fenced.Reason.Should().Be(DocumentCacheWriterFenceReason.LifecycleNotEligible);
        fenced.LifecycleState.Should().Be(DocumentCacheLifecycleState.Disabled);
    }

    [Test]
    public async Task It_sets_the_cache_ahead_latch_only_after_reclassifying_current_cache_ahead()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 11);

        DocumentCacheWriterResult result = await WriteAsync(source, candidate: null);

        var cacheAhead = result.Should().BeOfType<DocumentCacheWriterResult.CacheAheadLatchSet>().Subject;
        cacheAhead.SourceContentVersion.Should().Be(10);
        cacheAhead.CacheContentVersion.Should().Be(11);
        (await ReadCacheAheadLatchAsync()).Should().BeTrue();
        (await ReadWorkCountAsync(source.DocumentId)).Should().Be(1);
    }

    private async Task<DocumentCacheWriterResult> WriteAsync(
        SourceDocument source,
        DocumentCacheMaterializationCandidate? candidate
    ) =>
        await _writer.WriteAsync(
            new DocumentCacheWriterRequest(
                CreateTargetContext(),
                source.DocumentId,
                selectedRequiredContentVersion: source.ContentVersion,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate,
                CancellationToken.None
            )
        );

    private DocumentCacheMaterializationTargetContext CreateTargetContext() =>
        new(
            new DocumentCacheProjectionTargetKey("tenant-cache-writer", new DataStoreId(7)),
            _fixture.MappingSet,
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            _database.ConnectionString
        );

    private async Task SetLifecycleAsync(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired = false
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycleState", lifecycleState.ToString()),
            new NpgsqlParameter("cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
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
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz)
            {
                Value = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            }
        );

        return new SourceDocument(Convert.ToInt64(rows.Single()["DocumentId"]), documentUuid, contentVersion);
    }

    private async Task InsertCacheRowAsync(
        SourceDocument source,
        long contentVersion,
        DateTimeOffset? computedAt = null
    )
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[
            _fixture.MappingSet.ResourceKeyIdByResource[PersonResource]
        ];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentCache" (
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson",
                "ComputedAt"
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
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = source.DocumentId },
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = source.DocumentUuid },
            new NpgsqlParameter("projectName", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new NpgsqlParameter("resourceName", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.ResourceVersion,
            },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar) { Value = $"etag-{contentVersion}" },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz)
            {
                Value = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            },
            new NpgsqlParameter("documentJson", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["value"] = $"cache-{contentVersion}" }.ToJsonString(),
            },
            new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz)
            {
                Value = computedAt ?? new DateTimeOffset(2026, 7, 31, 12, 1, 0, TimeSpan.Zero),
            }
        );
    }

    private static DocumentCacheMaterializationCandidate CreateCandidate(
        SourceDocument source,
        string value,
        long? contentVersion = null,
        Guid? documentUuid = null
    ) =>
        new(
            source.DocumentId,
            new DocumentUuid(documentUuid ?? source.DocumentUuid),
            "Ed-Fi",
            "Person",
            "5.0.0",
            contentVersion ?? source.ContentVersion,
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            $"etag-{contentVersion ?? source.ContentVersion}",
            new JsonObject { ["value"] = value }
        );

    private async Task<long> ReadWorkCountAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private async Task<long> ReadCacheCountAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private async Task<CacheRow> ReadCacheRowAsync(long documentId)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT
                "ContentVersion",
                "StreamEtag",
                "DocumentJson"::text AS "DocumentJson"
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );
        IReadOnlyDictionary<string, object?> row = rows.Should().ContainSingle().Subject;

        return new CacheRow(
            Convert.ToInt64(row["ContentVersion"]),
            (string)row["StreamEtag"]!,
            (string)row["DocumentJson"]!
        );
    }

    private async Task<string> ReadCacheComputedAtTextAsync(long documentId) =>
        await _database.ExecuteScalarAsync<string>(
            """
            SELECT to_char(
                "ComputedAt" AT TIME ZONE 'UTC',
                'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'
            )
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private async Task<bool> ReadCacheAheadLatchAsync() =>
        await _database.ExecuteScalarAsync<bool>(
            """
            SELECT "CacheAheadRecoveryRequired"
            FROM "dms"."DocumentCacheState"
            WHERE "StateId" = 1;
            """
        );

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);

    private sealed record CacheRow(long ContentVersion, string StreamEtag, string DocumentJson);
}
