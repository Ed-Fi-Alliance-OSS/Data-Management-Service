// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
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
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
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
    private MappingSet _descriptorMappingSet = null!;
    private PostgresqlDocumentCacheReadLookupAdapter _adapter = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _descriptorMappingSet = DocumentCacheMaterializerDescriptorMappingSet.Create(SqlDialect.Pgsql);
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
            NullLogger<PostgresqlDocumentCacheReadLookupAdapter>.Instance,
            new ServedEtagComposer(),
            new ThrowingResponseShaper()
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
        JsonNode.Parse(hit.DocumentJson)!["value"]!.GetValue<string>().Should().Be("cache-10");
    }

    [Test]
    public async Task It_returns_a_fresh_descriptor_hit_from_real_descriptor_source_rows()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        await InsertDescriptorResourceKeyAsync();
        SourceDocument descriptor = await InsertSourceDocumentAsync(
            resourceKeyId: DescriptorResourceKeyId(),
            contentVersion: 30
        );
        await InsertDescriptorRowAsync(descriptor);
        await InsertCacheRowAsync(descriptor, contentVersion: 30, _descriptorMappingSet);

        DocumentCacheReadDocumentLookupResult result = await LookupAsync(
            Candidate(descriptor),
            _descriptorMappingSet
        );

        var hit = result.Should().BeOfType<DocumentCacheReadDocumentLookupResult.FreshHit>().Subject;
        hit.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        hit.Candidate.DocumentId.Should().Be(descriptor.DocumentId);
        JsonNode.Parse(hit.DocumentJson)!["value"]!.GetValue<string>().Should().Be("cache-30");
    }

    [Test]
    public async Task It_returns_a_fresh_batch_hit_in_candidate_order()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument first = await InsertSourceDocumentAsync(contentVersion: 10);
        SourceDocument second = await InsertSourceDocumentAsync(contentVersion: 11);
        await InsertCacheRowAsync(first, contentVersion: 10);
        await InsertCacheRowAsync(second, contentVersion: 11);

        DocumentCacheReadBatchLookupResult result = await LookupBatchAsync([
            Candidate(second),
            Candidate(first),
        ]);

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        result.IsFreshHit.Should().BeTrue();
        result
            .Documents.Select(static document => document.Candidate.DocumentId)
            .Should()
            .Equal(second.DocumentId, first.DocumentId);
        result
            .Documents.Cast<DocumentCacheReadDocumentLookupResult.FreshHit>()
            .Select(hit => JsonNode.Parse(hit.DocumentJson)!["value"]!.GetValue<string>())
            .Should()
            .Equal("cache-11", "cache-10");
    }

    [Test]
    public async Task It_returns_a_fresh_maximum_size_batch_hit_from_structured_candidate_page()
    {
        const int maximumPageSize = 500;

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        IReadOnlyList<SourceDocument> sources = await InsertSourceDocumentsAsync(
            count: maximumPageSize,
            firstContentVersion: 1_000
        );
        await InsertCacheRowsAsync(sources);

        DocumentCacheReadAccelerationCandidate[] candidates = sources.Reverse().Select(Candidate).ToArray();

        DocumentCacheReadBatchLookupResult result = await LookupBatchAsync(candidates);

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        result.IsFreshHit.Should().BeTrue();
        result.Documents.Should().HaveCount(maximumPageSize);
        result.Documents.Select(static document => document.Candidate).Should().Equal(candidates);
        result
            .Documents.Cast<DocumentCacheReadDocumentLookupResult.FreshHit>()
            .Select(hit => JsonNode.Parse(hit.DocumentJson)!["value"]!.GetValue<string>())
            .Should()
            .Equal(candidates.Select(static candidate => $"cache-{candidate.ContentVersion}"));
    }

    [Test]
    public async Task It_returns_a_non_fresh_batch_when_one_candidate_is_stale()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        SourceDocument first = await InsertSourceDocumentAsync(contentVersion: 10);
        SourceDocument second = await InsertSourceDocumentAsync(contentVersion: 11);
        await InsertCacheRowAsync(first, contentVersion: 10);
        await InsertCacheRowAsync(second, contentVersion: 10);

        DocumentCacheReadBatchLookupResult result = await LookupBatchAsync([
            Candidate(first),
            Candidate(second),
        ]);

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.StaleCacheRow);
        result.IsFreshHit.Should().BeFalse();
        result.Documents[0].Should().BeOfType<DocumentCacheReadDocumentLookupResult.FreshHit>();
        var fallback = result
            .Documents[1]
            .Should()
            .BeOfType<DocumentCacheReadDocumentLookupResult.Fallback>()
            .Subject;
        fallback.Outcome.Should().Be(DocumentCacheReadLookupOutcome.StaleCacheRow);
        fallback.Candidate.DocumentId.Should().Be(second.DocumentId);
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
            fallback.Outcome.Should().Be(scenario.ExpectedOutcome, scenario.Name);
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
        DocumentCacheReadAccelerationCandidate candidate,
        MappingSet? mappingSet = null
    ) =>
        await _adapter.LookupDocumentAsync(
            new DocumentCacheReadDocumentLookupRequest(mappingSet ?? _fixture.MappingSet, candidate),
            ExecutionContext()
        );

    private async Task<DocumentCacheReadBatchLookupResult> LookupBatchAsync(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates
    ) =>
        await _adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(_fixture.MappingSet, candidates),
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

    private short DescriptorResourceKeyId() =>
        _descriptorMappingSet.ResourceKeyIdByResource[DescriptorResource];

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

    private async Task InsertDescriptorResourceKeyAsync()
    {
        ResourceKeyEntry descriptorKey = _descriptorMappingSet.ResourceKeyById[DescriptorResourceKeyId()];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ResourceKey" (
                "ResourceKeyId",
                "ProjectName",
                "ResourceName",
                "ResourceVersion"
            )
            VALUES (
                @resourceKeyId,
                @projectName,
                @resourceName,
                @resourceVersion
            )
            ON CONFLICT ("ResourceKeyId") DO NOTHING;
            """,
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint)
            {
                Value = descriptorKey.ResourceKeyId,
            },
            new NpgsqlParameter("projectName", NpgsqlDbType.Varchar)
            {
                Value = descriptorKey.Resource.ProjectName,
            },
            new NpgsqlParameter("resourceName", NpgsqlDbType.Varchar)
            {
                Value = descriptorKey.Resource.ResourceName,
            },
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar)
            {
                Value = descriptorKey.ResourceVersion,
            }
        );
    }

    private async Task<SourceDocument> InsertSourceDocumentAsync(long contentVersion)
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        return await InsertSourceDocumentAsync(resourceKeyId, contentVersion);
    }

    private async Task<SourceDocument> InsertSourceDocumentAsync(short resourceKeyId, long contentVersion)
    {
        var documentUuid = Guid.NewGuid();
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

    private async Task<IReadOnlyList<SourceDocument>> InsertSourceDocumentsAsync(
        int count,
        long firstContentVersion
    )
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        SourceDocumentSeed[] seed = Enumerable
            .Range(0, count)
            .Select(index => new SourceDocumentSeed(
                index,
                Guid.NewGuid(),
                resourceKeyId,
                firstContentVersion + index
            ))
            .ToArray();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            WITH source_documents AS (
                SELECT
                    source."Ordinal",
                    source."DocumentUuid",
                    source."ResourceKeyId",
                    source."ContentVersion"
                FROM jsonb_to_recordset(CAST(@sourceDocumentsJson AS jsonb)) AS source(
                    "Ordinal" integer,
                    "DocumentUuid" uuid,
                    "ResourceKeyId" smallint,
                    "ContentVersion" bigint
                )
            )
            INSERT INTO "dms"."Document" (
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            SELECT
                source_documents."DocumentUuid",
                source_documents."ResourceKeyId",
                source_documents."ContentVersion",
                @lastModifiedAt
            FROM source_documents
            ORDER BY source_documents."Ordinal"
            RETURNING
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion";
            """,
            new NpgsqlParameter("sourceDocumentsJson", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(seed),
            },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt }
        );

        return rows.OrderBy(static row => Convert.ToInt64(row["ContentVersion"]))
            .Select(static row => new SourceDocument(
                Convert.ToInt64(row["DocumentId"]),
                (Guid)row["DocumentUuid"]!,
                Convert.ToInt16(row["ResourceKeyId"]),
                Convert.ToInt64(row["ContentVersion"])
            ))
            .ToArray();
    }

    private async Task InsertDescriptorRowAsync(SourceDocument descriptor)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "EffectiveBeginDate",
                "EffectiveEndDate",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @effectiveBeginDate,
                @effectiveEndDate,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = descriptor.DocumentId },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = descriptor.ResourceKeyId },
            new NpgsqlParameter("namespace", NpgsqlDbType.Varchar)
            {
                Value = "uri://ed-fi.org/SchoolTypeDescriptor",
            },
            new NpgsqlParameter("codeValue", NpgsqlDbType.Varchar) { Value = "Alternative" },
            new NpgsqlParameter("shortDescription", NpgsqlDbType.Varchar) { Value = "Alternative" },
            new NpgsqlParameter("description", NpgsqlDbType.Varchar) { Value = "Alternative school type" },
            new NpgsqlParameter("effectiveBeginDate", NpgsqlDbType.Date)
            {
                Value = new DateOnly(2025, 1, 15),
            },
            new NpgsqlParameter("effectiveEndDate", NpgsqlDbType.Date) { Value = new DateOnly(2025, 12, 31) },
            new NpgsqlParameter("discriminator", NpgsqlDbType.Varchar)
            {
                Value = DescriptorResource.ResourceName,
            },
            new NpgsqlParameter("uri", NpgsqlDbType.Varchar)
            {
                Value = "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            }
        );
    }

    private async Task InsertCacheRowsAsync(IReadOnlyList<SourceDocument> sources)
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[sources[0].ResourceKeyId];
        CacheRowSeed[] seed = sources
            .Select(source => new CacheRowSeed(
                source.DocumentId,
                source.DocumentUuid,
                source.ContentVersion,
                ComposeFixedStreamEtag(_fixture.MappingSet, resourceKey, source.ContentVersion),
                new JsonObject { ["value"] = $"cache-{source.ContentVersion}" }.ToJsonString()
            ))
            .ToArray();

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
            SELECT
                cache_row."DocumentId",
                cache_row."DocumentUuid",
                @projectName,
                @resourceName,
                @resourceVersion,
                cache_row."ContentVersion",
                cache_row."StreamEtag",
                @lastModifiedAt,
                cache_row."DocumentJson"::jsonb,
                @computedAt
            FROM jsonb_to_recordset(CAST(@cacheRowsJson AS jsonb)) AS cache_row(
                "DocumentId" bigint,
                "DocumentUuid" uuid,
                "ContentVersion" bigint,
                "StreamEtag" text,
                "DocumentJson" text
            );
            """,
            new NpgsqlParameter("cacheRowsJson", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(seed),
            },
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
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = LastModifiedAt },
            new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz)
            {
                Value = LastModifiedAt.AddMinutes(1),
            }
        );
    }

    private async Task InsertCacheRowAsync(
        SourceDocument source,
        long contentVersion,
        MappingSet? mappingSet = null,
        string? resourceNameOverride = null
    )
    {
        ResourceKeyEntry resourceKey = (mappingSet ?? _fixture.MappingSet).ResourceKeyById[
            source.ResourceKeyId
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
                Value = resourceNameOverride ?? resourceKey.Resource.ResourceName,
            },
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.ResourceVersion,
            },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar)
            {
                Value = ComposeFixedStreamEtag(
                    mappingSet ?? _fixture.MappingSet,
                    resourceKey,
                    contentVersion
                ),
            },
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

    private static string ComposeFixedStreamEtag(
        MappingSet mappingSet,
        ResourceKeyEntry resourceKey,
        long contentVersion
    )
    {
        ConcreteResourceModel model = mappingSet.GetConcreteResourceModelOrThrow(resourceKey.Resource);

        return model.StorageKind == ResourceStorageKind.SharedDescriptorTable
            ? DocumentCacheMaterializerStreamEtagComposer.ComposeForDescriptor(
                new ServedEtagComposer(),
                mappingSet,
                contentVersion
            )
            : DocumentCacheMaterializerStreamEtagComposer.ComposeForResource(
                new ServedEtagComposer(),
                mappingSet,
                contentVersion
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

    private sealed record SourceDocumentSeed(
        int Ordinal,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion
    );

    private sealed record CacheRowSeed(
        long DocumentId,
        Guid DocumentUuid,
        long ContentVersion,
        string StreamEtag,
        string DocumentJson
    );

    private sealed record ProviderLookupScenario(
        string Name,
        Func<Given_A_Postgresql_DocumentCacheReadLookupAdapter, SourceDocument, Task> ArrangeAsync,
        DocumentCacheReadLookupOutcome ExpectedOutcome
    );

    private sealed class ThrowingResponseShaper : IDocumentCacheReadResponseShaper
    {
        public DocumentCacheReadLookupResult<GetResult> ShapeGetById(
            DocumentCacheReadAccelerationGetByIdRequest request,
            DocumentCacheReadDocumentLookupResult.FreshHit hit
        ) => throw new InvalidOperationException("Response shaping is not used by lookup integration tests.");

        public DocumentCacheReadLookupResult<QueryResult> ShapeQuery(
            DocumentCacheReadAccelerationQueryRequest request,
            DocumentCacheReadAccelerationCandidatePage authorizedCandidatePage,
            DocumentCacheReadBatchLookupResult hitPage
        ) => throw new InvalidOperationException("Response shaping is not used by lookup integration tests.");
    }
}
