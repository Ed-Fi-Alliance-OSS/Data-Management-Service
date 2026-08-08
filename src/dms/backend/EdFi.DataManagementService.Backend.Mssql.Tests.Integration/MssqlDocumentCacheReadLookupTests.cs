// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheReadLookup")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheReadLookupAdapter
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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MappingSet _descriptorMappingSet = null!;
    private MssqlDocumentCacheReadLookupAdapter _adapter = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _descriptorMappingSet = DocumentCacheMaterializerDescriptorMappingSet.Create(SqlDialect.Mssql);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheReadLookupAdapter)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _adapter = new MssqlDocumentCacheReadLookupAdapter(
            new MssqlRelationalWriteExceptionClassifier(),
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<MssqlDocumentCacheReadLookupAdapter>.Instance
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
        hit.StreamEtag.Should().Be("etag-30");
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
    public async Task It_executes_a_maximum_page_size_batch_lookup_without_exceeding_mssql_parameter_limits()
    {
        const int maximumPageSize = 500;

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking);
        IReadOnlyList<SourceDocument> sources = await InsertSourceDocumentsAsync(
            maximumPageSize,
            firstContentVersion: 1000
        );
        await InsertCacheRowsAsync(sources);

        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates = sources
            .Select(Candidate)
            .ToArray();

        DocumentCacheReadBatchLookupResult result = await LookupBatchAsync(candidates);

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        result.IsFreshHit.Should().BeTrue();
        result.Documents.Should().HaveCount(maximumPageSize);
        result
            .Documents.Select(static document => document.Candidate.DocumentId)
            .Should()
            .Equal(candidates.Select(static candidate => candidate.DocumentId));

        DocumentCacheReadDocumentLookupResult.FreshHit[] hits = result
            .Documents.Cast<DocumentCacheReadDocumentLookupResult.FreshHit>()
            .ToArray();
        JsonNode.Parse(hits[0].DocumentJson)!["value"]!.GetValue<string>().Should().Be("cache-1000");
        JsonNode.Parse(hits[^1].DocumentJson)!["value"]!
            .GetValue<string>()
            .Should()
            .Be($"cache-{1000 + maximumPageSize - 1}");
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
                RelationalProviderToken.SqlServer.Value
            ),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
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
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycleState,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@lifecycleState", SqlDbType.VarChar, 16) { Value = lifecycleState.ToString() },
            new SqlParameter("@cacheAheadRecoveryRequired", SqlDbType.Bit)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );
    }

    private async Task DeleteLifecycleStateAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        );
    }

    private async Task SetInvalidLifecycleStateAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE [dms].[DocumentCacheState]
            DROP CONSTRAINT [CK_DocumentCacheState_Lifecycle];

            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = 'Paused'
            WHERE [StateId] = 1;
            """
        );
    }

    private async Task InsertDescriptorResourceKeyAsync()
    {
        ResourceKeyEntry descriptorKey = _descriptorMappingSet.ResourceKeyById[DescriptorResourceKeyId()];

        await _database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = @resourceKeyId)
            BEGIN
                INSERT INTO [dms].[ResourceKey] (
                    [ResourceKeyId],
                    [ProjectName],
                    [ResourceName],
                    [ResourceVersion]
                )
                VALUES (
                    @resourceKeyId,
                    @projectName,
                    @resourceName,
                    @resourceVersion
                );
            END
            """,
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = descriptorKey.ResourceKeyId },
            new SqlParameter("@projectName", SqlDbType.NVarChar, 256)
            {
                Value = descriptorKey.Resource.ProjectName,
            },
            new SqlParameter("@resourceName", SqlDbType.NVarChar, 256)
            {
                Value = descriptorKey.Resource.ResourceName,
            },
            new SqlParameter("@resourceVersion", SqlDbType.NVarChar, 32)
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
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.UtcDateTime }
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
        SourceDocumentSeed[] seeds = Enumerable
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
            DECLARE @requested TABLE (
                [Ordinal] int NOT NULL,
                [DocumentUuid] uniqueidentifier NOT NULL,
                [ResourceKeyId] smallint NOT NULL,
                [ContentVersion] bigint NOT NULL
            );

            INSERT INTO @requested (
                [Ordinal],
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion]
            )
            SELECT
                [source].[Ordinal],
                [source].[DocumentUuid],
                [source].[ResourceKeyId],
                [source].[ContentVersion]
            FROM OPENJSON(@sourceDocumentsJson)
            WITH (
                [Ordinal] int '$.Ordinal',
                [DocumentUuid] uniqueidentifier '$.DocumentUuid',
                [ResourceKeyId] smallint '$.ResourceKeyId',
                [ContentVersion] bigint '$.ContentVersion'
            ) AS [source];

            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            SELECT
                [requested].[DocumentUuid],
                [requested].[ResourceKeyId],
                [requested].[ContentVersion],
                @lastModifiedAt
            FROM @requested AS [requested];

            SELECT
                [document].[DocumentId],
                [document].[DocumentUuid],
                [document].[ResourceKeyId],
                [document].[ContentVersion]
            FROM @requested AS [requested]
            INNER JOIN [dms].[Document] AS [document]
                ON [document].[DocumentUuid] = [requested].[DocumentUuid]
            ORDER BY [requested].[Ordinal];
            """,
            new SqlParameter("@sourceDocumentsJson", SqlDbType.NVarChar, -1)
            {
                Value = JsonSerializer.Serialize(seeds),
            },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.UtcDateTime }
        );

        return rows.Select(row => new SourceDocument(
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [ResourceKeyId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [EffectiveBeginDate],
                [EffectiveEndDate],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = descriptor.DocumentId },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = descriptor.ResourceKeyId },
            new SqlParameter("@namespace", SqlDbType.VarChar, 255)
            {
                Value = "uri://ed-fi.org/SchoolTypeDescriptor",
            },
            new SqlParameter("@codeValue", SqlDbType.VarChar, 50) { Value = "Alternative" },
            new SqlParameter("@shortDescription", SqlDbType.VarChar, 75) { Value = "Alternative" },
            new SqlParameter("@description", SqlDbType.VarChar, 1024) { Value = "Alternative school type" },
            new SqlParameter("@effectiveBeginDate", SqlDbType.Date) { Value = new DateOnly(2025, 1, 15) },
            new SqlParameter("@effectiveEndDate", SqlDbType.Date) { Value = new DateOnly(2025, 12, 31) },
            new SqlParameter("@discriminator", SqlDbType.VarChar, 128)
            {
                Value = DescriptorResource.ResourceName,
            },
            new SqlParameter("@uri", SqlDbType.VarChar, 306)
            {
                Value = "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
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
            new SqlParameter("@projectName", SqlDbType.NVarChar, 256)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new SqlParameter("@resourceName", SqlDbType.NVarChar, 256)
            {
                Value = resourceNameOverride ?? resourceKey.Resource.ResourceName,
            },
            new SqlParameter("@resourceVersion", SqlDbType.NVarChar, 32)
            {
                Value = resourceKey.ResourceVersion,
            },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64) { Value = $"etag-{contentVersion}" },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.UtcDateTime },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = $"cache-{contentVersion}" }.ToJsonString(),
            },
            new SqlParameter("@computedAt", SqlDbType.DateTime2)
            {
                Value = LastModifiedAt.AddMinutes(1).UtcDateTime,
            }
        );
    }

    private async Task InsertCacheRowsAsync(IReadOnlyList<SourceDocument> sources)
    {
        short resourceKeyId = sources.Select(static source => source.ResourceKeyId).Distinct().Single();
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[resourceKeyId];
        CacheRowSeed[] seeds = sources
            .Select(source => new CacheRowSeed(
                source.DocumentId,
                source.DocumentUuid,
                source.ContentVersion,
                $"etag-{source.ContentVersion}",
                new JsonObject { ["value"] = $"cache-{source.ContentVersion}" }.ToJsonString()
            ))
            .ToArray();

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
            SELECT
                [cache].[DocumentId],
                [cache].[DocumentUuid],
                @projectName,
                @resourceName,
                @resourceVersion,
                [cache].[ContentVersion],
                [cache].[StreamEtag],
                @lastModifiedAt,
                [cache].[DocumentJson],
                @computedAt
            FROM OPENJSON(@cacheRowsJson)
            WITH (
                [DocumentId] bigint '$.DocumentId',
                [DocumentUuid] uniqueidentifier '$.DocumentUuid',
                [ContentVersion] bigint '$.ContentVersion',
                [StreamEtag] varchar(64) '$.StreamEtag',
                [DocumentJson] nvarchar(max) '$.DocumentJson'
            ) AS [cache];
            """,
            new SqlParameter("@cacheRowsJson", SqlDbType.NVarChar, -1)
            {
                Value = JsonSerializer.Serialize(seeds),
            },
            new SqlParameter("@projectName", SqlDbType.NVarChar, 256)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new SqlParameter("@resourceName", SqlDbType.NVarChar, 256)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new SqlParameter("@resourceVersion", SqlDbType.NVarChar, 32)
            {
                Value = resourceKey.ResourceVersion,
            },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = LastModifiedAt.UtcDateTime },
            new SqlParameter("@computedAt", SqlDbType.DateTime2)
            {
                Value = LastModifiedAt.AddMinutes(1).UtcDateTime,
            }
        );
    }

    private async Task UpdateSourceContentVersionAsync(long documentId, long contentVersion)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Document]
            SET [ContentVersion] = @contentVersion,
                [ContentLastModifiedAt] = @lastModifiedAt
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2)
            {
                Value = LastModifiedAt.AddMinutes(5).UtcDateTime,
            }
        );
    }

    private async Task DeleteSourceDocumentAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
        );
    }

    private async Task<bool> SourceDocumentExistsAsync(long documentId) =>
        await _database.ExecuteScalarAsync<bool>(
            """
            SELECT CAST(
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM [dms].[Document]
                        WHERE [DocumentId] = @documentId
                    )
                    THEN 1
                    ELSE 0
                END
                AS bit
            );
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId }
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
        Func<Given_A_Mssql_DocumentCacheReadLookupAdapter, SourceDocument, Task> ArrangeAsync,
        DocumentCacheReadLookupOutcome ExpectedOutcome
    );
}
