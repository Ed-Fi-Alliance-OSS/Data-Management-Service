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
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("DocumentCacheReadLookup")]
public class Given_A_Postgresql_DocumentCacheReadLookupAdapter
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create(
        "tenant-cache-read",
        7
    );
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private PostgresqlDocumentCacheReadLookupAdapter _adapter = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheReadLookupAdapter)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _adapter = new PostgresqlDocumentCacheReadLookupAdapter(
            _dataSourceCache,
            new PostgresqlRelationalWriteExceptionClassifier(),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<PostgresqlDocumentCacheReadLookupAdapter>.Instance
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
    public async Task It_returns_a_fresh_hit_from_the_selected_target_database()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
        await InsertCacheRowAsync(source, contentVersion: 10);

        DocumentCacheReadDocumentLookupResult result = await LookupAsync(Candidate(source));

        var hit = result.Should().BeOfType<DocumentCacheReadDocumentLookupResult.FreshHit>().Subject;
        hit.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        hit.Candidate.DocumentId.Should().Be(source.DocumentId);
        hit.StreamEtag.Should().Be("etag-10");
        hit.CacheLastModifiedAt.Should().Be(LastModifiedAt);
        JsonNode.Parse(hit.DocumentJson)!["value"]!.GetValue<string>().Should().Be("cache-10");
    }

    [Test]
    public async Task It_returns_bounded_fallback_outcomes_from_real_provider_rows()
    {
        foreach (ProviderLookupScenario scenario in ProviderLookupScenarios())
        {
            using var scope = new AssertionScope(scenario.Name);
            await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
            SourceDocument source = await InsertSourceDocumentAsync(contentVersion: 10);
            DocumentCacheReadAccelerationCandidate candidate = Candidate(source);

            await scenario.ArrangeAsync(this, source);

            DocumentCacheReadDocumentLookupResult result = await LookupAsync(candidate);

            var fallback = result.Should().BeOfType<DocumentCacheReadDocumentLookupResult.Fallback>().Subject;
            fallback.Outcome.Should().Be(scenario.ExpectedOutcome, fallback.Message);
            fallback.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static IEnumerable<ProviderLookupScenario> ProviderLookupScenarios()
    {
        yield return new(
            "Disabled lifecycle",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.SetLifecycleAsync(DocumentCacheLifecycleState.Disabled);
            },
            DocumentCacheReadLookupOutcome.LifecycleDisabled
        );
        yield return new(
            "Resetting lifecycle",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.SetLifecycleAsync(DocumentCacheLifecycleState.Resetting);
            },
            DocumentCacheReadLookupOutcome.LifecycleResetting
        );
        yield return new(
            "Rebuilding lifecycle",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.SetLifecycleAsync(DocumentCacheLifecycleState.Rebuilding);
            },
            DocumentCacheReadLookupOutcome.LifecycleRebuilding
        );
        yield return new(
            "Cache-ahead latch",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.SetLifecycleAsync(
                    DocumentCacheLifecycleState.Tracking,
                    cacheAheadRecoveryRequired: true
                );
            },
            DocumentCacheReadLookupOutcome.CacheAheadRecoveryRequired
        );
        yield return new(
            "Missing source row",
            async (fixture, source) =>
            {
                await fixture.DeleteSourceDocumentAsync(source.DocumentId);
                (await fixture.SourceDocumentExistsAsync(source.DocumentId)).Should().BeFalse();
            },
            DocumentCacheReadLookupOutcome.MissingSourceRow
        );
        yield return new(
            "Source drift",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.UpdateSourceContentVersionAsync(source.DocumentId, contentVersion: 11);
            },
            DocumentCacheReadLookupOutcome.SourceDrift
        );
        yield return new(
            "Missing cache row",
            static (_, _) => Task.CompletedTask,
            DocumentCacheReadLookupOutcome.MissingCacheRow
        );
        yield return new(
            "Stale cache row",
            async (fixture, source) => await fixture.InsertCacheRowAsync(source, contentVersion: 9),
            DocumentCacheReadLookupOutcome.StaleCacheRow
        );
        yield return new(
            "Cache resource mismatch",
            async (fixture, source) =>
                await fixture.InsertCacheRowAsync(source, contentVersion: 10, resourceNameOverride: "School"),
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Invalid lifecycle",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.SetInvalidLifecycleStateAsync();
            },
            DocumentCacheReadLookupOutcome.InvalidLifecycleState
        );
        yield return new(
            "Missing lifecycle",
            async (fixture, source) =>
            {
                await fixture.InsertCacheRowAsync(source, contentVersion: 10);
                await fixture.DeleteLifecycleStateAsync();
            },
            DocumentCacheReadLookupOutcome.MissingLifecycleState
        );
    }

    private async Task<DocumentCacheReadDocumentLookupResult> LookupAsync(
        DocumentCacheReadAccelerationCandidate candidate
    ) =>
        await _adapter.LookupDocumentAsync(
            new DocumentCacheReadDocumentLookupRequest(_fixture.MappingSet, candidate),
            ExecutionContext()
        );

    private static DocumentCacheReadAccelerationCandidate Candidate(SourceDocument source) =>
        new(
            source.DocumentId,
            new DocumentUuid(source.DocumentUuid),
            source.ResourceKeyId,
            source.ContentVersion,
            LastModifiedAt
        );

    private DocumentCacheTargetExecutionContext ExecutionContext() =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            EffectiveSettings(),
            new DocumentCacheTargetDataStoreMetadata(
                TargetKey.DataStoreId,
                RelationalProviderToken.Postgresql.Value
            ),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
                _database.ConnectionString
            ),
            Fingerprint,
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings() =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 2,
            projectorFailureBackoff: TimeSpan.FromSeconds(10),
            projectorBaselineHighWaterMark: 1000,
            administrationWorkflowTimeout: TimeSpan.FromHours(24)
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

    private async Task DeleteLifecycleStateAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "dms"."DocumentCacheState"
            WHERE "StateId" = 1;
            """
        );
    }

    private async Task SetInvalidLifecycleStateAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "dms"."DocumentCacheState"
            DROP CONSTRAINT "CK_DocumentCacheState_Lifecycle";

            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Paused'
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

        return new SourceDocument(
            Convert.ToInt64(rows.Single()["DocumentId"]),
            documentUuid,
            resourceKeyId,
            contentVersion
        );
    }

    private async Task InsertCacheRowAsync(
        SourceDocument source,
        long contentVersion,
        string? resourceNameOverride = null
    )
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[source.ResourceKeyId];

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
                Value = resourceNameOverride ?? resourceKey.Resource.ResourceName,
            },
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.ResourceVersion,
            },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar) { Value = $"etag-{contentVersion}" },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt },
            new NpgsqlParameter("documentJson", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["value"] = $"cache-{contentVersion}" }.ToJsonString(),
            },
            new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz)
            {
                Value = LastModifiedAt.AddMinutes(1),
            }
        );
    }

    private async Task UpdateSourceContentVersionAsync(long documentId, long contentVersion)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."Document"
            SET "ContentVersion" = @contentVersion,
                "ContentLastModifiedAt" = @lastModifiedAt
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz)
            {
                Value = LastModifiedAt.AddMinutes(5),
            }
        );
    }

    private async Task DeleteSourceDocumentAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "dms"."Document"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );
    }

    private async Task<bool> SourceDocumentExistsAsync(long documentId) =>
        await _database.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM "dms"."Document"
                WHERE "DocumentId" = @documentId
            );
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId }
        );

    private sealed record SourceDocument(
        long DocumentId,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion
    );

    private sealed record ProviderLookupScenario(
        string Name,
        Func<Given_A_Postgresql_DocumentCacheReadLookupAdapter, SourceDocument, Task> ArrangeAsync,
        DocumentCacheReadLookupOutcome ExpectedOutcome
    );
}
